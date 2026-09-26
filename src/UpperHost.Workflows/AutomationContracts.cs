using System.Collections.ObjectModel;
using UpperHost.Control.Commands;
using UpperHost.Control.Scheduling;

namespace UpperHost.Workflows;

public enum AutomationMode
{
    Auto,
    Manual,
    Engineering
}

public enum AutomationStationState
{
    Idle,
    Preparing,
    Ready,
    Running,
    Paused,
    Stopping,
    Faulted,
    Recovering,
    Completed
}

public enum AutomationExecutionState
{
    Created,
    Preparing,
    Running,
    PauseRequested,
    Paused,
    StopRequested,
    Stopping,
    AbortRequested,
    Aborting,
    RecoveryRequired,
    Recovering,
    Completed,
    Failed,
    Aborted,
    Stopped
}

public enum AutomationStepStatus
{
    Succeeded,
    Skipped,
    Rejected,
    TimedOutBeforeSideEffect,
    CancelledBeforeSideEffect,
    Failed,
    UnknownPhysicalOutcome,
    RecoveryRequired,
    Stopped
}

public enum AutomationJoinMode
{
    WaitAll,
    FailFast
}

public enum AutomationCheckpointKind
{
    SafePause,
    SafeRecovery
}

public sealed record AutomationDataKey<T>(string NodeId, string Name)
{
    internal string Identity => $"{NodeId}:{Name}:{typeof(T).AssemblyQualifiedName}";
}

public sealed class AutomationExecutionData
{
    private sealed record Entry(Type Type, object? Value);

    private readonly AutomationExecutionData? _parent;
    private readonly Dictionary<string, Entry> _values = new(StringComparer.Ordinal);

    internal AutomationExecutionData(AutomationExecutionData? parent = null) => _parent = parent;

    public bool TryGet<T>(AutomationDataKey<T> key, out T value)
    {
        ArgumentNullException.ThrowIfNull(key);

        if (TryGetEntry(key.Identity, out var entry))
        {
            if (entry.Type != typeof(T))
                throw new InvalidOperationException($"Automation output '{key.NodeId}/{key.Name}' has type '{entry.Type}', not '{typeof(T)}'.");

            value = entry.Value is null ? default! : (T)entry.Value;
            return true;
        }

        value = default!;
        return false;
    }

    public T GetRequired<T>(AutomationDataKey<T> key) =>
        TryGet(key, out T value)
            ? value
            : throw new KeyNotFoundException($"Automation output '{key.NodeId}/{key.Name}' was not produced.");

    public IReadOnlyDictionary<string, object?> Snapshot()
    {
        var result = _parent is null
            ? new Dictionary<string, object?>(StringComparer.Ordinal)
            : new Dictionary<string, object?>(_parent.Snapshot(), StringComparer.Ordinal);

        foreach (var pair in _values)
            result[pair.Key] = pair.Value.Value;

        return new ReadOnlyDictionary<string, object?>(result);
    }

    internal AutomationExecutionData Fork() => new(this);

    internal void Set<T>(AutomationDataKey<T> key, T value)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (ContainsIdentity(key.Identity))
            throw new InvalidOperationException($"Automation output '{key.NodeId}/{key.Name}' already exists.");

        _values.Add(key.Identity, new Entry(typeof(T), value));
    }

    internal void MergeFrom(AutomationExecutionData branch)
    {
        ArgumentNullException.ThrowIfNull(branch);

        foreach (var pair in branch._values.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            if (ContainsIdentity(pair.Key))
                throw new InvalidOperationException($"Parallel automation output conflict for '{pair.Key}'.");

            _values.Add(pair.Key, pair.Value);
        }
    }

    private bool ContainsIdentity(string identity) =>
        _values.ContainsKey(identity) || (_parent?.ContainsIdentity(identity) ?? false);

    private bool TryGetEntry(string identity, out Entry entry)
    {
        if (_values.TryGetValue(identity, out entry!))
            return true;

        if (_parent is not null)
            return _parent.TryGetEntry(identity, out entry!);

        entry = null!;
        return false;
    }
}

