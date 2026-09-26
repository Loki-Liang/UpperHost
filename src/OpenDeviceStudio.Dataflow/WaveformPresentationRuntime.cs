using System.Diagnostics;
using System.Diagnostics.Metrics;
using OpenDeviceStudio.Abstractions.Observability;
using OpenDeviceStudio.Abstractions.Presentation;

namespace OpenDeviceStudio.Dataflow;

public enum PresentationAcceptStatus
{
    Accepted,
    SourceMismatch,
    OutOfOrder,
    Disposed
}

public sealed record PresentationAcceptResult(
    PresentationAcceptStatus Status,
    string? Reason = null)
{
    public bool Accepted => Status == PresentationAcceptStatus.Accepted;

    public static PresentationAcceptResult Success { get; } =
        new(PresentationAcceptStatus.Accepted);
}

public sealed record WaveformPresentationRuntimeSnapshot(
    PresentationSourceDescriptor Source,
    long Generation,
    int RetainedBlocks,
    int RetainedSamples,
    int MaxRetainedSamples,
    DateTimeOffset? RetainedStart,
    DateTimeOffset? RetainedEnd,
    long AcceptedBlocks,
    long AcceptedSamples,
    long RetentionEvictedBlocks,
    long RetentionEvictedSamples,
    long OutOfOrderBlocks,
    long SourceGapCount,
    long ProcessingGapCount,
    long ProcessingFaultCount,
    long PresentationDropCount,
    long SnapshotCount,
    long SnapshotInputSamples,
    long SnapshotOutputPoints);

public sealed record WaveformPresentationOptions
{
    public WaveformPresentationOptions(
        int maxRetainedSamples = 200_000,
        TimeSpan? maxRetainedDuration = null,
        int maxSnapshotBuckets = 4096,
        PresentationDownsamplingMode downsamplingMode = PresentationDownsamplingMode.MinMaxEnvelope)
    {
        if (maxRetainedSamples <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxRetainedSamples));

        var duration = maxRetainedDuration ?? TimeSpan.FromSeconds(30);
        if (duration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(maxRetainedDuration));

        if (maxSnapshotBuckets <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxSnapshotBuckets));

        MaxRetainedSamples = maxRetainedSamples;
        MaxRetainedDuration = duration;
        MaxSnapshotBuckets = maxSnapshotBuckets;
        DownsamplingMode = downsamplingMode;
    }

    public int MaxRetainedSamples { get; }
    public TimeSpan MaxRetainedDuration { get; }
    public int MaxSnapshotBuckets { get; }
    public PresentationDownsamplingMode DownsamplingMode { get; }
}

public interface IPresentationDownsampler
{
    PresentationDownsamplingMode Mode { get; }

    PresentationDownsamplingResult Downsample(
        PresentationSourceDescriptor source,
        IReadOnlyList<WaveformPresentationBlock> blocks,
        PresentationViewport viewport,
        int maxBuckets);
}

public sealed record PresentationDownsamplingResult(
    IReadOnlyList<PresentationChannelRenderSeries> Series,
    long InputSamples,
    long OutputPoints);

public sealed class WaveformPresentationRuntime : IDisposable
{
    private readonly object _gate = new();
    private readonly WaveformPresentationOptions _options;
    private readonly IReadOnlyDictionary<PresentationDownsamplingMode, IPresentationDownsampler> _downsamplers;
    private readonly Queue<WaveformPresentationBlock> _blocks = new();

    private PresentationSourceDescriptor _source;
    private int _retainedSamples;
    private long _generation = 1;
    private DateTimeOffset? _lastTimestamp;
    private long _acceptedBlocks;
    private long _acceptedSamples;
    private long _retentionEvictedBlocks;
    private long _retentionEvictedSamples;
    private long _outOfOrderBlocks;
    private long _sourceGapCount;
    private long _processingGapCount;
    private long _processingFaultCount;
    private long _presentationDropCount;
    private long _snapshotCount;
    private long _snapshotInputSamples;
    private long _snapshotOutputPoints;
    private int _disposed;

    public WaveformPresentationRuntime(
        PresentationSourceDescriptor source,
        WaveformPresentationOptions? options = null,
        IEnumerable<IPresentationDownsampler>? downsamplers = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Kind != PresentationSourceKind.Waveform)
            throw new ArgumentException("Waveform runtime requires a Waveform source.", nameof(source));

