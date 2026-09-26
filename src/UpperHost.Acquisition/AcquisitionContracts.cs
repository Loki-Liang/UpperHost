using System.Collections.ObjectModel;

namespace UpperHost.Acquisition;

public enum AcquisitionSessionMode
{
    LiveAcquisition,
    Replay
}

public enum AcquisitionSessionState
{
    Created,
    Preparing,
    Ready,
    Running,
    Stopping,
    Faulting,
    Aborting,
    Completed,
    Faulted,
    Aborted
}

public enum AcquisitionStartupPhase
{
    None,
    Validating,
    PreparingRequiredComponents,
    PreparingSources,
    RequiredReady,
    StartingSources,
    AttachingOptionalComponents,
    Running,
    Stopping,
    Finalizing,
    Aborting,
    Completed
}

public enum AcquisitionComponentKind
{
    Processing,
    Router,
    RawRecorder,
    RequiredArtifact,
    Source,
    OptionalPresentation,
    OptionalAlgorithm,
    Other
}

public enum AcquisitionComponentRuntimeState
{
    Created,
    Preparing,
    Ready,
    Running,
    Stopping,
    Completed,
    Faulted,
    Isolated,
    Aborted,
    Disposed
}

public enum AcquisitionFaultCategory
{
    Configuration,
    Preparation,
    Source,
    RawIntegrity,
    Processing,
    RequiredBranch,
    OptionalComponent,
    Finalization,
    ShutdownTimeout,
    OperatorAbort,
    HostShutdown,
    Unknown
}

public enum AcquisitionMultiSourceFailurePolicy
{
    FailWholeSession,
    IsolateFailedSource
}

public sealed record AcquisitionSessionOptions(
    AcquisitionMultiSourceFailurePolicy MultiSourceFailurePolicy = AcquisitionMultiSourceFailurePolicy.FailWholeSession,
    TimeSpan? StopTimeout = null,
    TimeSpan? AbortTimeout = null,
    int SecondaryFaultCapacity = 64,
    bool AllowReplayRawRecording = false)
{
    internal TimeSpan EffectiveStopTimeout => StopTimeout ?? TimeSpan.FromSeconds(15);
    internal TimeSpan EffectiveAbortTimeout => AbortTimeout ?? TimeSpan.FromSeconds(5);

    internal void Validate()
    {
        if (EffectiveStopTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(StopTimeout));
        if (EffectiveAbortTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(AbortTimeout));
        if (SecondaryFaultCapacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(SecondaryFaultCapacity));
    }
}

public sealed class AcquisitionSessionConfiguration
{
    private readonly IReadOnlyDictionary<string, string> _values;

    public AcquisitionSessionConfiguration(IReadOnlyDictionary<string, string>? values = null)
    {
        _values = new ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(
                values ?? new Dictionary<string, string>(),
                StringComparer.Ordinal));
    }

    public IReadOnlyDictionary<string, string> Values => _values;
}

public interface IAcquisitionSessionComponent : IAsyncDisposable
{
    string ComponentId { get; }
    AcquisitionComponentKind Kind { get; }

    ValueTask PrepareAsync(
        AcquisitionComponentContext context,
        CancellationToken cancellationToken = default);

    ValueTask StopAsync(CancellationToken cancellationToken = default);

    ValueTask FinalizeAsync(CancellationToken cancellationToken = default);

    ValueTask AbortAsync(CancellationToken cancellationToken = default);
}

public interface IAcquisitionSource : IAcquisitionSessionComponent
{
    string SourceId { get; }
    long ConnectionEpoch { get; }
    bool IsReplay { get; }
    bool IsReadOnly { get; }

    ValueTask StartAsync(CancellationToken cancellationToken = default);
}

public interface IAcquisitionOptionalComponent : IAsyncDisposable
{
    string ComponentId { get; }
    AcquisitionComponentKind Kind { get; }

    ValueTask AttachAsync(
        AcquisitionComponentContext context,
        CancellationToken cancellationToken = default);

    ValueTask DetachAsync(CancellationToken cancellationToken = default);

    ValueTask AbortAsync(CancellationToken cancellationToken = default);
}

