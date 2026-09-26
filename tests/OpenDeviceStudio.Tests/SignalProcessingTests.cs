using System.Collections.Concurrent;
using OpenDeviceStudio.Dataflow;
using OpenDeviceStudio.SignalProcessing;
using OpenDeviceStudio.SignalProcessing.NWaves;

namespace OpenDeviceStudio.Tests;

public sealed class SignalProcessingTests
{
    [Fact]
    public void Compiler_rejects_invalid_graphs_before_runtime_start()
    {
        var descriptor = Descriptor();
        var passA = new PassThroughFactory("a");
        var passB = new PassThroughFactory("b");

        Assert.Throws<ArgumentException>(() =>
            SignalProcessingCompiler.Compile(new SignalProcessingDefinition<float>(
                "v1",
                descriptor,
                [
                    Required(SignalProcessingStageIds.RawInput, passA),
                    Required(SignalProcessingStageIds.RawInput, new PassThroughFactory("a"))
                ])));

        Assert.Throws<ArgumentException>(() =>
            SignalProcessingCompiler.Compile(new SignalProcessingDefinition<float>(
                "v1",
                descriptor,
                [Required("missing", passA)])));

        Assert.Throws<ArgumentException>(() =>
            SignalProcessingCompiler.Compile(new SignalProcessingDefinition<float>(
                "v1",
                descriptor,
                [
                    Required("b", passA),
                    Required("a", passB)
                ])));

        Assert.Throws<ArgumentException>(() =>
            SignalProcessingCompiler.Compile(new SignalProcessingDefinition<float>(
                "v1",
                descriptor,
                [
                    Required(
                        SignalProcessingStageIds.RawInput,
                        new RequireUnitsFactory("units", "mV"))
                ])));

        Assert.Throws<ArgumentException>(() =>
            SignalProcessingCompiler.Compile(new SignalProcessingDefinition<float>(
                "v1",
                descriptor,
                [
                    new SignalStageRegistration<float>(
                        SignalProcessingStageIds.RawInput,
                        new CumulativeFactory("continuity", SignalGapPolicy.Fault),
                        new SignalEdgeOptions(
                            2,
                            StreamBranchDelivery.Optional,
                            StreamOverflowPolicy.DropOldest,
                            StreamBranchFailurePolicy.Isolate))
                ])));

        var optional = new PassThroughFactory("optional-parent");
        Assert.Throws<ArgumentException>(() =>
            SignalProcessingCompiler.Compile(new SignalProcessingDefinition<float>(
                "v1",
                descriptor,
                [
                    new SignalStageRegistration<float>(
                        SignalProcessingStageIds.RawInput,
                        optional,
                        new SignalEdgeOptions(
                            2,
                            StreamBranchDelivery.Optional,
                            StreamOverflowPolicy.DropOldest,
                            StreamBranchFailurePolicy.Isolate)),
                    Required("optional-parent", new PassThroughFactory("fake-required-child"))
                ])));
    }

    [Fact]
    public async Task Nwaves_moving_average_is_chunk_boundary_equivalent_and_has_known_steady_state()
    {
        var firstCapture = new CaptureFactory("capture");
        var secondCapture = new CaptureFactory("capture");

        var firstPlan = SignalProcessingCompiler.Compile(new SignalProcessingDefinition<float>(
            "moving-average-v1",
            Descriptor(),
            [
                Required(
                    SignalProcessingStageIds.RawInput,
                    new NwavesMovingAverageStageFactory("ma", 3)),
                Required("ma", firstCapture)
            ]));

        var secondPlan = SignalProcessingCompiler.Compile(new SignalProcessingDefinition<float>(
            "moving-average-v1",
            Descriptor(),
            [
                Required(
                    SignalProcessingStageIds.RawInput,
                    new NwavesMovingAverageStageFactory("ma", 3)),
                Required("ma", secondCapture)
            ]));

        Assert.Equal(firstPlan.PlanHash, secondPlan.PlanHash);

        await using (var runtime = firstPlan.CreateRuntime("online", "epoch-a"))
        {
            runtime.Start();
            await runtime.ProcessAsync(Block("online", "epoch-a", "source-a", 0, [1, 2]));
            await runtime.ProcessAsync(Block("online", "epoch-a", "source-a", 2, [3, 4, 5]));
            var completed = await runtime.CompleteAsync();
            Assert.Equal(SignalProcessingRuntimeState.Completed, completed.State);
        }

        await using (var runtime = secondPlan.CreateRuntime("replay", "epoch-b"))
        {
            runtime.Start();
            await runtime.ProcessAsync(Block("replay", "epoch-b", "source-a", 0, [1, 2, 3, 4, 5]));
            var completed = await runtime.CompleteAsync();
            Assert.Equal(SignalProcessingRuntimeState.Completed, completed.State);
        }

        var first = Flatten(firstCapture.Received);
        var second = Flatten(secondCapture.Received);

        Assert.Equal(first.Length, second.Length);
        for (var i = 0; i < first.Length; i++)
            Assert.InRange(Math.Abs(first[i] - second[i]), 0, 1e-5f);

        Assert.InRange(Math.Abs(first[2] - 2f), 0, 1e-5f);
        Assert.InRange(Math.Abs(first[3] - 3f), 0, 1e-5f);
        Assert.InRange(Math.Abs(first[4] - 4f), 0, 1e-5f);
    }

