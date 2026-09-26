namespace OpenDeviceStudio.Acquisition;

public sealed record AcquisitionRawAcceptance(bool Accepted, string? Reason = null)
{
    public static AcquisitionRawAcceptance Success { get; } = new(true);
}

public interface IAcquisitionRawSink<in TBlock>
{
    ValueTask<AcquisitionRawAcceptance> AcceptAsync(
        TBlock block,
        CancellationToken cancellationToken = default);
}

public interface IAcquisitionProcessingSink<in TBlock>
{
    ValueTask HandoffAsync(
        TBlock block,
        CancellationToken cancellationToken = default);
}

public sealed class AcquisitionRawRejectedException(string? reason)
    : InvalidOperationException(reason ?? "Raw recorder ingress rejected the acquisition block.");

public sealed class AcquisitionIngressClosedException()
    : InvalidOperationException("Acquisition ingress is closed; the block was rejected before Raw acceptance.");

internal sealed class AcquisitionIngressGate(AcquisitionSessionMode mode)
{
    private TaskCompletionSource _drained =
        CompletedSource();
    private int _state;
    private int _active;
    private long _rejectedAfterClose;

    public bool IsAccepting => Volatile.Read(ref _state) == 1;
    public long RejectedAfterClose => Interlocked.Read(ref _rejectedAfterClose);

    public void Open()
    {
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (Interlocked.CompareExchange(ref _state, 1, 0) != 0)
            throw new InvalidOperationException("Acquisition ingress can only be opened once.");

        Volatile.Write(ref _drained, drained);
    }

    public void Close()
    {
        var previous = Interlocked.Exchange(ref _state, 2);
        if (previous == 2)
            return;

        if (Volatile.Read(ref _active) == 0)
            Volatile.Read(ref _drained).TrySetResult();
    }

    public bool TryAcquire(out IDisposable? lease)
    {
        while (Volatile.Read(ref _state) == 1)
        {
            Interlocked.Increment(ref _active);
            if (Volatile.Read(ref _state) == 1)
            {
                lease = new Lease(this);
                return true;
            }

            Release();
        }

        Interlocked.Increment(ref _rejectedAfterClose);
        AcquisitionTelemetry.ClosedIngressRejections.Add(
            1,
            AcquisitionTelemetry.ModeTags(mode));
        lease = null;
        return false;
    }

    public Task WaitForDrainAsync(CancellationToken cancellationToken = default) =>
        Volatile.Read(ref _drained).Task.WaitAsync(cancellationToken);

    private void Release()
    {
        if (Interlocked.Decrement(ref _active) == 0 && Volatile.Read(ref _state) != 1)
            Volatile.Read(ref _drained).TrySetResult();
    }

    private static TaskCompletionSource CompletedSource()
    {
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.TrySetResult();
        return source;
    }

    private sealed class Lease(AcquisitionIngressGate owner) : IDisposable
    {
        private AcquisitionIngressGate? _owner = owner;

        public void Dispose() =>
            Interlocked.Exchange(ref _owner, null)?.Release();
    }
}

public sealed class RawFirstAcquisitionIngress<TBlock>
{
    private readonly AcquisitionIngressGate _gate;
    private readonly IAcquisitionRawSink<TBlock> _raw;
    private readonly IAcquisitionProcessingSink<TBlock> _processing;

    internal RawFirstAcquisitionIngress(
        AcquisitionIngressGate gate,
        IAcquisitionRawSink<TBlock> raw,
        IAcquisitionProcessingSink<TBlock> processing)
    {
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _raw = raw ?? throw new ArgumentNullException(nameof(raw));
        _processing = processing ?? throw new ArgumentNullException(nameof(processing));
    }

    public async ValueTask PublishAsync(
        TBlock block,
        CancellationToken cancellationToken = default)
    {
        if (!_gate.TryAcquire(out var lease))
            throw new AcquisitionIngressClosedException();

        using (lease)
        {
            var acceptance = await _raw
                .AcceptAsync(block, cancellationToken)
                .ConfigureAwait(false);

            if (!acceptance.Accepted)
                throw new AcquisitionRawRejectedException(acceptance.Reason);

            await _processing
                .HandoffAsync(block, cancellationToken)
                .ConfigureAwait(false);
        }
    }
}
