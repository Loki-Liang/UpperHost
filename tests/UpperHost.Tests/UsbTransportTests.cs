using UpperHost.Abstractions.Transports;
using UpperHost.Transport.Usb;

namespace UpperHost.Tests;

public sealed class UsbTransportTests
{
    [Fact]
    public async Task Open_send_and_receive_delegate_to_usb_backend()
    {
        var backend = new FakeUsbByteChannel();
        backend.Inject([0x10, 0x20, 0x30]);
        var options = new UsbTransportOptions(0x1234, 0x5678, InEndpoint: 0x82, OutEndpoint: 0x02);
        await using var transport = new UsbTransport(backend, options);

        await transport.OpenAsync();
        await transport.SendAsync(new byte[] { 0xAA, 0x55 });

        ReadOnlyMemory<byte> received = default;
        await foreach (var chunk in transport.ReceiveAsync())
        {
            received = chunk;
            break;
        }

        Assert.Equal(TransportState.Open, transport.State);
        Assert.Equal("usb", transport.Endpoint.Scheme);
        Assert.Equal(options, backend.OpenedOptions);
        Assert.Equal((byte)0x02, backend.LastWriteEndpoint);
        Assert.Equal(new byte[] { 0xAA, 0x55 }, backend.LastWrite);
        Assert.Equal((byte)0x82, backend.LastReadEndpoint);
        Assert.Equal(new byte[] { 0x10, 0x20, 0x30 }, received.ToArray());
    }

    [Fact]
    public async Task Send_requires_open_transport()
    {
        await using var transport = new UsbTransport(new FakeUsbByteChannel(), new UsbTransportOptions(1, 2));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await transport.SendAsync(new byte[] { 0x01 }));
    }

    private sealed class FakeUsbByteChannel : IUsbByteChannel
    {
        private readonly Queue<byte[]> _reads = new();

        public UsbTransportOptions? OpenedOptions { get; private set; }
        public byte LastWriteEndpoint { get; private set; }
        public byte[] LastWrite { get; private set; } = [];
        public byte LastReadEndpoint { get; private set; }

        public void Inject(byte[] data) => _reads.Enqueue(data);

        public Task OpenAsync(UsbTransportOptions options, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OpenedOptions = options;
            return Task.CompletedTask;
        }

        public Task CloseAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public ValueTask WriteAsync(byte endpoint, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastWriteEndpoint = endpoint;
            LastWrite = data.ToArray();
            return ValueTask.CompletedTask;
        }

        public ValueTask<int> ReadAsync(byte endpoint, Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastReadEndpoint = endpoint;
            if (_reads.Count == 0)
                return ValueTask.FromResult(0);

            var data = _reads.Dequeue();
            data.CopyTo(buffer);
            return ValueTask.FromResult(data.Length);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