    [Fact]
    public async Task Stateful_stage_instances_are_isolated_per_source_partition()
    {
        var capture = new CaptureFactory("capture");
        var plan = SignalProcessingCompiler.Compile(new SignalProcessingDefinition<float>(
            "partition-v1",
            Descriptor(),
            [
                Required(
                    SignalProcessingStageIds.RawInput,
                    new CumulativeFactory("accumulate", SignalGapPolicy.Fault)),
                Required("accumulate", capture)
            ]));

        await using var runtime = plan.CreateRuntime("session", "epoch");
        runtime.Start();

        await runtime.ProcessAsync(Block("session", "epoch", "source-a", 0, [1]));
        await runtime.ProcessAsync(Block("session", "epoch", "source-b", 0, [10]));
        await runtime.ProcessAsync(Block("session", "epoch", "source-a", 1, [2]));
        await runtime.ProcessAsync(Block("session", "epoch", "source-b", 1, [20]));
        await runtime.CompleteAsync();

        var a = Flatten(capture.Received.Where(static block => block.SourceId == "source-a"));
        var b = Flatten(capture.Received.Where(static block => block.SourceId == "source-b"));

        Assert.Equal(new[] { 1f, 3f }, a);
        Assert.Equal(new[] { 10f, 30f }, b);

        var stage = runtime.GetSnapshot().Stages.Single(static item => item.StageId == "accumulate");
        Assert.Equal(2, stage.ActivePartitions);
    }

    [Fact]
    public async Task Gap_reset_policy_resets_state_and_marks_quality()
    {
        var capture = new CaptureFactory("capture");
        var plan = SignalProcessingCompiler.Compile(new SignalProcessingDefinition<float>(
            "gap-reset-v1",
            Descriptor(),
            [
                Required(
                    SignalProcessingStageIds.RawInput,
                    new CumulativeFactory("accumulate", SignalGapPolicy.ResetAndMarkQuality)),
                Required("accumulate", capture)
            ]));

        await using var runtime = plan.CreateRuntime("session", "epoch");
        runtime.Start();

        await runtime.ProcessAsync(Block("session", "epoch", "source-a", 0, [1]));
        await runtime.ProcessAsync(Block("session", "epoch", "source-a", 2, [10]));
        await runtime.CompleteAsync();

        var blocks = capture.Received
            .OrderBy(static block => block.SequenceStart)
            .ToArray();

        Assert.Equal(2, blocks.Length);
        Assert.Equal(1f, blocks[0].Samples.Span[0]);
        Assert.Equal(10f, blocks[1].Samples.Span[0]);
        Assert.True(blocks[1].Quality.HasFlag(SignalQualityFlags.GapDetected));
        Assert.True(blocks[1].Quality.HasFlag(SignalQualityFlags.ResetAfterGap));
        Assert.True(blocks[1].Quality.HasFlag(SignalQualityFlags.Discontinuous));

        var stage = runtime.GetSnapshot().Stages.Single(static item => item.StageId == "accumulate");
        Assert.Equal(1, stage.GapCount);
        Assert.Equal(1, stage.ResetCount);
    }