public interface IAcquisitionSourceIsolationObserver
{
    ValueTask SourceIsolatedAsync(
        AcquisitionSourceIsolation isolation,
        CancellationToken cancellationToken = default);
}

public sealed record AcquisitionRequiredComponentRegistration(
    IAcquisitionSessionComponent Component,
    int PrepareOrder = 0,
    int StopOrder = 0,
    int FinalizeOrder = 0);

public sealed record AcquisitionOptionalComponentRegistration(
    IAcquisitionOptionalComponent Component,
    bool EscalateFault = false);

public sealed record AcquisitionFault(
    AcquisitionFaultCategory Category,
    string ComponentId,
    string? SourceId,
    string Message,
    Exception? Exception,
    DateTimeOffset OccurredAt);

public sealed record AcquisitionSourceIsolation(
    string SessionId,
    string SourceId,
    long ConnectionEpoch,
    AcquisitionFault Fault,
    string ProcessingEpoch);

public sealed record AcquisitionComponentResult(
    string ComponentId,
    AcquisitionComponentKind Kind,
    AcquisitionComponentRuntimeState State,
    string? Error);

public sealed record AcquisitionSourceResult(
    string ComponentId,
    string SourceId,
    long ConnectionEpoch,
    AcquisitionComponentRuntimeState State,
    string? Error);

public sealed record AcquisitionSessionSnapshot(
    string SessionId,
    AcquisitionSessionMode Mode,
    AcquisitionSessionState State,
    AcquisitionStartupPhase Phase,
    int RequiredReady,
    int RequiredTotal,
    AcquisitionFault? RootFault,
    DateTimeOffset? StartedAt,
    DateTimeOffset? EndedAt);

public sealed record AcquisitionSessionResult(
    string SessionId,
    AcquisitionSessionMode Mode,
    AcquisitionSessionState TerminalState,
    AcquisitionFault? RootFault,
    IReadOnlyList<AcquisitionFault> SecondaryFaults,
    int DroppedSecondaryFaults,
    DateTimeOffset? StartedAt,
    DateTimeOffset EndedAt,
    string ProcessingEpoch,
    string? ReplaySourceArtifactId,
    IReadOnlyList<AcquisitionComponentResult> Components,
    IReadOnlyList<AcquisitionSourceResult> Sources);

public sealed class AcquisitionComponentContext
{
    private readonly Func<AcquisitionFaultCategory, Exception?, string?, bool> _faultReporter;

    internal AcquisitionComponentContext(
        string sessionId,
        AcquisitionSessionMode mode,
        string processingEpoch,
        string componentId,
        string? sourceId,
        AcquisitionSessionConfiguration configuration,
        CancellationToken sessionStopToken,
        CancellationToken abortToken,
        TimeProvider timeProvider,
        Func<AcquisitionFaultCategory, Exception?, string?, bool> faultReporter)
    {
        SessionId = sessionId;
        Mode = mode;
        ProcessingEpoch = processingEpoch;
        ComponentId = componentId;
        SourceId = sourceId;
        Configuration = configuration;
        SessionStopToken = sessionStopToken;
        AbortToken = abortToken;
        TimeProvider = timeProvider;
        _faultReporter = faultReporter;
    }

    public string SessionId { get; }
    public AcquisitionSessionMode Mode { get; }
    public string ProcessingEpoch { get; }
    public string ComponentId { get; }
    public string? SourceId { get; }
    public AcquisitionSessionConfiguration Configuration { get; }
    public CancellationToken SessionStopToken { get; }
    public CancellationToken AbortToken { get; }
    public TimeProvider TimeProvider { get; }

    public bool TryReportFault(
        AcquisitionFaultCategory category,
        Exception? exception = null,
        string? message = null) =>
        _faultReporter(category, exception, message);
}

