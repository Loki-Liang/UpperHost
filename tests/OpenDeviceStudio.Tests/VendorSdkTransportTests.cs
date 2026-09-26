using OpenDeviceStudio.Abstractions.Transports;
using OpenDeviceStudio.Transport.VendorSdk;

namespace OpenDeviceStudio.Tests;

public sealed class VendorSdkTransportTests
{
    [Fact]
    public async Task Open_send_and_receive_delegate_to_vendor_session()
    {
        var session = new FakeVendorByteSession();
        session.Inject([0x10, 0x20, 0x30]);

        var options = new VendorSdkTransportOptions("Acme", "SN-001");
        await using var transport = new VendorSdkTransport(session, options);

        await transport.OpenAsync();
        await transport.SendAsync(new byte[] { 0xAA, 0x55 });

        ReadOnlyMemory<byte> received = default;
        await foreach (var chunk in transport.ReceiveAsync())
        {
            received = chunk;
            break;
        }

        Assert.Equal(TransportState.Open, transport.State);
        Assert.Equal("vendor-sdk", transport.Endpoint.Scheme);
        Assert.Equal(options, session.OpenedOptions);
        Assert.Equal(new byte[] { 0xAA, 0x55 }, session.LastWrite);
        Assert.Equal(new byte[] { 0x10, 0x20, 0x30 }, received.ToArray());
    }

    [Fact]
    public async Task Send_requires_open_transport()
    {
        await using var transport = new VendorSdkTransport(
            new FakeVendorByteSession(),
            new VendorSdkTransportOptions("Acme", "SN-001"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await transport.SendAsync(new byte[] { 0x01 }));
    }

    [Fact]
    public async Task Open_failure_faults_transport()
    {
        var session = new FakeVendorByteSession { FailOpen = true };
        await using var transport = new VendorSdkTransport(
            session,
            new VendorSdkTransportOptions("Acme", "SN-001"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => transport.OpenAsync());
        Assert.Equal(TransportState.Faulted, transport.State);
    }

    private sealed class FakeVendorByteSession : IVendorByteSession
    {
        private readonly Queue<byte[]> _reads = new();

        public bool FailOpen { get; init; }
        public VendorSdkTransportOptions? OpenedOptions { get; private set; }
        public byte[] LastWrite { get; private set; } = [];

        public void Inject(byte[] data) => _reads.Enqueue(data);

        public Task OpenAsync(VendorSdkTransportOptions options, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (FailOpen)
                throw new InvalidOperationException("simulated vendor open failure");

            OpenedOptions = options;
            return Task.CompletedTask;
        }

        public Task CloseAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastWrite = data.ToArray();
            return ValueTask.CompletedTask;
        }

        public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_reads.Count == 0)
                return ValueTask.FromResult(0);

            var data = _reads.Dequeue();
            data.CopyTo(buffer);
            return ValueTask.FromResult(data.Length);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
