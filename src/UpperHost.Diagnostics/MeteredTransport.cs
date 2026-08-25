using System.Runtime.CompilerServices;
using UpperHost.Abstractions.Transports;

namespace UpperHost.Diagnostics;

public sealed record TransportMetricsSnapshot(
    long BytesSent,
    long BytesReceived,
    long SendOperations,
    long ReceiveChunks,
    DateTimeOffset? LastSendAt,
    DateTimeOffset? LastReceiveAt);

public sealed class MeteredTransport : ITransport
{
    private readonly ITransport _inner;
    private long _bytesSent;
    private long _bytesReceived;
    private long _sendOperations;
    private long _receiveChunks;
    private long _lastSendTicks;
    private long _lastReceiveTicks;

    public MeteredTransport(ITransport inner) => _inner = inner;

    public TransportEndpoint Endpoint => _inner.Endpoint;
    public TransportState State => _inner.State;

    public Task OpenAsync(CancellationToken cancellationToken = default) => _inner.OpenAsync(cancellationToken);
    public Task CloseAsync(CancellationToken cancellationToken = default) => _inner.CloseAsync(cancellationToken);

    public async ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        await _inner.SendAsync(data, cancellationToken).ConfigureAwait(false);
        Interlocked.Add(ref _bytesSent, data.Length);
        Interlocked.Increment(ref _sendOperations);
        Interlocked.Exchange(ref _lastSendTicks, DateTimeOffset.UtcNow.UtcTicks);
    }

    public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var chunk in _inner.ReceiveAsync(cancellationToken).ConfigureAwait(false))
        {
            Interlocked.Add(ref _bytesReceived, chunk.Length);
            Interlocked.Increment(ref _receiveChunks);
            Interlocked.Exchange(ref _lastReceiveTicks, DateTimeOffset.UtcNow.UtcTicks);
            yield return chunk;
        }
    }

    public TransportMetricsSnapshot Snapshot()
    {
        var lastSend = Interlocked.Read(ref _lastSendTicks);
        var lastReceive = Interlocked.Read(ref _lastReceiveTicks);

        return new TransportMetricsSnapshot(
            Interlocked.Read(ref _bytesSent),
            Interlocked.Read(ref _bytesReceived),
            Interlocked.Read(ref _sendOperations),
            Interlocked.Read(ref _receiveChunks),
            lastSend == 0 ? null : new DateTimeOffset(lastSend, TimeSpan.Zero),
            lastReceive == 0 ? null : new DateTimeOffset(lastReceive, TimeSpan.Zero));
    }

    public ValueTask DisposeAsync() => _inner.DisposeAsync();
}