public sealed record AutomationRetryPolicy(
    int MaxAttempts = 1,
    TimeSpan Delay = default,
    IReadOnlySet<AutomationStepStatus>? RetryableStatuses = null)
{
    public static AutomationRetryPolicy None { get; } = new();

    internal void Validate()
    {
        if (MaxAttempts <= 0)
            throw new InvalidOperationException("Automation retry MaxAttempts must be greater than zero.");
        if (Delay < TimeSpan.Zero)
            throw new InvalidOperationException("Automation retry Delay cannot be negative.");

        if (RetryableStatuses is null)
            return;

        if (RetryableStatuses.Contains(AutomationStepStatus.UnknownPhysicalOutcome) ||
            RetryableStatuses.Contains(AutomationStepStatus.RecoveryRequired) ||
            RetryableStatuses.Contains(AutomationStepStatus.CancelledBeforeSideEffect) ||
            RetryableStatuses.Contains(AutomationStepStatus.Stopped))
        {
            throw new InvalidOperationException(
                "Unknown/recovery/cancelled/stopped automation outcomes cannot be configured for automatic retry.");
        }
    }

    internal bool ShouldRetry(AutomationStepStatus status) =>
        MaxAttempts > 1 &&
        RetryableStatuses is not null &&
        RetryableStatuses.Contains(status);
}

public sealed record AutomationStepPolicy(
    TimeSpan? Timeout = null,
    IReadOnlyList<CommandResourceClaim>? Resources = null,
    bool PauseBoundaryAfter = false,
    bool CancelOnStop = false,
    AutomationRetryPolicy? Retry = null)
{
    internal void Validate()
    {
        if (Timeout is { } timeout && timeout <= TimeSpan.Zero)
            throw new InvalidOperationException("Automation step timeout must be greater than zero.");

        (Retry ?? AutomationRetryPolicy.None).Validate();

        if (Resources is null)
            return;

        foreach (var claim in Resources)
        {
            if (string.IsNullOrWhiteSpace(claim.Resource.Category) ||
                string.IsNullOrWhiteSpace(claim.Resource.Value))
                throw new InvalidOperationException("Automation resource category/value cannot be empty.");
        }
    }

    internal IReadOnlyList<CommandResourceClaim> NormalizeResources()
    {
        if (Resources is null || Resources.Count == 0)
            return Array.Empty<CommandResourceClaim>();

        var claims = new Dictionary<CommandResourceKey, CommandResourceAccess>();
        foreach (var claim in Resources)
        {
            if (claims.TryGetValue(claim.Resource, out var existing) &&
                existing == CommandResourceAccess.Exclusive)
                continue;

            claims[claim.Resource] = claim.Access;
        }

        return claims
            .OrderBy(static pair => pair.Key)
            .Select(static pair => new CommandResourceClaim(pair.Key, pair.Value))
            .ToArray();
    }
}

public sealed record AutomationStepResult(
    AutomationStepStatus Status,
    string? Message = null,
    Exception? Exception = null,
    string? CommandExecutionId = null)
{
    public bool IsSuccess => Status is AutomationStepStatus.Succeeded or AutomationStepStatus.Skipped;

    public static AutomationStepResult Success(string? message = null) =>
        new(AutomationStepStatus.Succeeded, message);

    public static AutomationStepResult Skipped(string? message = null) =>
        new(AutomationStepStatus.Skipped, message);

    public static AutomationStepResult Failure(string message, Exception? exception = null) =>
        new(AutomationStepStatus.Failed, message, exception);

    public static AutomationStepResult FromCommand<TResult>(CommandExecutionResult<TResult> result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var status = result.Status switch
        {
            CommandExecutionStatus.Succeeded => AutomationStepStatus.Succeeded,
            CommandExecutionStatus.Rejected => AutomationStepStatus.Rejected,
            CommandExecutionStatus.TimedOut when result.Milestone >= CommandExecutionMilestone.SideEffectMayHaveStarted =>
                AutomationStepStatus.UnknownPhysicalOutcome,
            CommandExecutionStatus.TimedOut => AutomationStepStatus.TimedOutBeforeSideEffect,
            CommandExecutionStatus.Cancelled when result.Milestone >= CommandExecutionMilestone.SideEffectMayHaveStarted =>
                AutomationStepStatus.UnknownPhysicalOutcome,
            CommandExecutionStatus.Cancelled => AutomationStepStatus.CancelledBeforeSideEffect,
            CommandExecutionStatus.UnknownOutcome => AutomationStepStatus.UnknownPhysicalOutcome,
            _ => AutomationStepStatus.Failed
        };

        return new AutomationStepResult(
            status,
            result.Message,
            result.Exception,
            result.ExecutionId);
    }
}

