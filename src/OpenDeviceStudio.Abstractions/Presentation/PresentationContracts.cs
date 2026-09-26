namespace OpenDeviceStudio.Abstractions.Presentation;

public enum PresentationSourceKind
{
    Waveform,
    NumericTrend,
    EventMarker
}

[Flags]
public enum PresentationDataQualityFlags : ulong
{
    None = 0,
    SourceGap = 1UL << 0,
    ProcessingGap = 1UL << 1,
    ProcessingFault = 1UL << 2,
    Invalid = 1UL << 3,
    Artifact = 1UL << 4,
    Discontinuous = 1UL << 5
}

public enum PresentationDownsamplingMode
{
    MinMaxEnvelope,
    Uniform,
    Latest
}

public enum PresentationConnectionState
{
    Connecting,
    Running,
    Stopped,
    Faulted,
    Reconnecting
}

public sealed record PresentationChannelDescriptor
{
    public PresentationChannelDescriptor(
        string channelId,
        string displayName,
        string unit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channelId);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(unit);

        ChannelId = channelId;
        DisplayName = displayName;
        Unit = unit;
    }

    public string ChannelId { get; }
    public string DisplayName { get; }
    public string Unit { get; }
}

public sealed record PresentationSourceDescriptor
{
    private readonly IReadOnlyList<PresentationChannelDescriptor> _channels;

    public PresentationSourceDescriptor(
        string sourceId,
        string stageId,
        string displayName,
        PresentationSourceKind kind,
        string processingEpoch,
        string pipelineVersion,
        string configurationHash,
        string clockDomain,
        IReadOnlyList<PresentationChannelDescriptor> channels,
        double? sampleRateHz = null,
        double? updateRateHz = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(stageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(processingEpoch);
        ArgumentException.ThrowIfNullOrWhiteSpace(pipelineVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(clockDomain);
        ArgumentNullException.ThrowIfNull(channels);

        var copy = channels.ToArray();
        if (copy.Length == 0)
            throw new ArgumentException("At least one presentation channel is required.", nameof(channels));

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var channel in copy)
        {
            ArgumentNullException.ThrowIfNull(channel);
            if (!ids.Add(channel.ChannelId))
                throw new ArgumentException(
                    $"Duplicate presentation ChannelId '{channel.ChannelId}'.",
                    nameof(channels));
        }

        if (sampleRateHz is <= 0 ||
            (sampleRateHz.HasValue &&
             (double.IsNaN(sampleRateHz.Value) || double.IsInfinity(sampleRateHz.Value))))
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRateHz));
        }

        if (updateRateHz is <= 0 ||
            (updateRateHz.HasValue &&
             (double.IsNaN(updateRateHz.Value) || double.IsInfinity(updateRateHz.Value))))
        {
            throw new ArgumentOutOfRangeException(nameof(updateRateHz));
        }

        if (kind == PresentationSourceKind.Waveform && !sampleRateHz.HasValue)
            throw new ArgumentException("Waveform sources require SampleRateHz.", nameof(sampleRateHz));

        SourceId = sourceId;
        StageId = stageId;
        DisplayName = displayName;
        Kind = kind;
        ProcessingEpoch = processingEpoch;
        PipelineVersion = pipelineVersion;
        ConfigurationHash = configurationHash;
        ClockDomain = clockDomain;
        _channels = copy;
        SampleRateHz = sampleRateHz;
        UpdateRateHz = updateRateHz;
    }

    public string SourceId { get; }
    public string StageId { get; }
    public string DisplayName { get; }
    public PresentationSourceKind Kind { get; }
    public string ProcessingEpoch { get; }
    public string PipelineVersion { get; }
    public string ConfigurationHash { get; }
    public string ClockDomain { get; }
    public IReadOnlyList<PresentationChannelDescriptor> Channels => _channels;
    public double? SampleRateHz { get; }
    public double? UpdateRateHz { get; }

    public string EpochIdentity =>
        $"{SourceId}|{StageId}|{ProcessingEpoch}|{ConfigurationHash}";
}

public sealed record PresentationViewport
{
    private readonly IReadOnlyList<string> _enabledChannels;

