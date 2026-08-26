using System.Net.Sockets;
using System.Runtime.CompilerServices;
using UpperHost.Abstractions.Transports;

namespace UpperHost.Transport.Tcp;

public sealed record TcpTransportOptions(string Host, int Port, int ReadBufferSize = 16 * 1024);

public sealed class TcpTransport : ITransport
{
    private readonly TcpTransportOptions _options;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private TcpClient? _client;
    private NetworkStream? _stream;

    public TcpTransport(TcpTransportOptions options)
    {
        _options = options;
        Endpoint = new TransportEndpoint("tcp", $"{options.Host}:{options.Port}");
    }

    public TransportEndpoint Endpoint { get; }
    public TransportState State { get; private set; } = TransportState.Closed;

    public async Task OpenAsync(CancellationToken cancellationToken = default)
    {
        if (State == TransportState.Open) return;
        State = TransportState.Opening;
        try
        {
            _client = new TcpClient();
            await _client.ConnectAsync(_options.Host, _options.Port, cancellationToken).ConfigureAwait(false);
            _stream = _client.GetStream();
            State = TransportState.Open;
        }
        catch
        {
            State = TransportState.Faulted;
            _stream?.Dispose();
            _client?.Dispose();
            _stream = null;
            _client = null;
            throw;
        }
    }

    public Task CloseAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _stream?.Dispose();
        _client?.Dispose();
        _stream = null;
        _client = null;
        State = TransportState.Closed;
        return Task.CompletedTask;
    }

    public async ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        var stream = GetOpenStream();
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var stream = GetOpenStream();
        var buffer = new byte[_options.ReadBufferSize];
        while (!cancellationToken.IsCancellationRequested)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) yield break;
            yield return buffer.AsMemory(0, read).ToArray();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync().ConfigureAwait(false);
        _writeGate.Dispose();
    }

    private NetworkStream GetOpenStream() =>
        State == TransportState.Open && _stream is not null
            ? _stream
            : throw new InvalidOperationException("TCP transport is not open.");
}
