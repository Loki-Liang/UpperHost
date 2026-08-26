using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using UpperHost.Abstractions.Events;

namespace UpperHost.Events;

public sealed class EventBus : IEventBus, IAsyncDisposable
{
    private readonly ConcurrentDictionary<Guid, Subscription> _subscriptions = new();
    private readonly int _capacity;

    public EventBus(int capacity = 256)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
    }

    public async ValueTask PublishAsync<TEvent>(TEvent @event, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(@event);
        var eventType = @event.GetType();

        foreach (var subscription in _subscriptions.Values)
        {
            if (!subscription.EventType.IsAssignableFrom(eventType))
                continue;

            await subscription.Channel.Writer.WriteAsync(@event, cancellationToken).ConfigureAwait(false);
        }
    }

    public IAsyncEnumerable<TEvent> Subscribe<TEvent>(CancellationToken cancellationToken = default)
    {
        var id = Guid.NewGuid();
        var channel = Channel.CreateBounded<object>(new BoundedChannelOptions(_capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });

        if (!_subscriptions.TryAdd(id, new Subscription(typeof(TEvent), channel)))
            throw new InvalidOperationException("Unable to register event subscription.");

        return ReadAsync<TEvent>(id, channel, cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        foreach (var subscription in _subscriptions.Values)
            subscription.Channel.Writer.TryComplete();

        _subscriptions.Clear();
        return ValueTask.CompletedTask;
    }

    private async IAsyncEnumerable<TEvent> ReadAsync<TEvent>(
        Guid id,
        Channel<object> channel,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var item in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                if (item is TEvent typed)
                    yield return typed;
            }
        }
        finally
        {
            if (_subscriptions.TryRemove(id, out var subscription))
                subscription.Channel.Writer.TryComplete();
        }
    }

    private sealed record Subscription(Type EventType, Channel<object> Channel);
}