    [Fact]
    public async Task Drop_until_reinitialized_blocks_partition_until_explicit_reset()
    {
        var capture = new CaptureFactory("capture");
        var plan = SignalProcessingCompiler.Compile(new SignalProcessingDefinition<float>(
            "gap-block-v1",
            Descriptor(),
            [
                Required(
                    SignalProcessingStageIds.RawInput,
                    new CumulativeFactory("accumulate", SignalGapPolicy.DropUntilReinitialized)),
                Required("accumulate", capture)
            ]));

        await using var runtime = plan.CreateRuntime("session", "epoch");
        runtime.Start();

        await runtime.ProcessAsync(Block("session", "epoch", "source-a", 0, [1]));
        await runtime.ProcessAsync(Block("session", "epoch", "source-a", 2, [10]));
        await runtime.ProcessAsync(Block("session", "epoch", "source-a", 3, [20]));

        Assert.True(await runtime.ResetPartitionAsync(
            "accumulate",
            new SignalPartitionKey("source-a", "layout-1")));

        await runtime.ProcessAsync(Block("session", "epoch", "source-a", 4, [5]));
        await runtime.CompleteAsync();

        var values = Flatten(capture.Received);
        Assert.Equal(new[] { 1f, 5f }, values);

        var stage = runtime.GetSnapshot().Stages.Single(static item => item.StageId == "accumulate");
        Assert.Equal(2, stage.DroppedWhileBlocked);
        Assert.Equal(1, stage.ResetCount);
    }

    [Fact]
    public async Task Optional_algorithm_fault_is_isolated_while_required_path_continues()
    {
        var capture = new CaptureFactory("capture");
        var pass = new PassThroughFactory("conditioned");
        var optional = new ThrowingFactory("optional-algorithm", throwOnCall: 1);

        var plan = SignalProcessingCompiler.Compile(new SignalProcessingDefinition<float>(
            "optional-fault-v1",
            Descriptor(),
            [
                Required(SignalProcessingStageIds.RawInput, pass),
                Required("conditioned", capture),
                new SignalStageRegistration<float>(
                    "conditioned",
                    optional,
                    new SignalEdgeOptions(
                        2,
                        StreamBranchDelivery.Optional,
                        StreamOverflowPolicy.DropOldest,
                        StreamBranchFailurePolicy.Isolate))
            ]));

        await using var runtime = plan.CreateRuntime("session", "epoch");
        runtime.Start();

        for (var i = 0; i < 6; i++)
            await runtime.ProcessAsync(Block("session", "epoch", "source-a", i, [(float)i]));

        var completed = await runtime.CompleteAsync();

        Assert.Equal(SignalProcessingRuntimeState.Completed, completed.State);
        Assert.Null(completed.Fault);
        Assert.Equal(6, capture.Received.Count);

        var optionalSnapshot = completed.Stages.Single(
            static item => item.StageId == "optional-algorithm");
        Assert.True(optionalSnapshot.FaultCount >= 1);
    }

    [Fact]
    public async Task Required_stage_fault_converges_runtime_to_faulted()
    {
        var plan = SignalProcessingCompiler.Compile(new SignalProcessingDefinition<float>(
            "required-fault-v1",
            Descriptor(),
            [
                Required(
                    SignalProcessingStageIds.RawInput,
                    new ThrowingFactory("required-stage", throwOnCall: 1))
            ]));

        await using var runtime = plan.CreateRuntime("session", "epoch");
        runtime.Start();
        await runtime.ProcessAsync(Block("session", "epoch", "source-a", 0, [1]));

        var terminal = await runtime.Completion.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(SignalProcessingRuntimeState.Faulted, terminal.State);
        Assert.NotNull(terminal.Fault);
        Assert.Equal("required-stage", terminal.Fault!.StageId);
    }

