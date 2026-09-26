using Microsoft.Extensions.Time.Testing;
using OpenDeviceStudio.Abstractions.Devices;
using OpenDeviceStudio.Control.Commands;
using OpenDeviceStudio.Control.Scheduling;

namespace OpenDeviceStudio.Tests;

public sealed class CommandQueueDeadlineTests
{
    [Fact]
    public async Task Queued_command_expires_before_device_dispatch()
    {
        var time = new FakeTimeProvider();
        var target = new GateTarget();
        var runtime = new CommandRuntime<TestCommand, string>(target);
        await using var dispatcher = new BoundedCommandDispatcher<TestCommand, string>(
            runtime,
            new BoundedCommandDispatcherOptions(Capacity: 8, PerPriorityCapacity: 8, MaxConcurrency: 1),
            timeProvider: time);

        await dispatcher.StartAsync();

        var first = dispatcher.EnqueueAsync(new TestCommand("first", Block: true));
        await target.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var expiring = dispatcher.EnqueueAsync(
            new TestCommand("expired"),
            new CommandDispatchOptions(QueueTimeout: TimeSpan.FromSeconds(5)));

        await WaitUntilAsync(() => dispatcher.PendingCount == 1);
        time.Advance(TimeSpan.FromSeconds(6));
        target.Release.TrySetResult();

        Assert.True((await first).IsSuccess);
        var result = await expiring.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(CommandExecutionStatus.TimedOut, result.Status);
        Assert.Equal("command_expired_before_dispatch", result.Code);
        Assert.Equal(1, target.CallCount);
    }

    [Fact]
    public async Task Queue_timeout_can_expire_while_waiting_for_global_capacity()
    {
        var time = new FakeTimeProvider();
        var target = new GateTarget();
        var runtime = new CommandRuntime<TestCommand, string>(target);
        await using var dispatcher = new BoundedCommandDispatcher<TestCommand, string>(
            runtime,
            new BoundedCommandDispatcherOptions(Capacity: 1, PerPriorityCapacity: 2, MaxConcurrency: 1),
            timeProvider: time);

        await dispatcher.StartAsync();

        var first = dispatcher.EnqueueAsync(new TestCommand("first", Block: true));
        await target.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var queued = dispatcher.EnqueueAsync(new TestCommand("queued"));
        await WaitUntilAsync(() => dispatcher.PendingCount == 1);

        var waitingAdmission = dispatcher.EnqueueAsync(
            new TestCommand("waiting"),
            new CommandDispatchOptions(QueueTimeout: TimeSpan.FromSeconds(3)));

        time.Advance(TimeSpan.FromSeconds(4));
        var result = await waitingAdmission.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(CommandExecutionStatus.TimedOut, result.Status);
        Assert.Equal("command_expired_before_dispatch", result.Code);

        target.Release.TrySetResult();
        await Task.WhenAll(first, queued);
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
        public TaskCompletionSource FirstStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<string> ExecuteAsync(TestCommand command, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _callCount);
            if (command.Block)
            {
                FirstStarted.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }

            return command.Name;
        }
    }
}
