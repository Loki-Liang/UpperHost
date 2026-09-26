namespace UpperHost.Dataflow;

public enum StreamBranchDelivery
{
    Required,
    Optional
}

public enum StreamOverflowPolicy
{
    Wait,
    Fail,
    DropOldest,
    DropNewest,
    DropWrite,
    Latest
}

public enum StreamBranchFailurePolicy
{
    FaultRouter,
    Isolate
}

public enum StreamOrdering
{
    PreserveRouterSequence
}

public enum StreamShutdownPolicy
{
    Drain,
    Cancel
}

public enum StreamRouterState
{
    Created,
    Running,
    Completing,
    Completed,
    Faulted,
    Disposed
}

public enum StreamBranchState
{
    Created,
    Running,
    Completing,
    Completed,
    Faulted,
    Cancelled
}

public enum StreamBranchPublishStatus
{
    Accepted,
    Dropped,
    Rejected,
    Faulted,
    Cancelled,
    NotAttempted
}

public sealed record StreamBranchOptions(
    string BranchId,
    string Name,
    int Capacity,
    StreamBranchDelivery Delivery,
    StreamOverflowPolicy Overflow,
    StreamBranchFailurePolicy FailurePolicy,
    StreamOrdering Ordering = StreamOrdering.PreserveRouterSequence,
    StreamShutdownPolicy ShutdownPolicy = StreamShutdownPolicy.Drain)
{
    public static StreamBranchOptions Required(
        string branchId,
        string name,
        int capacity = 256,
        StreamOverflowPolicy overflow = StreamOverflowPolicy.Wait,
        StreamShutdownPolicy shutdownPolicy = StreamShutdownPolicy.Drain) =>
        new(
            branchId,
            name,
            capacity,
            StreamBranchDelivery.Required,
            overflow,
            StreamBranchFailurePolicy.FaultRouter,
            StreamOrdering.PreserveRouterSequence,
            shutdownPolicy);

    public static StreamBranchOptions Optional(
        string branchId,
        string name,
        int capacity = 64,
        StreamOverflowPolicy overflow = StreamOverflowPolicy.DropOldest,
        StreamBranchFailurePolicy failurePolicy = StreamBranchFailurePolicy.Isolate,
        StreamShutdownPolicy shutdownPolicy = StreamShutdownPolicy.Cancel) =>
        new(
            branchId,
            name,
            capacity,
            StreamBranchDelivery.Optional,
            overflow,
            failurePolicy,
            StreamOrdering.PreserveRouterSequence,
            shutdownPolicy);
}

public readonly record struct StreamBranchPublishResult(
    string BranchId,
    StreamBranchDelivery Delivery,
    StreamBranchPublishStatus Status,
    string? Reason = null)
{
    public bool Accepted => Status == StreamBranchPublishStatus.Accepted;
}

public sealed record StreamPublishResult(
    long Sequence,
    StreamRouterState RouterState,
    IReadOnlyList<StreamBranchPublishResult> Branches)
{
    public bool HasRequiredFailure => Branches.Any(static branch =>
        branch.Delivery == StreamBranchDelivery.Required &&
        branch.Status is StreamBranchPublishStatus.Dropped or
            StreamBranchPublishStatus.Rejected or
            StreamBranchPublishStatus.Faulted);

    public bool RequiresStop => RouterState != StreamRouterState.Running || HasRequiredFailure;

    public bool IsSuccess =>
        RouterState == StreamRouterState.Running &&
        Branches
            .Where(static branch => branch.Delivery == StreamBranchDelivery.Required)
            .All(static branch => branch.Status == StreamBranchPublishStatus.Accepted);
}

public sealed record StreamBranchSnapshot(
    string BranchId,
    string Name,
    StreamBranchDelivery Delivery,
    StreamOverflowPolicy Overflow,
    StreamBranchState State,
    int Capacity,
    long QueueDepth,
    long QueueHighWater,
    long Accepted,
    long Delivered,
    long Dropped,
    long Rejected,
    long Abandoned,
    long Faults,
    string? LastFaultType);

public sealed record StreamRouterSnapshot(
    StreamRouterState State,
    long TopologyVersion,
    long LastPublishSequence,
    string? FaultType,
    IReadOnlyList<StreamBranchSnapshot> Branches)
{
    public int RequiredBranches => Branches.Count(static branch => branch.Delivery == StreamBranchDelivery.Required);
    public int OptionalBranches => Branches.Count - RequiredBranches;
}

public interface IStreamItemOwnership<T>
{
    T Retain(T item);
    void Release(T item);
}