        _source = source;
        _options = options ?? new WaveformPresentationOptions();

        var builtIns = new IPresentationDownsampler[]
        {
            new MinMaxEnvelopeDownsampler(),
            new UniformPresentationDownsampler(),
            new LatestPresentationDownsampler()
        };

        var map = builtIns
            .Concat(downsamplers ?? Array.Empty<IPresentationDownsampler>())
            .GroupBy(static item => item.Mode)
            .ToDictionary(static group => group.Key, static group => group.Last());

        if (!map.ContainsKey(_options.DownsamplingMode))
        {
            throw new ArgumentException(
                $"No downsampler is registered for {_options.DownsamplingMode}.",
                nameof(downsamplers));
        }

        _downsamplers = map;
    }

    public PresentationSourceDescriptor Source
    {
        get
        {
            lock (_gate)
                return _source;
        }
    }

    public long Generation => Interlocked.Read(ref _generation);

    public PresentationAcceptResult Accept(WaveformPresentationBlock block)
    {
        ArgumentNullException.ThrowIfNull(block);

        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return new PresentationAcceptResult(
                    PresentationAcceptStatus.Disposed,
                    "Presentation runtime is disposed.");
            }

            if (!DescriptorsMatch(_source, block.Source))
            {
                PresentationTelemetry.SourceMismatches.Add(
                    1,
                    PresentationTelemetry.SourceTags(_source));
                return new PresentationAcceptResult(
                    PresentationAcceptStatus.SourceMismatch,
                    "Presentation block descriptor does not match the current source epoch.");
            }

            if (_lastTimestamp.HasValue && block.StartTimestamp < _lastTimestamp.Value)
            {
                _outOfOrderBlocks++;
                _presentationDropCount++;
                PresentationTelemetry.OutOfOrderBlocks.Add(
                    1,
                    PresentationTelemetry.SourceTags(_source));
                PresentationTelemetry.Dropped.Add(
                    1,
                    PresentationTelemetry.SourceTags(_source));
                return new PresentationAcceptResult(
                    PresentationAcceptStatus.OutOfOrder,
                    "Presentation block timestamp moved backwards and was dropped.");
            }

            if (block.BreakBefore ||
                block.Quality.HasFlag(PresentationDataQualityFlags.SourceGap))
            {
                _sourceGapCount++;
            }

            if (block.Quality.HasFlag(PresentationDataQualityFlags.ProcessingGap) ||
                block.Quality.HasFlag(PresentationDataQualityFlags.Discontinuous))
            {
                _processingGapCount++;
            }

            if (block.Quality.HasFlag(PresentationDataQualityFlags.ProcessingFault))
                _processingFaultCount++;

            _blocks.Enqueue(block);
            _retainedSamples += block.SampleCount;
            _acceptedBlocks++;
            _acceptedSamples += block.SampleCount;
            _lastTimestamp = block.EndTimestamp;

            EvictRetentionLocked();

            var tags = PresentationTelemetry.SourceTags(_source);
            PresentationTelemetry.InputBlocks.Add(1, tags);
            PresentationTelemetry.InputSamples.Add(block.SampleCount, tags);
            PresentationTelemetry.BufferDepth.Record(_retainedSamples, tags);
            return PresentationAcceptResult.Success;
        }
    }

    public long SwitchSource(PresentationSourceDescriptor source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Kind != PresentationSourceKind.Waveform)
            throw new ArgumentException("Waveform runtime requires a Waveform source.", nameof(source));

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

            _source = source;
            _blocks.Clear();
            _retainedSamples = 0;
            _lastTimestamp = null;
            _acceptedBlocks = 0;
            _acceptedSamples = 0;
            _retentionEvictedBlocks = 0;
            _retentionEvictedSamples = 0;
            _outOfOrderBlocks = 0;
            _sourceGapCount = 0;
            _processingGapCount = 0;
            _processingFaultCount = 0;
            _presentationDropCount = 0;
            _snapshotCount = 0;
            _snapshotInputSamples = 0;
            _snapshotOutputPoints = 0;

            var generation = Interlocked.Increment(ref _generation);
            PresentationTelemetry.SourceSwitches.Add(
                1,
                PresentationTelemetry.SourceTags(source));
            return generation;
        }
    }

    public void ObservePresentationDrops(long totalDropped)
    {
        if (totalDropped < 0)
            throw new ArgumentOutOfRangeException(nameof(totalDropped));

        lock (_gate)
        {
            if (totalDropped <= _presentationDropCount)
                return;

            var delta = totalDropped - _presentationDropCount;
            _presentationDropCount = totalDropped;
            PresentationTelemetry.Dropped.Add(
                delta,
                PresentationTelemetry.SourceTags(_source));
        }
    }

    public WaveformRenderSnapshot BuildSnapshot(
        PresentationViewport viewport,
        TimeProvider? timeProvider = null,
        PresentationDownsamplingMode? mode = null)
    {
        ArgumentNullException.ThrowIfNull(viewport);

        PresentationSourceDescriptor source;
        WaveformPresentationBlock[] blocks;
        long generation;
        long sourceGaps;
        long processingGaps;
        long processingFaults;
        long presentationDrops;
        DateTimeOffset? retainedStart;
        DateTimeOffset? retainedEnd;

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

            source = _source;
            blocks = _blocks.ToArray();
            generation = Interlocked.Read(ref _generation);
            sourceGaps = _sourceGapCount;
            processingGaps = _processingGapCount;
            processingFaults = _processingFaultCount;
            presentationDrops = _presentationDropCount;
            retainedStart = blocks.Length == 0 ? null : blocks[0].StartTimestamp;
            retainedEnd = blocks.Length == 0 ? null : blocks[^1].EndTimestamp;
        }

        var selectedMode = mode ?? _options.DownsamplingMode;
        if (!_downsamplers.TryGetValue(selectedMode, out var downsampler))
            throw new InvalidOperationException($"No downsampler is registered for {selectedMode}.");

        var started = Stopwatch.GetTimestamp();
        var result = downsampler.Downsample(
            source,
            blocks,
            viewport,
            Math.Min(viewport.PixelWidth, _options.MaxSnapshotBuckets));
        var elapsed = Stopwatch.GetElapsedTime(started);

        var requestedRangeAvailable =
            retainedStart.HasValue &&
            retainedEnd.HasValue &&
            viewport.Start >= retainedStart.Value &&
            viewport.End <= retainedEnd.Value;

        lock (_gate)
        {
            if (generation == Interlocked.Read(ref _generation))
            {
                _snapshotCount++;
                _snapshotInputSamples += result.InputSamples;
                _snapshotOutputPoints += result.OutputPoints;
            }
        }

        var tags = PresentationTelemetry.SourceTags(source);
        PresentationTelemetry.Snapshots.Add(1, tags);
        PresentationTelemetry.SnapshotInputSamples.Add(result.InputSamples, tags);
        PresentationTelemetry.SnapshotOutputPoints.Add(result.OutputPoints, tags);
        if (result.OutputPoints > 0)
        {
            PresentationTelemetry.DownsampleRatio.Record(
                result.InputSamples / (double)result.OutputPoints,
                tags);
        }
        PresentationTelemetry.AggregationLatencyMs.Record(elapsed.TotalMilliseconds, tags);

        return new WaveformRenderSnapshot(
            source,
            generation,
            viewport.Revision,
            (timeProvider ?? TimeProvider.System).GetUtcNow(),
            viewport,
            retainedStart,
            retainedEnd,
            requestedRangeAvailable,
            sourceGaps,
            processingGaps,
            processingFaults,
            presentationDrops,
            result.InputSamples,
            result.OutputPoints,
            result.Series);
    }

    public WaveformPresentationRuntimeSnapshot GetSnapshot()
    {
        lock (_gate)
        {
            var blocks = _blocks.ToArray();
            return new WaveformPresentationRuntimeSnapshot(
                _source,
                Interlocked.Read(ref _generation),
                blocks.Length,
                _retainedSamples,
                _options.MaxRetainedSamples,
                blocks.Length == 0 ? null : blocks[0].StartTimestamp,
                blocks.Length == 0 ? null : blocks[^1].EndTimestamp,
                _acceptedBlocks,
                _acceptedSamples,
                _retentionEvictedBlocks,
                _retentionEvictedSamples,
                _outOfOrderBlocks,
                _sourceGapCount,
                _processingGapCount,
                _processingFaultCount,
                _presentationDropCount,
                _snapshotCount,
                _snapshotInputSamples,
                _snapshotOutputPoints);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        lock (_gate)
        {
            _blocks.Clear();
            _retainedSamples = 0;
            _lastTimestamp = null;
        }
    }

    private void EvictRetentionLocked()
    {
        while (_blocks.Count > 0)
        {
            var first = _blocks.Peek();
            var latest = _blocks.Last().EndTimestamp;
            var overSamples = _retainedSamples > _options.MaxRetainedSamples;
            var overTime = latest - first.StartTimestamp > _options.MaxRetainedDuration;

            if (!overSamples && !overTime)
                break;

            var removed = _blocks.Dequeue();
            _retainedSamples -= removed.SampleCount;
            _retentionEvictedBlocks++;
            _retentionEvictedSamples += removed.SampleCount;
            PresentationTelemetry.RetentionEvictedSamples.Add(
                removed.SampleCount,
                PresentationTelemetry.SourceTags(_source));
        }
    }

    private static bool DescriptorsMatch(
        PresentationSourceDescriptor expected,
        PresentationSourceDescriptor actual)
    {
        if (!string.Equals(expected.EpochIdentity, actual.EpochIdentity, StringComparison.Ordinal) ||
            expected.Kind != actual.Kind ||
            expected.SampleRateHz != actual.SampleRateHz ||
            expected.UpdateRateHz != actual.UpdateRateHz ||
            !string.Equals(expected.ClockDomain, actual.ClockDomain, StringComparison.Ordinal) ||
            expected.Channels.Count != actual.Channels.Count)
        {
            return false;
        }

        for (var index = 0; index < expected.Channels.Count; index++)
        {
            if (expected.Channels[index] != actual.Channels[index])
                return false;
        }

        return true;
    }
}

