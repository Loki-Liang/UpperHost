namespace OpenDeviceStudio.Workflows;

public sealed record WorkflowJournalEvent(
    long Sequence,
    string ExecutionId,
    DateTimeOffset TimestampUtc,
    string EventType,
    WorkflowExecutionStatus ExecutionStatus,
    string? NodeId = null,
    string? Message = null,
    string? RecipeHash = null,
    string? CommandExecutionId = null);

public interface IWorkflowExecutionJournal
{
    ValueTask AppendAsync(
        WorkflowJournalEvent journalEvent,
        CancellationToken cancellationToken = default);
}

public sealed class NullWorkflowExecutionJournal : IWorkflowExecutionJournal
{
    public static NullWorkflowExecutionJournal Instance { get; } = new();

    private NullWorkflowExecutionJournal()
    {
    }

    public ValueTask AppendAsync(
        WorkflowJournalEvent journalEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(journalEvent);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Bounded deterministic journal useful for tests, simulators, and short-lived hosts.
/// Production persistence adapters can write directly or apply their own bounded backpressure,
/// but must never hide an unbounded queue behind this contract.
/// </summary>
public sealed class InMemoryWorkflowExecutionJournal : IWorkflowExecutionJournal
{
    private readonly object _gate = new();
    private readonly Queue<WorkflowJournalEvent> _events;
    private readonly int _capacity;

    public InMemoryWorkflowExecutionJournal(int capacity = 1024)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity));

        _capacity = capacity;
        _events = new Queue<WorkflowJournalEvent>(capacity);
    }

    public int Capacity => _capacity;

    public IReadOnlyList<WorkflowJournalEvent> Events
    {
        get
        {
            lock (_gate)
                return _events.ToArray();
        }
    }

    public ValueTask AppendAsync(
        WorkflowJournalEvent journalEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(journalEvent);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (_events.Count >= _capacity)
            {
                throw new InvalidOperationException(
                    $"Workflow journal capacity {_capacity} was reached.");
            }

            _events.Enqueue(journalEvent);
        }

        return ValueTask.CompletedTask;
    }
}
