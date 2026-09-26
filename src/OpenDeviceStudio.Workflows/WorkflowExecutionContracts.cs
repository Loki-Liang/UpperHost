using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using OpenDeviceStudio.Abstractions.Workflows;
using OpenDeviceStudio.Control.Scheduling;

namespace OpenDeviceStudio.Workflows;

public enum WorkflowExecutionStatus
{
    Created,
    Preparing,
    Running,
    PauseRequested,
    Paused,
    StopRequested,
    Stopping,
    AbortRequested,
    RecoveryRequired,
    Recovering,
    Completed,
    Failed,
    Stopped,
    Aborted
}

public enum WorkflowNodeStatus
{
    Succeeded,
    Failed,
    Stopped,
    Aborted,
    RecoveryRequired
}

public enum WorkflowParallelJoinPolicy
{
    FailFast,
    WaitAll
}

public enum WorkflowExecutionDataMergePolicy
{
    RejectConflicts,
    KeepExisting,
    Overwrite
}

public enum WorkflowResourceAccess
{
    SharedRead,
    Exclusive
}

public enum WorkflowJournalFailurePolicy
{
    ContinueExecution,
    FailExecution
}

public enum WorkflowRecoveryAction
{
    Reset,
    Restart,
    ResumeFromSafeCheckpoint,
    ManualIntervention
}

public sealed record WorkflowRecipeSnapshot(
    string RecipeId,
    string Version,
    string SchemaVersion,
    string CanonicalData,
    string Hash,
    DateTimeOffset FrozenAtUtc)
{
    public static WorkflowRecipeSnapshot Create(
        string recipeId,
        string version,
        string canonicalData,
        string schemaVersion = "1",
        DateTimeOffset? frozenAtUtc = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recipeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentNullException.ThrowIfNull(canonicalData);
        ArgumentException.ThrowIfNullOrWhiteSpace(schemaVersion);

        return new WorkflowRecipeSnapshot(
            recipeId.Trim(),
            version.Trim(),
            schemaVersion.Trim(),
            canonicalData,
            ComputeHash(canonicalData),
            frozenAtUtc ?? DateTimeOffset.UtcNow);
    }

    internal void ValidateIntegrity()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(RecipeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(Version);
        ArgumentException.ThrowIfNullOrWhiteSpace(SchemaVersion);
        ArgumentNullException.ThrowIfNull(CanonicalData);
        ArgumentException.ThrowIfNullOrWhiteSpace(Hash);

        var expected = ComputeHash(CanonicalData);
        if (!string.Equals(Hash, expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Workflow recipe snapshot hash does not match its canonical data.");
        }
    }

    private static string ComputeHash(string canonicalData) =>
        Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(canonicalData)))
            .ToLowerInvariant();
}

public sealed record WorkflowResourceRequirement(
    string Kind,
    string Value,
    WorkflowResourceAccess Access = WorkflowResourceAccess.Exclusive)
{
    internal CommandResourceClaim ToCommandClaim()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(Value);

        // Workflow resources intentionally use a logical key space that is disjoint from
        // the physical device/connection resources owned by #60. This lets an Automation
        // node hold a station/fixture lease and then invoke the #60 dispatcher without
        // recursively acquiring the same physical resource.
        var key = new CommandResourceKey(
            "workflow",
            $"{Kind.Trim().ToLowerInvariant()}:{Value.Trim()}");

        return new CommandResourceClaim(
            key,
            Access == WorkflowResourceAccess.SharedRead
                ? CommandResourceAccess.SharedRead
                : CommandResourceAccess.Exclusive);
    }
}

public sealed record WorkflowRetryPolicy(
    int MaxAttempts = 1,
    TimeSpan Delay = default)
{
    internal void Validate()
    {
        if (MaxAttempts <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxAttempts));
        if (Delay < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(Delay));
    }
}

