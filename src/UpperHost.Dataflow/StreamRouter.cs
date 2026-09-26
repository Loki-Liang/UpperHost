using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Threading.Channels;

namespace UpperHost.Dataflow;

public enum StreamRouterState
{
    Configuring,
    Running,
    Completing,
    Completed,
    Faulted,
    Disposed
}

public enum StreamBranchState
{
    Configuring,
    Running,
    Completing,
    Completed,
    Faulted,
    Disposed
}

public enum StreamBranchDelivery
{
    Required,
    Optional
}

public enum StreamOverflowPolicy
{
    Wait,
    Reject,
    DropOldest,
    DropNewest,
    Latest
}

public enum StreamBranchFailurePolicy
{
    Propagate,
    Isolate
}

public enum StreamOrderingPolicy
{
    SerializedPublisherFifo
}

public enum StreamCompletionMode
{
    Drain,
    Cancel
}

public enum StreamBranchPublishStatus
{
    Accepted,
    Dropped,
    Rejected,
    Faulted,
    Cancelled,
    Skipped
}

public sealed record StreamBranchOptions(
    string BranchId,
    string Name,
    int Capacity = 256,
    StreamBranchDelivery Delivery = StreamBranchDelivery.Optional,
    StreamOverflowPolicy Overflow = StreamOverflowPolicy.DropOldest,
    StreamBranchFailurePolicy FailurePolicy = StreamBranchFailurePolicy.Isolate,
    StreamOrderingPolicy Ordering = StreamOrderingPolicy.SerializedPublisherFifo);

public readonly record struct StreamItem<T>(long PublishSequence, T Value);

public sealed record StreamBranchPublishResult(
    string BranchId,
    string Name,
    StreamBranchDelivery Delivery,
    StreamBranchPublishStatus Status,
    int DroppedCount = 0,
    string? Reason = null)
{
    public bool Accepted => Status == StreamBranchPublishStatus.Accepted;
}

public sealed record StreamPublishResult(
    long PublishSequence,
    StreamRouterState RouterState,
    IReadOnlyList<StreamBranchPublishResult> Branches,
    string? RouterFault = null)
{
    public bool HasRequiredFailure =>
        Branches.Any(static branch =>
            branch.Delivery == StreamBranchDelivery.Required &&
            branch.Status != StreamBranchPublishStatus.Accepted);

    public bool IsPartialRequiredDelivery =>
        Branches.Any(static branch =>
            branch.Delivery == StreamBranchDelivery.Required &&
            branch.Status == StreamBranchPublishStatus.Accepted) &&
        HasRequiredFailure;
}

public sealed record StreamBranchSnapshot(
    string BranchId,
    string Name,
    StreamBranchState State,
    StreamBranchDelivery Delivery,
    StreamOverflowPolicy Overflow,
    StreamBranchFailurePolicy FailurePolicy,
    StreamOrderingPolicy Ordering,
    int Capacity,
    int QueueDepth,
    int HighWatermark,
    long Accepted,
    long Dequeued,
    long Delivered,
    long Dropped,
    long Rejected,
    long FaultCount,
    TimeSpan LastQueueLatency,
    TimeSpan MaxQueueLatency,
    bool IsFaulted,
    string? FaultMessage);

public sealed record StreamRouterSnapshot(
    StreamRouterState State,
    long TopologyGeneration,
    int BranchCount,
    int RequiredBranchCount,
    int OptionalBranchCount,
    long PublishCount,
    string? FaultMessage);

public interface IStreamOwnershipAdapter<T>
{
    T Retain(T item);

    void Release(T item);
}

public sealed class ImmutableStreamOwnershipAdapter<T> : IStreamOwnershipAdapter<T>
{
    public static ImmutableStreamOwnershipAdapter<T> Instance { get; } = new();

    private ImmutableStreamOwnershipAdapter()
    {
    }

    public T Retain(T item) => item;

    public void Release(T item)
    {
    }
}

public sealed class CopyStreamOwnershipAdapter<T>(
    Func<T, T> clone,
    Action<T>? release = null) : IStreamOwnershipAdapter<T>
{
    public T Retain(T item) => clone(item);

    public void Release(T item) => release?.Invoke(item);
}

