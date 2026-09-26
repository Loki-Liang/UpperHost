using UpperHost.Acquisition;
using UpperHost.Dataflow;
using UpperHost.Sample.DataAcquisition.Acquisition;

namespace UpperHost.Sample.DataAcquisition.Consumers;

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
            StreamBranchOptions.Required(
                "sample.processed-storage",
                "Processed JSONL storage",
                capacity: 64,
                overflow: StreamOverflowPolicy.Wait,
                shutdownPolicy: StreamShutdownPolicy.Drain),
            _storage.ConsumeAsync);

        _router.RegisterBranch(
            StreamBranchOptions.Optional(
                "sample.presentation",
                "Console presentation",
                capacity: 16,
                overflow: StreamOverflowPolicy.DropOldest,
                failurePolicy: StreamBranchFailurePolicy.Isolate,
                shutdownPolicy: StreamShutdownPolicy.Cancel),
            _display.ConsumeAsync);
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
        await _router.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask PublishAsync(
        SampleFrame frame,
        CancellationToken cancellationToken = default)
    {
        var result = await _router.PublishAsync(frame, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (result.RequiresStop)
            {
                throw new InvalidOperationException(
                    $"Sample dataflow publish sequence {result.Sequence} failed on a required route.");
            }
        }
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.Exchange(ref _stopped, 1) != 0)
            return;

        await _router.CompleteAsync(cancellationToken).ConfigureAwait(false);
        await _storage.CompleteAsync(cancellationToken).ConfigureAwait(false);
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

        if (Interlocked.Exchange(ref _stopped, 1) == 0)
            await _router.DisposeAsync().ConfigureAwait(false);

        await _storage.DisposeAsync().ConfigureAwait(false);
    }
}
