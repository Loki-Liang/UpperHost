using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using UpperHost.Abstractions.Transports;
using UpperHost.Testing;
using UpperHost.Transport.Serial;
using UpperHost.Transport.Simulator;
using UpperHost.Transport.Tcp;

namespace UpperHost.Tests;

public sealed class ProviderContractTests
{
    [Fact]
    public Task Simulator_runs_common_transport_contract() =>
        TransportContractTestKit.VerifyAsync(
            _ => ValueTask.FromResult<ITransportContractFixture>(new SimulatorContractFixture()));

    [Fact]
    public Task Tcp_runs_common_transport_contract() =>
        TransportContractTestKit.VerifyAsync(
            _ => ValueTask.FromResult<ITransportContractFixture>(new TcpContractFixture()));

    [Fact]
    public Task Serial_runs_common_transport_contract_without_real_hardware() =>
        TransportContractTestKit.VerifyAsync(
            _ => ValueTask.FromResult<ITransportContractFixture>(new SerialContractFixture()));

    private sealed class SimulatorContractFixture : ITransportContractFixture
    {
        private readonly SimulatorTransport _transport = new("contract", capacity: 4);
        private readonly Channel<byte[]> _sent = Channel.CreateUnbounded<byte[]>();
        private int _disposed;

        public SimulatorContractFixture() =>
            _transport.Sent += OnSent;

        public string Name => "simulator";
        public ITransport Transport => _transport;

        public ValueTask SendFromPeerAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default) =>
            _transport.InjectAsync(data, cancellationToken);

        public async ValueTask<ReadOnlyMemory<byte>> ReceiveFromTransportAsync(
            int expectedLength,
            CancellationToken cancellationToken = default) =>
            await _sent.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);

        public ValueTask AssertResourcesReleasedAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.True(Volatile.Read(ref _disposed) != 0);
            Assert.Equal(TransportState.Closed, _transport.State);
            return ValueTask.CompletedTask;
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            _transport.Sent -= OnSent;
            _sent.Writer.TryComplete();
            await _transport.DisposeAsync().ConfigureAwait(false);
        }

        private void OnSent(ReadOnlyMemory<byte> data) => _sent.Writer.TryWrite(data.ToArray());
    }

    private sealed class TcpContractFixture : ITransportContractFixture
    {
        private readonly TcpListener _listener;
        private readonly TcpTransport _transport;
        private readonly Task<TcpClient> _acceptTask;
        private TcpClient? _peer;
        private int _disposed;

        public TcpContractFixture()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            var endpoint = (IPEndPoint)_listener.LocalEndpoint;
            _transport = new TcpTransport(new TcpTransportOptions("127.0.0.1", endpoint.Port, 1024));
            _acceptTask = _listener.AcceptTcpClientAsync(CancellationToken.None).AsTask();
        }

        public string Name => "tcp-loopback";
        public ITransport Transport => _transport;

        public async ValueTask SendFromPeerAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        {
            var peer = await GetPeerAsync(cancellationToken).ConfigureAwait(false);
            await peer.GetStream().WriteAsync(data, cancellationToken).ConfigureAwait(false);
            await peer.GetStream().FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        public async ValueTask<ReadOnlyMemory<byte>> ReceiveFromTransportAsync(
            int expectedLength,
            CancellationToken cancellationToken = default)
        {
            var peer = await GetPeerAsync(cancellationToken).ConfigureAwait(false);
            var stream = peer.GetStream();
            var buffer = new byte[expectedLength];
            var offset = 0;

            while (offset < expectedLength)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(offset), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    throw new IOException($"TCP peer closed after {offset} of {expectedLength} bytes.");
                offset += read;
            }

            return buffer;
        }

        public ValueTask AssertResourcesReleasedAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.True(Volatile.Read(ref _disposed) != 0);
            Assert.Equal(TransportState.Closed, _transport.State);
            return ValueTask.CompletedTask;
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            await _transport.DisposeAsync().ConfigureAwait(false);
            if (_acceptTask.IsCompletedSuccessfully)
                _peer ??= _acceptTask.Result;

            _peer?.Dispose();
            _listener.Stop();
        }

        private async Task<TcpClient> GetPeerAsync(CancellationToken cancellationToken)
        {
            _peer ??= await _acceptTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            return _peer;
        }
    }

    private sealed class SerialContractFixture : ITransportContractFixture
    {
        private readonly FakeSerialByteChannel _channel = new();
        private readonly SerialTransport _transport;
        private int _disposed;

        public SerialContractFixture() =>
            _transport = new SerialTransport(
                new SerialTransportOptions("TEST", ReadBufferSize: 1024),
                _channel);

        public string Name => "serial-test-channel";
        public ITransport Transport => _transport;

        public ValueTask SendFromPeerAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default) =>
            _channel.SendFromPeerAsync(data, cancellationToken);

        public ValueTask<ReadOnlyMemory<byte>> ReceiveFromTransportAsync(
            int expectedLength,
            CancellationToken cancellationToken = default) =>
            _channel.ReceiveFromTransportAsync(expectedLength, cancellationToken);

        public ValueTask AssertResourcesReleasedAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.True(Volatile.Read(ref _disposed) != 0);
            Assert.True(_channel.Released);
            Assert.Equal(TransportState.Closed, _transport.State);
            return ValueTask.CompletedTask;
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            await _transport.DisposeAsync().ConfigureAwait(false);
        }
    }

    private sealed class FakeSerialByteChannel : ISerialByteChannel
    {
        private readonly Channel<byte[]> _toTransport = Channel.CreateBounded<byte[]>(4);
        private readonly Channel<byte[]> _fromTransport = Channel.CreateBounded<byte[]>(4);
        private byte[]? _pending;
        private int _pendingOffset;
        private bool _open;
        private int _disposed;

        public bool Released => Volatile.Read(ref _disposed) != 0;

        public ValueTask OpenAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfDisposed();
            _open = true;
            return ValueTask.CompletedTask;
        }

        public ValueTask CloseAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _open = false;
            return ValueTask.CompletedTask;
        }

        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        {
            EnsureOpen();
            return _fromTransport.Writer.WriteAsync(data.ToArray(), cancellationToken);
        }

        public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            EnsureOpen();

            if (_pending is null || _pendingOffset >= _pending.Length)
            {
                _pending = await _toTransport.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                _pendingOffset = 0;
            }

            var count = Math.Min(buffer.Length, _pending.Length - _pendingOffset);
            _pending.AsMemory(_pendingOffset, count).CopyTo(buffer);
            _pendingOffset += count;
            if (_pendingOffset >= _pending.Length)
            {
                _pending = null;
                _pendingOffset = 0;
            }

            return count;
        }

        public ValueTask SendFromPeerAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken) =>
            _toTransport.Writer.WriteAsync(data.ToArray(), cancellationToken);

        public async ValueTask<ReadOnlyMemory<byte>> ReceiveFromTransportAsync(
            int expectedLength,
            CancellationToken cancellationToken)
        {
            var payload = await _fromTransport.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            Assert.Equal(expectedLength, payload.Length);
            return payload;
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return ValueTask.CompletedTask;

            _open = false;
            _toTransport.Writer.TryComplete();
            _fromTransport.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }

        private void EnsureOpen()
        {
            ThrowIfDisposed();
            if (!_open)
                throw new InvalidOperationException("Fake serial channel is not open.");
        }

        private void ThrowIfDisposed()
        {
            ObjectDisposedException.ThrowIf(Released, this);
        }
    }
}