public sealed class MinMaxEnvelopeDownsampler : IPresentationDownsampler
{
    public PresentationDownsamplingMode Mode => PresentationDownsamplingMode.MinMaxEnvelope;

    public PresentationDownsamplingResult Downsample(
        PresentationSourceDescriptor source,
        IReadOnlyList<WaveformPresentationBlock> blocks,
        PresentationViewport viewport,
        int maxBuckets)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(blocks);
        ArgumentNullException.ThrowIfNull(viewport);
        if (maxBuckets <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxBuckets));

        var selectedChannels = PresentationDownsampling.SelectChannels(source, viewport);
        var seriesBuilders = selectedChannels
            .Select(channel => new SeriesBuilder(source.Channels[channel.Index]))
            .ToArray();

        if (blocks.Count == 0 || seriesBuilders.Length == 0)
            return PresentationDownsampling.Empty(seriesBuilders);

        var bucketCount = Math.Max(1, Math.Min(viewport.PixelWidth, maxBuckets));
        var startTicks = viewport.Start.UtcDateTime.Ticks;
        var endTicks = viewport.End.UtcDateTime.Ticks;
        var durationTicks = Math.Max(1, endTicks - startTicks);

        var currentBucket = -1;
        var currentSegment = -1;
        var segment = 0;
        var hasGroup = false;
        var inputSamples = 0L;
        var states = seriesBuilders.Select(_ => new MinMaxState()).ToArray();

        foreach (var block in blocks)
        {
            var gapBefore =
                block.BreakBefore ||
                block.Quality.HasFlag(PresentationDataQualityFlags.SourceGap) ||
                block.Quality.HasFlag(PresentationDataQualityFlags.ProcessingGap) ||
                block.Quality.HasFlag(PresentationDataQualityFlags.Discontinuous);

            if (gapBefore)
                segment++;

            if (block.Quality.HasFlag(PresentationDataQualityFlags.Invalid) ||
                block.Quality.HasFlag(PresentationDataQualityFlags.ProcessingFault))
            {
                segment++;
                continue;
            }

            var timestamps = block.Timestamps.Span;
            var values = block.Values.Span;
            var channels = block.ChannelCount;

            for (var sample = 0; sample < block.SampleCount; sample++)
            {
                var timestamp = timestamps[sample];
                if (timestamp < viewport.Start || timestamp > viewport.End)
                    continue;

                var ticks = timestamp.UtcDateTime.Ticks;
                var relative = Math.Clamp(ticks - startTicks, 0, durationTicks);
                var bucket = (int)Math.Min(
                    bucketCount - 1L,
                    relative * bucketCount / durationTicks);

                if (!hasGroup || bucket != currentBucket || segment != currentSegment)
                {
                    if (hasGroup)
                        FlushGroup(seriesBuilders, states, currentSegment);

                    foreach (var state in states)
                        state.Reset();

                    currentBucket = bucket;
                    currentSegment = segment;
                    hasGroup = true;
                }

                inputSamples++;
                var sequence = checked(block.SequenceStart + sample);
                var offset = sample * channels;
                for (var channel = 0; channel < selectedChannels.Length; channel++)
                {
                    var sourceIndex = selectedChannels[channel].Index;
                    states[channel].Observe(
                        values[offset + sourceIndex],
                        timestamp,
                        sequence);
                }
            }
        }

        if (hasGroup)
            FlushGroup(seriesBuilders, states, currentSegment);

        return PresentationDownsampling.Create(seriesBuilders, inputSamples);
    }

    private static void FlushGroup(
        IReadOnlyList<SeriesBuilder> builders,
        IReadOnlyList<MinMaxState> states,
        int segment)
    {
        for (var index = 0; index < builders.Count; index++)
        {
            var state = states[index];
            if (!state.HasValue)
                continue;

            var builder = builders[index];
            var breakBefore = builder.LastSegment != segment;

            if (state.MinSequence == state.MaxSequence)
            {
                builder.Add(new PresentationRenderPoint(
                    state.MinTimestamp,
                    state.MinValue,
                    state.MinSequence,
                    breakBefore));
            }
            else if (state.MinTimestamp < state.MaxTimestamp ||
                     (state.MinTimestamp == state.MaxTimestamp &&
                      state.MinSequence < state.MaxSequence))
            {
                builder.Add(new PresentationRenderPoint(
                    state.MinTimestamp,
                    state.MinValue,
                    state.MinSequence,
                    breakBefore));
                builder.Add(new PresentationRenderPoint(
                    state.MaxTimestamp,
                    state.MaxValue,
                    state.MaxSequence,
                    false));
            }
            else
            {
                builder.Add(new PresentationRenderPoint(
                    state.MaxTimestamp,
                    state.MaxValue,
                    state.MaxSequence,
                    breakBefore));
                builder.Add(new PresentationRenderPoint(
                    state.MinTimestamp,
                    state.MinValue,
                    state.MinSequence,
                    false));
            }

            builder.LastSegment = segment;
        }
    }

    private sealed class MinMaxState
    {
        public bool HasValue { get; private set; }
        public double MinValue { get; private set; }
        public double MaxValue { get; private set; }
        public DateTimeOffset MinTimestamp { get; private set; }
        public DateTimeOffset MaxTimestamp { get; private set; }
        public long MinSequence { get; private set; }
        public long MaxSequence { get; private set; }

        public void Observe(double value, DateTimeOffset timestamp, long sequence)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                return;

            if (!HasValue)
            {
                HasValue = true;
                MinValue = value;
                MaxValue = value;
                MinTimestamp = timestamp;
                MaxTimestamp = timestamp;
                MinSequence = sequence;
                MaxSequence = sequence;
                return;
            }

            if (value < MinValue)
            {
                MinValue = value;
                MinTimestamp = timestamp;
                MinSequence = sequence;
            }

            if (value > MaxValue)
            {
                MaxValue = value;
                MaxTimestamp = timestamp;
                MaxSequence = sequence;
            }
        }

        public void Reset()
        {
            HasValue = false;
            MinValue = 0;
            MaxValue = 0;
            MinTimestamp = default;
            MaxTimestamp = default;
            MinSequence = 0;
            MaxSequence = 0;
        }
    }
}

