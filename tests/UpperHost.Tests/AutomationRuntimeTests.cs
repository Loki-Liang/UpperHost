using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using UpperHost.Abstractions.Devices;
using UpperHost.Control.Scheduling;
using UpperHost.Workflows;

namespace UpperHost.Tests;

public sealed class AutomationRuntimeTests
{
    [Fact]
    public void Plan_compile_freezes_recipe_and_rejects_duplicate_or_unsafe_retry_contracts()
    {
        var source = new Dictionary<string, int> { ["speed"] = 10 };
        var snapshot = AutomationRecipeSnapshot.Create("recipe-a", "1", source);
        source["speed"] = 99;

        var restored = snapshot.Deserialize<Dictionary<string, int>>();
        Assert.NotNull(restored);
        Assert.Equal(10, restored!["speed"]);

        var duplicate = new AutomationWorkflowDefinition(
            "wf",
            "1",
            new AutomationSequenceNode(
                "root",
                new AutomationActionNode("same", Step(_ => AutomationStepResult.Success())),
                new AutomationActionNode("same", Step(_ => AutomationStepResult.Success()))));

        Assert.Throws<InvalidOperationException>(() => AutomationExecutionPlan.Compile(duplicate));

        var unsafeRetry = new AutomationWorkflowDefinition(
            "wf",
            "1",
            new AutomationActionNode(
                "step",
                Step(
                    _ => AutomationStepResult.Success(),
                    new AutomationStepPolicy(
                        Retry: new AutomationRetryPolicy(
                            2,
                            TimeSpan.Zero,
                            new HashSet<AutomationStepStatus>
                            {
                                AutomationStepStatus.UnknownPhysicalOutcome
                            })))));

        Assert.Throws<InvalidOperationException>(() => AutomationExecutionPlan.Compile(unsafeRetry));
    }

    [Fact]
    public async Task Coordinator_allows_only_one_active_execution_per_station()
    {
        var entered = Signal();
        var release = Signal();

        var plan = Plan(
            new AutomationActionNode(
                "hold",
                new AutomationStepDescriptor(async (_, cancellationToken) =>
                {
                    entered.TrySetResult(true);
                    await release.Task.WaitAsync(cancellationToken);
                    return AutomationStepResult.Success();
                })));

        await using var coordinator = new AutomationExecutionCoordinator(new NoopArbiter());
        var first = coordinator.TryStart(plan, Recipe());

        Assert.True(first.Accepted);
        await entered.Task;

        var second = coordinator.TryStart(plan, Recipe());
        Assert.False(second.Accepted);
        Assert.Equal("station_busy", second.Code);

        release.TrySetResult(true);
        var completed = await first.Execution!.Completion;
        Assert.Equal(AutomationExecutionState.Completed, completed.State);
        Assert.Equal(AutomationStationState.Completed, coordinator.StationState);
    }

    [Fact]
    public async Task Parallel_runtime_is_bounded_and_joins_all_owned_children()
    {
        var release = Signal();
        var firstWave = Signal();
        var current = 0;
        var max = 0;
        var started = 0;

        AutomationActionNode Child(string id) =>
            new(
                id,
                new AutomationStepDescriptor(async (_, cancellationToken) =>
                {
                    var running = Interlocked.Increment(ref current);
                    UpdateMax(ref max, running);
                    if (Interlocked.Increment(ref started) == 2)
                        firstWave.TrySetResult(true);

                    await release.Task.WaitAsync(cancellationToken);
                    Interlocked.Decrement(ref current);
                    return AutomationStepResult.Success();
                }));

        var plan = Plan(
            new AutomationParallelNode(
                "parallel",
                2,
                AutomationJoinMode.WaitAll,
                Child("a"),
                Child("b"),
                Child("c"),
                Child("d")));

        await using var coordinator = new AutomationExecutionCoordinator(new NoopArbiter());
        var start = coordinator.TryStart(plan, Recipe());

        await firstWave.Task;
        Assert.Equal(2, Volatile.Read(ref max));
        Assert.Equal(2, Volatile.Read(ref current));

        release.TrySetResult(true);
        var result = await start.Execution!.Completion;

        Assert.Equal(AutomationExecutionState.Completed, result.State);
        Assert.Equal(4, result.Steps.Count);
        Assert.Equal(0, Volatile.Read(ref current));
    }

