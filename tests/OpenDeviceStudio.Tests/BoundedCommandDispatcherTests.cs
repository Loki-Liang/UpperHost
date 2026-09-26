using OpenDeviceStudio.Abstractions.Devices;
using OpenDeviceStudio.Control.Commands;
using OpenDeviceStudio.Control.Scheduling;

namespace OpenDeviceStudio.Tests;

public sealed class BoundedCommandDispatcherTests
{
    [Fact]
    public async Task Reject_mode_never_exceeds_bounded_pending_capacity()
    {
        var target = new GateTarget();
        var runtime = new CommandRuntime<TestCommand, string>(target);
        await using var dispatcher = new BoundedCommandDispatcher<TestCommand, string>(
            runtime,
            new BoundedCommandDispatcherOptions(Capacity: 1, PerPriorityCapacity: 1, MaxConcurrency: 1));

        await dispatcher.StartAsync();

        var first = dispatcher.EnqueueAsync(new TestCommand("first", Block: true));
        await target.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var second = dispatcher.EnqueueAsync(
            new TestCommand("second"),
            new CommandDispatchOptions(AdmissionMode: CommandAdmissionMode.Reject));

        await WaitUntilAsync(() => dispatcher.PendingCount == 1);

        var rejected = await dispatcher.EnqueueAsync(
            new TestCommand("third"),
            new CommandDispatchOptions(AdmissionMode: CommandAdmissionMode.Reject));

        Assert.Equal(CommandExecutionStatus.Rejected, rejected.Status);
        Assert.Equal("dispatcher_capacity", rejected.Code);
        Assert.Equal(1, dispatcher.PendingCount);

        target.Release.TrySetResult();
        await Task.WhenAll(first, second);
    }

    [Fact]
    public async Task Weighted_scheduler_does_not_starve_low_priority_lane()
    {
        var target = new OrderingTarget();
        var runtime = new CommandRuntime<TestCommand, string>(target);
        await using var dispatcher = new BoundedCommandDispatcher<TestCommand, string>(
            runtime,
            new BoundedCommandDispatcherOptions(Capacity: 64, PerPriorityCapacity: 64, MaxConcurrency: 1));

        await dispatcher.StartAsync();

        var first = dispatcher.EnqueueAsync(new TestCommand("first", Block: true));
        await target.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var low = dispatcher.EnqueueAsync(
            new TestCommand("low"),
            new CommandDispatchOptions(Priority: CommandPriority.Low));

        var critical = Enumerable.Range(0, 16)
            .Select(i => dispatcher.EnqueueAsync(
                new TestCommand($"critical-{i}"),
                new CommandDispatchOptions(Priority: CommandPriority.Critical)))
            .ToArray();

        target.Release.TrySetResult();
        await Task.WhenAll([first, low, .. critical]);

        var order = target.ExecutionOrder;
        var lowIndex = Array.IndexOf(order.ToArray(), "low");
        Assert.True(lowIndex >= 0);
        Assert.True(lowIndex < order.Count - 1);
    }

    [Fact]
    public async Task Same_resource_is_serialized()
    {
        var target = new ConcurrencyGateTarget(expectedConcurrentStarts: 1);
        var runtime = new CommandRuntime<TestCommand, string>(target);
        await using var dispatcher = new BoundedCommandDispatcher<TestCommand, string>(
            runtime,
            new BoundedCommandDispatcherOptions(Capacity: 8, PerPriorityCapacity: 8, MaxConcurrency: 4));

        await dispatcher.StartAsync();

        var resource = new CommandResourceKey("connection", "shared");
        var first = dispatcher.EnqueueAsync(
            new TestCommand("a"),
            new CommandDispatchOptions(Resources: [resource]));
        var second = dispatcher.EnqueueAsync(
            new TestCommand("b"),
            new CommandDispatchOptions(Resources: [resource]));

        await target.AtLeastOneStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, target.MaximumConcurrency);

        target.Release.TrySetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(1, target.MaximumConcurrency);
    }

    [Fact]
    public async Task Independent_resources_can_run_concurrently()
    {
        var target = new ConcurrencyGateTarget(expectedConcurrentStarts: 2);
        var runtime = new CommandRuntime<TestCommand, string>(target);
        await using var dispatcher = new BoundedCommandDispatcher<TestCommand, string>(
            runtime,
            new BoundedCommandDispatcherOptions(Capacity: 8, PerPriorityCapacity: 8, MaxConcurrency: 4));

        await dispatcher.StartAsync();

        var first = dispatcher.EnqueueAsync(
            new TestCommand("a"),
            new CommandDispatchOptions(Resources: [new CommandResourceKey("connection", "a")]));
        var second = dispatcher.EnqueueAsync(
            new TestCommand("b"),
            new CommandDispatchOptions(Resources: [new CommandResourceKey("connection", "b")]));

        await target.ExpectedConcurrencyReached.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(2, target.MaximumConcurrency);

        target.Release.TrySetResult();
        await Task.WhenAll(first, second);
    }

    [Fact]
    public async Task Construction_has_no_background_execution_and_stop_drains_pending_commands()
    {
        var target = new OrderingTarget();
        var runtime = new CommandRuntime<TestCommand, string>(target);
        await using var dispatcher = new BoundedCommandDispatcher<TestCommand, string>(runtime);

        var beforeStart = await dispatcher.EnqueueAsync(new TestCommand("before-start"));
        Assert.Equal(CommandExecutionStatus.Rejected, beforeStart.Status);
        Assert.Empty(target.ExecutionOrder);

        await dispatcher.StartAsync();
        var afterStart = dispatcher.EnqueueAsync(new TestCommand("after-start"));
        await dispatcher.StopAsync();

        var result = await afterStart;
        Assert.True(result.IsSuccess);
        Assert.Equal(["after-start"], target.ExecutionOrder);
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
        public TaskCompletionSource FirstStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<string> ExecuteAsync(
            TestCommand command,
            CancellationToken cancellationToken = default)
        {
            if (command.Block)
            {
                FirstStarted.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }

            return command.Name;
        }
    }

    private sealed class OrderingTarget : ICommandable<TestCommand, string>
    {
        private readonly List<string> _executionOrder = [];
        private readonly object _gate = new();

        public TaskCompletionSource FirstStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<string> ExecutionOrder
        {
            get
            {
                lock (_gate)
                    return _executionOrder.ToArray();
            }
        }

        public async Task<string> ExecuteAsync(
            TestCommand command,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
                _executionOrder.Add(command.Name);

            if (command.Block)
            {
                FirstStarted.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }

            return command.Name;
        }
    }

    private sealed class ConcurrencyGateTarget(int expectedConcurrentStarts)
        : ICommandable<TestCommand, string>
    {
        private int _active;
        private int _maximum;

        public TaskCompletionSource AtLeastOneStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ExpectedConcurrencyReached { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int MaximumConcurrency => Volatile.Read(ref _maximum);

        public async Task<string> ExecuteAsync(
            TestCommand command,
            CancellationToken cancellationToken = default)
        {
            var current = Interlocked.Increment(ref _active);
            UpdateMaximum(current);
            AtLeastOneStarted.TrySetResult();
            if (current >= expectedConcurrentStarts)
                ExpectedConcurrencyReached.TrySetResult();

            try
            {
                await Release.Task.WaitAsync(cancellationToken);
                return command.Name;
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }

        private void UpdateMaximum(int current)
        {
            while (true)
            {
                var observed = Volatile.Read(ref _maximum);
                if (current <= observed ||
                    Interlocked.CompareExchange(ref _maximum, current, observed) == observed)
                    return;
            }
        }
    }
}