public sealed record WorkflowStepPolicy
{
    public WorkflowStepPolicy(
        TimeSpan? Timeout = null,
        WorkflowRetryPolicy? Retry = null,
        bool SideEffecting = false,
        IReadOnlyList<WorkflowResourceRequirement>? Resources = null,
        Func<WorkflowExecutionContext, CancellationToken, ValueTask<bool>>? Precondition = null,
        Func<WorkflowExecutionContext, WorkflowStepResult, CancellationToken, ValueTask<bool>>? CompletionCondition = null,
        Func<WorkflowStepResult, bool>? RetryPredicate = null,
        Func<WorkflowExecutionContext, CancellationToken, Task<WorkflowStepResult>>? Compensation = null,
        IReadOnlyDictionary<string, string>? Diagnostics = null)
    {
        this.Timeout = Timeout;
        this.Retry = Retry;
        this.SideEffecting = SideEffecting;
        this.Resources = Resources?.ToArray();
        this.Precondition = Precondition;
        this.CompletionCondition = CompletionCondition;
        this.RetryPredicate = RetryPredicate;
        this.Compensation = Compensation;
        this.Diagnostics = Diagnostics is null
            ? null
            : new ReadOnlyDictionary<string, string>(
                new Dictionary<string, string>(
                    Diagnostics,
                    StringComparer.Ordinal));
    }

    public TimeSpan? Timeout { get; }
    public WorkflowRetryPolicy? Retry { get; }
    public bool SideEffecting { get; }
    public IReadOnlyList<WorkflowResourceRequirement>? Resources { get; }
    public Func<WorkflowExecutionContext, CancellationToken, ValueTask<bool>>? Precondition { get; }
    public Func<WorkflowExecutionContext, WorkflowStepResult, CancellationToken, ValueTask<bool>>? CompletionCondition { get; }
    public Func<WorkflowStepResult, bool>? RetryPredicate { get; }
    public Func<WorkflowExecutionContext, CancellationToken, Task<WorkflowStepResult>>? Compensation { get; }
    public IReadOnlyDictionary<string, string>? Diagnostics { get; }

    internal WorkflowRetryPolicy EffectiveRetry =>
        Retry ?? new WorkflowRetryPolicy();

    internal void Validate()
    {
        if (Timeout is { } timeout && timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(Timeout));

        EffectiveRetry.Validate();

        if (SideEffecting &&
            EffectiveRetry.MaxAttempts > 1 &&
            RetryPredicate is null)
        {
            throw new InvalidOperationException(
                "A side-effecting workflow action may retry only when an explicit RetryPredicate is supplied.");
        }

        if (Resources is not null)
        {
            foreach (var resource in Resources)
            {
                ArgumentNullException.ThrowIfNull(resource);
                ArgumentException.ThrowIfNullOrWhiteSpace(resource.Kind);
                ArgumentException.ThrowIfNullOrWhiteSpace(resource.Value);
            }
        }
    }
}

public sealed class WorkflowPhysicalOutcomeUnknownException : Exception
{
    public WorkflowPhysicalOutcomeUnknownException(string message)
        : base(message)
    {
    }

    public WorkflowPhysicalOutcomeUnknownException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed record WorkflowDataKey<T>
{
    public WorkflowDataKey(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name.Trim();
    }

    public string Name { get; }
}

public sealed class WorkflowExecutionData
{
    private sealed record Entry(Type Type, object? Value);

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _local = new(StringComparer.Ordinal);
    private readonly IReadOnlyDictionary<string, Entry>? _baseline;

    public WorkflowExecutionData()
    {
    }

    private WorkflowExecutionData(IReadOnlyDictionary<string, Entry> baseline)
    {
        _baseline = baseline;
    }

    public void Set<T>(WorkflowDataKey<T> key, T value)
    {
        ArgumentNullException.ThrowIfNull(key);

        lock (_gate)
        {
            if (TryGetEntryUnsafe(key.Name, out var existing) &&
                existing.Type != typeof(T))
            {
                throw new InvalidOperationException(
                    $"Workflow data key '{key.Name}' is already bound to '{existing.Type.FullName}'.");
            }

            _local[key.Name] = new Entry(typeof(T), value);
        }
    }

    public bool TryGet<T>(WorkflowDataKey<T> key, out T? value)
    {
        ArgumentNullException.ThrowIfNull(key);

        lock (_gate)
        {
            if (!TryGetEntryUnsafe(key.Name, out var entry))
            {
                value = default;
                return false;
            }

            if (entry.Type != typeof(T))
            {
                throw new InvalidOperationException(
                    $"Workflow data key '{key.Name}' contains '{entry.Type.FullName}', not '{typeof(T).FullName}'.");
            }

            value = (T?)entry.Value;
            return true;
        }
    }

