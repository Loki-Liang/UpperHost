namespace UpperHost.Abstractions.Events;

public interface IEventBus
{
    ValueTask PublishAsync<TEvent>(TEvent @event, CancellationToken cancellationToken = default);
    IAsyncEnumerable<TEvent> Subscribe<TEvent>(CancellationToken cancellationToken = default);
}
