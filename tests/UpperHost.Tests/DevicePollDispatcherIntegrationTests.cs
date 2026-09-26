using UpperHost.Abstractions.Devices;
using UpperHost.Control.Commands;
using UpperHost.Control.Scheduling;
using UpperHost.Control.State;

namespace UpperHost.Tests;

public sealed class DevicePollDispatcherIntegrationTests
{
    [Fact]
    public async Task Poll_read_waits_behind_conflicting_exclusive_command()
    {
        var resource = new CommandResourceKey("connection", "device-1");
        var target = new GateTarget();
        var commandRuntime = new CommandRuntime<TestCommand, string>(target);
        await using var dispatcher = new BoundedCommandDispatcher<TestCommand, string>(
            commandRuntime,
            new BoundedCommandDispatcherOptions(
                Capacity: 8,
                PerPriorityCapacity: 8,
                MaxConcurrency: 2,
                PerResourcePendingCapacity: 8,
                MaxSharedReadersPerResource: 2));
        await dispatcher.StartAsync();

        var exclusive = dispatcher.EnqueueAsync(
            new TestCommand("write", Block: true),
            new CommandDispatchOptions(Resources: [resource]));
        await target.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await using var store = new DeviceSnapshotStore<string>();
        var epoch = new MutableEpochSource { CurrentEpoch = 1 };
        var readStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var operation = DevicePollOperations.FromCommandDispatcher<TestCommand, string, string>(
            dispatcher,
            _ => new TestCommand("read"),
            result =>
            {
                readStarted.TrySetResult();
                return new DevicePollSample<string>(result.Value!);
            },
            [new CommandResourceClaim(resource, CommandResourceAccess.SharedRead)]);

        var group = new DevicePollGroup<string>(
            "status",
            "device-1",
            new DeviceStatePartitionKey("status"),
            TimeSpan.FromMinutes(1),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(10),
            operation);

        await using var poller = new DevicePollRuntime<string>(
            [group],
            store,
            epoch,
            new DevicePollRuntimeOptions(WorkCapacity: 4, MaxConcurrency: 1));

        await poller.StartAsync();
        await WaitUntilAsync(() => poller.IsRunning);

        Assert.False(readStarted.Task.IsCompleted);
        Assert.Equal(1, target.CallCount);

        target.ReleaseWrite.TrySetResult();

        Assert.True((await exclusive).IsSuccess);
        await readStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var snapshot = await WaitForSnapshotAsync(store);
        Assert.Equal("read", snapshot.Value);
        Assert.Equal(2, target.CallCount);
    }

    [Fact]
    public async Task Poll_queued_on_old_epoch_is_rejected_before_provider_call()
    {
        var resource = new CommandResourceKey("connection", "device-1");
        var epoch = new MutableEpochSource { CurrentEpoch = 1 };
        var validator = new EpochValidator(epoch);
        var target = new GateTarget();
        var commandRuntime = new CommandRuntime<TestCommand, string>(target);

        await using var dispatcher = new BoundedCommandDispatcher<TestCommand, string>(
            commandRuntime,
            new BoundedCommandDispatcherOptions(
                Capacity: 8,
                PerPriorityCapacity: 8,
                MaxConcurrency: 2,
                PerResourcePendingCapacity: 8,
                MaxSharedReadersPerResource: 2),
            validator);
        await dispatcher.StartAsync();

        var exclusive = dispatcher.EnqueueAsync(
            new TestCommand("write", Block: true),
            new CommandDispatchOptions(
                Resources: [resource],
                ConnectionEpoch: 1));
        await target.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await using var store = new DeviceSnapshotStore<string>();
        var pollCapturedOldEpoch = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var staleRejected = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var baseOperation = DevicePollOperations.FromCommandDispatcher<TestCommand, string, string>(
            dispatcher,
            context =>
            {
                pollCapturedOldEpoch.TrySetResult();
                return new TestCommand("read");
            },
            result => new DevicePollSample<string>(result.Value!),
            [new CommandResourceClaim(resource, CommandResourceAccess.SharedRead)]);

        DevicePollOperation<string> operation = async (context, cancellationToken) =>
        {
            try
            {
                return await baseOperation(context, cancellationToken);
            }
            catch (DevicePollDispatchException ex)
                when (ex.Code == "connection_epoch_stale")
            {
                staleRejected.TrySetResult();
                throw;
            }
        };

        var group = new DevicePollGroup<string>(
            "status",
            "device-1",
            new DeviceStatePartitionKey("status"),
            TimeSpan.FromMinutes(1),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(10),
            operation);

        await using var poller = new DevicePollRuntime<string>(
            [group],
            store,
            epoch,
            new DevicePollRuntimeOptions(WorkCapacity: 4, MaxConcurrency: 1));

        await poller.StartAsync();
        await pollCapturedOldEpoch.Task.WaitAsync(TimeSpan.FromSeconds(2));

        epoch.CurrentEpoch = 2;
        target.ReleaseWrite.TrySetResult();

        Assert.True((await exclusive).IsSuccess);
        await staleRejected.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1, target.CallCount);
        Assert.Null(store.Get("device-1", new DeviceStatePartitionKey("status")));
    }

    private static async Task<DeviceSnapshot<string>> WaitForSnapshotAsync(
        DeviceSnapshotStore<string> store)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (true)
        {
            var snapshot = store.Get("device-1", new DeviceStatePartitionKey("status"));
            if (snapshot is not null)
                return snapshot;

            await Task.Delay(1, timeout.Token);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!predicate())
            await Task.Delay(1, timeout.Token);
    }

    private sealed record TestCommand(string Name, bool Block = false);

    private sealed class GateTarget : ICommandable<TestCommand, string>
    {
        private int _callCount;
        public int CallCount => Volatile.Read(ref _callCount);
        public TaskCompletionSource WriteStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseWrite { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<string> ExecuteAsync(
            TestCommand command,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _callCount);

            if (command.Block)
            {
                WriteStarted.TrySetResult();
                await ReleaseWrite.Task.WaitAsync(cancellationToken);
            }

            return command.Name;
        }
    }

    private sealed class MutableEpochSource : IDeviceConnectionEpochSource
    {
        public long CurrentEpoch { get; set; }
        public long GetCurrentEpoch(string deviceId) => CurrentEpoch;
    }

    private sealed class EpochValidator(MutableEpochSource source)
        : ICommandConnectionEpochValidator
    {
        public bool IsCurrent(
            long connectionEpoch,
            IReadOnlyList<CommandResourceKey>? resources) =>
            connectionEpoch == source.CurrentEpoch;
    }
}
