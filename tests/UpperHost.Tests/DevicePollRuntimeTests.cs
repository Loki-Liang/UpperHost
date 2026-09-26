using Microsoft.Extensions.Time.Testing;
using UpperHost.Control.State;

namespace UpperHost.Tests;

public sealed class DevicePollRuntimeTests
{
    [Fact]
    public async Task Polling_uses_fake_time_and_updates_the_authoritative_snapshot_store()
    {
        var time = new FakeTimeProvider();
        await using var store = new DeviceSnapshotStore<int>(time);
        var epoch = new FixedEpochSource(3);
        var calls = 0;
        var secondCall = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var group = new DevicePollGroup<int>(
            "temperature",
            "device-1",
            new DeviceStatePartitionKey("temperature"),
            Interval: TimeSpan.FromSeconds(5),
            Timeout: TimeSpan.FromSeconds(2),
            StaleAfter: TimeSpan.FromSeconds(10),
            Operation: (context, cancellationToken) =>
            {
                var call = Interlocked.Increment(ref calls);
                if (call >= 2)
                    secondCall.TrySetResult();
                return ValueTask.FromResult(new DevicePollSample<int>(call));
            });

        await using var runtime = new DevicePollRuntime<int>(
            [group],
            store,
            epoch,
            new DevicePollRuntimeOptions(WorkCapacity: 4, MaxConcurrency: 1),
            time);

        await runtime.StartAsync();
        await WaitUntilAsync(() => Volatile.Read(ref calls) == 1);

        var first = store.Get("device-1", new DeviceStatePartitionKey("temperature"));
        Assert.NotNull(first);
        Assert.Equal(1, first.Value);
        Assert.Equal(3, first.ConnectionEpoch);

        time.Advance(TimeSpan.FromSeconds(5));
        await secondCall.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var second = store.Get("device-1", new DeviceStatePartitionKey("temperature"));
        Assert.NotNull(second);
        Assert.Equal(2, second.Value);
        Assert.True(second.SnapshotVersion > first.SnapshotVersion);
    }

    [Fact]
    public async Task Slow_poll_coalesces_missed_ticks_into_one_follow_up()
    {
        var time = new FakeTimeProvider();
        await using var store = new DeviceSnapshotStore<int>(time);
        var epoch = new FixedEpochSource(1);
        var calls = 0;
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var group = new DevicePollGroup<int>(
            "slow",
            "device-1",
            new DeviceStatePartitionKey("slow"),
            Interval: TimeSpan.FromSeconds(1),
            Timeout: TimeSpan.FromSeconds(30),
            StaleAfter: TimeSpan.FromSeconds(5),
            Operation: async (context, cancellationToken) =>
            {
                var call = Interlocked.Increment(ref calls);
                if (call == 1)
                {
                    firstStarted.TrySetResult();
                    await releaseFirst.Task.WaitAsync(cancellationToken);
                }

                if (call == 2)
                    secondCompleted.TrySetResult();

                return new DevicePollSample<int>(call);
            },
            MissedTickPolicy: PollMissedTickPolicy.Coalesce);

        await using var runtime = new DevicePollRuntime<int>(
            [group], store, epoch,
            new DevicePollRuntimeOptions(WorkCapacity: 4, MaxConcurrency: 1),
            time);

        await runtime.StartAsync();
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        time.Advance(TimeSpan.FromSeconds(5));
        releaseFirst.TrySetResult();

        await secondCompleted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Yield();

        Assert.Equal(2, Volatile.Read(ref calls));
    }

    [Fact]
    public async Task Slow_device_does_not_block_an_independent_poll_group()
    {
        var time = new FakeTimeProvider();
        await using var store = new DeviceSnapshotStore<int>(time);
        var epoch = new FixedEpochSource(1);
        var slowStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSlow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fastCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var slow = new DevicePollGroup<int>(
            "slow",
            "device-a",
            new DeviceStatePartitionKey("state"),
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(20),
            async (context, cancellationToken) =>
            {
                slowStarted.TrySetResult();
                await releaseSlow.Task.WaitAsync(cancellationToken);
                return new DevicePollSample<int>(1);
            });

        var fast = new DevicePollGroup<int>(
            "fast",
            "device-b",
            new DeviceStatePartitionKey("state"),
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(20),
            (context, cancellationToken) =>
            {
                fastCompleted.TrySetResult();
                return ValueTask.FromResult(new DevicePollSample<int>(2));
            });

        await using var runtime = new DevicePollRuntime<int>(
            [slow, fast], store, epoch,
            new DevicePollRuntimeOptions(WorkCapacity: 4, MaxConcurrency: 2),
            time);

        await runtime.StartAsync();

        await slowStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await fastCompleted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(2, store.Get("device-b", new DeviceStatePartitionKey("state"))!.Value);

        releaseSlow.TrySetResult();
    }

    [Fact]
    public async Task Skip_policy_does_not_enqueue_a_historical_backlog()
    {
        var time = new FakeTimeProvider();
        await using var store = new DeviceSnapshotStore<int>(time);
        var epoch = new FixedEpochSource(1);
        var calls = 0;
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var group = new DevicePollGroup<int>(
            "skip",
            "device-1",
            new DeviceStatePartitionKey("state"),
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(10),
            async (context, cancellationToken) =>
            {
                var call = Interlocked.Increment(ref calls);
                if (call == 1)
                {
                    firstStarted.TrySetResult();
                    await releaseFirst.Task.WaitAsync(cancellationToken);
                }

                return new DevicePollSample<int>(call);
            },
            MissedTickPolicy: PollMissedTickPolicy.Skip);

        await using var runtime = new DevicePollRuntime<int>(
            [group], store, epoch,
            new DevicePollRuntimeOptions(WorkCapacity: 2, MaxConcurrency: 1),
            time);

        await runtime.StartAsync();
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        time.Advance(TimeSpan.FromSeconds(10));
        releaseFirst.TrySetResult();
        await WaitUntilAsync(() => store.Get("device-1", new DeviceStatePartitionKey("state")) is not null);

        Assert.Equal(1, Volatile.Read(ref calls));
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!predicate())
            await Task.Delay(1, timeout.Token);
    }

    private sealed class FixedEpochSource(long epoch) : IDeviceConnectionEpochSource
    {
        public long GetCurrentEpoch(string deviceId) => epoch;
    }
}
