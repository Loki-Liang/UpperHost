using Microsoft.Extensions.DependencyInjection;
using OpenDeviceStudio.Control.Scheduling;
using OpenDeviceStudio.Control.State;
using OpenDeviceStudio.Hosting;
using OpenDeviceStudio.Starters;

namespace OpenDeviceStudio.Tests;

public sealed class DeviceStateHostingTests
{
    [Fact]
    public async Task Starter_registers_single_epoch_authority_and_host_owned_poller()
    {
        var polled = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var builder = OpenDeviceStudioApplication.CreateBuilder().AddOpenDeviceStudio();
        builder.AddDeviceState<int>();
        builder.AddDevicePolling<int>(
            _ =>
            [
                new DevicePollGroup<int>(
                    "state",
                    "device-1",
                    new DeviceStatePartitionKey("state"),
                    TimeSpan.FromMinutes(1),
                    TimeSpan.FromSeconds(2),
                    TimeSpan.FromMinutes(2),
                    (context, cancellationToken) =>
                    {
                        polled.TrySetResult();
                        return ValueTask.FromResult(new DevicePollSample<int>(42));
                    })
            ],
            new DevicePollRuntimeOptions(WorkCapacity: 2, MaxConcurrency: 1));

        await using var app = builder.Build();
        var registry = app.Services.GetRequiredService<DeviceControlStateRegistry>();
        var epochSource = app.Services.GetRequiredService<IDeviceConnectionEpochSource>();
        var epochValidator = app.Services.GetRequiredService<ICommandConnectionEpochValidator>();
        var store = app.Services.GetRequiredService<DeviceSnapshotStore<int>>();
        var poller = app.Services.GetRequiredService<DevicePollRuntime<int>>();

        Assert.Same(registry, epochSource);
        Assert.Same(registry, epochValidator);
        Assert.False(poller.IsRunning);

        registry.BeginRehydrate("device-1");
        await app.StartAsync();

        await polled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var partition = new DeviceStatePartitionKey("state");
        DeviceSnapshot<int>? snapshot = null;
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
        {
            while ((snapshot = store.Get("device-1", partition)) is null)
                await Task.Delay(10, timeout.Token);
        }

        Assert.True(poller.IsRunning);
        Assert.Equal(42, snapshot.Value);

        await app.StopAsync();
        Assert.False(poller.IsRunning);
    }

    [Fact]
    public void Resource_arbiter_can_be_registered_without_a_command_dispatcher()
    {
        var services = new ServiceCollection();

        services.AddOpenDeviceStudioControlResourceArbiter(maxSharedReadersPerResource: 3);

        using var provider = services.BuildServiceProvider();
        Assert.NotNull(provider.GetService<ICommandResourceArbiter>());
    }
}
