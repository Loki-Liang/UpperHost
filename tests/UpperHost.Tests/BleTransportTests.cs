using System.Runtime.CompilerServices;
using UpperHost.Abstractions.Transports;
using UpperHost.Transport.Ble;

namespace UpperHost.Tests;

public sealed class BleTransportTests
{
    [Fact]
    public async Task Write_and_notification_preserve_gatt_identity()
    {
        var backend = new FakeBleBackend();
        var service = Guid.NewGuid();
        var characteristic = Guid.NewGuid();
        var notification = new BleMessage(service, characteristic, new byte[] { 0x42 }, BleMessageType.Notification);
        backend.Inject(notification);
        await using var transport = new BleTransport(backend, new BleTransportOptions("device-1"));

        await transport.OpenAsync();
        var write = new BleMessage(service, characteristic, new byte[] { 0xAA }, BleMessageType.Write);
        await transport.SendAsync(write);

        BleMessage? received = null;
        await foreach (var message in transport.ReceiveAsync())
        {
            received = message;
            break;
        }

        Assert.Equal(TransportState.Open, transport.State);
        Assert.Equal(service, backend.LastService);
        Assert.Equal(characteristic, backend.LastCharacteristic);
        Assert.Equal(new byte[] { 0xAA }, backend.LastValue);
        Assert.Same(notification, received);
    }

    [Fact]
    public async Task Notification_cannot_be_sent()
    {
        await using var transport = new BleTransport(new FakeBleBackend(), new BleTransportOptions("device-1"));
        await transport.OpenAsync();
        var message = new BleMessage(Guid.NewGuid(), Guid.NewGuid(), Array.Empty<byte>(), BleMessageType.Notification);

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await transport.SendAsync(message));
    }

    private sealed class FakeBleBackend : IBleBackend
    {
        private readonly Queue<BleMessage> _notifications = new();

        public Guid LastService { get; private set; }
        public Guid LastCharacteristic { get; private set; }
        public byte[] LastValue { get; private set; } = [];

        public void Inject(BleMessage message) => _notifications.Enqueue(message);

        public Task ConnectAsync(BleTransportOptions options, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public ValueTask WriteAsync(
            Guid serviceUuid,
            Guid characteristicUuid,
            ReadOnlyMemory<byte> value,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastService = serviceUuid;
            LastCharacteristic = characteristicUuid;
            LastValue = value.ToArray();
            return ValueTask.CompletedTask;
        }

        public async IAsyncEnumerable<BleMessage> NotificationsAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask.ConfigureAwait(false);
            while (_notifications.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return _notifications.Dequeue();
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
