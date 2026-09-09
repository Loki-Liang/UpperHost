using UpperHost.Abstractions.Transports;

namespace UpperHost.Transport.Ble;

public enum BleMessageType
{
    Write,
    Notification
}

public sealed class BleMessage
{
    public BleMessage(Guid serviceUuid, Guid characteristicUuid, byte[] value, BleMessageType type)
    {
        ArgumentNullException.ThrowIfNull(value);
        ServiceUuid = serviceUuid;
        CharacteristicUuid = characteristicUuid;
        Value = value.ToArray();
        Type = type;
    }

    public Guid ServiceUuid { get; }
    public Guid CharacteristicUuid { get; }
    public byte[] Value { get; }
    public BleMessageType Type { get; }
}

public sealed record BleTransportOptions(string DeviceAddress);

public interface IBleBackend : IAsyncDisposable
{
    Task ConnectAsync(BleTransportOptions options, CancellationToken cancellationToken = default);
    Task DisconnectAsync(CancellationToken cancellationToken = default);
    ValueTask WriteAsync(
        Guid serviceUuid,
        Guid characteristicUuid,
        ReadOnlyMemory<byte> value,
        CancellationToken cancellationToken = default);
    IAsyncEnumerable<BleMessage> NotificationsAsync(CancellationToken cancellationToken = default);
}

public sealed class BleTransport : IMessageTransport<BleMessage>
{
    private readonly IBleBackend _backend;
    private readonly BleTransportOptions _options;

    public BleTransport(IBleBackend backend, BleTransportOptions options)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        if (string.IsNullOrWhiteSpace(options.DeviceAddress))
            throw new ArgumentException("BLE device address/id is required.", nameof(options));

        Endpoint = new TransportEndpoint("ble", options.DeviceAddress);
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
            await _backend.ConnectAsync(_options, cancellationToken).ConfigureAwait(false);
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
        await _backend.DisconnectAsync(cancellationToken).ConfigureAwait(false);
        State = TransportState.Closed;
    }

    public ValueTask SendAsync(BleMessage message, CancellationToken cancellationToken = default)
    {
        EnsureOpen();
        ArgumentNullException.ThrowIfNull(message);
        if (message.Type != BleMessageType.Write)
            throw new InvalidOperationException("Only BLE Write messages can be sent. Notifications are receive-only.");

        return _backend.WriteAsync(
            message.ServiceUuid,
            message.CharacteristicUuid,
            message.Value,
            cancellationToken);
    }

    public IAsyncEnumerable<BleMessage> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        EnsureOpen();
        return _backend.NotificationsAsync(cancellationToken);
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
            throw new InvalidOperationException("BLE transport is not open.");
    }
}