public sealed class RetainReleaseStreamOwnershipAdapter<T>(
    Func<T, T> retain,
    Action<T> release) : IStreamOwnershipAdapter<T>
{
    public T Retain(T item) => retain(item);

    public void Release(T item) => release(item);
}

public sealed class StreamBranchOverflowException(string branchId)
    : InvalidOperationException(
        $"Required stream branch '{branchId}' rejected an item because its bounded capacity was exhausted.");

public sealed class StreamBranchConsumerException(string branchId, Exception innerException)
    : InvalidOperationException($"Stream branch '{branchId}' consumer faulted.", innerException);

public static class StreamRouterMetrics
{
    public const string MeterName = "UpperHost.Dataflow.StreamRouter";
}

public sealed class StreamRouter<T> : IAsyncDisposable
{
    private readonly object _topologyGate = new();
    private readonly Dictionary<string, BranchRuntime> _branches = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _publishGate = new(1, 1);
    private readonly TimeProvider _timeProvider;
    private BranchRuntime[] _requiredBranches = [];
    private BranchRuntime[] _optionalBranches = [];
    private Exception? _fault;
    private long _topologyGeneration;
    private long _publishSequence;
    private long _publishCount;
    private int _state = (int)StreamRouterState.Configuring;
    private int _disposed;

    public StreamRouter(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public StreamRouterState State => (StreamRouterState)Volatile.Read(ref _state);

    public long TopologyGeneration => Interlocked.Read(ref _topologyGeneration);

    public StreamBranchSubscription RegisterBranch(
        StreamBranchOptions options,
        Func<StreamItem<T>, CancellationToken, ValueTask> consumer,
        IStreamOwnershipAdapter<T>? ownership = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(consumer);
        Validate(options);

        lock (_topologyGate)
        {
            ThrowIfDisposed();

            var state = State;
            if (state is not StreamRouterState.Configuring and not StreamRouterState.Running)
                throw new InvalidOperationException($"Cannot register a branch while router state is {state}.");

            if (state == StreamRouterState.Running && options.Delivery == StreamBranchDelivery.Required)
                throw new InvalidOperationException("Required topology is sealed after StreamRouter.Start().");

            if (_branches.ContainsKey(options.BranchId))
                throw new InvalidOperationException($"A branch with id '{options.BranchId}' is already registered.");

            var branch = new BranchRuntime(
                options,
                consumer,
                ownership ?? ImmutableStreamOwnershipAdapter<T>.Instance,
                _timeProvider,
                OnBranchFault);

            if (state == StreamRouterState.Running)
                branch.Start();

            _branches.Add(options.BranchId, branch);
            RebuildTopologyLocked();
            Interlocked.Increment(ref _topologyGeneration);
            StreamRouterTelemetry.ActiveBranches.Add(1, StreamRouterTelemetry.BranchTags(options));

            return new StreamBranchSubscription(this, branch);
        }
    }

    public void Start()
    {
        BranchRuntime[] branches;

        lock (_topologyGate)
        {
            ThrowIfDisposed();

            if (State != StreamRouterState.Configuring)
                throw new InvalidOperationException($"StreamRouter can only start from Configuring; current state is {State}.");

            branches = _branches.Values.ToArray();
            foreach (var branch in branches)
                branch.Start();

            Volatile.Write(ref _state, (int)StreamRouterState.Running);
        }
    }

    public async ValueTask<StreamPublishResult> PublishAsync(
        T item,
        CancellationToken cancellationToken = default)
    {
        await _publishGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = State;
            if (state != StreamRouterState.Running)
            {
                return new StreamPublishResult(
                    0,
                    state,
                    Array.Empty<StreamBranchPublishResult>(),
                    Volatile.Read(ref _fault)?.Message);
            }

            var sequence = Interlocked.Increment(ref _publishSequence);
            Interlocked.Increment(ref _publishCount);
            StreamRouterTelemetry.Published.Add(1);

            var required = Volatile.Read(ref _requiredBranches);
            var optional = Volatile.Read(ref _optionalBranches);
            var results = new StreamBranchPublishResult[required.Length + optional.Length];
            var resultIndex = 0;

            for (var index = 0; index < required.Length; index++)
            {
                var branch = required[index];
                var result = await branch.PublishAsync(sequence, item, cancellationToken).ConfigureAwait(false);
                results[resultIndex++] = result;

                if (result.Status == StreamBranchPublishStatus.Accepted)
                    continue;

                for (var remaining = index + 1; remaining < required.Length; remaining++)
                {
                    var skipped = required[remaining];
                    results[resultIndex++] = skipped.CreateSkippedResult("A previous Required branch did not accept this publish.");
                }

                foreach (var skipped in optional)
                    results[resultIndex++] = skipped.CreateSkippedResult("Optional delivery skipped after Required branch failure.");

                if (result.Status is StreamBranchPublishStatus.Rejected or StreamBranchPublishStatus.Faulted)
                {
                    FaultRouter(new StreamBranchOverflowException(branch.Options.BranchId));
                }

                return new StreamPublishResult(
                    sequence,
                    State,
                    results,
                    Volatile.Read(ref _fault)?.Message);
            }

            for (var index = 0; index < optional.Length; index++)
            {
                var branch = optional[index];
                var result = await branch.PublishAsync(sequence, item, cancellationToken).ConfigureAwait(false);
                results[resultIndex++] = result;

                if (branch.Options.FailurePolicy == StreamBranchFailurePolicy.Propagate &&
                    result.Status is StreamBranchPublishStatus.Rejected or StreamBranchPublishStatus.Faulted)
                {
                    FaultRouter(new InvalidOperationException(
                        $"Optional branch '{branch.Options.BranchId}' escalated a publish failure: {result.Reason}"));

                    for (var remaining = index + 1; remaining < optional.Length; remaining++)
                    {
                        var skipped = optional[remaining];
                        results[resultIndex++] = skipped.CreateSkippedResult(
                            "Optional delivery skipped after an escalated branch failure.");
                    }

                    break;
                }
            }

            return new StreamPublishResult(
                sequence,
                State,
                results,
                Volatile.Read(ref _fault)?.Message);
        }
        finally
        {
            _publishGate.Release();
        }
    }