    [Fact]
    public async Task Stop_does_not_start_a_new_node()
    {
        var entered = Signal();
        var release = Signal();
        var secondRan = 0;

        var plan = Plan(
            new AutomationSequenceNode(
                "root",
                new AutomationActionNode(
                    "first",
                    new AutomationStepDescriptor(async (_, cancellationToken) =>
                    {
                        entered.TrySetResult(true);
                        await release.Task.WaitAsync(cancellationToken);
                        return AutomationStepResult.Success();
                    })),
                new AutomationActionNode(
                    "second",
                    Step(_ =>
                    {
                        Interlocked.Increment(ref secondRan);
                        return AutomationStepResult.Success();
                    }))));

        await using var coordinator = new AutomationExecutionCoordinator(new NoopArbiter());
        var start = coordinator.TryStart(plan, Recipe());

        await entered.Task;
        var stop = await start.Execution!.StopAsync();
        Assert.True(stop.Accepted);

        release.TrySetResult(true);
        var result = await start.Execution.Completion;

        Assert.Equal(AutomationExecutionState.Stopped, result.State);
        Assert.Equal(0, Volatile.Read(ref secondRan));
    }

    [Fact]
    public async Task Pause_is_applied_only_at_declared_safe_boundary()
    {
        var entered = Signal();
        var release = Signal();
        var journal = new RecordingJournal();
        var secondRan = 0;

        var plan = Plan(
            new AutomationSequenceNode(
                "root",
                new AutomationActionNode(
                    "first",
                    new AutomationStepDescriptor(async (_, cancellationToken) =>
                    {
                        entered.TrySetResult(true);
                        await release.Task.WaitAsync(cancellationToken);
                        return AutomationStepResult.Success();
                    })),
                new AutomationCheckpointNode("safe-pause", AutomationCheckpointKind.SafePause),
                new AutomationActionNode(
                    "second",
                    Step(_ =>
                    {
                        Interlocked.Increment(ref secondRan);
                        return AutomationStepResult.Success();
                    }))));

        await using var coordinator = new AutomationExecutionCoordinator(
            new NoopArbiter(),
            journal);
        var start = coordinator.TryStart(plan, Recipe());

        await entered.Task;
        var pause = await start.Execution!.PauseAsync();
        Assert.True(pause.Accepted);
        Assert.Equal(AutomationExecutionState.PauseRequested, start.Execution.State);
        Assert.Equal(AutomationStationState.Running, coordinator.StationState);

        release.TrySetResult(true);
        await journal.Paused.Task;

        Assert.Equal(AutomationStationState.Paused, coordinator.StationState);
        Assert.Equal(0, Volatile.Read(ref secondRan));

        var resume = await start.Execution.ResumeAsync();
        Assert.True(resume.Accepted);
        var result = await start.Execution.Completion;

        Assert.Equal(AutomationExecutionState.Completed, result.State);
        Assert.Equal(1, Volatile.Read(ref secondRan));
    }