    [Fact]
    public async Task Slow_optional_stage_uses_bounded_router_qos_without_stalling_required_stage()
    {
        var capture = new CaptureFactory("capture");
        var slow = new GatedFactory("slow-ui");
        var plan = SignalProcessingCompiler.Compile(new SignalProcessingDefinition<float>(
            "slow-optional-v1",
            Descriptor(),
            [
                Required(SignalProcessingStageIds.RawInput, capture),
                new SignalStageRegistration<float>(
                    SignalProcessingStageIds.RawInput,
                    slow,
                    new SignalEdgeOptions(
                        2,
                        StreamBranchDelivery.Optional,
                        StreamOverflowPolicy.DropOldest,
                        StreamBranchFailurePolicy.Isolate))
            ]));

        await using var runtime = plan.CreateRuntime("session", "epoch");
        runtime.Start();

        await runtime.ProcessAsync(Block("session", "epoch", "source-a", 0, [0]));
        await slow.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        for (var i = 1; i < 20; i++)
            await runtime.ProcessAsync(Block("session", "epoch", "source-a", i, [(float)i]));

        var edge = runtime.GetSnapshot().Edges.Single(
            static item => item.BranchId == "stage:slow-ui");
        Assert.InRange(edge.QueueDepth, 0, 2);
        Assert.InRange(edge.HighWatermark, 0, 2);
        Assert.True(edge.Dropped > 0);

        slow.Release.TrySetResult();
        var completed = await runtime.CompleteAsync();

        Assert.Equal(SignalProcessingRuntimeState.Completed, completed.State);
        Assert.Equal(20, capture.Received.Count);
    }

    [Fact]
    public async Task Lineage_and_online_replay_outputs_use_same_plan_and_stage_contract()
    {
        var onlineCapture = new CaptureFactory("capture");
        var replayCapture = new CaptureFactory("capture");
        var onlinePlan = BuildLineagePlan(onlineCapture);
        var replayPlan = BuildLineagePlan(replayCapture);

        Assert.Equal(onlinePlan.PlanHash, replayPlan.PlanHash);

        await using (var online = onlinePlan.CreateRuntime("online-session", "epoch-online"))
        {
            online.Start();
            await online.ProcessAsync(Block(
                "online-session",
                "epoch-online",
                "source-a",
                100,
                [1, 2, 3]));
            await online.CompleteAsync();
        }

        await using (var replay = replayPlan.CreateRuntime("replay-session", "epoch-replay"))
        {
            replay.Start();
            await replay.ProcessAsync(Block(
                "replay-session",
                "epoch-replay",
                "source-a",
                100,
                [1, 2, 3]));
            await replay.CompleteAsync();
        }

        var onlineBlock = Assert.Single(onlineCapture.Received);
        var replayBlock = Assert.Single(replayCapture.Received);

        Assert.Equal(onlineBlock.Samples.ToArray(), replayBlock.Samples.ToArray());
        Assert.Equal(onlineBlock.SequenceStart, replayBlock.SequenceStart);
        Assert.Equal(onlineBlock.SequenceEndExclusive, replayBlock.SequenceEndExclusive);
        Assert.Equal(onlineBlock.Descriptor, replayBlock.Descriptor);

        var onlineLineage = Assert.Single(onlineBlock.Lineage.Stages);
        var replayLineage = Assert.Single(replayBlock.Lineage.Stages);
        Assert.Equal("calibration", onlineLineage.StageId);
        Assert.Equal(onlineLineage.StageVersion, replayLineage.StageVersion);
        Assert.Equal(onlineLineage.ConfigurationHash, replayLineage.ConfigurationHash);
        Assert.Equal(100, onlineLineage.InputRange.SequenceStart);
        Assert.Equal(103, onlineLineage.InputRange.SequenceEndExclusive);
        Assert.Equal("epoch-online", onlineBlock.Lineage.ProcessingEpoch);
        Assert.Equal("epoch-replay", replayBlock.Lineage.ProcessingEpoch);
    }

    private static CompiledSignalProcessingPlan<float> BuildLineagePlan(CaptureFactory capture) =>
        SignalProcessingCompiler.Compile(new SignalProcessingDefinition<float>(
            "lineage-v1",
            Descriptor(),
            [
                Required(
                    SignalProcessingStageIds.RawInput,
                    new GainFactory("calibration", 2f)),
                Required("calibration", capture)
            ]));

    private static SignalStageRegistration<float> Required(
        string input,
        ISignalStageFactory<float> factory,
        int capacity = 16) =>
        new(
            input,
            factory,
            new SignalEdgeOptions(
                capacity,
                StreamBranchDelivery.Required,
                StreamOverflowPolicy.Wait,
                StreamBranchFailurePolicy.Propagate));

