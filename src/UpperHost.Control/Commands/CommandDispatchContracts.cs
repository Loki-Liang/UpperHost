namespace UpperHost.Control.Commands;

public enum CommandAdmissionMode
{
    Wait,
    Reject
}

public enum CommandAccessMode
{
    ReadOnly,
    Mutating
}

public enum CommandIdempotency
{
    Idempotent,
    NonIdempotent
}

public enum CommandHazardClass
{
    Standard,
    Motion,
    Hazardous
}

public enum CommandSideEffectState
{
    BeforeSideEffect,
    SideEffectMayHaveStarted,
    Acknowledged,
    Completed
}

public enum CommandResourceKind
{
    Connection,
    Device,
    Axis,
    Fixture,
    Instrument,
    Custom
}

public readonly record struct CommandResourceKey : IComparable<CommandResourceKey>
{
    public CommandResourceKey(CommandResourceKind kind, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Kind = kind;
        Value = value;
    }

    public CommandResourceKind Kind { get; }

    public string Value { get; }

    public int CompareTo(CommandResourceKey other)
    {
        var kindComparison = Kind.CompareTo(other.Kind);
        return kindComparison != 0
            ? kindComparison
            : StringComparer.Ordinal.Compare(Value, other.Value);
    }

    public override string ToString() => $"{Kind}:{Value}";
}

public sealed class CommandResourceSet : IReadOnlyList<CommandResourceKey>
{
    private static readonly CommandResourceKey[] NoResources = [];
    private readonly CommandResourceKey[] _items;

    public static CommandResourceSet Empty { get; } = new(NoResources);

    public CommandResourceSet(IEnumerable<CommandResourceKey> resources)
    {
        ArgumentNullException.ThrowIfNull(resources);
        _items = resources
            .Distinct()
            .Order()
            .ToArray();
    }

    public int Count => _items.Length;

    public CommandResourceKey this[int index] => _items[index];

    public IEnumerator<CommandResourceKey> GetEnumerator() =>
        ((IEnumerable<CommandResourceKey>)_items).GetEnumerator();

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() =>
        _items.GetEnumerator();
}

public sealed record CommandSafetyPolicy(
    CommandAccessMode AccessMode,
    CommandIdempotency Idempotency,
    CommandHazardClass HazardClass,
    bool RetryAllowed = false)
{
    public static CommandSafetyPolicy SafeRead { get; } =
        new(CommandAccessMode.ReadOnly, CommandIdempotency.Idempotent, CommandHazardClass.Standard);

    public static CommandSafetyPolicy MutatingDefault { get; } =
        new(CommandAccessMode.Mutating, CommandIdempotency.NonIdempotent, CommandHazardClass.Standard);

    public bool CanAutomaticallyRetry =>
        RetryAllowed
        && Idempotency == CommandIdempotency.Idempotent
        && HazardClass == CommandHazardClass.Standard;
}

public sealed record CommandDispatcherOptions
{
    public int Capacity { get; init; } = 256;

    public int PerResourceCapacity { get; init; } = 64;

    public int MaxConcurrentExecutions { get; init; } = 4;

    public int HighPriorityBurstLimit { get; init; } = 8;

    public TimeSpan DefaultQueueWaitTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public CommandAdmissionMode AdmissionMode { get; init; } = CommandAdmissionMode.Wait;

    public void Validate()
    {
        if (Capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(Capacity), "Dispatcher capacity must be greater than zero.");
        if (PerResourceCapacity <= 0 || PerResourceCapacity > Capacity)
            throw new ArgumentOutOfRangeException(nameof(PerResourceCapacity), "Per-resource capacity must be between 1 and the global capacity.");
        if (MaxConcurrentExecutions <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxConcurrentExecutions), "Maximum concurrent executions must be greater than zero.");
        if (HighPriorityBurstLimit <= 0)
            throw new ArgumentOutOfRangeException(nameof(HighPriorityBurstLimit), "High-priority burst limit must be greater than zero.");
        if (DefaultQueueWaitTimeout <= TimeSpan.Zero && DefaultQueueWaitTimeout != Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(DefaultQueueWaitTimeout), "Queue wait timeout must be positive or infinite.");
    }
}
