using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace UpperHost.Dataflow;

public enum FanOutBackpressureMode
{
    Wait,
    DropOldest,
    DropNewest
}

public sealed record FanOutOptions(int Capacity = 256, FanOutBackpressureMode BackpressureMode = FanOutBackpressureMode.DropOldest);

public sealed class FanOutHub<T> : IAsyncDisposable
{
    private readonly ConcurrentDictionary<Guid, Channel<T>> _subscribers = new();
    private readonly FanOutOptions _options;

    public FanOutHub(FanOutOptions? options = null) => _options = options ?? new FanOutOptions();

    public int SubscriberCount => _subscribers.Count;

    public IAsyncEnumerable<T> Subscribe(CancellationToken cancellationToken = default)
    {
        var id = Guid.NewGuid();
        var channel = Channel.CreateBounded<T>(new BoundedChannelOptions(_options.Capacity)
        {
            FullMode = _options.BackpressureMode switch
            {
                FanOutBackpressureMode.Wait => BoundedChannelFullMode.Wait,
                FanOutBackpressureMode.DropNewest => BoundedChannelFullMode.DropNewest,
                _ => BoundedChannelFullMode.DropOldest
            },
            SingleReader = true,
            SingleWriter = false
        });

        if (!_subscribers.TryAdd(id, channel))
            throw new InvalidOperationException("Unable to register dataflow subscriber.");

        return ReadSubscriberAsync(id, channel, cancellationToken);
    }

    public async ValueTask PublishAsync(T item, CancellationToken cancellationToken = default)
    {
        foreach (var channel in _subscribers.Values)
        {
            if (_options.BackpressureMode == FanOutBackpressureMode.Wait)
                await channel.Writer.WriteAsync(item, cancellationToken).ConfigureAwait(false);
            else
                channel.Writer.TryWrite(item);
        }
    }

    public ValueTask DisposeAsync()
    {
        foreach (var channel in _subscribers.Values)
            channel.Writer.TryComplete();
        _subscribers.Clear();
        return ValueTask.CompletedTask;
    }

    private async IAsyncEnumerable<T> ReadSubscriberAsync(
        Guid id,
        Channel<T> channel,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var item in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                yield return item;
        }
        finally
        {
            if (_subscribers.TryRemove(id, out var removed))
                removed.Writer.TryComplete();
        }
    }
}
