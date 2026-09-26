using Microsoft.Extensions.Time.Testing;
using UpperHost.Abstractions.Devices;
using UpperHost.Control.Commands;
using UpperHost.Control.Scheduling;

namespace UpperHost.Tests;

public sealed class CommandOutcomeAndEpochTests
{
    [Fact]
    public async Task Mutating_legacy_command_timeout_after_dispatch_is_unknown_outcome()
    {
        var time = new FakeTimeProvider();
        var target = new NeverCompletesTarget();
        var runtime = CommandRuntime<TestCommand, string>.CreateContextual(target, timeProvider: time);
        await using var dispatcher = new BoundedCommandDispatcher<TestCommand, string>(runtime);

        await dispatcher.StartAsync();

        var pending = dispatcher.EnqueueAsync(
            new TestCommand("write"),
            new CommandDispatchOptions(
                Execution: new CommandExecutionOptions(TimeSpan.FromSeconds(5)),
                Safety: CommandSafetyMetadata.MutatingNonIdempotent));

        await target.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        time.Advance(TimeSpan.FromSeconds(6));

        var result = await pending.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(CommandExecutionStatus.UnknownOutcome, result.Status);
        Assert.Equal(CommandExecutionMilestone.SideEffectMayHaveStarted, result.Milestone);
        Assert.Equal("command_outcome_unknown", result.Code);
        Assert.Equal(1, target.CallCount);
    }

    [Fact]
    public async Task Read_only_timeout_remains_timed_out()
    {
        var time = new FakeTimeProvider();
        var target = new NeverCompletesTarget();
        var runtime = new CommandRuntime<TestCommand, string>(target, guards: null, time);
        await using var dispatcher = new BoundedCommandDispatcher<TestCommand, string>(runtime);

        await dispatcher.StartAsync();

        var pending = dispatcher.EnqueueAsync(
            new TestCommand("read"),
            new CommandDispatchOptions(
                Execution: new CommandExecutionOptions(TimeSpan.FromSeconds(5)),
                Safety: new CommandSafetyMetadata(
                    ReadOnly: true,
                    Idempotent: true,
                    Motion: false,
                    Hazardous: false,
                    RetryAllowed: true)));

        await target.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        time.Advance(TimeSpan.FromSeconds(6));

        var result = await pending.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(CommandExecutionStatus.TimedOut, result.Status);
        Assert.Equal(CommandExecutionMilestone.BeforeSideEffect, result.Milestone);
    }

    [Fact]
    public async Task Contextual_target_can_report_acknowledged_milestone()
    {
        var time = new FakeTimeProvider();
        var target = new AcknowledgedNeverCompletesTarget();
        var runtime = new CommandRuntime<TestCommand, string>(target, guards: null, time);
        await using var dispatcher = new BoundedCommandDispatcher<TestCommand, string>(runtime);

        await dispatcher.StartAsync();

        var pending = dispatcher.EnqueueAsync(
            new TestCommand("move"),
            new CommandDispatchOptions(
                Execution: new CommandExecutionOptions(TimeSpan.FromSeconds(5)),
                Safety: new CommandSafetyMetadata(
                    ReadOnly: false,
                    Idempotent: false,
                    Motion: true,
                    Hazardous: true,
                    RetryAllowed: false)));

        await target.Acknowledged.Task.WaitAsync(TimeSpan.FromSeconds(2));
        time.Advance(TimeSpan.FromSeconds(6));

        var result = await pending.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(CommandExecutionStatus.UnknownOutcome, result.Status);
        Assert.Equal(CommandExecutionMilestone.DeviceAcknowledged, result.Milestone);
    }

    [Fact]
    public async Task Queued_mutating_command_from_old_epoch_is_not_replayed_after_reconnect()
    {
        var validator = new MutableEpochValidator { CurrentEpoch = 1 };
        var target = new BlockingCountingTarget();
        var runtime = new CommandRuntime<TestCommand, string>(target);
        await using var dispatcher = new BoundedCommandDispatcher<TestCommand, string>(
            runtime,
            new BoundedCommandDispatcherOptions(Capacity: 8, PerPriorityCapacity: 8, MaxConcurrency: 1),
            validator);

        await dispatcher.StartAsync();

        var resource = new CommandResourceKey("connection", "device-1");
        var first = dispatcher.EnqueueAsync(
            new TestCommand("first"),
            new CommandDispatchOptions(
                Resources: [resource],
                ConnectionEpoch: 1));

        await target.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var stale = dispatcher.EnqueueAsync(
            new TestCommand("stale-write"),
            new CommandDispatchOptions(
                Resources: [resource],
                ConnectionEpoch: 1));

        validator.CurrentEpoch = 2;
        target.ReleaseFirst.TrySetResult();

        Assert.True((await first).IsSuccess);
        var staleResult = await stale;

        Assert.Equal(CommandExecutionStatus.Rejected, staleResult.Status);
        Assert.Equal("connection_epoch_stale", staleResult.Code);
        Assert.Equal(1, target.CallCount);
    }

    private sealed record TestCommand(string Name);

    private sealed class NeverCompletesTarget : ICommandable<TestCommand, string>
    {
        private int _callCount;
        public int CallCount => Volatile.Read(ref _callCount);
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<string> ExecuteAsync(
            TestCommand command,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _callCount);
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return command.Name;
        }
    }

    private sealed class AcknowledgedNeverCompletesTarget
        : IContextualCommandable<TestCommand, string>
    {
        public TaskCompletionSource Acknowledged { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<string> ExecuteAsync(
            TestCommand command,
            CommandExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            context.MarkSideEffectMayHaveStarted();
            context.MarkDeviceAcknowledged();
            Acknowledged.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return command.Name;
        }
    }

    private sealed class BlockingCountingTarget : ICommandable<TestCommand, string>
    {
        private int _callCount;
        public int CallCount => Volatile.Read(ref _callCount);
        public TaskCompletionSource FirstStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirst { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<string> ExecuteAsync(
            TestCommand command,
            CancellationToken cancellationToken = default)
        {
            var count = Interlocked.Increment(ref _callCount);
            if (count == 1)
            {
                FirstStarted.TrySetResult();
                await ReleaseFirst.Task.WaitAsync(cancellationToken);
            }

            return command.Name;
        }
    }

    private sealed class MutableEpochValidator : ICommandConnectionEpochValidator
    {
        public long CurrentEpoch { get; set; }

        public bool IsCurrent(long connectionEpoch, IReadOnlyList<CommandResourceKey>? resources) =>
            connectionEpoch == CurrentEpoch;
    }
}