    public T GetRequired<T>(WorkflowDataKey<T> key)
    {
        if (TryGet(key, out T? value))
            return value!;

        throw new KeyNotFoundException($"Workflow data key '{key.Name}' was not found.");
    }

    internal WorkflowExecutionData Fork()
    {
        lock (_gate)
            return new WorkflowExecutionData(SnapshotUnsafe());
    }

    internal void MergeFrom(
        WorkflowExecutionData branch,
        WorkflowExecutionDataMergePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(branch);

        var changes = branch.SnapshotLocal();
        lock (_gate)
        {
            foreach (var pair in changes.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
            {
                if (TryGetEntryUnsafe(pair.Key, out var existing))
                {
                    if (existing.Type != pair.Value.Type)
                    {
                        throw new InvalidOperationException(
                            $"Workflow data key '{pair.Key}' has conflicting types '{existing.Type.FullName}' and '{pair.Value.Type.FullName}'.");
                    }

                    switch (policy)
                    {
                        case WorkflowExecutionDataMergePolicy.KeepExisting:
                            continue;
                        case WorkflowExecutionDataMergePolicy.RejectConflicts:
                            throw new InvalidOperationException(
                                $"Parallel workflow branches produced conflicting output key '{pair.Key}'.");
                    }
                }

                _local[pair.Key] = pair.Value;
            }
        }
    }

    private Dictionary<string, Entry> SnapshotLocal()
    {
        lock (_gate)
            return new Dictionary<string, Entry>(_local, StringComparer.Ordinal);
    }

    private Dictionary<string, Entry> SnapshotUnsafe()
    {
        var result = _baseline is null
            ? new Dictionary<string, Entry>(StringComparer.Ordinal)
            : new Dictionary<string, Entry>(_baseline, StringComparer.Ordinal);

        foreach (var pair in _local)
            result[pair.Key] = pair.Value;

        return result;
    }

    private bool TryGetEntryUnsafe(string name, out Entry entry)
    {
        if (_local.TryGetValue(name, out entry!))
            return true;

        return _baseline is not null && _baseline.TryGetValue(name, out entry!);
    }
}

public sealed class WorkflowExecutionContext
{
    private readonly ConcurrentQueue<string> _commandExecutionIds;

    internal WorkflowExecutionContext(
        string executionId,
        string nodeId,
        WorkflowRecipeSnapshot recipeSnapshot,
        WorkflowExecutionData data,
        ConcurrentQueue<string> commandExecutionIds)
    {
        ExecutionId = executionId;
        NodeId = nodeId;
        RecipeSnapshot = recipeSnapshot;
        Data = data;
        _commandExecutionIds = commandExecutionIds;
    }

    public string ExecutionId { get; }
    public string NodeId { get; }
    public WorkflowRecipeSnapshot RecipeSnapshot { get; }
    public WorkflowExecutionData Data { get; }

    public void RecordCommandExecutionId(string commandExecutionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandExecutionId);
        _commandExecutionIds.Enqueue(commandExecutionId);
    }

    internal WorkflowExecutionContext ForNode(
        string nodeId,
        WorkflowExecutionData? data = null) =>
        new(
            ExecutionId,
            nodeId,
            RecipeSnapshot,
            data ?? Data,
            _commandExecutionIds);
}

public abstract record WorkflowNode
{
    protected WorkflowNode(string nodeId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        NodeId = nodeId.Trim();
    }

    public string NodeId { get; }
}

public sealed record WorkflowActionNode : WorkflowNode
{
    public WorkflowActionNode(
        string nodeId,
        Func<WorkflowExecutionContext, CancellationToken, Task<WorkflowStepResult>> execute,
        WorkflowStepPolicy? policy = null,
        string? displayName = null)
        : base(nodeId)
    {
        Execute = execute ?? throw new ArgumentNullException(nameof(execute));
        Policy = policy ?? new WorkflowStepPolicy();
        DisplayName = string.IsNullOrWhiteSpace(displayName) ? NodeId : displayName.Trim();
    }

