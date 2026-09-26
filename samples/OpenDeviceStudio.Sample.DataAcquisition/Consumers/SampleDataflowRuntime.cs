using OpenDeviceStudio.Acquisition;
using OpenDeviceStudio.Dataflow;
using OpenDeviceStudio.Sample.DataAcquisition.Acquisition;

namespace OpenDeviceStudio.Sample.DataAcquisition.Consumers;

public sealed class SampleDataflowRuntime : IAcquisitionSessionComponent
{
    private readonly StreamRouter<SampleFrame> _router = new();
    private readonly ConsoleDisplayConsumer _display;
    private readonly JsonLinesStorageConsumer _storage;
    private int _stopped;
    private int _disposed;

    public SampleDataflowRuntime(string storagePath)
    {
        _display = new ConsoleDisplayConsumer(
            artificialRenderDelay: TimeSpan.FromMilliseconds(8),
            renderEvery: 25);
        _storage = new JsonLinesStorageConsumer(storagePath);

        _router.RegisterBranch(
            new StreamBranchOptions(
                "sample.processed-storage",
                "Processed JSONL storage",
                Capacity: 64,
                Delivery: StreamBranchDelivery.Required,
                Overflow: StreamOverflowPolicy.Wait,
                FailurePolicy: StreamBranchFailurePolicy.Propagate,
                Ordering: StreamOrderingPolicy.SerializedPublisherFifo),
            (item, token) => _storage.ConsumeAsync(item.Value, token));

        _router.RegisterBranch(
            new StreamBranchOptions(
                "sample.presentation",
                "Console presentation",
                Capacity: 16,
                Delivery: StreamBranchDelivery.Optional,
                Overflow: StreamOverflowPolicy.DropOldest,
                FailurePolicy: StreamBranchFailurePolicy.Isolate,
                Ordering: StreamOrderingPolicy.SerializedPublisherFifo),
            (item, token) => _display.ConsumeAsync(item.Value, token));
    }

    public string ComponentId => "sample:processed-dataflow-artifact";
    public AcquisitionComponentKind Kind => AcquisitionComponentKind.RequiredArtifact;
    public string StoragePath => _storage.Path;
    public DisplayStatistics? DisplayStatistics { get; private set; }
    public int StoredFrames { get; private set; }

    public async ValueTask PrepareAsync(
        AcquisitionComponentContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _storage.PrepareAsync(cancellationToken).ConfigureAwait(false);
        _router.Start();
    }

    public async ValueTask PublishAsync(
        SampleFrame frame,
        CancellationToken cancellationToken = default)
    {
        var result = await _router.PublishAsync(frame, cancellationToken).ConfigureAwait(false);

        if (result.RouterState == StreamRouterState.Faulted)
        {
            throw new InvalidOperationException(
                $"Sample dataflow faulted during publish sequence {result.PublishSequence}: {result.RouterFault}");
        }

        if (result.HasRequiredFailure)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException(
                $"Sample dataflow publish sequence {result.PublishSequence} was not accepted by every Required route.");
        }
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.Exchange(ref _stopped, 1) != 0)
            return;

        var terminal = await _router
            .CompleteAsync(StreamCompletionMode.Drain, cancellationToken)
            .ConfigureAwait(false);
        await _storage.CompleteAsync(cancellationToken).ConfigureAwait(false);

        if (terminal.State == StreamRouterState.Faulted)
        {
            throw new InvalidOperationException(
                $"Sample dataflow stopped in Faulted state: {terminal.FaultMessage}");
        }
    }

    public ValueTask FinalizeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DisplayStatistics = _display.Statistics;
        StoredFrames = _storage.Count;
        return ValueTask.CompletedTask;
    }

    public async ValueTask AbortAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _stopped, 1) == 0)
            await _router.DisposeAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        await _router.DisposeAsync().ConfigureAwait(false);
        await _storage.DisposeAsync().ConfigureAwait(false);
    }
}
