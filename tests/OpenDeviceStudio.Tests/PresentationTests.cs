using System.Collections.Concurrent;
using Microsoft.Extensions.Time.Testing;
using OpenDeviceStudio.Abstractions.Presentation;
using OpenDeviceStudio.Dataflow;
using OpenDeviceStudio.SignalProcessing;

namespace OpenDeviceStudio.Tests;

public sealed class PresentationTests
{
    [Fact]
    public void MinMax_envelope_preserves_spike_and_actual_extreme_order()
    {
        var source = WaveformSource("raw", "epoch-a", channelCount: 1);
        using var runtime = new WaveformPresentationRuntime(
            source,
            new WaveformPresentationOptions(
                maxRetainedSamples: 100,
                maxRetainedDuration: TimeSpan.FromSeconds(10),
                maxSnapshotBuckets: 1));

        var timestamps = Enumerable.Range(0, 6)
            .Select(index => DateTimeOffset.UnixEpoch.AddMilliseconds(index))
            .ToArray();
        var values = new[] { 0d, 0d, 0d, 10d, -5d, 0d };

        Assert.True(runtime.Accept(
            WaveformPresentationBlock.CopyFrom(
                source,
                0,
                timestamps,
                values)).Accepted);

        var snapshot = runtime.BuildSnapshot(new PresentationViewport(
            timestamps[0],
            timestamps[^1],
            pixelWidth: 1,
            revision: 1));

        var series = Assert.Single(snapshot.Series);
        Assert.Equal(2, series.Points.Count);
        Assert.Equal(10d, series.Points[0].Value);
        Assert.Equal(-5d, series.Points[1].Value);
        Assert.True(series.Points[0].Timestamp < series.Points[1].Timestamp);
        Assert.Equal(3, series.Points[0].Sequence);
        Assert.Equal(4, series.Points[1].Sequence);
        Assert.True(series.Points[0].BreakBefore);
        Assert.False(series.Points[1].BreakBefore);
    }

    [Fact]
    public void MinMax_single_point_does_not_create_duplicate_render_points()
    {
        var source = WaveformSource("raw", "epoch-a", 1);
        using var runtime = new WaveformPresentationRuntime(source);

        var timestamp = DateTimeOffset.UnixEpoch;
        runtime.Accept(WaveformPresentationBlock.CopyFrom(
            source,
            10,
            [timestamp],
            [42d]));

        var snapshot = runtime.BuildSnapshot(new PresentationViewport(
            timestamp.AddMilliseconds(-1),
            timestamp.AddMilliseconds(1),
            100,
            1));

        var series = Assert.Single(snapshot.Series);
        var point = Assert.Single(series.Points);
        Assert.Equal(42d, point.Value);
        Assert.Equal(10, point.Sequence);
    }

    [Fact]
    public void Irregular_time_and_gap_are_preserved_without_cross_gap_line()
    {
        var source = WaveformSource("filtered", "epoch-a", 1);
        using var runtime = new WaveformPresentationRuntime(source);

        runtime.Accept(WaveformPresentationBlock.CopyFrom(
            source,
            0,
            [
                DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch.AddMilliseconds(1.7)
            ],
            [1d, 2d]));

        runtime.Accept(WaveformPresentationBlock.CopyFrom(
            source,
            2,
            [
                DateTimeOffset.UnixEpoch.AddMilliseconds(8.2),
                DateTimeOffset.UnixEpoch.AddMilliseconds(11.4)
            ],
            [3d, 4d],
            PresentationDataQualityFlags.SourceGap,
            breakBefore: true));

        var snapshot = runtime.BuildSnapshot(new PresentationViewport(
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch.AddMilliseconds(12),
            64,
            5));

        var points = Assert.Single(snapshot.Series).Points;
        Assert.True(points.Zip(points.Skip(1), static (left, right) =>
            left.Timestamp <= right.Timestamp).All(static ordered => ordered));
        Assert.Contains(
            points,
            point =>
                point.Timestamp == DateTimeOffset.UnixEpoch.AddMilliseconds(8.2) &&
                point.BreakBefore);
        Assert.Equal(1, snapshot.SourceGapCount);
        Assert.Equal(0, snapshot.PresentationDropCount);
    }