    [Fact]
    public async Task Unknown_physical_outcome_requires_reconcile_and_never_blindly_resumes()
    {
        var device = new CommandResourceKey("device", "axis-1");
        var plan = Plan(
            new AutomationSequenceNode(
                "root",
                new AutomationActionNode(
                    "move",
                    Step(
                        _ => new AutomationStepResult(
                            AutomationStepStatus.UnknownPhysicalOutcome,
                            "Device completion is unknown.",
                            CommandExecutionId: "command-1"),
                        new AutomationStepPolicy(
                            Resources: [new CommandResourceClaim(device)]))),
                new AutomationCheckpointNode("checkpoint", AutomationCheckpointKind.SafeRecovery)));

        await using var coordinator = new AutomationExecutionCoordinator(new NoopArbiter());
        var start = coordinator.TryStart(plan, Recipe());
        var result = await start.Execution!.Completion;

        Assert.Equal(AutomationExecutionState.RecoveryRequired, result.State);
        Assert.True(result.RecoveryRequired);
        Assert.Equal(AutomationStationState.Faulted, coordinator.StationState);
        Assert.Contains("axis-1", coordinator.LastRecoveryEvidence!.RequiredDeviceIds);

        var blocked = coordinator.TryStart(plan, Recipe());
        Assert.False(blocked.Accepted);
        Assert.Equal("recovery_required", blocked.Code);

        var recovery = await coordinator.RecoverAsync();
        Assert.Equal(AutomationRecoveryDecision.ManualIntervention, recovery.Decision);
        Assert.Equal(AutomationStationState.Faulted, coordinator.StationState);

        var manual = coordinator.CompleteManualRecovery();
        Assert.True(manual.Accepted);
        Assert.Equal(AutomationStationState.Idle, coordinator.StationState);
    }

    [Fact]
    public async Task Compensation_failure_requires_recovery()
    {
        var plan = Plan(
            new AutomationActionNode(
                "physical-action",
                new AutomationStepDescriptor(
                    (_, _) => Task.FromResult(AutomationStepResult.Failure("primary failed")),
                    compensation: (_, _) => Task.FromResult(AutomationStepResult.Failure("compensation failed")))));

        await using var coordinator = new AutomationExecutionCoordinator(new NoopArbiter());
        var result = await coordinator.TryStart(plan, Recipe()).Execution!.Completion;

        Assert.Equal(AutomationStepStatus.RecoveryRequired, result.Outcome);
        Assert.True(result.RecoveryRequired);
    }

    [Fact]
    public async Task Shared_control_arbiter_blocks_manual_command_without_self_deadlocking_automation_dispatch()
    {
        var resource = new CommandResourceKey("device", "shared-axis");
        var entered = Signal();
        var release = Signal();
        var executed = new ConcurrentQueue<string>();

        var services = new ServiceCollection();
        services.AddSingleton<ICommandable<TestCommand, string>>(new TestCommandTarget(executed));
        services.AddUpperHostCommandDispatcher<TestCommand, string>(
            new BoundedCommandDispatcherOptions(MaxConcurrency: 2));
        await using var provider = services.BuildServiceProvider();

        var dispatcher = provider.GetRequiredService<BoundedCommandDispatcher<TestCommand, string>>();
        var arbiter = provider.GetRequiredService<ICommandResourceArbiter>();
        await dispatcher.StartAsync();

        var plan = Plan(
            new AutomationActionNode(
                "automation-command",
                new AutomationStepDescriptor(
                    async (context, cancellationToken) =>
                    {
                        entered.TrySetResult(true);
                        await release.Task.WaitAsync(cancellationToken);

                        var command = await context.DispatchAsync(
                            dispatcher,
                            new TestCommand("automation"),
                            new CommandDispatchOptions(
                                ResourceClaims: [new CommandResourceClaim(resource)]),
                            cancellationToken);

                        return AutomationStepResult.FromCommand(command);
                    },
                    new AutomationStepPolicy(
                        Resources: [new CommandResourceClaim(resource)]))));

        await using var coordinator = new AutomationExecutionCoordinator(arbiter);
        var run = coordinator.TryStart(plan, Recipe());
        await entered.Task;

        var manual = dispatcher.EnqueueAsync(
            new TestCommand("manual"),
            new CommandDispatchOptions(
                ResourceClaims: [new CommandResourceClaim(resource)]));

        await Task.Yield();
        Assert.False(manual.IsCompleted);

        release.TrySetResult(true);

        var automation = await run.Execution!.Completion;
        var manualResult = await manual;

        Assert.Equal(AutomationExecutionState.Completed, automation.State);
        Assert.True(manualResult.IsSuccess);
        Assert.Contains("automation", executed);
        Assert.Contains("manual", executed);

        await dispatcher.StopAsync();
    }

