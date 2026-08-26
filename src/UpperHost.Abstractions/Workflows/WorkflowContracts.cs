namespace UpperHost.Abstractions.Workflows;

public enum WorkflowStepStatus
{
    Succeeded,
    Skipped,
    Failed,
    Cancelled
}

public sealed record WorkflowStepResult(WorkflowStepStatus Status, string? Message = null, Exception? Exception = null)
{
    public static WorkflowStepResult Success(string? message = null) => new(WorkflowStepStatus.Succeeded, message);
    public static WorkflowStepResult Failure(string message, Exception? exception = null) => new(WorkflowStepStatus.Failed, message, exception);
}

public sealed class WorkflowContext
{
    private readonly Dictionary<string, object?> _items = new(StringComparer.OrdinalIgnoreCase);

    public IDictionary<string, object?> Items => _items;

    public T? Get<T>(string key) => _items.TryGetValue(key, out var value) && value is T typed ? typed : default;
    public void Set<T>(string key, T value) => _items[key] = value;
}

public interface IWorkflowStep
{
    string Name { get; }
    Task<WorkflowStepResult> ExecuteAsync(WorkflowContext context, CancellationToken cancellationToken = default);
}
