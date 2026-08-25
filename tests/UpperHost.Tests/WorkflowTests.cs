using UpperHost.Abstractions.Workflows;
using UpperHost.Workflows;

namespace UpperHost.Tests;

public sealed class WorkflowTests
{
    [Fact]
    public async Task Workflow_stops_on_failure()
    {
        var runner = new WorkflowRunner();
        var definition = WorkflowDefinition.Create(
            "test",
            new DelegateStep("one", _ => WorkflowStepResult.Success()),
            new DelegateStep("two", _ => WorkflowStepResult.Failure("boom")),
            new DelegateStep("three", _ => WorkflowStepResult.Success()));

        var result = await runner.RunAsync(definition);

        Assert.Equal(WorkflowStepStatus.Failed, result.Status);
        Assert.Equal(2, result.Steps.Count);
    }

    private sealed class DelegateStep(string name, Func<WorkflowContext, WorkflowStepResult> action) : IWorkflowStep
    {
        public string Name { get; } = name;
        public Task<WorkflowStepResult> ExecuteAsync(WorkflowContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult(action(context));
    }
}