    public StreamRouterSnapshot GetSnapshot()
    {
        var required = Volatile.Read(ref _requiredBranches);
        var optional = Volatile.Read(ref _optionalBranches);

        return new StreamRouterSnapshot(
            State,
            TopologyGeneration,
            required.Length + optional.Length,
            required.Length,
            optional.Length,
            Interlocked.Read(ref _publishCount),
            Volatile.Read(ref _fault)?.Message);
    }

    public IReadOnlyList<StreamBranchSnapshot> GetBranchSnapshots()
    {
        lock (_topologyGate)
        {
            return _branches.Values
                .OrderBy(static branch => branch.Options.BranchId, StringComparer.Ordinal)
                .Select(static branch => branch.GetSnapshot())
                .ToArray();
        }
    }

    public async ValueTask<StreamRouterSnapshot> CompleteAsync(
        StreamCompletionMode mode = StreamCompletionMode.Drain,
        CancellationToken cancellationToken = default)
    {
        BranchRuntime[] branches;

        lock (_topologyGate)
        {
            ThrowIfDisposed();

            var state = State;
            if (state == StreamRouterState.Completed)
                return GetSnapshot();

            if (state == StreamRouterState.Disposed)
                return GetSnapshot();

            if (state != StreamRouterState.Faulted)
                Volatile.Write(ref _state, (int)StreamRouterState.Completing);

            branches = _branches.Values.ToArray();
        }

        foreach (var branch in branches)
            branch.StopAccepting(mode);

        foreach (var branch in branches)
            await branch.WaitForCompletionAsync(cancellationToken).ConfigureAwait(false);

        lock (_topologyGate)
        {
            if (State != StreamRouterState.Faulted)
                Volatile.Write(ref _state, (int)StreamRouterState.Completed);
        }

        return GetSnapshot();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        BranchRuntime[] branches;

        lock (_topologyGate)
        {
            branches = _branches.Values.ToArray();
            _branches.Clear();
            Volatile.Write(ref _requiredBranches, Array.Empty<BranchRuntime>());
            Volatile.Write(ref _optionalBranches, Array.Empty<BranchRuntime>());
            Volatile.Write(ref _state, (int)StreamRouterState.Disposed);
        }

        foreach (var branch in branches)
            branch.StopAccepting(StreamCompletionMode.Cancel);

        foreach (var branch in branches)
            await branch.WaitForCompletionAsync(CancellationToken.None).ConfigureAwait(false);

        foreach (var branch in branches)
        {
            branch.DisposeRuntime();
            if (branch.TryDeactivateMetric())
                StreamRouterTelemetry.ActiveBranches.Add(-1, StreamRouterTelemetry.BranchTags(branch.Options));
        }

    }