    private static SignalDescriptor Descriptor(string units = "raw-count") =>
        new(
            SignalSampleFormat.Float32,
            1,
            "layout-1",
            1000,
            units,
            "device-clock");

    private static SignalBlock<float> Block(
        string session,
        string epoch,
        string source,
        long sequenceStart,
        float[] samples)
    {
        var timestamp = DateTimeOffset.UnixEpoch.AddMilliseconds(sequenceStart);
        return SignalBlock<float>.CreateInput(
            session,
            epoch,
            source,
            connectionEpoch: 1,
            sequenceStart,
            sequenceCount: samples.Length,
            sampleCount: samples.Length,
            effectiveTimestamp: timestamp,
            quality: SignalQualityFlags.None,
            descriptor: Descriptor(),
            samples,
            inputRangeEnd: timestamp + TimeSpan.FromMilliseconds(samples.Length));
    }

    private static float[] Flatten(IEnumerable<SignalBlock<float>> blocks) =>
        blocks
            .OrderBy(static block => block.SequenceStart)
            .SelectMany(static block => block.Samples.ToArray())
            .ToArray();

    private sealed class PassThroughFactory(string stageId) : FactoryBase(stageId)
    {
        public override ISignalStage<float> Create(SignalStageInstanceContext context) =>
            new DelegateStage(input =>
                SignalStageResult<float>.One(
                    SignalStageOutput<float>.CopyFromInput(
                        input,
                        input.Descriptor,
                        input.Samples.Span)));
    }

    private sealed class GainFactory : FactoryBase
    {
        private readonly float _gain;

        public GainFactory(string stageId, float gain)
            : base(stageId)
        {
            _gain = gain;
            GainConfigurationHash = SignalConfigurationHash.Compute(
                new Dictionary<string, string>
                {
                    ["gain"] = gain.ToString(System.Globalization.CultureInfo.InvariantCulture)
                });
        }

        private string GainConfigurationHash { get; }
        public override string ConfigurationHash => GainConfigurationHash;

        public override ISignalStage<float> Create(SignalStageInstanceContext context) =>
            new DelegateStage(input =>
            {
                var source = input.Samples.Span;
                var output = new float[source.Length];
                for (var i = 0; i < source.Length; i++)
                    output[i] = source[i] * _gain;

                return SignalStageResult<float>.One(
                    SignalStageOutput<float>.CopyFromInput(
                        input,
                        input.Descriptor,
                        output));
            });
    }

    private sealed class RequireUnitsFactory(string stageId, string units) : FactoryBase(stageId)
    {
        public override void ValidateInput(SignalDescriptor inputDescriptor)
        {
            base.ValidateInput(inputDescriptor);
            if (!string.Equals(inputDescriptor.Units, units, StringComparison.Ordinal))
                throw new ArgumentException($"Expected units '{units}'.");
        }

        public override ISignalStage<float> Create(SignalStageInstanceContext context) =>
            new DelegateStage(static input =>
                SignalStageResult<float>.One(
                    SignalStageOutput<float>.CopyFromInput(
                        input,
                        input.Descriptor,
                        input.Samples.Span)));
    }

    private sealed class CaptureFactory(string stageId) : FactoryBase(stageId)
    {
        public ConcurrentQueue<SignalBlock<float>> Received { get; } = new();

        public override ISignalStage<float> Create(SignalStageInstanceContext context) =>
            new DelegateStage(input =>
            {
                Received.Enqueue(input);
                return SignalStageResult<float>.Empty;
            });
    }

    private sealed class CumulativeFactory : FactoryBase
    {
        public CumulativeFactory(string stageId, SignalGapPolicy gapPolicy)
            : base(stageId)
        {
            GapPolicy = gapPolicy;
        }

        public override bool IsStateful => true;
        public override bool RequiresContinuity => true;
        public override SignalGapPolicy GapPolicy { get; }

        public override ISignalStage<float> Create(SignalStageInstanceContext context) =>
            new CumulativeStage(context.OutputDescriptor.ChannelCount);
    }

    private sealed class ThrowingFactory : FactoryBase
    {
        private readonly int _throwOnCall;

        public ThrowingFactory(string stageId, int throwOnCall)
            : base(stageId)
        {
            _throwOnCall = throwOnCall;
        }

        public override ISignalStage<float> Create(SignalStageInstanceContext context) =>
            new ThrowingStage(_throwOnCall);
    }

