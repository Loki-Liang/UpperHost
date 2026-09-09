using UpperHost.Abstractions.Transports;

namespace UpperHost.Transport.Can;

public sealed class CanFrame
{
    public CanFrame(
        uint arbitrationId,
        byte[] data,
        bool isExtendedId = false,
        bool isFlexibleDataRate = false,
        bool bitRateSwitch = false,
        DateTimeOffset? timestamp = null)
    {
        ArgumentNullException.ThrowIfNull(data);

        var maxId = isExtendedId ? 0x1FFFFFFFu : 0x7FFu;
        if (arbitrationId > maxId)
            throw new ArgumentOutOfRangeException(nameof(arbitrationId), $"CAN identifier exceeds 0x{maxId:X}.");

        var maxPayload = isFlexibleDataRate ? 64 : 8;
        if (data.Length > maxPayload)
            throw new ArgumentOutOfRangeException(nameof(data), $"CAN payload exceeds {maxPayload} bytes.");

        if (bitRateSwitch && !isFlexibleDataRate)
            throw new ArgumentException("Bit-rate switch is valid only for CAN FD frames.", nameof(bitRateSwitch));

        ArbitrationId = arbitrationId;
        Data = data.ToArray();
        IsExtendedId = isExtendedId;
        IsFlexibleDataRate = isFlexibleDataRate;
        BitRateSwitch = bitRateSwitch;
        Timestamp = timestamp;
    }

    public uint ArbitrationId { get; }
    public byte[] Data { get; }
    public bool IsExtendedId { get; }
    public bool IsFlexibleDataRate { get; }
    public bool BitRateSwitch { get; }
    public DateTimeOffset? Timestamp { get; }
}

public sealed record CanTransportOptions(
    string Channel,
    int NominalBitRate = 500_000,
    int? DataBitRate = null);

public interface ICanBackend : IAsyncDisposable
{
    Task OpenAsync(CanTransportOptions options, CancellationToken cancellationToken = default);
    Task CloseAsync(CancellationToken cancellationToken = default);
    ValueTask SendAsync(CanFrame frame, CancellationToken cancellationToken = default);
    IAsyncEnumerable<CanFrame> ReceiveAsync(CancellationToken cancellationToken = default);
}

public sealed class CanTransport : IMessageTransport<CanFrame>
{
    private readonly ICanBackend _backend;
    private readonly CanTransportOptions _options;

    public CanTransport(ICanBackend backend, CanTransportOptions options)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        if (string.IsNullOrWhiteSpace(options.Channel))
            throw new ArgumentException("CAN channel is required.", nameof(options));
        if (options.NominalBitRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "NominalBitRate must be greater than zero.");
        if (options.DataBitRate is <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "DataBitRate must be greater than zero when provided.");

        Endpoint = new TransportEndpoint("can", options.Channel);
    }

    public TransportEndpoint Endpoint { get; }

    public TransportState State { get; private set; } = TransportState.Closed;

    public async Task OpenAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (State == TransportState.Open)
            return;

        State = TransportState.Opening;
        try
        {
            await _backend.OpenAsync(_options, cancellationToken).ConfigureAwait(false);
            State = TransportState.Open;
        }
        catch
        {
            State = TransportState.Faulted;
            throw;
        }
    }

    public async Task CloseAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _backend.CloseAsync(cancellationToken).ConfigureAwait(false);
        State = TransportState.Closed;
    }

    public ValueTask SendAsync(CanFrame message, CancellationToken cancellationToken = default)
    {
        EnsureOpen();
        ArgumentNullException.ThrowIfNull(message);
        return _backend.SendAsync(message, cancellationToken);
    }

    public IAsyncEnumerable<CanFrame> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        EnsureOpen();
        return _backend.ReceiveAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (State != TransportState.Closed)
            await CloseAsync().ConfigureAwait(false);
        await _backend.DisposeAsync().ConfigureAwait(false);
    }

    private void EnsureOpen()
    {
        if (State != TransportState.Open)
            throw new InvalidOperationException("CAN transport is not open.");
    }
}
