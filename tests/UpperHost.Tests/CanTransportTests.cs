using System.Runtime.CompilerServices;
using UpperHost.Abstractions.Transports;
using UpperHost.Transport.Can;

namespace UpperHost.Tests;

public sealed class CanTransportTests
{
    [Fact]
    public async Task Open_send_and_receive_preserve_can_frames()
    {
        var backend = new FakeCanBackend();
        var incoming = new CanFrame(0x321, new byte[] { 0x10, 0x20 });
        backend.Inject(incoming);
        await using var transport = new CanTransport(backend, new CanTransportOptions("can0"));

        await transport.OpenAsync();
        var outgoing = new CanFrame(0x123, new byte[] { 0xAA, 0x55 });
        await transport.SendAsync(outgoing);

        CanFrame? received = null;
        await foreach (var frame in transport.ReceiveAsync())
        {
            received = frame;
            break;
        }

        Assert.Equal(TransportState.Open, transport.State);
        Assert.Same(outgoing, backend.LastSent);
        Assert.Same(incoming, received);
        Assert.Equal("can", transport.Endpoint.Scheme);
    }

    [Fact]
    public void Classic_can_payload_cannot_exceed_eight_bytes()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CanFrame(0x100, new byte[9]));
    }

    [Fact]
    public void Standard_identifier_cannot_exceed_11_bits()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CanFrame(0x800, Array.Empty<byte>()));
    }

    private sealed class FakeCanBackend : ICanBackend
    {
        private readonly Queue<CanFrame> _incoming = new();

        public CanFrame? LastSent { get; private set; }

        public void Inject(CanFrame frame) => _incoming.Enqueue(frame);

        public Task OpenAsync(CanTransportOptions options, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task CloseAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public ValueTask SendAsync(CanFrame frame, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastSent = frame;
            return ValueTask.CompletedTask;
        }

        public async IAsyncEnumerable<CanFrame> ReceiveAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask.ConfigureAwait(false);
            while (_incoming.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return _incoming.Dequeue();
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