    private async ValueTask DetachBranchAsync(
        BranchRuntime branch,
        StreamCompletionMode mode,
        CancellationToken cancellationToken)
    {
        var removed = false;

        lock (_topologyGate)
        {
            ThrowIfDisposed();

            if (!_branches.TryGetValue(branch.Options.BranchId, out var current) ||
                !ReferenceEquals(current, branch))
            {
                return;
            }

            if (branch.Options.Delivery == StreamBranchDelivery.Required &&
                State is StreamRouterState.Running or StreamRouterState.Completing)
            {
                throw new InvalidOperationException("Required branches cannot detach while the router is running/completing.");
            }

            removed = _branches.Remove(branch.Options.BranchId);
            if (removed)
            {
                RebuildTopologyLocked();
                Interlocked.Increment(ref _topologyGeneration);
            }
        }

        if (!removed)
            return;

        branch.StopAccepting(mode);
        await branch.WaitForCompletionAsync(cancellationToken).ConfigureAwait(false);
        branch.DisposeRuntime();
        if (branch.TryDeactivateMetric())
            StreamRouterTelemetry.ActiveBranches.Add(-1, StreamRouterTelemetry.BranchTags(branch.Options));
    }

    private void OnBranchFault(BranchRuntime branch, Exception error)
    {
        BranchRuntime[]? stopAll = null;
        var detachOptional = false;
        var routerFaultedNow = false;

        lock (_topologyGate)
        {
            if (State == StreamRouterState.Disposed)
                return;

            var escalates =
                branch.Options.Delivery == StreamBranchDelivery.Required ||
                branch.Options.FailurePolicy == StreamBranchFailurePolicy.Propagate;

            if (escalates)
            {
                if (_fault is null)
                {
                    _fault = error;
                    routerFaultedNow = true;
                }

                Volatile.Write(ref _state, (int)StreamRouterState.Faulted);
                stopAll = _branches.Values.ToArray();
            }
            else if (_branches.TryGetValue(branch.Options.BranchId, out var current) &&
                     ReferenceEquals(current, branch))
            {
                RebuildTopologyLocked();
                Interlocked.Increment(ref _topologyGeneration);
                detachOptional = true;
            }
        }

        StreamRouterTelemetry.Faults.Add(1, StreamRouterTelemetry.BranchTags(branch.Options));
        if (routerFaultedNow)
            StreamRouterTelemetry.RouterFaults.Add(1);

        if (stopAll is not null)
        {
            foreach (var item in stopAll)
                item.StopAccepting(StreamCompletionMode.Drain);
        }
        else if (detachOptional)
        {
            branch.StopAccepting(StreamCompletionMode.Cancel);
            if (branch.TryDeactivateMetric())
                StreamRouterTelemetry.ActiveBranches.Add(-1, StreamRouterTelemetry.BranchTags(branch.Options));
        }
    }

    private void FaultRouter(Exception error)
    {
        BranchRuntime[] branches;

        lock (_topologyGate)
        {
            if (State is StreamRouterState.Faulted or StreamRouterState.Disposed)
                return;

            _fault ??= error;
            Volatile.Write(ref _state, (int)StreamRouterState.Faulted);
            branches = _branches.Values.ToArray();
        }

        StreamRouterTelemetry.RouterFaults.Add(1);

        foreach (var branch in branches)
            branch.StopAccepting(StreamCompletionMode.Drain);
    }

    private void RebuildTopologyLocked()
    {
        var required = _branches.Values
            .Where(static branch =>
                branch.Options.Delivery == StreamBranchDelivery.Required &&
                branch.IsRoutable)
            .OrderBy(static branch => branch.Options.BranchId, StringComparer.Ordinal)
            .ToArray();

        var optional = _branches.Values
            .Where(static branch =>
                branch.Options.Delivery == StreamBranchDelivery.Optional &&
                branch.IsRoutable)
            .OrderBy(static branch => branch.Options.BranchId, StringComparer.Ordinal)
            .ToArray();

        Volatile.Write(ref _requiredBranches, required);
        Volatile.Write(ref _optionalBranches, optional);
    }