    [Fact]
    public void Live_window_is_time_and_sample_bounded_and_snapshot_budget_tracks_pixels()
    {
        var source = WaveformSource("raw", "epoch-a", 2);
        using var runtime = new WaveformPresentationRuntime(
            source,
            new WaveformPresentationOptions(
                maxRetainedSamples: 20,
                maxRetainedDuration: TimeSpan.FromMilliseconds(12),
                maxSnapshotBuckets: 8));

        for (var sample = 0; sample < 100; sample++)
        {
            var timestamp = DateTimeOffset.UnixEpoch.AddMilliseconds(sample);
            runtime.Accept(WaveformPresentationBlock.CopyFrom(
                source,
                sample,
                [timestamp],
                [sample, -sample]));
        }

        var state = runtime.GetSnapshot();
        Assert.InRange(state.RetainedSamples, 1, 20);
        Assert.True(state.RetentionEvictedSamples > 0);
        Assert.True(state.RetainedEnd - state.RetainedStart <= TimeSpan.FromMilliseconds(12));

        var snapshot = runtime.BuildSnapshot(new PresentationViewport(
            state.RetainedStart!.Value,
            state.RetainedEnd!.Value.AddTicks(1),
            pixelWidth: 4,
            revision: 10));

        Assert.All(snapshot.Series, static series =>
            Assert.InRange(series.Points.Count, 0, 8));
        Assert.InRange(snapshot.OutputPointCount, 0, 16);
        Assert.Equal(2, snapshot.Series.Count);
    }

    [Fact]
    public void Zoom_pan_reaggregates_from_retained_source_blocks_not_previous_snapshot()
    {
        var source = WaveformSource("raw", "epoch-a", 1);
        using var runtime = new WaveformPresentationRuntime(
            source,
            new WaveformPresentationOptions(maxSnapshotBuckets: 1));

        var timestamps = Enumerable.Range(0, 20)
            .Select(index => DateTimeOffset.UnixEpoch.AddMilliseconds(index))
            .ToArray();
        var values = Enumerable.Range(0, 20).Select(static index => (double)index).ToArray();
        values[4] = 1000;

        runtime.Accept(WaveformPresentationBlock.CopyFrom(
            source,
            0,
            timestamps,
            values));

        var early = runtime.BuildSnapshot(new PresentationViewport(
            timestamps[0],
            timestamps[9],
            1,
            1));
        var late = runtime.BuildSnapshot(new PresentationViewport(
            timestamps[10],
            timestamps[19],
            1,
            2));

        Assert.Contains(Assert.Single(early.Series).Points, static point => point.Value == 1000);
        Assert.DoesNotContain(Assert.Single(late.Series).Points, static point => point.Value == 1000);
        Assert.True(late.ViewportRevision > early.ViewportRevision);
    }

    [Fact]
    public void Source_switch_increments_generation_clears_window_and_rejects_old_epoch()
    {
        var sourceA = WaveformSource("raw", "epoch-a", 1);
        var sourceB = WaveformSource("filtered", "epoch-b", 1);
        using var runtime = new WaveformPresentationRuntime(sourceA);

        runtime.Accept(Block(sourceA, 0, 1));
        var oldGeneration = runtime.Generation;

        var generation = runtime.SwitchSource(sourceB);

        Assert.True(generation > oldGeneration);
        Assert.Equal(0, runtime.GetSnapshot().RetainedSamples);
        Assert.Equal(
            PresentationAcceptStatus.SourceMismatch,
            runtime.Accept(Block(sourceA, 1, 2)).Status);
        Assert.True(runtime.Accept(Block(sourceB, 0, 3)).Accepted);

        var snapshot = runtime.BuildSnapshot(new PresentationViewport(
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch.AddMilliseconds(5),
            10,
            1));

        Assert.Equal(generation, snapshot.Generation);
        Assert.Equal("filtered", snapshot.Source.StageId);
        Assert.DoesNotContain(
            Assert.Single(snapshot.Series).Points,
            static point => point.Value == 2);
    }