public sealed class AutomationStepContext
{
    private readonly Dictionary<CommandResourceKey, CommandResourceAccess> _heldResources;
    private readonly List<string> _commandExecutionIds = [];

    internal AutomationStepContext(
        string executionId,
        string nodeId,
        AutomationMode mode,
        AutomationRecipeSnapshot recipe,
        AutomationExecutionData data,
        IReadOnlyList<CommandResourceClaim> heldResources)
    {
        ExecutionId = executionId;
        NodeId = nodeId;
        Mode = mode;
        Recipe = recipe;
        Data = data;
        _heldResources = heldResources.ToDictionary(static claim => claim.Resource, static claim => claim.Access);
    }

    public string ExecutionId { get; }
    public string NodeId { get; }
    public AutomationMode Mode { get; }
    public AutomationRecipeSnapshot Recipe { get; }
    public AutomationExecutionData Data { get; }

    public void SetOutput<T>(string name, T value)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Automation output name cannot be empty.", nameof(name));

        Data.Set(new AutomationDataKey<T>(NodeId, name), value);
    }

    public async Task<CommandExecutionResult<TResult>> DispatchAsync<TCommand, TResult>(
        BoundedCommandDispatcher<TCommand, TResult> dispatcher,
        TCommand command,
        CommandDispatchOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        options ??= new CommandDispatchOptions();

        var requestedClaims = NormalizeDispatchClaims(options);
        foreach (var claim in requestedClaims)
        {
            if (!_heldResources.TryGetValue(claim.Resource, out var held) ||
                (claim.Access == CommandResourceAccess.Exclusive && held != CommandResourceAccess.Exclusive))
            {
                throw new InvalidOperationException(
                    $"Command resource '{claim.Resource}' was not declared by automation step '{NodeId}'. " +
                    "Declare the complete physical resource set before executing the step.");
            }
        }

        var result = await dispatcher.EnqueueAsync(
            command,
            options with
            {
                Resources = null,
                ResourceClaims = null
            },
            cancellationToken).ConfigureAwait(false);

        _commandExecutionIds.Add(result.ExecutionId);
        return result;
    }

    internal IReadOnlyList<string> CommandExecutionIds => _commandExecutionIds;

    private static IReadOnlyList<CommandResourceClaim> NormalizeDispatchClaims(CommandDispatchOptions options)
    {
        var claims = new Dictionary<CommandResourceKey, CommandResourceAccess>();

        if (options.Resources is not null)
        {
            foreach (var resource in options.Resources)
                claims[resource] = CommandResourceAccess.Exclusive;
        }

        if (options.ResourceClaims is not null)
        {
            foreach (var claim in options.ResourceClaims)
            {
                if (claims.TryGetValue(claim.Resource, out var existing) &&
                    existing == CommandResourceAccess.Exclusive)
                    continue;

                claims[claim.Resource] = claim.Access;
            }
        }

        return claims
            .OrderBy(static pair => pair.Key)
            .Select(static pair => new CommandResourceClaim(pair.Key, pair.Value))
            .ToArray();
    }
}

public sealed class AutomationStepDescriptor
{
    public AutomationStepDescriptor(
        Func<AutomationStepContext, CancellationToken, Task<AutomationStepResult>> execute,
        AutomationStepPolicy? policy = null,
        Func<AutomationStepContext, CancellationToken, ValueTask<bool>>? precondition = null,
        Func<AutomationStepContext, CancellationToken, Task<AutomationStepResult>>? compensation = null,
        IReadOnlyList<string>? requiredCapabilities = null)
    {
        Execute = execute ?? throw new ArgumentNullException(nameof(execute));
        Policy = policy ?? new AutomationStepPolicy();
        Precondition = precondition;
        Compensation = compensation;
        RequiredCapabilities = requiredCapabilities?.ToArray() ?? Array.Empty<string>();
    }

    public Func<AutomationStepContext, CancellationToken, Task<AutomationStepResult>> Execute { get; }
    public AutomationStepPolicy Policy { get; }
    public Func<AutomationStepContext, CancellationToken, ValueTask<bool>>? Precondition { get; }
    public Func<AutomationStepContext, CancellationToken, Task<AutomationStepResult>>? Compensation { get; }
    public IReadOnlyList<string> RequiredCapabilities { get; }
}

public interface IAutomationCapabilityResolver
{
    bool IsAvailable(string capability);
}
