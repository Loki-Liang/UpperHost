using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using OpenDeviceStudio.Abstractions.Devices;
using OpenDeviceStudio.Abstractions.Workflows;
using OpenDeviceStudio.Control.Commands;
using OpenDeviceStudio.Control.Scheduling;
using OpenDeviceStudio.Workflows;

namespace OpenDeviceStudio.Tests;

public sealed class WorkflowExecutionCoordinatorTests
{
    [Fact]
    public async Task Station_allows_only_one_active_execution()
    {
        await using var fixture = CreateFixture();
        var started = Signal();
        var release = Signal();

        var first = fixture.Coordinator.Start(
            Request(
                Definition(
                    new WorkflowActionNode(
                        "block",
                        async (_, token) =>
                        {
                            started.TrySetResult();
                            await release.Task.WaitAsync(token);
                            return WorkflowStepResult.Success();
                        })),
                "station-a"));

        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Throws<InvalidOperationException>(() =>
            fixture.Coordinator.Start(
                Request(Definition(Success("other")), "station-a")));

        release.TrySetResult();
        Assert.Equal(
            WorkflowExecutionStatus.Completed,
            (await first.Completion).Status);

        var second = fixture.Coordinator.Start(
            Request(Definition(Success("after")), "station-a"));
        Assert.Equal(
            WorkflowExecutionStatus.Completed,
            (await second.Completion).Status);
    }

    [Fact]
    public async Task Stop_finishes_current_action_and_never_starts_next_node()
    {
        await using var fixture = CreateFixture();
        var started = Signal();
        var release = Signal();
        var secondStarted = 0;

        var handle = fixture.Coordinator.Start(
            Request(
                Definition(
                    new WorkflowSequenceNode(
                        "root",
                        [
                            new WorkflowActionNode(
                                "first",
                                async (_, token) =>
                                {
                                    started.TrySetResult();
                                    await release.Task.WaitAsync(token);
                                    return WorkflowStepResult.Success();
                                }),
                            new WorkflowActionNode(
                                "second",
                                (_, _) =>
                                {
                                    Interlocked.Exchange(ref secondStarted, 1);
                                    return Task.FromResult(WorkflowStepResult.Success());
                                })
                        ]))));

        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(handle.RequestStop());
        release.TrySetResult();

        var result = await handle.Completion;

        Assert.Equal(WorkflowExecutionStatus.Stopped, result.Status);
        Assert.Equal(0, Volatile.Read(ref secondStarted));
    }

    [Fact]
    public async Task Pause_is_applied_only_at_safe_checkpoint()
    {
        await using var fixture = CreateFixture();
        var authority = new RecordingAuthority();
        var started = Signal();
        var release = Signal();
        var after = 0;

        var handle = fixture.Coordinator.Start(
            Request(
                Definition(
                    new WorkflowSequenceNode(
                        "root",
                        [
                            new WorkflowActionNode(
                                "first",
                                async (_, token) =>
                                {
                                    started.TrySetResult();
                                    await release.Task.WaitAsync(token);
                                    return WorkflowStepResult.Success();
                                }),
                            new WorkflowSafeCheckpointNode("safe-1"),
                            new WorkflowActionNode(
                                "after",
                                (_, _) =>
                                {
                                    Interlocked.Exchange(ref after, 1);
                                    return Task.FromResult(WorkflowStepResult.Success());
                                })
                        ])),
                authority: authority));

        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(handle.RequestPause());
        Assert.Equal(WorkflowExecutionStatus.PauseRequested, handle.Status);

        release.TrySetResult();
        await authority.Paused.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(WorkflowExecutionStatus.Paused, handle.Status);
        Assert.Equal(0, Volatile.Read(ref after));
        Assert.True(handle.RequestResume());

        var result = await handle.Completion;

        Assert.Equal(WorkflowExecutionStatus.Completed, result.Status);
        Assert.Equal(1, Volatile.Read(ref after));
        Assert.Equal("safe-1", result.LastSafeCheckpoint);
    }