    [Fact]
    public void Source_processing_gap_and_presentation_drop_are_separate_diagnostics()
    {
        var source = WaveformSource("raw", "epoch-a", 1);
        using var runtime = new WaveformPresentationRuntime(source);

        runtime.Accept(WaveformPresentationBlock.CopyFrom(
            source,
            0,
            [DateTimeOffset.UnixEpoch],
            [1d],
            PresentationDataQualityFlags.SourceGap |
            PresentationDataQualityFlags.ProcessingGap,
            breakBefore: true));
        runtime.ObservePresentationDrops(7);

        var snapshot = runtime.BuildSnapshot(new PresentationViewport(
            DateTimeOffset.UnixEpoch.AddMilliseconds(-1),
            DateTimeOffset.UnixEpoch.AddMilliseconds(1),
            10,
            1));

        Assert.Equal(1, snapshot.SourceGapCount);
        Assert.Equal(1, snapshot.ProcessingGapCount);
        Assert.Equal(7, snapshot.PresentationDropCount);
    }

    [Fact]
    public async Task Fixed_rate_render_pump_is_timeprovider_driven_and_can_pause_without_stopping_input()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var source = WaveformSource("raw", "epoch-a", 1);
        using var runtime = new WaveformPresentationRuntime(source);
        runtime.Accept(Block(source, 0, 1));

        var snapshots = new ConcurrentQueue<WaveformRenderSnapshot>();
        var viewportRevision = 0L;
        await using var pump = new WaveformRenderPump(
            runtime,
            () => new PresentationViewport(
                DateTimeOffset.UnixEpoch.AddSeconds(-1),
                DateTimeOffset.UnixEpoch.AddSeconds(1),
                100,
                Interlocked.Increment(ref viewportRevision)),
            (snapshot, _) =>
            {
                snapshots.Enqueue(snapshot);
                return ValueTask.CompletedTask;
            },
            new WaveformRenderPumpOptions(TimeSpan.FromMilliseconds(100)),
            time);

        pump.Start();
        time.Advance(TimeSpan.FromMilliseconds(100));
        await WaitUntilAsync(() => snapshots.Count == 1);

        pump.SetActive(false);
        runtime.Accept(Block(source, 1, 2));
        time.Advance(TimeSpan.FromMilliseconds(500));
        await Task.Yield();
        Assert.Single(snapshots);
        Assert.Equal(2, runtime.GetSnapshot().AcceptedSamples);

