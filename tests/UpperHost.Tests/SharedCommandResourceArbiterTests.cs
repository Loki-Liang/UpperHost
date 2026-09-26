using Microsoft.Extensions.DependencyInjection;
using UpperHost.Abstractions.Devices;
using UpperHost.Control.Commands;
using UpperHost.Control.Scheduling;

namespace UpperHost.Tests;

public sealed class SharedCommandResourceArbiterTests
{
    [Fact]
    public async Task Host_registered_dispatcher_uses_the_shared_resource_arbiter()
    {
        var services = new ServiceCollection();
        var target = new CountingTarget();
        services.AddSingleton<ICommandable<TestCommand, string>>(target);
        services.AddUpperHostCommandDispatcher<TestCommand, string>(
            new BoundedCommandDispatcherOptions(
                Capacity: 8,
                PerPriorityCapacity: 8,
                MaxConcurrency: 2,
                PerResourcePendingCapacity: 8,
                MaxSharedReadersPerResource: 2));

        await using var provider = services.BuildServiceProvider();
        var arbiter = provider.GetRequiredService<ICommandResourceArbiter>();
        var dispatcher = provider.GetRequiredService<BoundedCommandDispatcher<TestCommand, string>>();
        await dispatcher.StartAsync();

        var resource = new CommandResourceKey("device", "shared");
        await using var exclusive = await arbiter.AcquireAsync(
            [new CommandResourceClaim(resource, CommandResourceAccess.Exclusive)]);

        var pending = dispatcher.EnqueueAsync(
            new TestCommand("read"),
            new CommandDispatchOptions(
                ResourceClaims:
                [
                    new CommandResourceClaim(resource, CommandResourceAccess.SharedRead)
                ],
                Safety: new CommandSafetyMetadata(
                    ReadOnly: true,
                    Idempotent: true,
                    Motion: false,
                    Hazardous: false,
                    RetryAllowed: true)));

        await Task.Yield();
        Assert.Equal(0, target.CallCount);

        await exclusive.DisposeAsync();

        var result = await pending.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(result.IsSuccess);
        Assert.Equal(1, target.CallCount);
    }

    [Fact]
    public void Conflicting_shared_reader_limits_fail_fast_at_registration()
    {
        var services = new ServiceCollection();
        services.AddUpperHostCommandDispatcher<TestCommand, string>(
            new BoundedCommandDispatcherOptions(MaxSharedReadersPerResource: 2));

        var exception = Assert.Throws<InvalidOperationException>(() =>
            services.AddUpperHostCommandDispatcher<OtherCommand, string>(
                new BoundedCommandDispatcherOptions(MaxSharedReadersPerResource: 4)));

        Assert.Contains("same MaxSharedReadersPerResource", exception.Message);
    }

    private sealed record TestCommand(string Value);
    private sealed record OtherCommand(string Value);

    private sealed class CountingTarget : ICommandable<TestCommand, string>
    {
        private int _calls;
        public int CallCount => Volatile.Read(ref _calls);

        public Task<string> ExecuteAsync(
            TestCommand command,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _calls);
            return Task.FromResult(command.Value);
        }
    }
}