    [Fact]
    public async Task Parallel_runtime_is_bounded_and_fail_fast_observes_siblings()
    {
        await using var fixture = CreateFixture();
        var twoStarted = Signal();
        var release = Signal();
        var active = 0;
        var maximum = 0;
        var starts = 0;

        WorkflowActionNode Blocking(string id) =>
            new(
                id,
                async (_, token) =>
                {
                    var now = Interlocked.Increment(ref active);
                    UpdateMaximum(ref maximum, now);
                    if (Interlocked.Increment(ref starts) == 2)
                        twoStarted.TrySetResult();

                    try
                    {
                        await release.Task.WaitAsync(token);
                        return WorkflowStepResult.Success();
                    }
                    finally
                    {
                        Interlocked.Decrement(ref active);
                    }
                });

        var bounded = fixture.Coordinator.Start(
            Request(
                Definition(
                    new WorkflowParallelNode(
                        "bounded",
                        [Blocking("a"), Blocking("b"), Blocking("c"), Blocking("d")],
                        maxConcurrency: 2,
                        joinPolicy: WorkflowParallelJoinPolicy.WaitAll)),
                "station-bounded"));

        await twoStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(2, Volatile.Read(ref maximum));
        release.TrySetResult();
        Assert.Equal(
            WorkflowExecutionStatus.Completed,
            (await bounded.Completion).Status);

        var siblingStarted = Signal();
        var siblingFinished = Signal();
        var failFast = fixture.Coordinator.Start(
            Request(
                Definition(
                    new WorkflowParallelNode(
                        "fail-fast",
                        [
                            new WorkflowActionNode(
                                "failure",
                                async (_, _) =>
                                {
                                    await siblingStarted.Task;
                                    return WorkflowStepResult.Failure("boom");
                                }),
                            new WorkflowActionNode(
                                "sibling",
                                async (_, token) =>
                                {
                                    siblingStarted.TrySetResult();
                                    try
                                    {
                                        await Task.Delay(Timeout.InfiniteTimeSpan, token);
                                        return WorkflowStepResult.Success();
                                    }
                                    finally
                                    {
                                        siblingFinished.TrySetResult();
                                    }
                                })
                        ],
                        maxConcurrency: 2,
                        joinPolicy: WorkflowParallelJoinPolicy.FailFast)),
                "station-fail-fast"));

        var failFastResult = await failFast.Completion;
        await siblingFinished.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(WorkflowExecutionStatus.Failed, failFastResult.Status);
        Assert.Equal(0, fixture.Coordinator.ActiveExecutionCount);
    }

    [Fact]
    public async Task Abort_priority_distinguishes_known_and_unknown_physical_outcome()
    {
        await using var fixture = CreateFixture();

        var readStarted = Signal();
        var read = fixture.Coordinator.Start(
            Request(
                Definition(
                    new WorkflowActionNode(
                        "read",
                        async (_, token) =>
                        {
                            readStarted.TrySetResult();
                            await Task.Delay(Timeout.InfiniteTimeSpan, token);
                            return WorkflowStepResult.Success();
                        })),
                "station-read"));

        await readStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(read.RequestAbort());
        Assert.Equal(
            WorkflowExecutionStatus.Aborted,
            (await read.Completion).Status);

        var moveStarted = Signal();
        var move = fixture.Coordinator.Start(
            Request(
                Definition(
                    new WorkflowActionNode(
                        "move",
                        async (_, token) =>
                        {
                            moveStarted.TrySetResult();
                            await Task.Delay(Timeout.InfiniteTimeSpan, token);
                            return WorkflowStepResult.Success();
                        },
                        new WorkflowStepPolicy(SideEffecting: true))),
                "station-move"));

        await moveStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(move.RequestAbort());
        Assert.Equal(
            WorkflowExecutionStatus.RecoveryRequired,
            (await move.Completion).Status);
    }

