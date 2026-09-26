using Microsoft.Extensions.DependencyInjection;
using UpperHost.Abstractions.Devices;
using UpperHost.Control.Scheduling;
using UpperHost.Hosting;
using UpperHost.Starters;

namespace UpperHost.Tests;

public sealed class CommandDispatcherHostingTests
{
    [Fact]
    public async Task Starter_registration_owns_dispatcher_with_host_lifecycle()
    {
        var builder = UpperHostApplication.CreateBuilder().AddUpperHost();
        builder.Services.AddSingleton<ICommandable<TestCommand, string>, TestTarget>();
        builder.AddCommandDispatcher<TestCommand, string>(
            new BoundedCommandDispatcherOptions(Capacity: 8, PerPriorityCapacity: 8, MaxConcurrency: 2));

        await using var app = builder.Build();
        var dispatcher = app.Services.GetRequiredService<BoundedCommandDispatcher<TestCommand, string>>();

        Assert.False(dispatcher.IsRunning);

        await app.StartAsync();
        Assert.True(dispatcher.IsRunning);

        var result = await dispatcher.EnqueueAsync(new TestCommand("run"));
        Assert.True(result.IsSuccess);

        await app.StopAsync();
        Assert.False(dispatcher.IsRunning);
    }

    [Fact]
    public void Registration_is_idempotent_for_the_same_command_contract()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ICommandable<TestCommand, string>, TestTarget>();

        services.AddUpperHostCommandDispatcher<TestCommand, string>();
        services.AddUpperHostCommandDispatcher<TestCommand, string>();

        using var provider = services.BuildServiceProvider();
        var dispatchers = provider.GetServices<BoundedCommandDispatcher<TestCommand, string>>().ToArray();

        Assert.Single(dispatchers);
    }

    private sealed record TestCommand(string Name);

    private sealed class TestTarget : ICommandable<TestCommand, string>
    {
        public Task<string> ExecuteAsync(
            TestCommand command,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(command.Name);
        }
    }
}
