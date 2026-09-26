using UpperHost.Acquisition;

namespace UpperHost.Sample.DataAcquisition.Acquisition;

public sealed class SessionManagedSimulatorSource : IAcquisitionSource
{
    private readonly SimulatorAcquisitionDevice _device;
    private readonly Func<SampleFrame, CancellationToken, ValueTask> _publish;
    private readonly TaskCompletionSource _productionCompleted =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private AcquisitionComponentContext? _context;
    private CancellationTokenSource? _runCancellation;
    private Task _producer = Task.CompletedTask;
    private int _started;
    private int _stopped;
    private int _disposed;

    public SessionManagedSimulatorSource(
        SimulatorAcquisitionDevice device,
        Func<SampleFrame, CancellationToken, ValueTask> publish)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
        _publish = publish ?? throw new ArgumentNullException(nameof(publish));
    }

    public string ComponentId => "source:sample-simulator";
    public string SourceId => _device.Descriptor.Id;
    public long ConnectionEpoch => 1;
    public bool IsReplay => false;
    public bool IsReadOnly => false;
    public AcquisitionComponentKind Kind => AcquisitionComponentKind.Source;
    public Task ProductionCompleted => _productionCompleted.Task;

    public async ValueTask PrepareAsync(
        AcquisitionComponentContext context,
        CancellationToken cancellationToken = default)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        await _device.ConnectAsync(cancellationToken).ConfigureAwait(false);
    }

    public ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.Exchange(ref _started, 1) != 0)
            throw new InvalidOperationException("Sample source can only be started once.");

        var context = _context ?? throw new InvalidOperationException("Sample source was not prepared.");
        _runCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            context.SessionStopToken,
            context.AbortToken);
        _producer = ProduceAsync(_runCancellation.Token);
        return ValueTask.CompletedTask;
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0)
            return;

        _runCancellation?.Cancel();

        try
        {
            await _producer.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_runCancellation?.IsCancellationRequested == true)
        {
        }

        await _device.DisconnectAsync(cancellationToken).ConfigureAwait(false);
    }

    public ValueTask FinalizeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    public async ValueTask AbortAsync(CancellationToken cancellationToken = default)
    {
        _runCancellation?.Cancel();

        try
        {
            await _producer.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        await _device.DisconnectAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _runCancellation?.Cancel();
        try
        {
            await _producer.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _runCancellation?.Dispose();
    }

    private async Task ProduceAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var frame in _device.ReadAsync(cancellationToken).ConfigureAwait(false))
                await _publish(frame, cancellationToken).ConfigureAwait(false);

            _productionCompleted.TrySetResult();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _productionCompleted.TrySetResult();
        }
        catch (Exception ex)
        {
            _context?.TryReportFault(AcquisitionFaultCategory.Source, ex, ex.Message);
            _productionCompleted.TrySetException(ex);
        }
    }
}