public sealed class UniformPresentationDownsampler : IPresentationDownsampler
{
    public PresentationDownsamplingMode Mode => PresentationDownsamplingMode.Uniform;

    public PresentationDownsamplingResult Downsample(
        PresentationSourceDescriptor source,
        IReadOnlyList<WaveformPresentationBlock> blocks,
        PresentationViewport viewport,
        int maxBuckets)
    {
        var selectedChannels = PresentationDownsampling.SelectChannels(source, viewport);
        var builders = selectedChannels
            .Select(channel => new SeriesBuilder(source.Channels[channel.Index]))
            .ToArray();

        if (blocks.Count == 0 || builders.Length == 0)
            return PresentationDownsampling.Empty(builders);

        var bucketCount = Math.Max(1, Math.Min(viewport.PixelWidth, maxBuckets));
        var startTicks = viewport.Start.UtcDateTime.Ticks;
        var endTicks = viewport.End.UtcDateTime.Ticks;
        var durationTicks = Math.Max(1, endTicks - startTicks);
        var lastBucketBySegment = new Dictionary<(int Segment, int Channel), int>();
        var segment = 0;
        var inputSamples = 0L;

        foreach (var block in blocks)
        {
            if (block.BreakBefore ||
                block.Quality.HasFlag(PresentationDataQualityFlags.SourceGap) ||
                block.Quality.HasFlag(PresentationDataQualityFlags.ProcessingGap) ||
                block.Quality.HasFlag(PresentationDataQualityFlags.Discontinuous))
            {
                segment++;
            }

            if (block.Quality.HasFlag(PresentationDataQualityFlags.Invalid) ||
                block.Quality.HasFlag(PresentationDataQualityFlags.ProcessingFault))
            {
                segment++;
                continue;
            }

            var timestamps = block.Timestamps.Span;
            var values = block.Values.Span;
            for (var sample = 0; sample < block.SampleCount; sample++)
            {
                var timestamp = timestamps[sample];
                if (timestamp < viewport.Start || timestamp > viewport.End)
                    continue;

                inputSamples++;
                var relative = Math.Clamp(
                    timestamp.UtcDateTime.Ticks - startTicks,
                    0,
                    durationTicks);
                var bucket = (int)Math.Min(
                    bucketCount - 1L,
                    relative * bucketCount / durationTicks);
                var sequence = checked(block.SequenceStart + sample);
                var offset = sample * block.ChannelCount;

                for (var channel = 0; channel < selectedChannels.Length; channel++)
                {
                    var key = (segment, channel);
                    if (lastBucketBySegment.TryGetValue(key, out var previous) &&
                        previous == bucket)
                    {
                        continue;
                    }

                    lastBucketBySegment[key] = bucket;
                    var value = values[offset + selectedChannels[channel].Index];
                    if (double.IsNaN(value) || double.IsInfinity(value))
                        continue;

                    var builder = builders[channel];
                    builder.Add(new PresentationRenderPoint(
                        timestamp,
                        value,
                        sequence,
                        builder.LastSegment != segment));
                    builder.LastSegment = segment;
                }
            }
        }

        return PresentationDownsampling.Create(builders, inputSamples);
    }
}