    [Fact]
    public async Task Bounded_journal_preserves_monotonic_execution_sequence()
    {
        var sink = new InMemoryAutomationJournalSink();
        await using var journal = new BoundedAutomationExecutionJournal(
            sink,
            new BoundedAutomationExecutionJournalOptions(Capacity: 8));
        await journal.StartAsync();

        await using var coordinator = new AutomationExecutionCoordinator(
            new NoopArbiter(),
            journal);
        var result = await coordinator.TryStart(
            Plan(new AutomationActionNode("step", Step(_ => AutomationStepResult.Success()))),
            Recipe()).Execution!.Completion;

        Assert.Equal(AutomationExecutionState.Completed, result.State);

        await journal.StopAsync();
        var events = sink.Snapshot();
        Assert.NotEmpty(events);
        Assert.Equal(
            Enumerable.Range(1, events.Count).Select(static value => (long)value),
            events.Select(static journalEvent => journalEvent.Sequence));
    }

    [Fact]
    public async Task Resource_sets_with_opposite_input_order_are_canonicalized_by_shared_arbiter()
    {
        var services = new ServiceCollection();
        services.AddUpperHostControlResourceArbiter();
        await using var provider = services.BuildServiceProvider();
        var arbiter = provider.GetRequiredService<ICommandResourceArbiter>();

        var a = new CommandResourceKey("device", "a");
        var b = new CommandResourceKey("device", "b");

        await using var first = await arbiter.AcquireAsync(
            [new CommandResourceClaim(b), new CommandResourceClaim(a)]);

        var secondTask = arbiter.AcquireAsync(
            [new CommandResourceClaim(a), new CommandResourceClaim(b)]).AsTask();

        Assert.False(secondTask.IsCompleted);
        await first.DisposeAsync();

        await using var second = await secondTask.WaitAsync(TimeSpan.FromSeconds(1));
    }

    private static AutomationExecutionPlan Plan(AutomationNode root) =>
        AutomationExecutionPlan.Compile(
            new AutomationWorkflowDefinition("test-workflow", "1.0", root));

    private static AutomationRecipeSnapshot Recipe() =>
        AutomationRecipeSnapshot.Create("test-recipe", "1", new { Speed = 1 });

    private static AutomationStepDescriptor Step(
        Func<AutomationStepContext, AutomationStepResult> action,
        AutomationStepPolicy? policy = null) =>
        new((context, _) => Task.FromResult(action(context)), policy);

    private static TaskCompletionSource<bool> Signal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static void UpdateMax(ref int max, int current)
    {
        while (true)
        {
            var observed = Volatile.Read(ref max);
            if (current <= observed ||
                Interlocked.CompareExchange(ref max, current, observed) == observed)
                return;
        }
    }

    private sealed class NoopArbiter : ICommandResourceArbiter
    {
        public ValueTask<IAsyncDisposable> AcquireAsync(
            IReadOnlyList<CommandResourceClaim> claims,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IAsyncDisposable>(NoopLease.Instance);
        }
    }

    private sealed class NoopLease : IAsyncDisposable
    {
        public static NoopLease Instance { get; } = new();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingJournal : IAutomationExecutionJournal
    {
        public TaskCompletionSource<bool> Paused { get; } = Signal();

        public ValueTask AppendAsync(
            AutomationJournalEvent journalEvent,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (journalEvent.EventType == AutomationJournalEventType.StationTransition &&
                journalEvent.StationTo == AutomationStationState.Paused)
            {
                Paused.TrySetResult(true);
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed record TestCommand(string Name);

    private sealed class TestCommandTarget(ConcurrentQueue<string> executed) :
        ICommandable<TestCommand, string>
    {
        public Task<string> ExecuteAsync(
            TestCommand command,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            executed.Enqueue(command.Name);
            return Task.FromResult(command.Name);
        }
    }
}
