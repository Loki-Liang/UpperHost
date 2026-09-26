using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using OpenDeviceStudio.Abstractions.Observability;
using OpenDeviceStudio.Dataflow;

namespace OpenDeviceStudio.SignalProcessing;

public sealed class SignalProcessingRuntime<T> : IAsyncDisposable
    where T : unmanaged
{
    private readonly CompiledSignalProcessingPlan<T> _plan;
    private readonly TimeProvider _timeProvider;
    private readonly StreamRouter<SignalBlock<T>> _inputRouter;
    private readonly Dictionary<string, StageNodeRuntime> _nodes;
    private readonly List<Task> _edgeMonitors = [];
    private readonly TaskCompletionSource<SignalProcessingSnapshot> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private SignalProcessingFault? _fault;
    private int _state = (int)SignalProcessingRuntimeState.Created;
    private int _faultOnce;
    private int _disposeOnce;

    internal SignalProcessingRuntime(
        CompiledSignalProcessingPlan<T> plan,
        string sessionId,
        string processingEpoch,
        TimeProvider? timeProvider)
    {
        _plan = plan ?? throw new ArgumentNullException(nameof(plan));
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(processingEpoch);

        SessionId = sessionId;
        ProcessingEpoch = processingEpoch;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _inputRouter = new StreamRouter<SignalBlock<T>>(_timeProvider);
        _nodes = _plan.CompiledStages.ToDictionary(
            static stage => stage.StageId,
            stage => new StageNodeRuntime(this, stage),
            StringComparer.Ordinal);

        WireGraph();
    }

    public string SessionId { get; }
    public string ProcessingEpoch { get; }
    public string PlanHash => _plan.PlanHash;
    public SignalProcessingRuntimeState State =>
        (SignalProcessingRuntimeState)Volatile.Read(ref _state);
    public Task<SignalProcessingSnapshot> Completion => _completion.Task;

    public void Start()
    {
        if (Interlocked.CompareExchange(
                ref _state,
                (int)SignalProcessingRuntimeState.Running,
                (int)SignalProcessingRuntimeState.Created) !=
            (int)SignalProcessingRuntimeState.Created)
        {
            throw new InvalidOperationException(
                $"Signal processing runtime can only start from Created; current state is {State}.");
        }

        foreach (var stageId in _plan.TopologicalOrder)
            _nodes[stageId].Router.Start();

        _inputRouter.Start();
    }

    public async ValueTask<StreamPublishResult> ProcessAsync(
        SignalBlock<T> input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (State != SignalProcessingRuntimeState.Running)
            throw new InvalidOperationException($"Signal processing runtime is not running; state={State}.");

        ValidateInput(input);

        var result = await _inputRouter
            .PublishAsync(input, cancellationToken)
            .ConfigureAwait(false);

        if (result.RouterState == StreamRouterState.Faulted || result.HasRequiredFailure)
        {
            await FaultAsync(
                    SignalProcessingStageIds.RawInput,
                    input.PartitionKey,
                    result.RouterFault ?? "Required Raw input edge did not accept the block.",
                    null)
                .ConfigureAwait(false);
        }

        return result;
    }

    public async ValueTask<SignalProcessingSnapshot> CompleteAsync(
        CancellationToken cancellationToken = default)
    {
        var previous = Interlocked.CompareExchange(
            ref _state,
            (int)SignalProcessingRuntimeState.Completing,
            (int)SignalProcessingRuntimeState.Running);

        if (previous == (int)SignalProcessingRuntimeState.Completed ||
            previous == (int)SignalProcessingRuntimeState.Faulted ||
            previous == (int)SignalProcessingRuntimeState.Disposed)
        {
            return GetSnapshot();
        }

        if (previous == (int)SignalProcessingRuntimeState.Completing)
            return await Completion.WaitAsync(cancellationToken).ConfigureAwait(false);

        if (previous != (int)SignalProcessingRuntimeState.Running)
        {
            throw new InvalidOperationException(
                $"Signal processing runtime cannot complete from state {(SignalProcessingRuntimeState)previous}.");
        }

        try
        {
            if (previous == (int)SignalProcessingRuntimeState.Running)
            {
                await _inputRouter
                    .CompleteAsync(StreamCompletionMode.Drain, cancellationToken)
                    .ConfigureAwait(false);

                foreach (var stageId in _plan.TopologicalOrder)
                {
                    if (State == SignalProcessingRuntimeState.Faulted)
                        break;

                    var node = _nodes[stageId];
                    if (node.Isolated)
                        continue;

                    await node.CompleteInstancesAsync(cancellationToken).ConfigureAwait(false);
                    await node.Router
                        .CompleteAsync(StreamCompletionMode.Drain, cancellationToken)
                        .ConfigureAwait(false);
                }

                await Task.WhenAll(_edgeMonitors)
                    .WaitAsync(cancellationToken)
                    .ConfigureAwait(false);
            }

            if (State != SignalProcessingRuntimeState.Faulted)
            {
                Volatile.Write(ref _state, (int)SignalProcessingRuntimeState.Completed);
                var snapshot = GetSnapshot();
                _completion.TrySetResult(snapshot);
                return snapshot;
            }

            return await Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (
            error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            await FaultAsync(
                    "runtime:complete",
                    null,
                    "Signal processing completion failed.",
                    error)
                .ConfigureAwait(false);
            return GetSnapshot();
        }
    }

    public async ValueTask<bool> ResetPartitionAsync(
        string stageId,
        SignalPartitionKey partitionKey,
        SignalResetReason reason = SignalResetReason.Explicit,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stageId);

        if (!_nodes.TryGetValue(stageId, out var node))
            throw new KeyNotFoundException($"Signal stage '{stageId}' is not part of this plan.");

        return await node
            .ResetPartitionAsync(partitionKey, reason, cancellationToken)
            .ConfigureAwait(false);
    }

    public SignalProcessingSnapshot GetSnapshot()
    {
        var stages = _plan.TopologicalOrder
            .Select(stageId => _nodes[stageId].GetSnapshot())
            .ToArray();

        var edges = new List<StreamBranchSnapshot>();
        edges.AddRange(_inputRouter.GetBranchSnapshots());
        foreach (var stageId in _plan.TopologicalOrder)
            edges.AddRange(_nodes[stageId].Router.GetBranchSnapshots());

        return new SignalProcessingSnapshot(
            State,
            SessionId,
            ProcessingEpoch,
            PlanHash,
            Volatile.Read(ref _fault),
            stages,
            edges);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeOnce, 1) != 0)
            return;

        Volatile.Write(ref _state, (int)SignalProcessingRuntimeState.Disposed);

        await _inputRouter.DisposeAsync().ConfigureAwait(false);
        foreach (var node in _nodes.Values)
            await node.DisposeAsync().ConfigureAwait(false);

        var snapshot = GetSnapshot();
        _completion.TrySetResult(snapshot);
    }

    private void WireGraph()
    {
        foreach (var compiled in _plan.CompiledStages)
        {
            var parent = string.Equals(
                compiled.InputStageId,
                SignalProcessingStageIds.RawInput,
                StringComparison.Ordinal)
                ? _inputRouter
                : _nodes[compiled.InputStageId].Router;

            var node = _nodes[compiled.StageId];
            var subscription = parent.RegisterBranch(
                compiled.Registration.Edge.ToRouterOptions(compiled.StageId),
                (item, token) => node.ConsumeAsync(item.Value, token));

            _edgeMonitors.Add(MonitorEdgeAsync(node, compiled, subscription));
        }
    }

    private async Task MonitorEdgeAsync(
        StageNodeRuntime node,
        CompiledSignalStage<T> compiled,
        StreamRouter<SignalBlock<T>>.StreamBranchSubscription subscription)
    {
        await subscription.Completion.ConfigureAwait(false);

        var snapshot = subscription.GetSnapshot();
        if (!snapshot.IsFaulted)
            return;

        node.RecordEdgeFault();

        var edge = compiled.Registration.Edge;
        if (edge.Delivery == StreamBranchDelivery.Required ||
            edge.FailurePolicy == StreamBranchFailurePolicy.Propagate)
        {
            await FaultAsync(
                    compiled.StageId,
                    null,
                    snapshot.FaultMessage ?? $"Required stage '{compiled.StageId}' faulted.",
                    null)
                .ConfigureAwait(false);
            return;
        }

        await node.IsolateAsync().ConfigureAwait(false);
    }

    private async ValueTask FaultAsync(
        string stageId,
        SignalPartitionKey? partitionKey,
        string message,
        Exception? exception)
    {
        if (Interlocked.Exchange(ref _faultOnce, 1) != 0)
            return;

        var fault = new SignalProcessingFault(
            stageId,
            partitionKey,
            message,
            exception,
            _timeProvider.GetUtcNow());
        Volatile.Write(ref _fault, fault);
        Volatile.Write(ref _state, (int)SignalProcessingRuntimeState.Faulted);
        SignalProcessingTelemetry.RuntimeFaults.Add(
            1,
            SignalProcessingTelemetry.ErrorTags(stageId, exception));

        try
        {
            await _inputRouter.DisposeAsync().ConfigureAwait(false);
            foreach (var node in _nodes.Values)
                await node.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _completion.TrySetResult(GetSnapshot());
        }
    }

    private void ValidateInput(SignalBlock<T> input)
    {
        if (!string.Equals(input.SessionId, SessionId, StringComparison.Ordinal))
            throw new ArgumentException("Signal block SessionId does not match runtime SessionId.", nameof(input));

        if (!string.Equals(input.ProcessingEpoch, ProcessingEpoch, StringComparison.Ordinal))
            throw new ArgumentException(
                "Signal block ProcessingEpoch does not match the frozen runtime epoch.",
                nameof(input));

        if (input.Descriptor != _plan.InputDescriptor)
        {
            throw new ArgumentException(
                "Signal block descriptor does not match the compiled plan input descriptor.",
                nameof(input));
        }
    }

    private sealed class StageNodeRuntime
    {
        private readonly SignalProcessingRuntime<T> _owner;
        private readonly CompiledSignalStage<T> _compiled;
        private readonly object _partitionGate = new();
        private readonly Dictionary<SignalPartitionKey, PartitionRuntime> _partitions = new();
        private long _blocksIn;
        private long _blocksOut;
        private long _samplesIn;
        private long _samplesOut;
        private long _gapCount;
        private long _resetCount;
        private long _droppedWhileBlocked;
        private long _faultCount;
        private int _isolated;
        private int _disposed;

        public StageNodeRuntime(
            SignalProcessingRuntime<T> owner,
            CompiledSignalStage<T> compiled)
        {
            _owner = owner;
            _compiled = compiled;
            Router = new StreamRouter<SignalBlock<T>>(owner._timeProvider);
        }

        public StreamRouter<SignalBlock<T>> Router { get; }
        public bool Isolated => Volatile.Read(ref _isolated) != 0;

        public async ValueTask ConsumeAsync(
            SignalBlock<T> input,
            CancellationToken cancellationToken)
        {
            if (Volatile.Read(ref _isolated) != 0)
                return;

            var partition = GetOrCreatePartition(input.PartitionKey);
            await partition.OperationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await partition.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

                if (partition.Blocked)
                {
                    Interlocked.Increment(ref _droppedWhileBlocked);
                    SignalProcessingTelemetry.BlockedDrops.Add(
                        1,
                        SignalProcessingTelemetry.StageTags(_compiled.StageId));
                    return;
                }

                var working = await ApplyContinuityPolicyAsync(
                        partition,
                        input,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (working is null)
                    return;

                Interlocked.Increment(ref _blocksIn);
                Interlocked.Add(ref _samplesIn, working.SampleCount);
                SignalProcessingTelemetry.BlocksIn.Add(
                    1,
                    SignalProcessingTelemetry.StageTags(_compiled.StageId));
                SignalProcessingTelemetry.SamplesIn.Add(
                    working.SampleCount,
                    SignalProcessingTelemetry.StageTags(_compiled.StageId));

                var started = _owner._timeProvider.GetTimestamp();
                SignalStageResult<T> result;
                try
                {
                    result = await partition.Stage
                        .ProcessAsync(working, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception error)
                {
                    Interlocked.Increment(ref _faultCount);
                    SignalProcessingTelemetry.StageFaults.Add(
                        1,
                        SignalProcessingTelemetry.ErrorTags(_compiled.StageId, error));
                    throw new SignalProcessingException(
                        $"Signal stage '{_compiled.StageId}' failed for partition '{input.PartitionKey}'.",
                        error);
                }
                finally
                {
                    SignalProcessingTelemetry.StageLatencyMs.Record(
                        _owner._timeProvider.GetElapsedTime(started).TotalMilliseconds,
                        SignalProcessingTelemetry.StageTags(_compiled.StageId));
                }

                partition.LastInput = working;
                await PublishOutputsAsync(working, result, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                partition.OperationGate.Release();
            }
        }

        public async ValueTask CompleteInstancesAsync(CancellationToken cancellationToken)
        {
            PartitionRuntime[] partitions;
            lock (_partitionGate)
                partitions = _partitions.Values.ToArray();

            foreach (var partition in partitions)
            {
                await partition.OperationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    if (!partition.Initialized)
                        continue;

                    var result = await partition.Stage
                        .CompleteAsync(cancellationToken)
                        .ConfigureAwait(false);

                    if (result.Outputs.Count > 0)
                    {
                        var input = partition.LastInput ??
                            throw new SignalProcessingException(
                                $"Stage '{_compiled.StageId}' emitted completion output without prior input.");
                        await PublishOutputsAsync(input, result, cancellationToken).ConfigureAwait(false);
                    }
                }
                finally
                {
                    partition.OperationGate.Release();
                }
            }
        }

        public async ValueTask<bool> ResetPartitionAsync(
            SignalPartitionKey partitionKey,
            SignalResetReason reason,
            CancellationToken cancellationToken)
        {
            PartitionRuntime? partition;
            lock (_partitionGate)
                _partitions.TryGetValue(partitionKey, out partition);

            if (partition is null)
                return false;

            await partition.OperationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await partition.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
                await partition.Stage.ResetAsync(reason, cancellationToken).ConfigureAwait(false);
                partition.Blocked = false;
                partition.ExpectedNextSequence = null;
                Interlocked.Increment(ref _resetCount);
                SignalProcessingTelemetry.Resets.Add(
                    1,
                    SignalProcessingTelemetry.StageTags(_compiled.StageId));
                return true;
            }
            finally
            {
                partition.OperationGate.Release();
            }
        }

        public SignalStageRuntimeSnapshot GetSnapshot()
        {
            int active;
            lock (_partitionGate)
                active = _partitions.Count;

            return new SignalStageRuntimeSnapshot(
                _compiled.StageId,
                active,
                Interlocked.Read(ref _blocksIn),
                Interlocked.Read(ref _blocksOut),
                Interlocked.Read(ref _samplesIn),
                Interlocked.Read(ref _samplesOut),
                Interlocked.Read(ref _gapCount),
                Interlocked.Read(ref _resetCount),
                Interlocked.Read(ref _droppedWhileBlocked),
                Interlocked.Read(ref _faultCount),
                _compiled.OutputDescriptor);
        }

        public void RecordEdgeFault() => Interlocked.Increment(ref _faultCount);

        public async ValueTask IsolateAsync()
        {
            if (Interlocked.Exchange(ref _isolated, 1) != 0)
                return;

            await Router.DisposeAsync().ConfigureAwait(false);
            await DisposePartitionsAsync().ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            await Router.DisposeAsync().ConfigureAwait(false);
            await DisposePartitionsAsync().ConfigureAwait(false);
        }

        private PartitionRuntime GetOrCreatePartition(SignalPartitionKey key)
        {
            lock (_partitionGate)
            {
                if (_partitions.TryGetValue(key, out var existing))
                    return existing;

                var context = new SignalStageInstanceContext(
                    _owner.SessionId,
                    _owner.ProcessingEpoch,
                    key,
                    _compiled.InputDescriptor,
                    _compiled.OutputDescriptor,
                    _owner._timeProvider);
                var stage = _compiled.Registration.Factory.Create(context)
                    ?? throw new SignalProcessingException(
                        $"Stage factory '{_compiled.StageId}' returned null.");
                var created = new PartitionRuntime(stage);
                _partitions.Add(key, created);
                SignalProcessingTelemetry.ActivePartitions.Add(
                    1,
                    SignalProcessingTelemetry.StageTags(_compiled.StageId));
                return created;
            }
        }

        private async ValueTask<SignalBlock<T>?> ApplyContinuityPolicyAsync(
            PartitionRuntime partition,
            SignalBlock<T> input,
            CancellationToken cancellationToken)
        {
            var expected = partition.ExpectedNextSequence;
            var endExclusive = input.SequenceEndExclusive;

            if (expected.HasValue && input.SequenceStart != expected.Value)
            {
                Interlocked.Increment(ref _gapCount);
                SignalProcessingTelemetry.Gaps.Add(
                    1,
                    SignalProcessingTelemetry.StageTags(_compiled.StageId));

                if (_compiled.Registration.Factory.RequiresContinuity)
                {
                    switch (_compiled.Registration.Factory.GapPolicy)
                    {
                        case SignalGapPolicy.Fault:
                            throw new SignalContinuityException(
                                $"Stage '{_compiled.StageId}' expected sequence {expected.Value} " +
                                $"but received {input.SequenceStart} for partition '{input.PartitionKey}'.");

                        case SignalGapPolicy.ResetAndMarkQuality:
                            await partition.Stage
                                .ResetAsync(SignalResetReason.Gap, cancellationToken)
                                .ConfigureAwait(false);
                            Interlocked.Increment(ref _resetCount);
                            SignalProcessingTelemetry.Resets.Add(
                                1,
                                SignalProcessingTelemetry.StageTags(_compiled.StageId));
                            input = input.WithQuality(
                                input.Quality |
                                SignalQualityFlags.GapDetected |
                                SignalQualityFlags.ResetAfterGap |
                                SignalQualityFlags.Discontinuous);
                            partition.ExpectedNextSequence = endExclusive;
                            break;

                        case SignalGapPolicy.DropUntilReinitialized:
                            partition.Blocked = true;
                            Interlocked.Increment(ref _droppedWhileBlocked);
                            SignalProcessingTelemetry.BlockedDrops.Add(
                                1,
                                SignalProcessingTelemetry.StageTags(_compiled.StageId));
                            partition.ExpectedNextSequence =
                                Math.Max(expected.Value, endExclusive);
                            return null;

                        default:
                            throw new ArgumentOutOfRangeException();
                    }
                }
                else
                {
                    input = input.WithQuality(
                        input.Quality |
                        SignalQualityFlags.GapDetected |
                        SignalQualityFlags.Discontinuous);
                }
            }

            if (!partition.ExpectedNextSequence.HasValue ||
                _compiled.Registration.Factory.GapPolicy != SignalGapPolicy.ResetAndMarkQuality ||
                !expected.HasValue ||
                input.SequenceStart == expected.Value)
            {
                partition.ExpectedNextSequence = expected.HasValue
                    ? Math.Max(expected.Value, endExclusive)
                    : endExclusive;
            }
            return input;
        }

        private async ValueTask PublishOutputsAsync(
            SignalBlock<T> input,
            SignalStageResult<T> result,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(result);

            foreach (var output in result.Outputs)
            {
                if (output.Descriptor != _compiled.OutputDescriptor)
                {
                    throw new SignalProcessingException(
                        $"Stage '{_compiled.StageId}' output descriptor drifted from the compiled plan.");
                }

                var normalized = SignalBlock<T>.FromStageOutput(
                    input,
                    output,
                    _owner.ProcessingEpoch,
                    _compiled.Registration.Factory);

                Interlocked.Increment(ref _blocksOut);
                Interlocked.Add(ref _samplesOut, normalized.SampleCount);
                SignalProcessingTelemetry.BlocksOut.Add(
                    1,
                    SignalProcessingTelemetry.StageTags(_compiled.StageId));
                SignalProcessingTelemetry.SamplesOut.Add(
                    normalized.SampleCount,
                    SignalProcessingTelemetry.StageTags(_compiled.StageId));

                var publish = await Router
                    .PublishAsync(normalized, cancellationToken)
                    .ConfigureAwait(false);

                if (publish.RouterState == StreamRouterState.Faulted ||
                    publish.HasRequiredFailure)
                {
                    throw new SignalProcessingException(
                        $"Stage '{_compiled.StageId}' downstream required edge failed: " +
                        (publish.RouterFault ?? "required branch did not accept output"));
                }
            }
        }

        private async ValueTask DisposePartitionsAsync()
        {
            PartitionRuntime[] partitions;
            lock (_partitionGate)
            {
                partitions = _partitions.Values.ToArray();
                _partitions.Clear();
            }

            foreach (var partition in partitions)
            {
                await partition.OperationGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                try
                {
                    await partition.Stage.DisposeAsync().ConfigureAwait(false);
                }
                finally
                {
                    partition.OperationGate.Release();
                    partition.OperationGate.Dispose();
                    SignalProcessingTelemetry.ActivePartitions.Add(
                        -1,
                        SignalProcessingTelemetry.StageTags(_compiled.StageId));
                }
            }
        }

        private sealed class PartitionRuntime(ISignalStage<T> stage)
        {
            public ISignalStage<T> Stage { get; } = stage;
            public SemaphoreSlim OperationGate { get; } = new(1, 1);
            public bool Initialized { get; private set; }
            public bool Blocked { get; set; }
            public long? ExpectedNextSequence { get; set; }
            public SignalBlock<T>? LastInput { get; set; }

            public async ValueTask EnsureInitializedAsync(CancellationToken cancellationToken)
            {
                if (Initialized)
                    return;

                await Stage.InitializeAsync(cancellationToken).ConfigureAwait(false);
                Initialized = true;
            }
        }
    }
}