public sealed class LatestPresentationDownsampler : IPresentationDownsampler
{
    public PresentationDownsamplingMode Mode => PresentationDownsamplingMode.Latest;

    public PresentationDownsamplingResult Downsample(
        PresentationSourceDescriptor source,
        IReadOnlyList<WaveformPresentationBlock> blocks,
        PresentationViewport viewport,
        int maxBuckets)
    {
        var selectedChannels = PresentationDownsampling.SelectChannels(source, viewport);
        var builders = selectedChannels
            .Select(channel => new SeriesBuilder(source.Channels[channel.Index]))
            .ToArray();

        if (blocks.Count == 0 || builders.Length == 0)
            return PresentationDownsampling.Empty(builders);

        var latest = new PresentationRenderPoint?[builders.Length];
        var inputSamples = 0L;
        var segment = 0;

        foreach (var block in blocks)
        {
            if (block.BreakBefore ||
                block.Quality.HasFlag(PresentationDataQualityFlags.SourceGap) ||
                block.Quality.HasFlag(PresentationDataQualityFlags.ProcessingGap) ||
                block.Quality.HasFlag(PresentationDataQualityFlags.Discontinuous))
            {
                segment++;
            }

            if (block.Quality.HasFlag(PresentationDataQualityFlags.Invalid) ||
                block.Quality.HasFlag(PresentationDataQualityFlags.ProcessingFault))
            {
                segment++;
                continue;
            }

            var timestamps = block.Timestamps.Span;
            var values = block.Values.Span;
            for (var sample = 0; sample < block.SampleCount; sample++)
            {
                var timestamp = timestamps[sample];
                if (timestamp < viewport.Start || timestamp > viewport.End)
                    continue;

                inputSamples++;
                var sequence = checked(block.SequenceStart + sample);
                var offset = sample * block.ChannelCount;

                for (var channel = 0; channel < selectedChannels.Length; channel++)
                {
                    var value = values[offset + selectedChannels[channel].Index];
                    if (double.IsNaN(value) || double.IsInfinity(value))
                        continue;

                    latest[channel] = new PresentationRenderPoint(
                        timestamp,
                        value,
                        sequence,
                        true);
                }
            }
        }

        for (var channel = 0; channel < latest.Length; channel++)
        {
            if (latest[channel] is { } point)
                builders[channel].Add(point);
        }

        return PresentationDownsampling.Create(builders, inputSamples);
    }
}