    private sealed class GatedFactory(string stageId) : FactoryBase(stageId)
    {
        public TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override ISignalStage<float> Create(SignalStageInstanceContext context) =>
            new GatedStage(Entered, Release);
    }

    private abstract class FactoryBase : ISignalStageFactory<float>
    {
        protected FactoryBase(string stageId)
        {
            StageId = stageId;
            ConfigurationHash = SignalConfigurationHash.Compute(
                new Dictionary<string, string> { ["stage"] = stageId });
        }

        public string StageId { get; }
        public virtual string Version => "test-v1";
        public virtual string ConfigurationHash { get; }
        public virtual bool IsStateful => false;
        public virtual bool RequiresContinuity => false;
        public virtual bool RequiresOrderedInput => true;
        public virtual SignalGapPolicy GapPolicy => SignalGapPolicy.Fault;
        public virtual TimeSpan AlgorithmicDelay => TimeSpan.Zero;
        public virtual SignalWindowContract? Window => null;

        public virtual void ValidateInput(SignalDescriptor inputDescriptor)
        {
            inputDescriptor.Validate();
            if (inputDescriptor.SampleFormat != SignalSampleFormat.Float32)
                throw new ArgumentException("Test stages require Float32.");
        }

        public virtual SignalDescriptor DescribeOutput(SignalDescriptor inputDescriptor)
        {
            ValidateInput(inputDescriptor);
            return inputDescriptor;
        }

        public abstract ISignalStage<float> Create(SignalStageInstanceContext context);
    }

    private sealed class DelegateStage(
        Func<SignalBlock<float>, SignalStageResult<float>> process) : ISignalStage<float>
    {
        public ValueTask InitializeAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }

        public ValueTask<SignalStageResult<float>> ProcessAsync(
            SignalBlock<float> input,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(process(input));
        }

        public ValueTask<SignalStageResult<float>> CompleteAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(SignalStageResult<float>.Empty);
        }

        public ValueTask ResetAsync(
            SignalResetReason reason,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class CumulativeStage(int channelCount) : ISignalStage<float>
    {
        private readonly float[] _sum = new float[channelCount];

        public ValueTask InitializeAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }

        public ValueTask<SignalStageResult<float>> ProcessAsync(
            SignalBlock<float> input,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = input.Samples.Span;
            var output = new float[source.Length];
            var channels = input.Descriptor.ChannelCount;

            for (var sample = 0; sample < input.SampleCount; sample++)
            {
                var offset = sample * channels;
                for (var channel = 0; channel < channels; channel++)
                {
                    _sum[channel] += source[offset + channel];
                    output[offset + channel] = _sum[channel];
                }
            }

            return ValueTask.FromResult(
                SignalStageResult<float>.One(
                    SignalStageOutput<float>.CopyFromInput(
                        input,
                        input.Descriptor,
                        output)));
        }

        public ValueTask<SignalStageResult<float>> CompleteAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(SignalStageResult<float>.Empty);
        }

        public ValueTask ResetAsync(
            SignalResetReason reason,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Array.Clear(_sum);
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ThrowingStage(int throwOnCall) : ISignalStage<float>
    {
        private int _calls;

        public ValueTask InitializeAsync(CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask<SignalStageResult<float>> ProcessAsync(
            SignalBlock<float> input,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Interlocked.Increment(ref _calls) >= throwOnCall)
                throw new InvalidOperationException("Injected algorithm failure.");

            return ValueTask.FromResult(SignalStageResult<float>.Empty);
        }

        public ValueTask<SignalStageResult<float>> CompleteAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(SignalStageResult<float>.Empty);

        public ValueTask ResetAsync(
            SignalResetReason reason,
            CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class GatedStage(
        TaskCompletionSource entered,
        TaskCompletionSource release) : ISignalStage<float>
    {
        public ValueTask InitializeAsync(CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public async ValueTask<SignalStageResult<float>> ProcessAsync(
            SignalBlock<float> input,
            CancellationToken cancellationToken = default)
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            return SignalStageResult<float>.Empty;
        }

        public ValueTask<SignalStageResult<float>> CompleteAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(SignalStageResult<float>.Empty);

        public ValueTask ResetAsync(
            SignalResetReason reason,
            CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