    public Func<WorkflowExecutionContext, CancellationToken, Task<WorkflowStepResult>> Execute { get; }
    public WorkflowStepPolicy Policy { get; }
    public string DisplayName { get; }
}

public sealed record WorkflowSequenceNode : WorkflowNode
{
    private readonly WorkflowNode[] _children;

    public WorkflowSequenceNode(string nodeId, IEnumerable<WorkflowNode> children)
        : base(nodeId)
    {
        ArgumentNullException.ThrowIfNull(children);
        _children = children.ToArray();
        Children = Array.AsReadOnly(_children);
    }

    public IReadOnlyList<WorkflowNode> Children { get; }
}

public sealed record WorkflowParallelNode : WorkflowNode
{
    private readonly WorkflowNode[] _children;

    public WorkflowParallelNode(
        string nodeId,
        IEnumerable<WorkflowNode> children,
        int maxConcurrency,
        WorkflowParallelJoinPolicy joinPolicy = WorkflowParallelJoinPolicy.FailFast,
        WorkflowExecutionDataMergePolicy mergePolicy = WorkflowExecutionDataMergePolicy.RejectConflicts)
        : base(nodeId)
    {
        ArgumentNullException.ThrowIfNull(children);
        if (maxConcurrency <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxConcurrency));

        _children = children.ToArray();
        Children = Array.AsReadOnly(_children);
        MaxConcurrency = maxConcurrency;
        JoinPolicy = joinPolicy;
        MergePolicy = mergePolicy;
    }

    public IReadOnlyList<WorkflowNode> Children { get; }
    public int MaxConcurrency { get; }
    public WorkflowParallelJoinPolicy JoinPolicy { get; }
    public WorkflowExecutionDataMergePolicy MergePolicy { get; }
}

public sealed record WorkflowSafeCheckpointNode : WorkflowNode
{
    public WorkflowSafeCheckpointNode(string nodeId)
        : base(nodeId)
    {
    }
}

public sealed record WorkflowExecutionDefinition(
    string WorkflowId,
    string Version,
    WorkflowNode Root)
{
    public WorkflowExecutionDefinition ValidateIdentity()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(WorkflowId);
        ArgumentException.ThrowIfNullOrWhiteSpace(Version);
        ArgumentNullException.ThrowIfNull(Root);
        return this;
    }
}

public sealed record WorkflowExecutionRequest(
    WorkflowExecutionDefinition Definition,
    WorkflowRecipeSnapshot RecipeSnapshot,
    string StationId,
    string Mode = "Auto",
    string? Operator = null,
    IWorkflowStationStateAuthority? StationStateAuthority = null);

public sealed record WorkflowNodeOutcome(
    string NodeId,
    WorkflowNodeStatus Status,
    int Attempts,
    TimeSpan Duration,
    string? Message = null,
    Exception? Exception = null);

public sealed record WorkflowExecutionResult(
    string ExecutionId,
    string WorkflowId,
    string WorkflowVersion,
    WorkflowExecutionStatus Status,
    WorkflowRecipeSnapshot RecipeSnapshot,
    string StationId,
    string Mode,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset EndedAtUtc,
    IReadOnlyList<WorkflowNodeOutcome> Nodes,
    IReadOnlyList<string> CommandExecutionIds,
    string? LastSafeCheckpoint,
    int JournalFailureCount,
    string? FailureReason = null);

public interface IWorkflowStationStateAuthority
{
    ValueTask OnExecutionTransitionAsync(
        string executionId,
        WorkflowExecutionStatus from,
        WorkflowExecutionStatus to,
        string? reason,
        CancellationToken cancellationToken = default);
}

public sealed record WorkflowRecoveryEvidence(
    string ExecutionId,
    string WorkflowId,
    string WorkflowVersion,
    WorkflowRecipeSnapshot RecipeSnapshot,
    string StationId,
    string? LastSafeCheckpoint,
    IReadOnlyList<string> CommandExecutionIds,
    string? FailureReason);

public sealed record WorkflowRecoveryDecision(
    WorkflowRecoveryAction Action,
    bool Reconciled,
    string Reason);

public interface IWorkflowRecoveryReconciler
{
    ValueTask<WorkflowRecoveryDecision> ReconcileAsync(
        WorkflowRecoveryEvidence evidence,
        CancellationToken cancellationToken = default);
}