    [Fact]
    public async Task Timeout_and_retry_use_injected_time_provider()
    {
        var time = new FakeTimeProvider();
        await using var fixture = CreateFixture(time);

        var started = Signal();
        var timed = fixture.Coordinator.Start(
            Request(
                Definition(
                    new WorkflowActionNode(
                        "move",
                        async (_, token) =>
                        {
                            started.TrySetResult();
                            await Task.Delay(Timeout.InfiniteTimeSpan, token);
                            return WorkflowStepResult.Success();
                        },
                        new WorkflowStepPolicy(
                            Timeout: TimeSpan.FromSeconds(10),
                            SideEffecting: true))),
                "station-timeout"));

        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        time.Advance(TimeSpan.FromSeconds(10));

        Assert.Equal(
            WorkflowExecutionStatus.RecoveryRequired,
            (await timed.Completion).Status);

        var attempts = 0;
        var retry = fixture.Coordinator.Start(
            Request(
                Definition(
                    new WorkflowActionNode(
                        "read",
                        (_, _) =>
                        {
                            var attempt = Interlocked.Increment(ref attempts);
                            return Task.FromResult(
                                attempt == 1
                                    ? WorkflowStepResult.Failure("transient")
                                    : WorkflowStepResult.Success());
                        },
                        new WorkflowStepPolicy(
                            Retry: new WorkflowRetryPolicy(
                                MaxAttempts: 2,
                                Delay: TimeSpan.FromMinutes(1))))),
                "station-retry"));

        time.Advance(TimeSpan.FromMinutes(1));

        var retryResult = await retry.Completion;
        var outcome = Assert.Single(
            retryResult.Nodes.Where(static node => node.NodeId == "read"));

        Assert.Equal(WorkflowExecutionStatus.Completed, retryResult.Status);
        Assert.Equal(2, outcome.Attempts);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task Unknown_physical_outcome_is_never_blindly_retried()
    {
        await using var fixture = CreateFixture();
        var attempts = 0;

        var result = await fixture.Coordinator.Start(
            Request(
                Definition(
                    new WorkflowActionNode(
                        "move",
                        (_, _) =>
                        {
                            Interlocked.Increment(ref attempts);
                            throw new WorkflowPhysicalOutcomeUnknownException(
                                "device outcome unknown");
                        },
                        new WorkflowStepPolicy(
                            Retry: new WorkflowRetryPolicy(MaxAttempts: 3),
                            SideEffecting: true,
                            RetryPredicate: static _ => true)))))
            .Completion;

        Assert.Equal(WorkflowExecutionStatus.RecoveryRequired, result.Status);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task Logical_workflow_lease_can_nest_into_shared_control_dispatcher()
    {
        var services = new ServiceCollection();
        services.AddOpenDeviceStudioControlResourceArbiter(maxSharedReadersPerResource: 2);
        services.AddSingleton<ICommandable<TestCommand, string>, TestTarget>();
        services.AddOpenDeviceStudioCommandDispatcher<TestCommand, string>(
            new BoundedCommandDispatcherOptions(
                Capacity: 8,
                PerPriorityCapacity: 8,
                MaxConcurrency: 2,
                PerResourcePendingCapacity: 8,
                MaxSharedReadersPerResource: 2));

        await using var provider = services.BuildServiceProvider();
        var arbiter = provider.GetRequiredService<ICommandResourceArbiter>();
        var dispatcher =
            provider.GetRequiredService<BoundedCommandDispatcher<TestCommand, string>>();
        await dispatcher.StartAsync();

        await using var coordinator = new WorkflowExecutionCoordinator(arbiter);
        var result = await coordinator.Start(
            Request(
                Definition(
                    new WorkflowActionNode(
                        "mutate",
                        async (context, token) =>
                        {
                            var command = await dispatcher.EnqueueAsync(
                                new TestCommand("move"),
                                new CommandDispatchOptions(
                                    Resources:
                                    [
                                        new CommandResourceKey("device", "axis-x")
                                    ]),
                                token);

                            context.RecordCommandExecutionId(command.ExecutionId);
                            return command.IsSuccess
                                ? WorkflowStepResult.Success()
                                : WorkflowStepResult.Failure(
                                    command.Message ?? command.Status.ToString());
                        },
                        new WorkflowStepPolicy(
                            SideEffecting: true,
                            Resources:
                            [
                                new WorkflowResourceRequirement("fixture", "main")
                            ])))))
            .Completion
            .WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(WorkflowExecutionStatus.Completed, result.Status);
        Assert.Single(result.CommandExecutionIds);

        await dispatcher.StopAsync();
    }

    [Fact]
    public async Task Workflow_resource_sets_are_canonical_and_do_not_deadlock()
    {
        await using var fixture = CreateFixture();
        var firstStarted = Signal();
        var release = Signal();
        var active = 0;
        var maximum = 0;

        WorkflowActionNode Action(
            string id,
            IReadOnlyList<WorkflowResourceRequirement> resources) =>
            new(
                id,
                async (_, token) =>
                {
                    var now = Interlocked.Increment(ref active);
                    UpdateMaximum(ref maximum, now);
                    firstStarted.TrySetResult();

                    try
                    {
                        await release.Task.WaitAsync(token);
                        return WorkflowStepResult.Success();
                    }
                    finally
                    {
                        Interlocked.Decrement(ref active);
                    }
                },
                new WorkflowStepPolicy(Resources: resources));

        var first = fixture.Coordinator.Start(
            Request(
                Definition(
                    Action(
                        "ab",
                        [
                            new WorkflowResourceRequirement("fixture", "a"),
                            new WorkflowResourceRequirement("fixture", "b")
                        ])),
                "station-a"));

        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var second = fixture.Coordinator.Start(
            Request(
                Definition(
                    Action(
                        "ba",
                        [
                            new WorkflowResourceRequirement("fixture", "b"),
                            new WorkflowResourceRequirement("fixture", "a")
                        ])),
                "station-b"));

        release.TrySetResult();

        var results = await Task.WhenAll(first.Completion, second.Completion)
            .WaitAsync(TimeSpan.FromSeconds(2));

        Assert.All(
            results,
            static result =>
                Assert.Equal(WorkflowExecutionStatus.Completed, result.Status));
        Assert.Equal(1, maximum);
    }

    [Fact]
    public async Task Preconditions_and_completion_conditions_are_authoritative()
    {
        await using var fixture = CreateFixture();
        var executed = 0;

        var guarded = await fixture.Coordinator.Start(
            Request(
                Definition(
                    new WorkflowActionNode(
                        "guarded",
                        (_, _) =>
                        {
                            Interlocked.Exchange(ref executed, 1);
                            return Task.FromResult(WorkflowStepResult.Success());
                        },
                        new WorkflowStepPolicy(
                            Precondition: static (_, _) =>
                                ValueTask.FromResult(false)))),
                "station-guard"))
            .Completion;

        Assert.Equal(WorkflowExecutionStatus.Failed, guarded.Status);
        Assert.Equal(0, executed);

        var completion = await fixture.Coordinator.Start(
            Request(
                Definition(
                    new WorkflowActionNode(
                        "move",
                        static (_, _) =>
                            Task.FromResult(WorkflowStepResult.Success()),
                        new WorkflowStepPolicy(
                            SideEffecting: true,
                            CompletionCondition: static (_, _, _) =>
                                ValueTask.FromResult(false)))),
                "station-completion"))
            .Completion;

        Assert.Equal(
            WorkflowExecutionStatus.RecoveryRequired,
            completion.Status);
    }

    private static WorkflowExecutionDefinition Definition(WorkflowNode root) =>
        new("test-workflow", "1", root);

    private static WorkflowExecutionRequest Request(
        WorkflowExecutionDefinition definition,
        string station = "station-1",
        IWorkflowStationStateAuthority? authority = null) =>
        new(
            definition,
            WorkflowRecipeSnapshot.Create(
                "recipe",
                "1",
                """{"mode":"test"}""",
                frozenAtUtc: new DateTimeOffset(
                    2026,
                    9,
                    27,
                    0,
                    0,
                    0,
                    TimeSpan.Zero)),
            station,
            StationStateAuthority: authority);

    private static WorkflowActionNode Success(string id) =>
        new(
            id,
            static (_, _) =>
                Task.FromResult(WorkflowStepResult.Success()));

    private static TaskCompletionSource Signal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static void UpdateMaximum(ref int maximum, int current)
    {
        while (true)
        {
            var observed = Volatile.Read(ref maximum);
            if (current <= observed ||
                Interlocked.CompareExchange(
                    ref maximum,
                    current,
                    observed) == observed)
            {
                return;
            }
        }
    }

    private static RuntimeFixture CreateFixture(
        TimeProvider? timeProvider = null)
    {
        var services = new ServiceCollection();
        services.AddOpenDeviceStudioControlResourceArbiter();
        var provider = services.BuildServiceProvider();

        var coordinator = new WorkflowExecutionCoordinator(
            provider.GetRequiredService<ICommandResourceArbiter>(),
            timeProvider);

        return new RuntimeFixture(provider, coordinator);
    }

    private sealed class RuntimeFixture(
        ServiceProvider provider,
        WorkflowExecutionCoordinator coordinator) : IAsyncDisposable
    {
        public WorkflowExecutionCoordinator Coordinator { get; } = coordinator;

        public async ValueTask DisposeAsync()
        {
            await Coordinator.DisposeAsync();
            await provider.DisposeAsync();
        }
    }

    private sealed class RecordingAuthority : IWorkflowStationStateAuthority
    {
        public TaskCompletionSource Paused { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask OnExecutionTransitionAsync(
            string executionId,
            WorkflowExecutionStatus from,
            WorkflowExecutionStatus to,
            string? reason,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (to == WorkflowExecutionStatus.Paused)
                Paused.TrySetResult();

            return ValueTask.CompletedTask;
        }
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
