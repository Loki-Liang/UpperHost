using System.Runtime.CompilerServices;
using UpperHost.Abstractions.Transports;

namespace UpperHost.Testing;

public sealed record FaultInjectionOptions(
    TimeSpan? SendLatency = null,
    TimeSpan? ReceiveLatency = null,
    int FailEverySend = 0,
    int DropEveryReceive = 0);

public sealed class FaultInjectingTransport : ITransport
{
    private readonly ITransport _inner;
    private readonly FaultInjectionOptions _options;
    private long _sendCount;
    private long _receiveCount;

    public FaultInjectingTransport(ITransport inner, FaultInjectionOptions? options = null)
    {
        _inner = inner;
        _options = options ?? new FaultInjectionOptions();
    }

    public TransportEndpoint Endpoint => _inner.Endpoint;
    public TransportState State => _inner.State;

    public Task OpenAsync(CancellationToken cancellationToken = default) => _inner.OpenAsync(cancellationToken);
    public Task CloseAsync(CancellationToken cancellationToken = default) => _inner.CloseAsync(cancellationToken);

    public async ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        var count = Interlocked.Increment(ref _sendCount);
        if (_options.SendLatency is { } latency)
            await Task.Delay(latency, cancellationToken).ConfigureAwait(false);

        if (_options.FailEverySend > 0 && count % _options.FailEverySend == 0)
            throw new IOException($"Injected send failure at operation {count}.");

        await _inner.SendAsync(data, cancellationToken).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var chunk in _inner.ReceiveAsync(cancellationToken).ConfigureAwait(false))
        {
            var count = Interlocked.Increment(ref _receiveCount);
            if (_options.ReceiveLatency is { } latency)
                await Task.Delay(latency, cancellationToken).ConfigureAwait(false);

            if (_options.DropEveryReceive > 0 && count % _options.DropEveryReceive == 0)
                continue;

            yield return chunk;
        }
    }

    public ValueTask DisposeAsync() => _inner.DisposeAsync();
}
