namespace UpperHost.Acquisition;

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

public sealed class RawFirstAcquisitionIngress<TBlock>
{
    private readonly IAcquisitionRawSink<TBlock> _raw;
    private readonly IAcquisitionProcessingSink<TBlock> _processing;

    public RawFirstAcquisitionIngress(
        IAcquisitionRawSink<TBlock> raw,
        IAcquisitionProcessingSink<TBlock> processing)
    {
        _raw = raw ?? throw new ArgumentNullException(nameof(raw));
        _processing = processing ?? throw new ArgumentNullException(nameof(processing));
    }

    public async ValueTask PublishAsync(
        TBlock block,
        CancellationToken cancellationToken = default)
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