        pump.SetActive(true);
        time.Advance(TimeSpan.FromMilliseconds(100));
        await WaitUntilAsync(() => snapshots.Count == 2);
    }

    [Fact]
    public async Task Signal_optional_tap_is_lossy_bounded_and_does_not_fault_required_processing()
    {
        var descriptor = SignalDescriptor();
        var required = new CountingStageFactory("required");
        var plan = SignalProcessingCompiler.Compile(new SignalProcessingDefinition<double>(
            "presentation-tap-v1",
            descriptor,
            [
                RequiredStage(SignalProcessingStageIds.RawInput, required)
            ]));

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var runtime = plan.CreateRuntime("session", "epoch");
        await using var tap = runtime.AttachOptionalTap(
            SignalProcessingStageIds.RawInput,
            "raw-waveform",
            capacity: 2,
            StreamOverflowPolicy.DropOldest,
            async (_, token) =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
            });

        runtime.Start();
        await runtime.ProcessAsync(SignalBlock("session", "epoch", 0, 0));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        for (var sequence = 1; sequence < 30; sequence++)
            await runtime.ProcessAsync(SignalBlock("session", "epoch", sequence, sequence));

        var tapState = tap.GetSnapshot();
        Assert.InRange(tapState.QueueDepth, 0, 2);
        Assert.True(tapState.Dropped > 0);
        Assert.Equal(SignalProcessingRuntimeState.Running, runtime.State);

        release.TrySetResult();
        var completed = await runtime.CompleteAsync();
        Assert.Equal(SignalProcessingRuntimeState.Completed, completed.State);
        Assert.Equal(30, required.Processed);
    }

    [Fact]
    public async Task Presentation_tap_can_switch_stage_without_rebuilding_processing_runtime()
    {
        var filtered = new PassSignalFactory("filtered");
        var algorithm = new PassSignalFactory("algorithm");
        var plan = SignalProcessingCompiler.Compile(new SignalProcessingDefinition<double>(
            "switch-v1",
            SignalDescriptor(),
            [
                RequiredStage(SignalProcessingStageIds.RawInput, filtered),
                RequiredStage("filtered", algorithm)
            ]));

        var rawSeen = 0;
        var filteredSeen = 0;

        await using var runtime = plan.CreateRuntime("session", "epoch");
        runtime.Start();

        await using (var raw = runtime.AttachOptionalTap(
                         SignalProcessingStageIds.RawInput,
                         "visible",
                         1,
                         StreamOverflowPolicy.Latest,
                         (_, _) =>
                         {
                             Interlocked.Increment(ref rawSeen);
                             return ValueTask.CompletedTask;
                         }))
        {
            await runtime.ProcessAsync(SignalBlock("session", "epoch", 0, 1));
            await WaitUntilAsync(() => Volatile.Read(ref rawSeen) == 1);
        }

        await using (var filteredTap = runtime.AttachOptionalTap(
                         "filtered",
                         "visible",
                         4,
                         StreamOverflowPolicy.DropOldest,
                         (_, _) =>
                         {
                             Interlocked.Increment(ref filteredSeen);
                             return ValueTask.CompletedTask;
                         }))
        {
            await runtime.ProcessAsync(SignalBlock("session", "epoch", 1, 2));
            await WaitUntilAsync(() => Volatile.Read(ref filteredSeen) == 1);
        }

        var completed = await runtime.CompleteAsync();
        Assert.Equal(SignalProcessingRuntimeState.Completed, completed.State);
        Assert.Equal(1, rawSeen);
        Assert.Equal(1, filteredSeen);
    }

    [Fact]
    public void Numeric_trend_and_event_marker_contracts_are_separate_from_waveform()
    {
        var numeric = new PresentationSourceDescriptor(
            "source",
            "rms",
            "RMS",
            PresentationSourceKind.NumericTrend,
            "epoch",
            "pipeline-v1",
            "cfg",
            "device-clock",
            [new PresentationChannelDescriptor("ch1", "Channel 1", "mV")],
            updateRateHz: 20);
        var events = new PresentationSourceDescriptor(
            "source",
            "events",
            "Events",
            PresentationSourceKind.EventMarker,
            "epoch",
            "pipeline-v1",
            "cfg",
            "device-clock",
            [new PresentationChannelDescriptor("event", "Event", "marker")]);

        var trend = new NumericTrendSnapshot(
            numeric,
            1,
            1,
            [new NumericPresentationPoint(
                DateTimeOffset.UnixEpoch,
                "ch1",
                12.3,
                PresentationDataQualityFlags.None)]);
        var marker = new EventMarkerSnapshot(
            events,
            1,
            1,
            [new PresentationEventMarker(
                DateTimeOffset.UnixEpoch,
                "artifact",
                "Artifact",
                PresentationDataQualityFlags.Artifact)]);

        Assert.Single(trend.Points);
        Assert.Single(marker.Markers);
    }

    private static PresentationSourceDescriptor WaveformSource(
        string stageId,
        string epoch,
        int channelCount)
    {
        var channels = Enumerable.Range(0, channelCount)
            .Select(index => new PresentationChannelDescriptor(
                $"ch{index}",
                $"Channel {index + 1}",
                "mV"))
            .ToArray();

        return new PresentationSourceDescriptor(
            "source-a",
            stageId,
            stageId,
            PresentationSourceKind.Waveform,
            epoch,
            "pipeline-v1",
            "cfg-v1",
            "device-clock",
            channels,
            sampleRateHz: 1000);
    }

    private static WaveformPresentationBlock Block(
        PresentationSourceDescriptor source,
        long sequence,
        double value) =>
        WaveformPresentationBlock.CopyFrom(
            source,
            sequence,
            [DateTimeOffset.UnixEpoch.AddMilliseconds(sequence)],
            [value]);

    private static SignalDescriptor SignalDescriptor() =>
        new(
            SignalSampleFormat.Float64,
            1,
            "layout-1",
            1000,
            "raw",
            "device-clock");

    private static SignalBlock<double> SignalBlock(
        string session,
        string epoch,
        long sequence,
        double value) =>
        SignalBlock<double>.CreateInput(
            session,
            epoch,
            "source-a",
            1,
            sequence,
            1,
            1,
            DateTimeOffset.UnixEpoch.AddMilliseconds(sequence),
            SignalQualityFlags.None,
            SignalDescriptor(),
            [value]);

    private static SignalStageRegistration<double> RequiredStage(
        string input,
        ISignalStageFactory<double> factory) =>
        new(
            input,
            factory,
            new SignalEdgeOptions(
                16,
                StreamBranchDelivery.Required,
                StreamOverflowPolicy.Wait,
                StreamBranchFailurePolicy.Propagate));

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!predicate())
            await Task.Delay(1, timeout.Token);
    }

    private sealed class CountingStageFactory(string stageId) : ISignalStageFactory<double>
    {
        private int _processed;

        public int Processed => Volatile.Read(ref _processed);
        public string StageId { get; } = stageId;
        public string Version => "test-v1";
        public string ConfigurationHash => "test";
        public bool IsStateful => false;
        public bool RequiresContinuity => false;
        public bool RequiresOrderedInput => true;
        public SignalGapPolicy GapPolicy => SignalGapPolicy.Fault;
        public TimeSpan AlgorithmicDelay => TimeSpan.Zero;
        public SignalWindowContract? Window => null;

        public void ValidateInput(SignalDescriptor inputDescriptor) =>
            inputDescriptor.Validate();

        public SignalDescriptor DescribeOutput(SignalDescriptor inputDescriptor) =>
            inputDescriptor;

        public ISignalStage<double> Create(SignalStageInstanceContext context) =>
            new DelegateSignalStage(input =>
            {
                Interlocked.Increment(ref _processed);
                return SignalStageResult<double>.Empty;
            });
    }

    private sealed class PassSignalFactory(string stageId) : ISignalStageFactory<double>
    {
        public string StageId { get; } = stageId;
        public string Version => "test-v1";
        public string ConfigurationHash => "test";
        public bool IsStateful => false;
        public bool RequiresContinuity => false;
        public bool RequiresOrderedInput => true;
        public SignalGapPolicy GapPolicy => SignalGapPolicy.Fault;
        public TimeSpan AlgorithmicDelay => TimeSpan.Zero;
        public SignalWindowContract? Window => null;

        public void ValidateInput(SignalDescriptor inputDescriptor) =>
            inputDescriptor.Validate();

        public SignalDescriptor DescribeOutput(SignalDescriptor inputDescriptor) =>
            inputDescriptor;

        public ISignalStage<double> Create(SignalStageInstanceContext context) =>
            new DelegateSignalStage(input =>
                SignalStageResult<double>.One(
                    SignalStageOutput<double>.CopyFromInput(
                        input,
                        input.Descriptor,
                        input.Samples.Span)));
    }

    private sealed class DelegateSignalStage(
        Func<SignalBlock<double>, SignalStageResult<double>> process)
        : ISignalStage<double>
    {
        public ValueTask InitializeAsync(CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask<SignalStageResult<double>> ProcessAsync(
            SignalBlock<double> input,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(process(input));
        }

        public ValueTask<SignalStageResult<double>> CompleteAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(SignalStageResult<double>.Empty);

        public ValueTask ResetAsync(
            SignalResetReason reason,
            CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
