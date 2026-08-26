using UpperHost.Abstractions.Workflows;

namespace UpperHost.Workflows;

public sealed record WorkflowDefinition(string Name, IReadOnlyList<IWorkflowStep> Steps)
{
    public static WorkflowDefinition Create(string name, params IWorkflowStep[] steps) => new(name, steps);
}

public sealed record WorkflowStepOutcome(string Step, WorkflowStepResult Result, TimeSpan Duration);

public sealed record WorkflowRunResult(
    string Workflow,
    WorkflowStepStatus Status,
    IReadOnlyList<WorkflowStepOutcome> Steps,
    TimeSpan Duration);

public sealed class WorkflowRunner
{
    public async Task<WorkflowRunResult> RunAsync(
        WorkflowDefinition definition,
        WorkflowContext? context = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        context ??= new WorkflowContext();

        var started = System.Diagnostics.Stopwatch.StartNew();
        var outcomes = new List<WorkflowStepOutcome>(definition.Steps.Count);

        foreach (var step in definition.Steps)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var stepTimer = System.Diagnostics.Stopwatch.StartNew();
            WorkflowStepResult result;

            try
            {
                result = await step.ExecuteAsync(context, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                result = new WorkflowStepResult(WorkflowStepStatus.Cancelled, "Workflow cancelled.");
            }
            catch (Exception ex)
            {
                result = WorkflowStepResult.Failure($"Step '{step.Name}' threw an exception.", ex);
            }

            stepTimer.Stop();
            outcomes.Add(new WorkflowStepOutcome(step.Name, result, stepTimer.Elapsed));

            if (result.Status is WorkflowStepStatus.Failed or WorkflowStepStatus.Cancelled)
            {
                started.Stop();
                return new WorkflowRunResult(definition.Name, result.Status, outcomes, started.Elapsed);
            }
        }

        started.Stop();
        return new WorkflowRunResult(definition.Name, WorkflowStepStatus.Succeeded, outcomes, started.Elapsed);
    }
}
