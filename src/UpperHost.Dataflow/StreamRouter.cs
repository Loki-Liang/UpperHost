using System.Diagnostics;
using System.Threading.Channels;

namespace UpperHost.Dataflow;

public sealed class StreamRouter<T> : IAsyncDisposable
{
    private readonly object _lifecycleGate = new();
    private readonly List<BranchRuntime> _configuredBranches = [];
    private readonly SemaphoreSlim _publishGate = new(1, 1);
    private readonly CancellationTokenSource _routerCancellation = new();
    private readonly IStreamItemOwnership<T> _ownership;
    private BranchRuntime[] _branches = [];
    private Task _completion = Task.CompletedTask;
    private Exception? _fault;
    private int _state = (int)StreamRouterState.Created;
    private long _topologyVersion;
    private long _publishSequence;
    private int _disposed;

    public StreamRouter(IStreamItemOwnership<T>? ownership = null) =>
        _ownership = ownership ?? NoopStreamItemOwnership<T>.Instance;

    public StreamRouterState State => (StreamRouterState)Volatile.Read(ref _state);

    public Task Completion => _completion;

    public void RegisterBranch(
        StreamBranchOptions options,
        Func<T, CancellationToken, ValueTask> consumer)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(consumer);
        ValidateOptions(options);

        lock (_lifecycleGate)
        {
            ThrowIfDisposed();
            if (State != StreamRouterState.Created)
                throw new InvalidOperationException("Stream router topology is sealed after StartAsync.");

            if (_configuredBranches.Any(branch =>
                    string.Equals(branch.Options.BranchId, options.BranchId, StringComparison.Ordinal)))
            {
                throw new ArgumentException(
                    $"Duplicate stream branch id '{options.BranchId}'.",
                    nameof(options));
            }

            _configuredBranches.Add(new BranchRuntime(this, options, consumer, _ownership));
            _topologyVersion++;
        }
    }

    public ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_lifecycleGate)
        {
            ThrowIfDisposed();
            if (State != StreamRouterState.Created)
                throw new InvalidOperationException($"Cannot start stream router from state {State}.");
            if (_configuredBranches.Count == 0)
                throw new InvalidOperationException("At least one stream branch must be registered before StartAsync.");

            _branches = _configuredBranches
                .OrderBy(static branch => branch.Options.Delivery == StreamBranchDelivery.Required ? 0 : 1)
                .ToArray();
            Volatile.Write(ref _state, (int)StreamRouterState.Running);

            foreach (var branch in _branches)
                branch.Start(_routerCancellation.Token);

            _completion = ObserveCompletionAsync(_branches);
        }

        return ValueTask.CompletedTask;
    }

    public async ValueTask<StreamPublishResult> PublishAsync(
        T item,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _publishGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var currentState = State;
            if (currentState == StreamRouterState.Created)
                throw new InvalidOperationException("Stream router must be started before publishing.");
            if (currentState != StreamRouterState.Running)
                return CreateUnavailablePublishResult(currentState);

            var sequence = Interlocked.Increment(ref _publishSequence);
            var results = new StreamBranchPublishResult[_branches.Length];
            var requiredFailure = false;
            var acceptedRequired = 0;

            for (var index = 0; index < _branches.Length; index++)
            {
                var branch = _branches[index];
                if (requiredFailure)
                {
                    results[index] = branch.NotAttempted("required_branch_failure");
                    continue;
                }

                if (branch.Options.Delivery == StreamBranchDelivery.Optional && State != StreamRouterState.Running)
                {
                    results[index] = branch.NotAttempted("router_not_running");
                    continue;
                }

                var result = await branch.PublishAsync(item, cancellationToken).ConfigureAwait(false);
                results[index] = result;

                if (branch.Options.Delivery == StreamBranchDelivery.Required)
                {
                    if (result.Status == StreamBranchPublishStatus.Accepted)
                    {
                        acceptedRequired++;
                        continue;
                    }

                    requiredFailure = true;

                    // If cancellation happens before any Required branch accepted the item,
                    // the caller may retry safely. Once a Required branch has accepted it,
                    // cancellation creates an explicit partial publish and therefore faults.
                    if (result.Status != StreamBranchPublishStatus.Cancelled || acceptedRequired > 0)
                    {
                        FaultRouter(
                            branch,
                            new InvalidOperationException(
                                $"Required stream branch '{branch.Options.BranchId}' did not accept publish sequence {sequence}: {result.Reason ?? result.Status.ToString()}."));
                    }
                }
                else if (result.Status == StreamBranchPublishStatus.Faulted &&
                         branch.Options.FailurePolicy == StreamBranchFailurePolicy.FaultRouter)
                {
                    FaultRouter(
                        branch,
                        new InvalidOperationException(
                            $"Optional stream branch '{branch.Options.BranchId}' faulted during publish sequence {sequence}."));
                }
            }

            return new StreamPublishResult(sequence, State, results);
        }
        finally
        {
            _publishGate.Release();
        }
    }

    public async ValueTask CompleteAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var awaitCompletion = false;

        await _publishGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();

            lock (_lifecycleGate)
            {
                var state = State;
                if (state == StreamRouterState.Completed)
                    return;
                if (state is StreamRouterState.Completing or StreamRouterState.Faulted)
                {
                    awaitCompletion = true;
                }
                else if (state == StreamRouterState.Running)
                {
                    Volatile.Write(ref _state, (int)StreamRouterState.Completing);
                    foreach (var branch in _branches)
                        branch.RequestCompletion();
                    awaitCompletion = true;
                }
                else
                {
                    throw new InvalidOperationException($"Cannot complete stream router from state {state}.");
                }
            }
        }
        finally
        {
            _publishGate.Release();
        }

        if (awaitCompletion)
            await _completion.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public StreamRouterSnapshot GetSnapshot()
    {
        var branches = _branches.Length == 0
            ? _configuredBranches.Select(static branch => branch.GetSnapshot()).ToArray()
            : _branches.Select(static branch => branch.GetSnapshot()).ToArray();

        return new StreamRouterSnapshot(
            State,
            Volatile.Read(ref _topologyVersion),
            Volatile.Read(ref _publishSequence),
            Volatile.Read(ref _fault)?.GetType().Name,
            branches);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        BranchRuntime[] snapshot;
        lock (_lifecycleGate)
        {
            snapshot = _branches.Length == 0 ? _configuredBranches.ToArray() : _branches;
            Volatile.Write(ref _state, (int)StreamRouterState.Disposed);
        }

        // Cancellation must happen before waiting for the serialized publish gate.
        // A Required/Wait publish can own that gate while blocked on a full branch.
        // Its branch write observes this router lifetime token and exits, allowing
        // deterministic shutdown instead of a publish-vs-dispose deadlock.
        _routerCancellation.Cancel();

        await _publishGate.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (var branch in snapshot)
                branch.RequestAbort();
        }
        finally
        {
            _publishGate.Release();
        }

        try
        {
            await _completion.ConfigureAwait(false);
        }
        catch
        {
            // Dispose is deterministic cleanup. Fault details remain available in the snapshot.
        }

        foreach (var branch in snapshot)
            branch.Dispose();

        _routerCancellation.Dispose();

        // Deliberately keep _publishGate undisposed. A publish racing with DisposeAsync
        // may already be waiting on it and must wake to observe the terminal state.
    }

    private StreamPublishResult CreateUnavailablePublishResult(StreamRouterState state)
    {
        var status = state == StreamRouterState.Faulted
            ? StreamBranchPublishStatus.Faulted
            : StreamBranchPublishStatus.Rejected;
        var reason = state == StreamRouterState.Faulted ? "router_faulted" : "router_not_running";
        var results = _branches
            .Select(branch => new StreamBranchPublishResult(
                branch.Options.BranchId,
                branch.Options.Delivery,
                status,
                reason))
            .ToArray();

        return new StreamPublishResult(Volatile.Read(ref _publishSequence), state, results);
    }

    private async Task ObserveCompletionAsync(BranchRuntime[] branches)
    {
        await Task.WhenAll(branches.Select(static branch => branch.Completion)).ConfigureAwait(false);

        StreamRouterState terminalState;
        Exception? fault;
        lock (_lifecycleGate)
        {
            terminalState = State;
            if (terminalState == StreamRouterState.Completing)
            {
                Volatile.Write(ref _state, (int)StreamRouterState.Completed);
                terminalState = StreamRouterState.Completed;
            }

            fault = _fault;
        }

        if (terminalState == StreamRouterState.Faulted && fault is not null)
            throw new InvalidOperationException("Stream router faulted.", fault);
    }

    private void OnBranchFault(BranchRuntime branch, Exception exception)
    {
        if (branch.Options.Delivery == StreamBranchDelivery.Required ||
            branch.Options.FailurePolicy == StreamBranchFailurePolicy.FaultRouter)
        {
            FaultRouter(branch, exception);
        }
    }

    private void FaultRouter(BranchRuntime source, Exception exception)
    {
        BranchRuntime[] snapshot;
        lock (_lifecycleGate)
        {
            var state = State;
            if (state is StreamRouterState.Faulted or StreamRouterState.Completed or StreamRouterState.Disposed)
                return;

            _fault ??= exception;
            Volatile.Write(ref _state, (int)StreamRouterState.Faulted);
            snapshot = _branches;
            DataflowTelemetry.RouterFaults.Add(
                1,
                DataflowTelemetry.RouterFaultTags(source.Options.Delivery));
        }

        _routerCancellation.Cancel();
        foreach (var branch in snapshot)
            branch.RequestAbort(exception);
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(StreamRouter<T>));
    }

    private static void ValidateOptions(StreamBranchOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.BranchId))
            throw new ArgumentException("BranchId is required.", nameof(options));
        if (options.BranchId.Length > 64 || options.BranchId.Any(static c =>
                !(char.IsLetterOrDigit(c) || c is '-' or '_' or '.')))
        {
            throw new ArgumentException(
                "BranchId must be <= 64 characters and contain only letters, digits, '-', '_' or '.'.",
                nameof(options));
        }

        if (string.IsNullOrWhiteSpace(options.Name))
            throw new ArgumentException("Name is required.", nameof(options));
        if (options.Capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Capacity must be greater than zero.");
        if (options.Ordering != StreamOrdering.PreserveRouterSequence)
            throw new ArgumentException("Unsupported stream ordering policy.", nameof(options));
        if (options.Delivery == StreamBranchDelivery.Required &&
            options.Overflow is StreamOverflowPolicy.DropOldest or
                StreamOverflowPolicy.DropNewest or
                StreamOverflowPolicy.DropWrite or
                StreamOverflowPolicy.Latest)
        {
            throw new ArgumentException(
                "Required branches cannot use lossy overflow policies.",
                nameof(options));
        }

        if (options.Delivery == StreamBranchDelivery.Required &&
            options.FailurePolicy != StreamBranchFailurePolicy.FaultRouter)
        {
            throw new ArgumentException(
                "Required branches must fault the router on branch failure.",
                nameof(options));
        }

        if (options.Delivery == StreamBranchDelivery.Optional &&
            options.Overflow == StreamOverflowPolicy.Wait)
        {
            throw new ArgumentException(
                "Optional branches cannot use Wait because they must not backpressure required delivery.",
                nameof(options));
        }

        if (options.Overflow == StreamOverflowPolicy.Latest && options.Capacity != 1)
            throw new ArgumentException("Latest overflow requires Capacity=1.", nameof(options));
    }

    private sealed class BranchRuntime : IDisposable
    {
        private readonly StreamRouter<T> _owner;
        private readonly Func<T, CancellationToken, ValueTask> _consumer;
        private readonly IStreamItemOwnership<T> _ownership;
        private readonly Channel<Envelope> _channel;
        private readonly CancellationTokenSource _branchCancellation = new();
        private CancellationTokenSource? _linkedCancellation;
        private Task _completion = Task.CompletedTask;
        private Exception? _lastFault;
        private long _queueDepth;
        private long _queueHighWater;
        private long _accepted;
        private long _delivered;
        private long _dropped;
        private long _rejected;
        private long _abandoned;
        private long _faults;
        private int _state = (int)StreamBranchState.Created;
        private int _activeMetric;
        private int _disposed;

        public BranchRuntime(
            StreamRouter<T> owner,
            StreamBranchOptions options,
            Func<T, CancellationToken, ValueTask> consumer,
            IStreamItemOwnership<T> ownership)
        {
            _owner = owner;
            Options = options;
            _consumer = consumer;
            _ownership = ownership;
            _channel = Channel.CreateBounded<Envelope>(
                new BoundedChannelOptions(options.Capacity)
                {
                    FullMode = options.Overflow switch
                    {
                        StreamOverflowPolicy.DropOldest or StreamOverflowPolicy.Latest => BoundedChannelFullMode.DropOldest,
                        StreamOverflowPolicy.DropNewest => BoundedChannelFullMode.DropNewest,
                        StreamOverflowPolicy.DropWrite => BoundedChannelFullMode.DropWrite,
                        _ => BoundedChannelFullMode.Wait
                    },
                    SingleReader = true,
                    SingleWriter = true,
                    AllowSynchronousContinuations = false
                },
                OnDropped);
        }

        public StreamBranchOptions Options { get; }

        public Task Completion => _completion;

        public void Start(CancellationToken routerCancellation)
        {
            _linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                routerCancellation,
                _branchCancellation.Token);
            Volatile.Write(ref _state, (int)StreamBranchState.Running);
            if (Interlocked.Exchange(ref _activeMetric, 1) == 0)
                DataflowTelemetry.ActiveBranches.Add(1, Tags());
            _completion = RunAsync(_linkedCancellation.Token);
        }

        public async ValueTask<StreamBranchPublishResult> PublishAsync(
            T item,
            CancellationToken cancellationToken)
        {
            var state = (StreamBranchState)Volatile.Read(ref _state);
            if (state != StreamBranchState.Running)
            {
                Interlocked.Increment(ref _rejected);
                DataflowTelemetry.Rejected.Add(1, Tags());
                return new StreamBranchPublishResult(
                    Options.BranchId,
                    Options.Delivery,
                    state == StreamBranchState.Faulted
                        ? StreamBranchPublishStatus.Faulted
                        : StreamBranchPublishStatus.Rejected,
                    state == StreamBranchState.Faulted ? "branch_faulted" : "branch_not_running");
            }

            if (cancellationToken.IsCancellationRequested)
                return Cancelled("publish_cancelled");

            T retained;
            try
            {
                retained = _ownership.Retain(item);
            }
            catch (Exception exception)
            {
                RecordFault(exception);
                return Faulted("ownership_retain_failed");
            }

            var envelope = new Envelope(retained, Stopwatch.GetTimestamp());
            var enqueueStarted = Stopwatch.GetTimestamp();

            try
            {
                switch (Options.Overflow)
                {
                    case StreamOverflowPolicy.Wait:
                        var lifetimeToken = _linkedCancellation?.Token ?? _branchCancellation.Token;
                        using (var writeCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                                   cancellationToken,
                                   lifetimeToken))
                        {
                            try
                            {
                                await _channel.Writer.WriteAsync(
                                        envelope,
                                        writeCancellation.Token)
                                    .ConfigureAwait(false);
                            }
                            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                            {
                                ReleaseUnaccepted(envelope, countRejected: false);
                                return Cancelled("publish_cancelled");
                            }
                            catch (OperationCanceledException) when (lifetimeToken.IsCancellationRequested)
                            {
                                ReleaseUnaccepted(envelope, countRejected: true);
                                return ClosedResult();
                            }
                            catch (ChannelClosedException)
                            {
                                ReleaseUnaccepted(envelope, countRejected: true);
                                return ClosedResult();
                            }
                        }
                        break;

                    case StreamOverflowPolicy.Fail:
                        if (!_channel.Writer.TryWrite(envelope))
                        {
                            ReleaseUnaccepted(envelope, countRejected: true);
                            return Rejected("queue_full_or_closed");
                        }
                        break;

                    default:
                        if (!_channel.Writer.TryWrite(envelope))
                        {
                            ReleaseUnaccepted(envelope, countRejected: true);
                            return Rejected("channel_closed");
                        }
                        break;
                }
            }
            catch (Exception exception)
            {
                ReleaseUnaccepted(envelope, countRejected: true);
                RecordFault(exception);
                return Faulted("branch_publish_fault");
            }

            envelope.MarkAccepted(IncrementQueueDepth);
            DataflowTelemetry.EnqueueLatency.Record(
                Stopwatch.GetElapsedTime(enqueueStarted).TotalSeconds,
                Tags());

            if (Volatile.Read(ref envelope.Dropped) != 0)
                return Dropped("drop_policy");

            Interlocked.Increment(ref _accepted);
            DataflowTelemetry.Accepted.Add(1, Tags());
            return Accepted();
        }

        public StreamBranchPublishResult NotAttempted(string reason) =>
            new(Options.BranchId, Options.Delivery, StreamBranchPublishStatus.NotAttempted, reason);

        public void RequestCompletion()
        {
            if (Interlocked.CompareExchange(
                    ref _state,
                    (int)StreamBranchState.Completing,
                    (int)StreamBranchState.Running) == (int)StreamBranchState.Running)
            {
                _channel.Writer.TryComplete();
                if (Options.ShutdownPolicy == StreamShutdownPolicy.Cancel)
                    _branchCancellation.Cancel();
            }
        }

        public void RequestAbort(Exception? exception = null)
        {
            _channel.Writer.TryComplete(exception);
            _branchCancellation.Cancel();
        }

        public StreamBranchSnapshot GetSnapshot() =>
            new(
                Options.BranchId,
                Options.Name,
                Options.Delivery,
                Options.Overflow,
                (StreamBranchState)Volatile.Read(ref _state),
                Options.Capacity,
                Volatile.Read(ref _queueDepth),
                Volatile.Read(ref _queueHighWater),
                Volatile.Read(ref _accepted),
                Volatile.Read(ref _delivered),
                Volatile.Read(ref _dropped),
                Volatile.Read(ref _rejected),
                Volatile.Read(ref _abandoned),
                Volatile.Read(ref _faults),
                Volatile.Read(ref _lastFault)?.GetType().Name);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            _linkedCancellation?.Dispose();
            _branchCancellation.Dispose();
        }

        private async Task RunAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (await _channel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    while (_channel.Reader.TryRead(out var envelope))
                    {
                        envelope.MarkRemoved(DecrementQueueDepth);
                        DataflowTelemetry.ConsumerLag.Record(
                            Stopwatch.GetElapsedTime(envelope.PublishedTimestamp).TotalSeconds,
                            Tags());

                        try
                        {
                            await _consumer(envelope.Item, cancellationToken).ConfigureAwait(false);
                            Interlocked.Increment(ref _delivered);
                            DataflowTelemetry.Delivered.Add(1, Tags());
                        }
                        finally
                        {
                            ReleaseEnvelope(envelope);
                        }
                    }
                }

                var current = (StreamBranchState)Volatile.Read(ref _state);
                if (current is StreamBranchState.Completing or StreamBranchState.Running)
                    Volatile.Write(ref _state, (int)StreamBranchState.Completed);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                if ((StreamBranchState)Volatile.Read(ref _state) != StreamBranchState.Faulted)
                    Volatile.Write(ref _state, (int)StreamBranchState.Cancelled);
            }
            catch (Exception exception)
            {
                RecordFault(exception);
            }
            finally
            {
                while (_channel.Reader.TryRead(out var envelope))
                {
                    envelope.MarkRemoved(DecrementQueueDepth);
                    Interlocked.Increment(ref _abandoned);
                    DataflowTelemetry.Abandoned.Add(1, Tags());
                    ReleaseEnvelope(envelope);
                }

                if (Interlocked.Exchange(ref _activeMetric, 0) == 1)
                    DataflowTelemetry.ActiveBranches.Add(-1, Tags());
            }
        }

        private void OnDropped(Envelope envelope)
        {
            Interlocked.Exchange(ref envelope.Dropped, 1);
            envelope.MarkRemoved(DecrementQueueDepth);
            Interlocked.Increment(ref _dropped);
            DataflowTelemetry.Dropped.Add(1, Tags());
            ReleaseEnvelope(envelope);
        }

        private void RecordFault(Exception exception)
        {
            var previous = Interlocked.Exchange(ref _state, (int)StreamBranchState.Faulted);
            if (previous == (int)StreamBranchState.Faulted)
                return;

            _lastFault = exception;
            Interlocked.Increment(ref _faults);
            DataflowTelemetry.Faults.Add(1, Tags());
            _channel.Writer.TryComplete(exception);
            _branchCancellation.Cancel();
            _owner.OnBranchFault(this, exception);
        }

        private void ReleaseUnaccepted(Envelope envelope, bool countRejected)
        {
            envelope.MarkRemoved(DecrementQueueDepth);
            if (countRejected)
            {
                Interlocked.Increment(ref _rejected);
                DataflowTelemetry.Rejected.Add(1, Tags());
            }

            ReleaseEnvelope(envelope);
        }

        private StreamBranchPublishResult Accepted() =>
            new(Options.BranchId, Options.Delivery, StreamBranchPublishStatus.Accepted);

        private StreamBranchPublishResult Dropped(string reason) =>
            new(Options.BranchId, Options.Delivery, StreamBranchPublishStatus.Dropped, reason);

        private StreamBranchPublishResult Rejected(string reason) =>
            new(Options.BranchId, Options.Delivery, StreamBranchPublishStatus.Rejected, reason);

        private StreamBranchPublishResult Cancelled(string reason) =>
            new(Options.BranchId, Options.Delivery, StreamBranchPublishStatus.Cancelled, reason);

        private StreamBranchPublishResult Faulted(string reason) =>
            new(Options.BranchId, Options.Delivery, StreamBranchPublishStatus.Faulted, reason);

        private StreamBranchPublishResult ClosedResult()
        {
            var state = (StreamBranchState)Volatile.Read(ref _state);
            return state == StreamBranchState.Faulted
                ? Faulted("branch_faulted")
                : Rejected("channel_closed");
        }

        private void IncrementQueueDepth()
        {
            var depth = Interlocked.Increment(ref _queueDepth);
            DataflowTelemetry.QueueDepth.Add(1, Tags());

            while (true)
            {
                var current = Volatile.Read(ref _queueHighWater);
                if (depth <= current ||
                    Interlocked.CompareExchange(ref _queueHighWater, depth, current) == current)
                {
                    break;
                }
            }
        }

        private void DecrementQueueDepth()
        {
            Interlocked.Decrement(ref _queueDepth);
            DataflowTelemetry.QueueDepth.Add(-1, Tags());
        }

        private void ReleaseEnvelope(Envelope envelope)
        {
            if (Interlocked.Exchange(ref envelope.Released, 1) != 0)
                return;

            try
            {
                _ownership.Release(envelope.Item);
            }
            catch (Exception exception)
            {
                RecordFault(exception);
            }
        }

        private TagList Tags() => DataflowTelemetry.BranchTags(Options);

        private sealed class Envelope(T item, long publishedTimestamp)
        {
            private int _queueAccounting;

            public T Item { get; } = item;
            public long PublishedTimestamp { get; } = publishedTimestamp;
            public int Dropped;
            public int Released;

            public void MarkAccepted(Action incrementDepth)
            {
                if (Interlocked.CompareExchange(ref _queueAccounting, 1, 0) == 0)
                    incrementDepth();
            }

            public void MarkRemoved(Action decrementDepth)
            {
                if (Interlocked.Exchange(ref _queueAccounting, 2) == 1)
                    decrementDepth();
            }
        }
    }

    private sealed class NoopStreamItemOwnership<TItem> : IStreamItemOwnership<TItem>
    {
        public static NoopStreamItemOwnership<TItem> Instance { get; } = new();

        public TItem Retain(TItem item) => item;

        public void Release(TItem item)
        {
        }
    }
}