    private static void Validate(StreamBranchOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.BranchId))
            throw new ArgumentException("Stable BranchId is required.", nameof(options));

        if (string.IsNullOrWhiteSpace(options.Name))
            throw new ArgumentException("Branch Name is required.", nameof(options));

        if (options.Capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Branch capacity must be greater than zero.");

        if (options.Delivery == StreamBranchDelivery.Required &&
            options.Overflow is StreamOverflowPolicy.DropOldest or StreamOverflowPolicy.DropNewest or StreamOverflowPolicy.Latest)
        {
            throw new ArgumentException(
                "Required branches cannot use lossy overflow. Use Wait or Reject.",
                nameof(options));
        }

        if (options.Delivery == StreamBranchDelivery.Required &&
            options.FailurePolicy == StreamBranchFailurePolicy.Isolate)
        {
            throw new ArgumentException(
                "Required branches must propagate failures.",
                nameof(options));
        }

        if (options.Delivery == StreamBranchDelivery.Optional &&
            options.Overflow == StreamOverflowPolicy.Wait)
        {
            throw new ArgumentException(
                "Optional branches cannot use Wait because they must not backpressure the main publisher.",
                nameof(options));
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }

    public sealed class StreamBranchSubscription
    {
        private readonly StreamRouter<T> _owner;
        private readonly BranchRuntime _branch;

        private StreamBranchSubscription(StreamRouter<T> owner, BranchRuntime branch)
        {
            _owner = owner;
            _branch = branch;
        }

        public string BranchId => _branch.Options.BranchId;

        public Task Completion => _branch.Completion;

        public StreamBranchSnapshot GetSnapshot() => _branch.GetSnapshot();

        public void ReportFailure(Exception error) => _branch.ReportFailure(error);

        public ValueTask DetachAsync(
            StreamCompletionMode mode = StreamCompletionMode.Cancel,
            CancellationToken cancellationToken = default) =>
            _owner.DetachBranchAsync(_branch, mode, cancellationToken);
    }

    private sealed class BranchRuntime
    {
        private readonly Channel<Envelope> _channel;
        private readonly Func<StreamItem<T>, CancellationToken, ValueTask> _consumer;
        private readonly IStreamOwnershipAdapter<T> _ownership;
        private readonly TimeProvider _timeProvider;
        private readonly Action<BranchRuntime, Exception> _onFault;
        private readonly CancellationTokenSource _stopSource = new();
        private readonly TaskCompletionSource _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Task _consumerTask = Task.CompletedTask;
        private Exception? _fault;
        private long _accepted;
        private long _dequeued;
        private long _delivered;
        private long _dropped;
        private long _rejected;
        private long _faultCount;
        private long _lastLatencyTicks;
        private long _maxLatencyTicks;
        private int _highWatermark;
        private int _state = (int)StreamBranchState.Configuring;
        private int _started;
        private int _stopRequested;
        private int _disposed;
        private int _activeMetric = 1;

        public BranchRuntime(
            StreamBranchOptions options,
            Func<StreamItem<T>, CancellationToken, ValueTask> consumer,
            IStreamOwnershipAdapter<T> ownership,
            TimeProvider timeProvider,
            Action<BranchRuntime, Exception> onFault)
        {
            Options = options;
            _consumer = consumer;
            _ownership = ownership;
            _timeProvider = timeProvider;
            _onFault = onFault;
            _channel = Channel.CreateBounded<Envelope>(new BoundedChannelOptions(options.Capacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = false,
                SingleWriter = true,
                AllowSynchronousContinuations = false
            });
        }

        public StreamBranchOptions Options { get; }

        public Task Completion => _completion.Task;

        public StreamBranchState State => (StreamBranchState)Volatile.Read(ref _state);

        public bool IsRoutable =>
            Volatile.Read(ref _stopRequested) == 0 &&
            Volatile.Read(ref _fault) is null &&
            State is StreamBranchState.Configuring or StreamBranchState.Running;

        public bool TryDeactivateMetric() =>
            Interlocked.Exchange(ref _activeMetric, 0) != 0;

        public void Start()
        {
            if (Interlocked.CompareExchange(ref _started, 1, 0) != 0)
                return;

            Volatile.Write(ref _state, (int)StreamBranchState.Running);
            _consumerTask = ConsumeLoopAsync();
        }

        public async ValueTask<StreamBranchPublishResult> PublishAsync(
            long publishSequence,
            T item,
            CancellationToken cancellationToken)
        {
            if (Volatile.Read(ref _stopRequested) != 0)
                return CreateSkippedResult("Branch is no longer accepting publishes.");

            var fault = Volatile.Read(ref _fault);
            if (fault is not null)
            {
                return new StreamBranchPublishResult(
                    Options.BranchId,
                    Options.Name,
                    Options.Delivery,
                    StreamBranchPublishStatus.Faulted,
                    Reason: fault.Message);
            }

            var retained = _ownership.Retain(item);
            var envelope = new Envelope(publishSequence, _timeProvider.GetTimestamp(), retained);

            switch (Options.Overflow)
            {
                case StreamOverflowPolicy.Wait:
                    try
                    {
                        await _channel.Writer.WriteAsync(envelope, cancellationToken).ConfigureAwait(false);
                        MarkAccepted();
                        return AcceptedResult();
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        _ownership.Release(retained);
                        return new StreamBranchPublishResult(
                            Options.BranchId,
                            Options.Name,
                            Options.Delivery,
                            StreamBranchPublishStatus.Cancelled,
                            Reason: "Publish cancelled before branch acceptance.");
                    }
                    catch (ChannelClosedException)
                    {
                        _ownership.Release(retained);
                        return FaultOrSkippedResult();
                    }

                case StreamOverflowPolicy.Reject:
                    if (_channel.Writer.TryWrite(envelope))
                    {
                        MarkAccepted();
                        return AcceptedResult();
                    }

                    _ownership.Release(retained);
                    Interlocked.Increment(ref _rejected);
                    StreamRouterTelemetry.Rejected.Add(1, StreamRouterTelemetry.BranchTags(Options));
                    return new StreamBranchPublishResult(
                        Options.BranchId,
                        Options.Name,
                        Options.Delivery,
                        StreamBranchPublishStatus.Rejected,
                        Reason: "Bounded branch capacity is full.");

                case StreamOverflowPolicy.DropNewest:
                    if (_channel.Writer.TryWrite(envelope))
                    {
                        MarkAccepted();
                        return AcceptedResult();
                    }

                    _ownership.Release(retained);
                    Interlocked.Increment(ref _dropped);
                    StreamRouterTelemetry.Dropped.Add(1, StreamRouterTelemetry.BranchTags(Options));
                    return new StreamBranchPublishResult(
                        Options.BranchId,
                        Options.Name,
                        Options.Delivery,
                        StreamBranchPublishStatus.Dropped,
                        DroppedCount: 1,
                        Reason: "Incoming item dropped because branch capacity is full.");

                case StreamOverflowPolicy.DropOldest:
                {
                    if (_channel.Writer.TryWrite(envelope))
                    {
                        MarkAccepted();
                        return AcceptedResult();
                    }

                    var dropped = 0;
                    if (_channel.Reader.TryRead(out var oldest))
                    {
                        _ownership.Release(oldest.Item);
                        dropped = 1;
                        Interlocked.Increment(ref _dropped);
                        StreamRouterTelemetry.Dropped.Add(1, StreamRouterTelemetry.BranchTags(Options));
                    }

                    if (_channel.Writer.TryWrite(envelope))
                    {
                        MarkAccepted();
                        return AcceptedResult(dropped);
                    }

                    _ownership.Release(retained);
                    Interlocked.Increment(ref _dropped);
                    StreamRouterTelemetry.Dropped.Add(1, StreamRouterTelemetry.BranchTags(Options));
                    return new StreamBranchPublishResult(
                        Options.BranchId,
                        Options.Name,
                        Options.Delivery,
                        StreamBranchPublishStatus.Dropped,
                        DroppedCount: dropped + 1,
                        Reason: "Branch stopped accepting while replacing the oldest item.");
                }

                case StreamOverflowPolicy.Latest:
                {
                    var dropped = 0;
                    while (_channel.Reader.TryRead(out var pending))
                    {
                        _ownership.Release(pending.Item);
                        dropped++;
                    }

                    if (dropped > 0)
                    {
                        Interlocked.Add(ref _dropped, dropped);
                        StreamRouterTelemetry.Dropped.Add(dropped, StreamRouterTelemetry.BranchTags(Options));
                    }

                    if (_channel.Writer.TryWrite(envelope))
                    {
                        MarkAccepted();
                        return AcceptedResult(dropped);
                    }

                    _ownership.Release(retained);
                    Interlocked.Increment(ref _dropped);
                    StreamRouterTelemetry.Dropped.Add(1, StreamRouterTelemetry.BranchTags(Options));
                    return new StreamBranchPublishResult(
                        Options.BranchId,
                        Options.Name,
                        Options.Delivery,
                        StreamBranchPublishStatus.Dropped,
                        DroppedCount: dropped + 1,
                        Reason: "Branch stopped accepting before the latest item could be queued.");
                }

                default:
                    _ownership.Release(retained);
                    throw new ArgumentOutOfRangeException();
            }
        }

        public StreamBranchSnapshot GetSnapshot()
        {
            var fault = Volatile.Read(ref _fault);
            var depth = _channel.Reader.CanCount ? _channel.Reader.Count : 0;

            return new StreamBranchSnapshot(
                Options.BranchId,
                Options.Name,
                State,
                Options.Delivery,
                Options.Overflow,
                Options.FailurePolicy,
                Options.Ordering,
                Options.Capacity,
                depth,
                Volatile.Read(ref _highWatermark),
                Interlocked.Read(ref _accepted),
                Interlocked.Read(ref _dequeued),
                Interlocked.Read(ref _delivered),
                Interlocked.Read(ref _dropped),
                Interlocked.Read(ref _rejected),
                Interlocked.Read(ref _faultCount),
                TimeSpan.FromTicks(Interlocked.Read(ref _lastLatencyTicks)),
                TimeSpan.FromTicks(Interlocked.Read(ref _maxLatencyTicks)),
                fault is not null,
                fault?.Message);
        }

        public StreamBranchPublishResult CreateSkippedResult(string reason) =>
            new(
                Options.BranchId,
                Options.Name,
                Options.Delivery,
                StreamBranchPublishStatus.Skipped,
                Reason: reason);

        public void ReportFailure(Exception error)
        {
            ArgumentNullException.ThrowIfNull(error);
            RecordFault(error);
        }

        public void StopAccepting(StreamCompletionMode mode)
        {
            if (Interlocked.Exchange(ref _stopRequested, 1) != 0)
                return;

            if (State != StreamBranchState.Faulted)
                Volatile.Write(ref _state, (int)StreamBranchState.Completing);

            _channel.Writer.TryComplete();

            if (mode == StreamCompletionMode.Cancel)
                _stopSource.Cancel();

            if (Volatile.Read(ref _started) == 0)
            {
                ReleaseBufferedItems();
                _completion.TrySetResult();
            }
        }

        public async ValueTask WaitForCompletionAsync(CancellationToken cancellationToken)
        {
            await _completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

            if (Volatile.Read(ref _started) != 0)
                await _consumerTask.ConfigureAwait(false);
        }

        public void DisposeRuntime()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            Volatile.Write(ref _state, (int)StreamBranchState.Disposed);
            _stopSource.Dispose();
        }

        private async Task ConsumeLoopAsync()
        {
            try
            {
                await foreach (var envelope in _channel.Reader
                                   .ReadAllAsync(_stopSource.Token)
                                   .ConfigureAwait(false))
                {
                    Interlocked.Increment(ref _dequeued);
                    var latency = _timeProvider.GetElapsedTime(envelope.EnqueuedTimestamp);
                    Interlocked.Exchange(ref _lastLatencyTicks, latency.Ticks);
                    UpdateMaxLatency(latency.Ticks);
                    StreamRouterTelemetry.QueueLatencyMs.Record(
                        latency.TotalMilliseconds,
                        StreamRouterTelemetry.BranchTags(Options));

                    try
                    {
                        await _consumer(
                                new StreamItem<T>(envelope.PublishSequence, envelope.Item),
                                _stopSource.Token)
                            .ConfigureAwait(false);
                        Interlocked.Increment(ref _delivered);
                        StreamRouterTelemetry.Delivered.Add(1, StreamRouterTelemetry.BranchTags(Options));
                    }
                    catch (OperationCanceledException) when (_stopSource.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception error)
                    {
                        RecordFault(new StreamBranchConsumerException(Options.BranchId, error));
                        break;
                    }
                    finally
                    {
                        _ownership.Release(envelope.Item);
                    }
                }
            }
            catch (OperationCanceledException) when (_stopSource.IsCancellationRequested)
            {
            }
            catch (Exception error)
            {
                RecordFault(error);
            }
            finally
            {
                ReleaseBufferedItems();
                if (State != StreamBranchState.Faulted)
                    Volatile.Write(ref _state, (int)StreamBranchState.Completed);
                _completion.TrySetResult();
            }
        }

        private void RecordFault(Exception error)
        {
            if (Interlocked.CompareExchange(ref _fault, error, null) is not null)
                return;

            Interlocked.Increment(ref _faultCount);
            Volatile.Write(ref _state, (int)StreamBranchState.Faulted);
            _channel.Writer.TryComplete(error);
            _onFault(this, error);
        }

        private void ReleaseBufferedItems()
        {
            while (_channel.Reader.TryRead(out var pending))
                _ownership.Release(pending.Item);
        }

        private void MarkAccepted()
        {
            Interlocked.Increment(ref _accepted);
            StreamRouterTelemetry.Accepted.Add(1, StreamRouterTelemetry.BranchTags(Options));

            var depth = _channel.Reader.CanCount ? _channel.Reader.Count : 0;
            while (true)
            {
                var current = Volatile.Read(ref _highWatermark);
                if (depth <= current)
                    break;

                if (Interlocked.CompareExchange(ref _highWatermark, depth, current) == current)
                    break;
            }
        }

        private void UpdateMaxLatency(long ticks)
        {
            while (true)
            {
                var current = Interlocked.Read(ref _maxLatencyTicks);
                if (ticks <= current)
                    return;

                if (Interlocked.CompareExchange(ref _maxLatencyTicks, ticks, current) == current)
                    return;
            }
        }

        private StreamBranchPublishResult AcceptedResult(int droppedCount = 0) =>
            new(
                Options.BranchId,
                Options.Name,
                Options.Delivery,
                StreamBranchPublishStatus.Accepted,
                DroppedCount: droppedCount);

        private StreamBranchPublishResult FaultOrSkippedResult()
        {
            var fault = Volatile.Read(ref _fault);
            return fault is null
                ? CreateSkippedResult("Branch is closed.")
                : new StreamBranchPublishResult(
                    Options.BranchId,
                    Options.Name,
                    Options.Delivery,
                    StreamBranchPublishStatus.Faulted,
                    Reason: fault.Message);
        }

        private readonly record struct Envelope(
            long PublishSequence,
            long EnqueuedTimestamp,
            T Item);
    }

}

