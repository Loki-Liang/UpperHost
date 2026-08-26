using UpperHost.Events;

namespace UpperHost.Tests;

public sealed class EventBusTests
{
    [Fact]
    public async Task Event_bus_delivers_typed_events()
    {
        await using var bus = new EventBus();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await using var subscription = bus.Subscribe<string>(cts.Token).GetAsyncEnumerator(cts.Token);

        await bus.PublishAsync("ready", cts.Token);

        Assert.True(await subscription.MoveNextAsync());
        Assert.Equal("ready", subscription.Current);
    }
}