    public PresentationViewport(
        DateTimeOffset start,
        DateTimeOffset end,
        int pixelWidth,
        long revision,
        IReadOnlyList<string>? enabledChannels = null,
        bool liveFollow = true)
    {
        if (end <= start)
            throw new ArgumentOutOfRangeException(nameof(end));
        if (pixelWidth <= 0)
            throw new ArgumentOutOfRangeException(nameof(pixelWidth));
        if (revision < 0)
            throw new ArgumentOutOfRangeException(nameof(revision));

        Start = start;
        End = end;
        PixelWidth = pixelWidth;
        Revision = revision;
        _enabledChannels = (enabledChannels ?? Array.Empty<string>()).ToArray();
        LiveFollow = liveFollow;
    }

    public DateTimeOffset Start { get; }
    public DateTimeOffset End { get; }
    public int PixelWidth { get; }
    public long Revision { get; }
    public IReadOnlyList<string> EnabledChannels => _enabledChannels;
    public bool LiveFollow { get; }
}

public sealed class WaveformPresentationBlock
{
    private readonly DateTimeOffset[] _timestamps;
    private readonly double[] _values;

    private WaveformPresentationBlock(
        PresentationSourceDescriptor source,
        long sequenceStart,
        PresentationDataQualityFlags quality,
        bool breakBefore,
        DateTimeOffset[] timestamps,
        double[] values)
    {
        Source = source;
        SequenceStart = sequenceStart;
        Quality = quality;
        BreakBefore = breakBefore;
        _timestamps = timestamps;
        _values = values;
    }

    public PresentationSourceDescriptor Source { get; }
    public long SequenceStart { get; }
    public long SequenceEndExclusive => checked(SequenceStart + SampleCount);
    public int SampleCount => _timestamps.Length;
    public int ChannelCount => Source.Channels.Count;
    public PresentationDataQualityFlags Quality { get; }
    public bool BreakBefore { get; }
    public ReadOnlyMemory<DateTimeOffset> Timestamps => _timestamps;
    public ReadOnlyMemory<double> Values => _values;
    public DateTimeOffset StartTimestamp => _timestamps[0];
    public DateTimeOffset EndTimestamp => _timestamps[^1];

    public static WaveformPresentationBlock CopyFrom(
        PresentationSourceDescriptor source,
        long sequenceStart,
        ReadOnlySpan<DateTimeOffset> timestamps,
        ReadOnlySpan<double> interleavedValues,
        PresentationDataQualityFlags quality = PresentationDataQualityFlags.None,
        bool breakBefore = false)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Kind != PresentationSourceKind.Waveform)
            throw new ArgumentException("Waveform block requires a Waveform source descriptor.", nameof(source));
        if (timestamps.Length == 0)
            throw new ArgumentException("Waveform block must contain at least one sample.", nameof(timestamps));

        var expectedValues = checked(timestamps.Length * source.Channels.Count);
        if (interleavedValues.Length != expectedValues)
        {
            throw new ArgumentException(
                "Waveform values length must equal SampleCount * ChannelCount.",
                nameof(interleavedValues));
        }

        for (var index = 1; index < timestamps.Length; index++)
        {
            if (timestamps[index] < timestamps[index - 1])
            {
                throw new ArgumentException(
                    "Waveform timestamps must be monotonic within a block.",
                    nameof(timestamps));
            }
        }

        return new WaveformPresentationBlock(
            source,
            sequenceStart,
            quality,
            breakBefore,
            timestamps.ToArray(),
            interleavedValues.ToArray());
    }
}

public readonly record struct PresentationRenderPoint(
    DateTimeOffset Timestamp,
    double Value,
    long Sequence,
    bool BreakBefore);

public sealed record PresentationChannelRenderSeries
{
    private readonly IReadOnlyList<PresentationRenderPoint> _points;

    public PresentationChannelRenderSeries(
        string channelId,
        string displayName,
        string unit,
        IReadOnlyList<PresentationRenderPoint> points,
        double? visibleMinimum,
        double? visibleMaximum)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channelId);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(unit);
        ArgumentNullException.ThrowIfNull(points);

        ChannelId = channelId;
        DisplayName = displayName;
        Unit = unit;
        _points = points.ToArray();
        VisibleMinimum = visibleMinimum;
        VisibleMaximum = visibleMaximum;
    }

    public string ChannelId { get; }
    public string DisplayName { get; }
    public string Unit { get; }
    public IReadOnlyList<PresentationRenderPoint> Points => _points;
    public double? VisibleMinimum { get; }
    public double? VisibleMaximum { get; }
}

public interface IPresentationSnapshot
{
    PresentationSourceDescriptor Source { get; }
    long Generation { get; }
    long ViewportRevision { get; }
}

