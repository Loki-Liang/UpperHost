using Microsoft.Extensions.DependencyInjection;
using OpenDeviceStudio.Abstractions.Workflows;
using OpenDeviceStudio.Control.Scheduling;
using OpenDeviceStudio.Workflows;

namespace OpenDeviceStudio.Tests;

public sealed class WorkflowRecoveryAndJournalTests
{
    [Fact]
    public async Task Journal_sequence_is_monotonic_and_contains_recipe_evidence()
    {
        await using var fixture = CreateFixture(
            new InMemoryWorkflowExecutionJournal(capacity: 64));

        var definition = new WorkflowExecutionDefinition(
            "journal-test",
            "3",
            new WorkflowSequenceNode(
                "root",
                [
                    SuccessAction("first"),
                    new WorkflowSafeCheckpointNode("checkpoint"),
                    SuccessAction("second")
                ]));

        var request = Request(definition);
        var result = await fixture.Coordinator.Start(request).Completion;

        Assert.Equal(WorkflowExecutionStatus.Completed, result.Status);

        var events = fixture.Journal.Events;
        Assert.NotEmpty(events);
        Assert.Equal(
            Enumerable.Range(1, events.Count).Select(static value => (long)value),
            events.Select(static item => item.Sequence));
        Assert.All(events, item =>
            Assert.Equal(request.RecipeSnapshot.Hash, item.RecipeHash));
        Assert.Contains(events, static item => item.EventType == "safe_checkpoint");
    }

    [Fact]
    public async Task Bounded_journal_failure_can_fail_execution()
    {
        await using var fixture = CreateFixture(
            new InMemoryWorkflowExecutionJournal(capacity: 1),
            WorkflowJournalFailurePolicy.FailExecution);

        var result = await fixture.Coordinator
            .Start(
                Request(
                    new WorkflowExecutionDefinition(
                        "journal-failure",
                        "1",
                        SuccessAction("action"))))
            .Completion;

        Assert.Equal(WorkflowExecutionStatus.Failed, result.Status);
        var failureReason = Assert.IsType<string>(result.FailureReason);
        Assert.Contains("capacity", failureReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Bounded_journal_failure_can_continue_with_explicit_evidence()
    {
        await using var fixture = CreateFixture(
            new InMemoryWorkflowExecutionJournal(capacity: 1),
            WorkflowJournalFailurePolicy.ContinueExecution);

        var result = await fixture.Coordinator
            .Start(
                Request(
                    new WorkflowExecutionDefinition(
                        "journal-continue",
                        "1",
                        SuccessAction("action"))))
            .Completion;

        Assert.Equal(WorkflowExecutionStatus.Completed, result.Status);
        Assert.True(result.JournalFailureCount > 0);
    }

    [Fact]
    public async Task Recovery_always_calls_authoritative_reconciler()
    {
        await using var fixture = CreateFixture(
            new InMemoryWorkflowExecutionJournal());
        var reconciler = new RecordingReconciler(
            new WorkflowRecoveryDecision(
                WorkflowRecoveryAction.Restart,
                true,
                "authoritative snapshots reconciled"));

        var decision = await fixture.Coordinator.PlanRecoveryAsync(
            Evidence(lastSafeCheckpoint: "checkpoint-1"),
            reconciler);

        Assert.Equal(1, reconciler.Calls);
        Assert.True(decision.Reconciled);
        Assert.Equal(WorkflowRecoveryAction.Restart, decision.Action);
    }

    [Fact]
    public async Task Recovery_never_resumes_without_successful_reconciliation()
    {
        await using var fixture = CreateFixture(
            new InMemoryWorkflowExecutionJournal());
        var reconciler = new RecordingReconciler(
            new WorkflowRecoveryDecision(
                WorkflowRecoveryAction.ResumeFromSafeCheckpoint,
                false,
                "device state unknown"));

        var decision = await fixture.Coordinator.PlanRecoveryAsync(
            Evidence(lastSafeCheckpoint: "checkpoint-1"),
            reconciler);

        Assert.Equal(WorkflowRecoveryAction.ManualIntervention, decision.Action);
        Assert.False(decision.Reconciled);
        Assert.Contains("device state unknown", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Recovery_rejects_resume_when_no_safe_checkpoint_exists()
    {
        await using var fixture = CreateFixture(
            new InMemoryWorkflowExecutionJournal());
        var reconciler = new RecordingReconciler(
            new WorkflowRecoveryDecision(
                WorkflowRecoveryAction.ResumeFromSafeCheckpoint,
                true,
                "devices reconciled"));

        var decision = await fixture.Coordinator.PlanRecoveryAsync(
            Evidence(lastSafeCheckpoint: null),
            reconciler);

        Assert.Equal(WorkflowRecoveryAction.ManualIntervention, decision.Action);
        Assert.True(decision.Reconciled);
        Assert.Contains("no completed safe checkpoint", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    private static WorkflowActionNode SuccessAction(string id) =>
        new(
            id,
            static (_, _) => Task.FromResult(WorkflowStepResult.Success()));

    private static WorkflowExecutionRequest Request(
        WorkflowExecutionDefinition definition) =>
        new(
            definition,
            WorkflowRecipeSnapshot.Create(
                "recipe",
                "5",
                """{"validated":true}""",
                frozenAtUtc: new DateTimeOffset(2026, 9, 27, 0, 0, 0, TimeSpan.Zero)),
            "station-1");

    private static WorkflowRecoveryEvidence Evidence(string? lastSafeCheckpoint) =>
        new(
            "execution-1",
            "workflow",
            "1",
            WorkflowRecipeSnapshot.Create(
                "recipe",
                "5",
                """{"validated":true}""",
                frozenAtUtc: new DateTimeOffset(2026, 9, 27, 0, 0, 0, TimeSpan.Zero)),
            "station-1",
            lastSafeCheckpoint,
            ["command-1"],
            "power loss");

    private static RuntimeFixture CreateFixture(
        InMemoryWorkflowExecutionJournal journal,
        WorkflowJournalFailurePolicy failurePolicy = WorkflowJournalFailurePolicy.FailExecution)
    {
        var services = new ServiceCollection();
        services.AddOpenDeviceStudioControlResourceArbiter();
        var provider = services.BuildServiceProvider();
        var coordinator = new WorkflowExecutionCoordinator(
            provider.GetRequiredService<ICommandResourceArbiter>(),
            journal: journal,
            journalFailurePolicy: failurePolicy);

        return new RuntimeFixture(provider, coordinator, journal);
    }

    private sealed class RuntimeFixture(
        ServiceProvider provider,
        WorkflowExecutionCoordinator coordinator,
        InMemoryWorkflowExecutionJournal journal) : IAsyncDisposable
    {
        public WorkflowExecutionCoordinator Coordinator { get; } = coordinator;
        public InMemoryWorkflowExecutionJournal Journal { get; } = journal;

        public async ValueTask DisposeAsync()
        {
            await Coordinator.DisposeAsync();
            await provider.DisposeAsync();
        }
    }

    private sealed class RecordingReconciler(
        WorkflowRecoveryDecision decision) : IWorkflowRecoveryReconciler
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);

        public ValueTask<WorkflowRecoveryDecision> ReconcileAsync(
            WorkflowRecoveryEvidence evidence,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _calls);
            return ValueTask.FromResult(decision);
        }
    }
}