public sealed class AcquisitionSessionDefinition
{
    public AcquisitionSessionDefinition(
        AcquisitionSessionMode mode,
        IReadOnlyList<IAcquisitionSource> sources,
        IReadOnlyList<AcquisitionRequiredComponentRegistration>? requiredComponents = null,
        IReadOnlyList<AcquisitionOptionalComponentRegistration>? optionalComponents = null,
        IReadOnlyList<IAcquisitionSourceIsolationObserver>? sourceIsolationObservers = null,
        AcquisitionSessionConfiguration? configuration = null,
        AcquisitionSessionOptions? options = null,
        string? sessionId = null,
        string? replaySourceArtifactId = null)
    {
        Mode = mode;
        Sources = (sources ?? throw new ArgumentNullException(nameof(sources))).ToArray();
        RequiredComponents = (requiredComponents ?? []).ToArray();
        OptionalComponents = (optionalComponents ?? []).ToArray();
        SourceIsolationObservers = (sourceIsolationObservers ?? []).ToArray();
        Configuration = configuration ?? new AcquisitionSessionConfiguration();
        Options = options ?? new AcquisitionSessionOptions();
        SessionId = string.IsNullOrWhiteSpace(sessionId)
            ? Guid.NewGuid().ToString("N")
            : sessionId;
        ReplaySourceArtifactId = replaySourceArtifactId;
        ProcessingEpoch = Guid.NewGuid().ToString("N");

        Validate();
    }

    public string SessionId { get; }
    public string ProcessingEpoch { get; }
    public AcquisitionSessionMode Mode { get; }
    public IReadOnlyList<IAcquisitionSource> Sources { get; }
    public IReadOnlyList<AcquisitionRequiredComponentRegistration> RequiredComponents { get; }
    public IReadOnlyList<AcquisitionOptionalComponentRegistration> OptionalComponents { get; }
    public IReadOnlyList<IAcquisitionSourceIsolationObserver> SourceIsolationObservers { get; }
    public AcquisitionSessionConfiguration Configuration { get; }
    public AcquisitionSessionOptions Options { get; }
    public string? ReplaySourceArtifactId { get; }

    private void Validate()
    {
        Options.Validate();

        if (Sources.Count == 0)
            throw new ArgumentException("At least one acquisition source is required.", nameof(Sources));

        if (Mode == AcquisitionSessionMode.Replay)
        {
            if (string.IsNullOrWhiteSpace(ReplaySourceArtifactId))
                throw new ArgumentException(
                    "Replay sessions require a source Raw artifact identity.",
                    nameof(ReplaySourceArtifactId));

            if (Sources.Any(static source => !source.IsReplay || !source.IsReadOnly))
                throw new ArgumentException(
                    "Replay sources must declare replay mode and read-only access.",
                    nameof(Sources));

            if (!Options.AllowReplayRawRecording &&
                RequiredComponents.Any(static item => item.Component.Kind == AcquisitionComponentKind.RawRecorder))
            {
                throw new ArgumentException(
                    "Replay sessions do not create a Raw recorder unless explicit Raw export is enabled.",
                    nameof(RequiredComponents));
            }
        }
        else if (Sources.Any(static source => source.IsReplay))
        {
            throw new ArgumentException(
                "Live acquisition sessions cannot contain replay sources.",
                nameof(Sources));
        }

        if (Options.MultiSourceFailurePolicy == AcquisitionMultiSourceFailurePolicy.IsolateFailedSource &&
            Sources.Count > 1 &&
            SourceIsolationObservers.Count == 0)
        {
            throw new ArgumentException(
                "Multi-source isolation requires at least one isolation observer so quality/dependency state cannot be silently lost.",
                nameof(SourceIsolationObservers));
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var component in RequiredComponents.Select(static item => item.Component))
            AddUnique(component.ComponentId);
        foreach (var source in Sources)
            AddUnique(source.ComponentId);
        foreach (var component in OptionalComponents.Select(static item => item.Component))
            AddUnique(component.ComponentId);

        var sourceIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in Sources)
        {
            if (string.IsNullOrWhiteSpace(source.SourceId) || !sourceIds.Add(source.SourceId))
                throw new ArgumentException("SourceId values must be non-empty and unique.", nameof(Sources));
        }

        void AddUnique(string id)
        {
            if (string.IsNullOrWhiteSpace(id) || !ids.Add(id))
                throw new ArgumentException(
                    "ComponentId values must be non-empty and unique within a session definition.");
        }
    }
}
