using UpperHost.Abstractions.Devices;
using UpperHost.Control.Commands;
using UpperHost.Control.Scheduling;

namespace UpperHost.Tests;

public sealed class CommandResourceArbitrationTests
{
    [Fact]
    public async Task Per_resource_pending_capacity_rejects_hot_resource_without_exhausting_global_capacity()
    {
        var target = new NamedGateTarget();
        var runtime = new CommandRuntime<TestCommand, string>(target);
        await using var dispatcher = new BoundedCommandDispatcher<TestCommand, string>(
            runtime,
            new BoundedCommandDispatcherOptions(
                Capacity: 8,
                PerPriorityCapacity: 8,
                MaxConcurrency: 1,
                PerResourcePendingCapacity: 1,
                MaxSharedReadersPerResource: 4));

        await dispatcher.StartAsync();
        var resource = new CommandResourceKey("device", "hot");

        var first = dispatcher.EnqueueAsync(
            new TestCommand("first"),
            new CommandDispatchOptions(Resources: [resource]));
        await target.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var second = dispatcher.EnqueueAsync(
            new TestCommand("second"),
            new CommandDispatchOptions(Resources: [resource]));
        await WaitUntilAsync(() => dispatcher.PendingCount == 1);

        var rejected = await dispatcher.EnqueueAsync(
            new TestCommand("third"),
            new CommandDispatchOptions(
                Resources: [resource],
                AdmissionMode: CommandAdmissionMode.Reject));

        Assert.Equal(CommandExecutionStatus.Rejected, rejected.Status);
        Assert.Equal("resource_pending_capacity", rejected.Code);
        Assert.True(dispatcher.PendingCount < dispatcher.Capacity);

        target.Release.TrySetResult();
        await Task.WhenAll(first, second);
    }

    [Fact]
    public async Task Shared_reads_on_same_resource_can_run_concurrently()
    {
        var target = new ConcurrentGateTarget(expectedStarts: 2);
        var runtime = new CommandRuntime<TestCommand, string>(target);
        await using var dispatcher = new BoundedCommandDispatcher<TestCommand, string>(
            runtime,
            new BoundedCommandDispatcherOptions(
                Capacity: 8,
                PerPriorityCapacity: 8,
                MaxConcurrency: 4,
                PerResourcePendingCapacity: 8,
                MaxSharedReadersPerResource: 2));

        await dispatcher.StartAsync();
        var resource = new CommandResourceKey("connection", "shared");
        var claim = new CommandResourceClaim(resource, CommandResourceAccess.SharedRead);

        var first = dispatcher.EnqueueAsync(
            new TestCommand("read-a"),
            new CommandDispatchOptions(ResourceClaims: [claim]));
        var second = dispatcher.EnqueueAsync(
            new TestCommand("read-b"),
            new CommandDispatchOptions(ResourceClaims: [claim]));

        await target.ExpectedStarts.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(2, target.MaximumConcurrency);

        target.Release.TrySetResult();
        await Task.WhenAll(first, second);
    }

    [Fact]
    public async Task Exclusive_command_waits_for_active_shared_readers()
    {
        var target = new OrderedGateTarget();
        var runtime = new CommandRuntime<TestCommand, string>(target);
        await using var dispatcher = new BoundedCommandDispatcher<TestCommand, string>(
            runtime,
            new BoundedCommandDispatcherOptions(
                Capacity: 8,
                PerPriorityCapacity: 8,
                MaxConcurrency: 3,
                PerResourcePendingCapacity: 8,
                MaxSharedReadersPerResource: 2));

        await dispatcher.StartAsync();
        var resource = new CommandResourceKey("connection", "shared");

        var read = dispatcher.EnqueueAsync(
            new TestCommand("read", Block: true),
            new CommandDispatchOptions(
                ResourceClaims: [new(resource, CommandResourceAccess.SharedRead)]));
        await target.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var write = dispatcher.EnqueueAsync(
            new TestCommand("write"),
            new CommandDispatchOptions(
                ResourceClaims: [new(resource, CommandResourceAccess.Exclusive)]));

        await Task.Delay(25);
        Assert.DoesNotContain("write", target.ExecutionOrder);

        target.ReleaseFirst.TrySetResult();
        await Task.WhenAll(read, write);

        Assert.Equal(["read", "write"], target.ExecutionOrder);
    }

    [Fact]
    public async Task Opposite_resource_order_is_canonicalized_and_does_not_deadlock()
    {
        var target = new ShortTarget();
        var runtime = new CommandRuntime<TestCommand, string>(target);
        await using var dispatcher = new BoundedCommandDispatcher<TestCommand, string>(
            runtime,
            new BoundedCommandDispatcherOptions(
                Capacity: 8,
                PerPriorityCapacity: 8,
                MaxConcurrency: 2,
                PerResourcePendingCapacity: 8,
                MaxSharedReadersPerResource: 2));

        await dispatcher.StartAsync();

        var a = new CommandResourceKey("device", "a");
        var b = new CommandResourceKey("device", "b");

        var first = dispatcher.EnqueueAsync(
            new TestCommand("ab"),
            new CommandDispatchOptions(Resources: [a, b]));
        var second = dispatcher.EnqueueAsync(
            new TestCommand("ba"),
            new CommandDispatchOptions(Resources: [b, a]));

        var results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.All(results, result => Assert.True(result.IsSuccess));
        Assert.Equal(1, target.MaximumConcurrency);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!predicate())
            await Task.Delay(1, timeout.Token);
    }

    private sealed record TestCommand(string Name, bool Block = false);

    private sealed class NamedGateTarget : ICommandable<TestCommand, string>
    {
        public TaskCompletionSource FirstStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<string> ExecuteAsync(TestCommand command, CancellationToken cancellationToken = default)
        {
            if (command.Name == "first")
            {
                FirstStarted.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }

            return command.Name;
        }
    }

    private sealed class ConcurrentGateTarget(int expectedStarts) : ICommandable<TestCommand, string>
    {
        private int _active;
        private int _maximum;
        private int _starts;

        public int MaximumConcurrency => Volatile.Read(ref _maximum);
        public TaskCompletionSource ExpectedStarts { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<string> ExecuteAsync(TestCommand command, CancellationToken cancellationToken = default)
        {
            var active = Interlocked.Increment(ref _active);
            UpdateMaximum(active);
            if (Interlocked.Increment(ref _starts) >= expectedStarts)
                ExpectedStarts.TrySetResult();

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

    private sealed class OrderedGateTarget : ICommandable<TestCommand, string>
    {
        private readonly List<string> _order = [];
        private readonly object _gate = new();

        public TaskCompletionSource FirstStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirst { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<string> ExecutionOrder
        {
            get
            {
                lock (_gate)
                    return _order.ToArray();
            }
        }

        public async Task<string> ExecuteAsync(TestCommand command, CancellationToken cancellationToken = default)
        {
            lock (_gate)
                _order.Add(command.Name);

            if (command.Block)
            {
                FirstStarted.TrySetResult();
                await ReleaseFirst.Task.WaitAsync(cancellationToken);
            }

            return command.Name;
        }
    }

    private sealed class ShortTarget : ICommandable<TestCommand, string>
    {
        private int _active;
        private int _maximum;
        public int MaximumConcurrency => Volatile.Read(ref _maximum);

        public async Task<string> ExecuteAsync(TestCommand command, CancellationToken cancellationToken = default)
        {
            var active = Interlocked.Increment(ref _active);
            UpdateMaximum(active);
            try
            {
                await Task.Yield();
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