public sealed record WaveformRenderSnapshot : IPresentationSnapshot
{
    private readonly IReadOnlyList<PresentationChannelRenderSeries> _series;

    public WaveformRenderSnapshot(
        PresentationSourceDescriptor source,
        long generation,
        long viewportRevision,
        DateTimeOffset builtAt,
        PresentationViewport viewport,
        DateTimeOffset? retainedStart,
        DateTimeOffset? retainedEnd,
        bool requestedRangeAvailable,
        long sourceGapCount,
        long processingGapCount,
        long processingFaultCount,
        long presentationDropCount,
        long inputSampleCount,
        long outputPointCount,
        IReadOnlyList<PresentationChannelRenderSeries> series)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(viewport);
        ArgumentNullException.ThrowIfNull(series);
        if (generation < 0)
            throw new ArgumentOutOfRangeException(nameof(generation));
        if (sourceGapCount < 0 ||
            processingGapCount < 0 ||
            processingFaultCount < 0 ||
            presentationDropCount < 0 ||
            inputSampleCount < 0 ||
            outputPointCount < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(presentationDropCount),
                "Presentation counters must be non-negative.");
        }

        Source = source;
        Generation = generation;
        ViewportRevision = viewportRevision;
        BuiltAt = builtAt;
        Viewport = viewport;
        RetainedStart = retainedStart;
        RetainedEnd = retainedEnd;
        RequestedRangeAvailable = requestedRangeAvailable;
        SourceGapCount = sourceGapCount;
        ProcessingGapCount = processingGapCount;
        ProcessingFaultCount = processingFaultCount;
        PresentationDropCount = presentationDropCount;
        InputSampleCount = inputSampleCount;
        OutputPointCount = outputPointCount;
        _series = series.ToArray();
    }

    public PresentationSourceDescriptor Source { get; }
    public long Generation { get; }
    public long ViewportRevision { get; }
    public DateTimeOffset BuiltAt { get; }
    public PresentationViewport Viewport { get; }
    public DateTimeOffset? RetainedStart { get; }
    public DateTimeOffset? RetainedEnd { get; }
    public bool RequestedRangeAvailable { get; }
    public long SourceGapCount { get; }
    public long ProcessingGapCount { get; }
    public long ProcessingFaultCount { get; }
    public long PresentationDropCount { get; }
    public long InputSampleCount { get; }
    public long OutputPointCount { get; }
    public IReadOnlyList<PresentationChannelRenderSeries> Series => _series;
}

public readonly record struct NumericPresentationPoint(
    DateTimeOffset Timestamp,
    string ChannelId,
    double Value,
    PresentationDataQualityFlags Quality);

public sealed record NumericTrendSnapshot : IPresentationSnapshot
{
    private readonly IReadOnlyList<NumericPresentationPoint> _points;

    public NumericTrendSnapshot(
        PresentationSourceDescriptor source,
        long generation,
        long viewportRevision,
        IReadOnlyList<NumericPresentationPoint> points)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(points);
        if (source.Kind != PresentationSourceKind.NumericTrend)
            throw new ArgumentException("Numeric trend snapshot requires NumericTrend source.", nameof(source));

        Source = source;
        Generation = generation;
        ViewportRevision = viewportRevision;
        _points = points.ToArray();
    }

    public PresentationSourceDescriptor Source { get; }
    public long Generation { get; }
    public long ViewportRevision { get; }
    public IReadOnlyList<NumericPresentationPoint> Points => _points;
}

public readonly record struct PresentationEventMarker(
    DateTimeOffset Timestamp,
    string Code,
    string Label,
    PresentationDataQualityFlags Quality);

public sealed record EventMarkerSnapshot : IPresentationSnapshot
{
    private readonly IReadOnlyList<PresentationEventMarker> _markers;

    public EventMarkerSnapshot(
        PresentationSourceDescriptor source,
        long generation,
        long viewportRevision,
        IReadOnlyList<PresentationEventMarker> markers)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(markers);
        if (source.Kind != PresentationSourceKind.EventMarker)
            throw new ArgumentException("Event snapshot requires EventMarker source.", nameof(source));

        Source = source;
        Generation = generation;
        ViewportRevision = viewportRevision;
        _markers = markers.ToArray();
    }

    public PresentationSourceDescriptor Source { get; }
    public long Generation { get; }
    public long ViewportRevision { get; }
    public IReadOnlyList<PresentationEventMarker> Markers => _markers;
}