public sealed record WaveformRenderPumpOptions
{
    public WaveformRenderPumpOptions(TimeSpan? refreshInterval = null)
    {
        var interval = refreshInterval ?? TimeSpan.FromMilliseconds(33.333);
        if (interval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(refreshInterval));

        RefreshInterval = interval;
    }

    public TimeSpan RefreshInterval { get; }
}

public sealed class WaveformRenderPump : IAsyncDisposable
{
    private readonly WaveformPresentationRuntime _runtime;
    private readonly Func<PresentationViewport> _viewportProvider;
    private readonly Func<WaveformRenderSnapshot, CancellationToken, ValueTask> _sink;
    private readonly WaveformRenderPumpOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly CancellationTokenSource _lifetime = new();
    private Task _loop = Task.CompletedTask;
    private int _started;
    private int _active = 1;
    private int _disposed;

    public WaveformRenderPump(
        WaveformPresentationRuntime runtime,
        Func<PresentationViewport> viewportProvider,
        Func<WaveformRenderSnapshot, CancellationToken, ValueTask> sink,
        WaveformRenderPumpOptions? options = null,
        TimeProvider? timeProvider = null)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _viewportProvider = viewportProvider ?? throw new ArgumentNullException(nameof(viewportProvider));
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _options = options ?? new WaveformRenderPumpOptions();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public Task Completion => _loop;
    public bool IsActive => Volatile.Read(ref _active) != 0;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.Exchange(ref _started, 1) != 0)
            throw new InvalidOperationException("Waveform render pump can only start once.");

        _loop = RunAsync();
    }

    public void SetActive(bool active) =>
        Volatile.Write(ref _active, active ? 1 : 0);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _lifetime.Cancel();
        try
        {
            await _loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        finally
        {
            _lifetime.Dispose();
        }
    }

    private async Task RunAsync()
    {
        while (true)
        {
            await Task.Delay(
                    _options.RefreshInterval,
                    _timeProvider,
                    _lifetime.Token)
                .ConfigureAwait(false);

            if (!IsActive)
                continue;

            var viewport = _viewportProvider();
            var started = _timeProvider.GetTimestamp();
            var snapshot = _runtime.BuildSnapshot(viewport, _timeProvider);
            await _sink(snapshot, _lifetime.Token).ConfigureAwait(false);
            PresentationTelemetry.RenderFrames.Add(
                1,
                PresentationTelemetry.SourceTags(snapshot.Source));
            PresentationTelemetry.RenderLatencyMs.Record(
                _timeProvider.GetElapsedTime(started).TotalMilliseconds,
                PresentationTelemetry.SourceTags(snapshot.Source));
        }
    }
}