internal static class SignalProcessingTelemetry
{
    private static readonly Meter Meter = OpenDeviceStudioTelemetry.Meter;

    public static readonly Counter<long> BlocksIn =
        Meter.CreateCounter<long>("opendevicestudio.signal.stage.blocks_in", "{block}");

    public static readonly Counter<long> BlocksOut =
        Meter.CreateCounter<long>("opendevicestudio.signal.stage.blocks_out", "{block}");

    public static readonly Counter<long> SamplesIn =
        Meter.CreateCounter<long>("opendevicestudio.signal.stage.samples_in", "{sample}");

    public static readonly Counter<long> SamplesOut =
        Meter.CreateCounter<long>("opendevicestudio.signal.stage.samples_out", "{sample}");

    public static readonly Counter<long> Gaps =
        Meter.CreateCounter<long>("opendevicestudio.signal.stage.gaps", "{gap}");

    public static readonly Counter<long> Resets =
        Meter.CreateCounter<long>("opendevicestudio.signal.stage.resets", "{reset}");

    public static readonly Counter<long> BlockedDrops =
        Meter.CreateCounter<long>("opendevicestudio.signal.stage.blocked_drops", "{block}");

    public static readonly Counter<long> StageFaults =
        Meter.CreateCounter<long>("opendevicestudio.signal.stage.faults", "{fault}");

    public static readonly Counter<long> RuntimeFaults =
        Meter.CreateCounter<long>("opendevicestudio.signal.runtime.faults", "{fault}");

    public static readonly UpDownCounter<long> ActivePartitions =
        Meter.CreateUpDownCounter<long>("opendevicestudio.signal.stage.active_partitions", "{partition}");

    public static readonly Histogram<double> StageLatencyMs =
        Meter.CreateHistogram<double>("opendevicestudio.signal.stage.latency", "ms");

    public static TagList StageTags(string stageId)
    {
        var tags = new TagList
        {
            { "stage.id", stageId }
        };
        return tags;
    }

    public static TagList ErrorTags(string stageId, Exception? error)
    {
        var tags = StageTags(stageId);
        if (error is not null)
            tags.Add("error.type", error.GetBaseException().GetType().Name);
        return tags;
    }
}
