using UpperHost.Control.State;

namespace UpperHost.Tests;

public sealed class ObservationEpochRaceTests
{
    [Fact]
    public async Task Poll_started_on_old_epoch_cannot_create_first_snapshot_after_reconnect()
    {
        await using var store = new DeviceSnapshotStore<int>();
        var epoch = new MutableEpochSource { CurrentEpoch = 1 };
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var group = new DevicePollGroup<int>(
            "state",
            "device-1",
            new DeviceStatePartitionKey("state"),
            TimeSpan.FromMinutes(1),
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(10),
            async (context, cancellationToken) =>
            {
                started.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
                return new DevicePollSample<int>(42);
            });

        await using var poller = new DevicePollRuntime<int>(
            [group],
            store,
            epoch,
            new DevicePollRuntimeOptions(WorkCapacity: 2, MaxConcurrency: 1));

        await poller.StartAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        epoch.CurrentEpoch = 2;
        release.TrySetResult();

        await WaitUntilAsync(() => poller.IsRunning);
        Assert.Null(store.Get("device-1", new DeviceStatePartitionKey("state")));
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!predicate())
            await Task.Delay(1, timeout.Token);
    }

    private sealed class MutableEpochSource : IDeviceConnectionEpochSource
    {
        public long CurrentEpoch { get; set; }
        public long GetCurrentEpoch(string deviceId) => CurrentEpoch;
    }
}