internal static class PresentationDownsampling
{
    public static (int Index, PresentationChannelDescriptor Channel)[] SelectChannels(
        PresentationSourceDescriptor source,
        PresentationViewport viewport)
    {
        if (viewport.EnabledChannels.Count == 0)
        {
            return source.Channels
                .Select((channel, index) => (index, channel))
                .ToArray();
        }

        var enabled = new HashSet<string>(
            viewport.EnabledChannels,
            StringComparer.Ordinal);

        return source.Channels
            .Select((channel, index) => (Index: index, Channel: channel))
            .Where(item => enabled.Contains(item.Channel.ChannelId))
            .ToArray();
    }

    public static PresentationDownsamplingResult Empty(
        IReadOnlyList<SeriesBuilder> builders) =>
        Create(builders, 0);

    public static PresentationDownsamplingResult Create(
        IReadOnlyList<SeriesBuilder> builders,
        long inputSamples)
    {
        var series = builders.Select(static builder => builder.Build()).ToArray();
        return new PresentationDownsamplingResult(
            series,
            inputSamples,
            series.Sum(static item => (long)item.Points.Count));
    }
}

internal sealed class SeriesBuilder
{
    private readonly PresentationChannelDescriptor _channel;
    private readonly List<PresentationRenderPoint> _points = [];
    private double? _min;
    private double? _max;

