using Microsoft.Extensions.DependencyInjection;
using OpenDeviceStudio.Control.Scheduling;
using OpenDeviceStudio.Abstractions.Workflows;
using OpenDeviceStudio.Workflows;

namespace OpenDeviceStudio.Tests;

public sealed class WorkflowExecutionPlanTests
{
    [Fact]
    public void Duplicate_node_ids_fail_before_execution()
    {
        var definition = new WorkflowExecutionDefinition(
            "inspection",
            "1",
            new WorkflowSequenceNode(
                "root",
                [
                    Action("duplicate"),
                    Action("duplicate")
                ]));

        var exception = Assert.Throws<InvalidOperationException>(
            () => WorkflowPlanCompiler.Compile(definition));

        Assert.Contains("Duplicate workflow node id", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Safe_checkpoint_inside_parallel_is_rejected()
    {
        var definition = new WorkflowExecutionDefinition(
            "inspection",
            "1",
            new WorkflowParallelNode(
                "parallel",
                [
                    Action("branch"),
                    new WorkflowSafeCheckpointNode("unsafe-checkpoint")
                ],
                maxConcurrency: 2));

        var exception = Assert.Throws<InvalidOperationException>(
            () => WorkflowPlanCompiler.Compile(definition));

        Assert.Contains("cannot be placed inside a Parallel", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Side_effecting_retry_requires_explicit_eligibility()
    {
        var definition = new WorkflowExecutionDefinition(
            "inspection",
            "1",
            new WorkflowActionNode(
                "move",
                static (_, _) => Task.FromResult(WorkflowStepResult.Success()),
                new WorkflowStepPolicy(
                    Retry: new WorkflowRetryPolicy(MaxAttempts: 2),
                    SideEffecting: true)));

        var exception = Assert.Throws<InvalidOperationException>(
            () => WorkflowPlanCompiler.Compile(definition));

        Assert.Contains("explicit RetryPredicate", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Plan_freezes_child_structure_from_mutable_source_list()
    {
        var source = new List<WorkflowNode>
        {
            Action("first")
        };

        var sequence = new WorkflowSequenceNode("root", source);
        var plan = WorkflowPlanCompiler.Compile(
            new WorkflowExecutionDefinition("inspection", "1", sequence));

        source.Add(Action("late"));

        Assert.Single(((WorkflowSequenceNode)plan.Root).Children);
        Assert.False(plan.Nodes.ContainsKey("late"));
    }

    [Fact]
    public void Recipe_snapshot_hash_is_stable_and_source_independent()
    {
        const string canonical = """{"speed":12.5,"mode":"auto"}""";
        var frozenAt = new DateTimeOffset(2026, 9, 27, 0, 0, 0, TimeSpan.Zero);

        var first = WorkflowRecipeSnapshot.Create(
            "recipe-a",
            "7",
            canonical,
            frozenAtUtc: frozenAt);
        var second = WorkflowRecipeSnapshot.Create(
            "recipe-a",
            "7",
            canonical,
            frozenAtUtc: frozenAt);

        Assert.Equal(first.Hash, second.Hash);
        Assert.Equal(canonical, first.CanonicalData);
        Assert.Equal(frozenAt, first.FrozenAtUtc);
    }

    [Fact]
    public void Parallel_data_merge_rejects_conflicting_outputs()
    {
        var key = new WorkflowDataKey<int>("result");
        var parent = new WorkflowExecutionData();
        var first = parent.Fork();
        var second = parent.Fork();

        first.Set(key, 1);
        second.Set(key, 2);

        parent.MergeFrom(first, WorkflowExecutionDataMergePolicy.RejectConflicts);

        var exception = Assert.Throws<InvalidOperationException>(
            () => parent.MergeFrom(second, WorkflowExecutionDataMergePolicy.RejectConflicts));

        Assert.Contains("conflicting output key", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Typed_execution_data_fails_fast_on_type_mismatch()
    {
        var integerKey = new WorkflowDataKey<int>("value");
        var textKey = new WorkflowDataKey<string>("value");
        var data = new WorkflowExecutionData();

        data.Set(integerKey, 42);

        Assert.Throws<InvalidOperationException>(
            () => data.Set(textKey, "forty-two"));
    }

    [Fact]
    public void Recipe_snapshot_integrity_is_checked_before_execution()
    {
        var frozen = WorkflowRecipeSnapshot.Create(
            "recipe-a",
            "7",
            """{"speed":12.5}""") with
        {
            Hash = "forged"
        };

        var definition = new WorkflowExecutionDefinition(
            "inspection",
            "1",
            Action("first"));

        var services = new ServiceCollection();
        services.AddOpenDeviceStudioControlResourceArbiter();
        using var provider = services.BuildServiceProvider();
        var coordinator = new WorkflowExecutionCoordinator(
            provider.GetRequiredService<ICommandResourceArbiter>());

        Assert.Throws<InvalidOperationException>(
            () => coordinator.Start(
                new WorkflowExecutionRequest(
                    definition,
                    frozen,
                    "station-1")));
    }

    private static WorkflowActionNode Action(string id) =>
        new(
            id,
            static (_, _) => Task.FromResult(WorkflowStepResult.Success()));
}