internal static class StreamRouterTelemetry
{
    private static readonly Meter Meter = new(StreamRouterMetrics.MeterName);

    public static readonly Counter<long> Published =
        Meter.CreateCounter<long>("upperhost.streamrouter.published");

    public static readonly Counter<long> Accepted =
        Meter.CreateCounter<long>("upperhost.streamrouter.branch.accepted");

    public static readonly Counter<long> Delivered =
        Meter.CreateCounter<long>("upperhost.streamrouter.branch.delivered");

    public static readonly Counter<long> Dropped =
        Meter.CreateCounter<long>("upperhost.streamrouter.branch.dropped");

    public static readonly Counter<long> Rejected =
        Meter.CreateCounter<long>("upperhost.streamrouter.branch.rejected");

    public static readonly Counter<long> Faults =
        Meter.CreateCounter<long>("upperhost.streamrouter.branch.faults");

    public static readonly Counter<long> RouterFaults =
        Meter.CreateCounter<long>("upperhost.streamrouter.faults");

    public static readonly UpDownCounter<long> ActiveBranches =
        Meter.CreateUpDownCounter<long>("upperhost.streamrouter.active_branches");

    public static readonly Histogram<double> QueueLatencyMs =
        Meter.CreateHistogram<double>("upperhost.streamrouter.branch.queue_latency", "ms");

    public static TagList BranchTags(StreamBranchOptions options)
    {
        var tags = new TagList
        {
            { "branch.id", options.BranchId },
            { "branch.delivery", options.Delivery.ToString() }
        };
        return tags;
    }
}
