using System.Security.Cryptography;
using System.Text;
using UpperHost.Abstractions.Storage;

namespace UpperHost.Acquisition;

public interface IAcquisitionSessionDefinitionEnricher
{
    AcquisitionSessionDefinition Enrich(AcquisitionSessionDefinition definition);
}

internal sealed class RawRecordingSessionDefinitionEnricher(
    IEnumerable<IRawRecorderFactory> factories) : IAcquisitionSessionDefinitionEnricher
{
    private readonly IReadOnlyList<IRawRecorderFactory> _factories = factories.ToArray();

    public AcquisitionSessionDefinition Enrich(AcquisitionSessionDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        if (definition.RequiredComponents.Any(static item =>
                item.Component.Kind == AcquisitionComponentKind.RawRecorder))
            return definition;

        if (_factories.Count == 0)
            return definition;

        if (_factories.Count != 1)
            throw new InvalidOperationException(
                "Exactly one default IRawRecorderFactory may be registered.");

        var factory = _factories[0];

        if (!factory.IsEnabled)
        {
            var values = definition.Configuration.Values.ToDictionary(
                static pair => pair.Key,
                static pair => pair.Value,
                StringComparer.Ordinal);
            values["raw.recording.enabled"] = "false";
            values["raw.recording.disabled_reason"] =
                string.IsNullOrWhiteSpace(factory.DisabledReason)
                    ? "disabled by configuration"
                    : factory.DisabledReason!;
            return definition.WithConfiguration(new AcquisitionSessionConfiguration(values));
        }

        if (definition.Mode == AcquisitionSessionMode.Replay &&
            !definition.Options.AllowReplayRawRecording)
            return definition;

        return definition.WithRequiredComponent(
            new AcquisitionRequiredComponentRegistration(
                new RawRecorderAcquisitionComponent(definition, factory.Create())));
    }
}

internal sealed class RawRecorderAcquisitionComponent :
    IAcquisitionSessionComponent,
    IAcquisitionRawSink<CanonicalRawBlock>,
    IAcquisitionTryRawSink<CanonicalRawBlock>,
    IRawRecorderFaultObserver
{
    private readonly AcquisitionSessionDefinition _definition;
    private readonly IRawRecorder _recorder;
    private AcquisitionComponentContext? _context;

    public RawRecorderAcquisitionComponent(
        AcquisitionSessionDefinition definition,
        IRawRecorder recorder)
    {
        _definition = definition ?? throw new ArgumentNullException(nameof(definition));
        _recorder = recorder ?? throw new ArgumentNullException(nameof(recorder));
    }

    public string ComponentId => "raw-recorder:default";
    public AcquisitionComponentKind Kind => AcquisitionComponentKind.RawRecorder;

    public async ValueTask PrepareAsync(
        AcquisitionComponentContext context,
        CancellationToken cancellationToken = default)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));

        var configuration = string.Join(
            "\n",
            context.Configuration.Values
                .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
                .Select(static pair => $"{pair.Key}={pair.Value}"));
        var hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(configuration)));

        var descriptor = new RawRecordingSessionDescriptor(
            context.SessionId,
            _definition.Sources
                .Select(static source =>
                    new RawRecordingSourceIdentity(source.SourceId, source.ConnectionEpoch))
                .ToArray(),
            hash,
            context.TimeProvider.GetUtcNow());

        await _recorder
            .PrepareAsync(descriptor, this, cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<AcquisitionRawAcceptance> AcceptAsync(
        CanonicalRawBlock block,
        CancellationToken cancellationToken = default)
    {
        var result = await _recorder.AcceptAsync(block, cancellationToken).ConfigureAwait(false);
        return new AcquisitionRawAcceptance(result.Accepted, result.Reason);
    }

    public AcquisitionRawAcceptance TryAccept(CanonicalRawBlock block)
    {
        var result = _recorder.TryAccept(block);
        return new AcquisitionRawAcceptance(result.Accepted, result.Reason);
    }

    public ValueTask StopAsync(CancellationToken cancellationToken = default) =>
        _recorder.StopAsync(cancellationToken);

    public ValueTask FinalizeAsync(CancellationToken cancellationToken = default) =>
        _recorder.FinalizeAsync(cancellationToken);

    public ValueTask AbortAsync(CancellationToken cancellationToken = default) =>
        _recorder.AbortAsync("Acquisition Session aborted.", cancellationToken);

    public ValueTask DisposeAsync() => _recorder.DisposeAsync();

    public void OnFault(RawRecorderFault fault)
    {
        _context?.TryReportFault(
            AcquisitionFaultCategory.RawIntegrity,
            fault.Exception,
            $"{fault.Code}: {fault.Message}");
    }
}