    public SeriesBuilder(PresentationChannelDescriptor channel)
    {
        _channel = channel;
    }

    public int LastSegment { get; set; } = int.MinValue;

    public void Add(PresentationRenderPoint point)
    {
        _points.Add(point);
        _min = !_min.HasValue || point.Value < _min.Value ? point.Value : _min;
        _max = !_max.HasValue || point.Value > _max.Value ? point.Value : _max;
    }

    public PresentationChannelRenderSeries Build() =>
        new(
            _channel.ChannelId,
            _channel.DisplayName,
            _channel.Unit,
            _points,
            _min,
            _max);
}

internal static class PresentationTelemetry
{
    private static readonly Meter Meter = OpenDeviceStudioTelemetry.Meter;

    public static readonly Counter<long> InputBlocks =
        Meter.CreateCounter<long>("opendevicestudio.presentation.input.blocks", "{block}");

    public static readonly Counter<long> InputSamples =
        Meter.CreateCounter<long>("opendevicestudio.presentation.input.samples", "{sample}");

    public static readonly Counter<long> Dropped =
        Meter.CreateCounter<long>("opendevicestudio.presentation.dropped", "{item}");

    public static readonly Counter<long> OutOfOrderBlocks =
        Meter.CreateCounter<long>("opendevicestudio.presentation.out_of_order.blocks", "{block}");

    public static readonly Counter<long> SourceMismatches =
        Meter.CreateCounter<long>("opendevicestudio.presentation.source_mismatch", "{item}");

    public static readonly Counter<long> SourceSwitches =
        Meter.CreateCounter<long>("opendevicestudio.presentation.source_switches", "{switch}");

    public static readonly Counter<long> RetentionEvictedSamples =
        Meter.CreateCounter<long>("opendevicestudio.presentation.retention.evicted_samples", "{sample}");

    public static readonly Histogram<long> BufferDepth =
        Meter.CreateHistogram<long>("opendevicestudio.presentation.buffer.depth", "{sample}");

    public static readonly Counter<long> Snapshots =
        Meter.CreateCounter<long>("opendevicestudio.presentation.snapshots", "{snapshot}");

    public static readonly Counter<long> SnapshotInputSamples =
        Meter.CreateCounter<long>("opendevicestudio.presentation.snapshot.input_samples", "{sample}");

    public static readonly Counter<long> SnapshotOutputPoints =
        Meter.CreateCounter<long>("opendevicestudio.presentation.snapshot.output_points", "{point}");

    public static readonly Histogram<double> DownsampleRatio =
        Meter.CreateHistogram<double>("opendevicestudio.presentation.downsample.ratio");

    public static readonly Histogram<double> AggregationLatencyMs =
        Meter.CreateHistogram<double>("opendevicestudio.presentation.aggregation.latency", "ms");

    public static readonly Counter<long> RenderFrames =
        Meter.CreateCounter<long>("opendevicestudio.presentation.render.frames", "{frame}");

    public static readonly Histogram<double> RenderLatencyMs =
        Meter.CreateHistogram<double>("opendevicestudio.presentation.render.latency", "ms");

    public static TagList SourceTags(PresentationSourceDescriptor source)
    {
        var tags = new TagList
        {
            { "source.kind", source.Kind.ToString() },
            { "stage.id", source.StageId }
        };
        return tags;
    }
}
