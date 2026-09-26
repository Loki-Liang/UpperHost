using OpenDeviceStudio.Acquisition;
using OpenDeviceStudio.Dataflow;
using OpenDeviceStudio.Sample.DataAcquisition.Acquisition;

namespace OpenDeviceStudio.Sample.DataAcquisition.Consumers;

public sealed class SampleDataflowRuntime : IAcquisitionSessionComponent
{
    private readonly FanOutHub<SampleFrame> _hub = new(new FanOutOptions(
        Capacity: 16,
        BackpressureMode: FanOutBackpressureMode.DropOldest));
    private readonly ConsoleDisplayConsumer _display;
    private readonly JsonLinesStorageConsumer _storage;

    private Task<DisplayStatistics>? _displayTask;
    private Task<int>? _storageTask;
    private int _stopped;
    private int _disposed;

    public SampleDataflowRuntime(string storagePath)
    {
        _display = new ConsoleDisplayConsumer(
            artificialRenderDelay: TimeSpan.FromMilliseconds(8),
            renderEvery: 25);
        _storage = new JsonLinesStorageConsumer(storagePath);
    }

    public string ComponentId => "sample:legacy-dataflow-artifact";
    public AcquisitionComponentKind Kind => AcquisitionComponentKind.RequiredArtifact;
    public string StoragePath => _storage.Path;
    public DisplayStatistics? DisplayStatistics { get; private set; }
    public int StoredFrames { get; private set; }

    public ValueTask PrepareAsync(
        AcquisitionComponentContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        _displayTask = _display.RunAsync(
            _hub.Subscribe(context.AbortToken),
            context.AbortToken);
        _storageTask = _storage.RunAsync(
            _hub.Subscribe(context.AbortToken),
            context.AbortToken);
        return ValueTask.CompletedTask;
    }

    public ValueTask PublishAsync(
        SampleFrame frame,
        CancellationToken cancellationToken = default) =>
        _hub.PublishAsync(frame, cancellationToken);

    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.Exchange(ref _stopped, 1) == 0)
            await _hub.DisposeAsync().ConfigureAwait(false);
    }

    public async ValueTask FinalizeAsync(CancellationToken cancellationToken = default)
    {
        if (_displayTask is null || _storageTask is null)
            throw new InvalidOperationException("Sample dataflow runtime was not prepared.");

        DisplayStatistics = await _displayTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        StoredFrames = await _storageTask.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask AbortAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _stopped, 1) == 0)
            await _hub.DisposeAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        if (Interlocked.Exchange(ref _stopped, 1) == 0)
            await _hub.DisposeAsync().ConfigureAwait(false);
    }
}
