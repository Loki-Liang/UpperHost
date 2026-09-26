namespace OpenDeviceStudio.Abstractions.Storage;

public enum RawNumericRepresentation { OpaqueBytes, Int8, UInt8, Int16, UInt16, Int32, UInt32, Int64, UInt64, Float32, Float64 }
public enum RawByteOrder { NotApplicable, LittleEndian, BigEndian }
public enum RawSourceFlowControl { SupportsBackpressure, CannotBackpressure }
public enum RawDurabilityLevel { Buffered, FlushOnFinalize, FlushToDiskOnFinalize }
public enum RawRecorderState { Created, Preparing, Ready, Running, Stopping, Completed, Faulted, Aborted, Disposed }
public enum RawRecorderAcceptStatus { Accepted, Closed, Overloaded, Faulted }
public enum RawRecoveryIssueKind { TruncatedTail, ChecksumMismatch, CorruptSegment, MissingSegment, UnsupportedFormat, IncompleteSession, UncommittedManifest }

public sealed record RawRecordingSourceIdentity(string SourceId, long ConnectionEpoch);
public sealed record RawRecordingSessionDescriptor(string SessionId, IReadOnlyList<RawRecordingSourceIdentity> Sources, string ConfigurationHash, DateTimeOffset StartedAt);

public sealed record RawRecorderAcceptResult(RawRecorderAcceptStatus Status, string? Reason = null)
{
    public bool Accepted => Status == RawRecorderAcceptStatus.Accepted;
    public static RawRecorderAcceptResult Success { get; } = new(RawRecorderAcceptStatus.Accepted);
}

public sealed record RawRecorderFault(string Code, string Message, Exception? Exception, DateTimeOffset OccurredAt);

public sealed record RawSourceSequenceSnapshot(
    string SourceId,
    long ConnectionEpoch,
    long LastSequence);

public sealed record RawRecorderSnapshot(
    RawRecorderState State,
    long AcceptedBlocks,
    long WrittenBlocks,
    long FlushedBlocks,
    long DurableBlocks,
    long AcceptedBytes,
    long WrittenBytes,
    long FlushedBytes,
    long DurableBytes,
    int QueueDepth,
    int QueueCapacity,
    int QueueHighWater,
    int SegmentCount,
    long SequenceGapCount,
    long DuplicateBlockCount,
    long OutOfOrderBlockCount,
    IReadOnlyList<RawSourceSequenceSnapshot> LastSequences,
    string? SessionDirectory,
    string? FaultReason);

public sealed record RawRecoveryIssue(RawRecoveryIssueKind Kind, string Path, string Message);

public sealed record RawRecoveryReport(
    string SessionDirectory,
    string? SessionId,
    string ManifestState,
    long VerifiedBlocks,
    long VerifiedBytes,
    long SequenceGapCount,
    long DuplicateBlockCount,
    long OutOfOrderBlockCount,
    IReadOnlyList<RawRecoveryIssue> Issues)
{
    public bool IsComplete =>
        string.Equals(ManifestState, "Completed", StringComparison.Ordinal) &&
        Issues.Count == 0;
}

public sealed class CanonicalRawBlock
{
    private readonly byte[] _payload;

    private CanonicalRawBlock(
        string sourceId,
        string? deviceId,
        long connectionEpoch,
        long sequenceStart,
        int sequenceCount,
        int sampleCount,
        int channelCount,
        string channelLayoutId,
        RawNumericRepresentation numericRepresentation,
        RawByteOrder byteOrder,
        double sampleRateHz,
        long? deviceTimestamp,
        long hostMonotonicTimestamp,
        DateTimeOffset wallClockTimestamp,
        ulong qualityFlags,
        string protocolVersion,
        string canonicalizationVersion,
        string configurationHash,
        byte[] payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(channelLayoutId);
        ArgumentException.ThrowIfNullOrWhiteSpace(protocolVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalizationVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationHash);
        if (sequenceCount <= 0) throw new ArgumentOutOfRangeException(nameof(sequenceCount));
        if (sampleCount <= 0) throw new ArgumentOutOfRangeException(nameof(sampleCount));
        if (channelCount <= 0) throw new ArgumentOutOfRangeException(nameof(channelCount));
        if (sampleRateHz <= 0 || double.IsNaN(sampleRateHz) || double.IsInfinity(sampleRateHz))
            throw new ArgumentOutOfRangeException(nameof(sampleRateHz));
        if (payload.Length == 0) throw new ArgumentException("Canonical Raw payload must not be empty.", nameof(payload));

        SourceId = sourceId;
        DeviceId = deviceId;
        ConnectionEpoch = connectionEpoch;
        SequenceStart = sequenceStart;
        SequenceCount = sequenceCount;
        SampleCount = sampleCount;
        ChannelCount = channelCount;
        ChannelLayoutId = channelLayoutId;
        NumericRepresentation = numericRepresentation;
        ByteOrder = byteOrder;
        SampleRateHz = sampleRateHz;
        DeviceTimestamp = deviceTimestamp;
        HostMonotonicTimestamp = hostMonotonicTimestamp;
        WallClockTimestamp = wallClockTimestamp;
        QualityFlags = qualityFlags;
        ProtocolVersion = protocolVersion;
        CanonicalizationVersion = canonicalizationVersion;
        ConfigurationHash = configurationHash;
        _payload = payload;
    }

    public string SourceId { get; }
    public string? DeviceId { get; }
    public long ConnectionEpoch { get; }
    public long SequenceStart { get; }
    public long SequenceEndExclusive => checked(SequenceStart + SequenceCount);
    public int SequenceCount { get; }
    public int SampleCount { get; }
    public int ChannelCount { get; }
    public string ChannelLayoutId { get; }
    public RawNumericRepresentation NumericRepresentation { get; }
    public RawByteOrder ByteOrder { get; }
    public double SampleRateHz { get; }
    public long? DeviceTimestamp { get; }
    public long HostMonotonicTimestamp { get; }
    public DateTimeOffset WallClockTimestamp { get; }
    public ulong QualityFlags { get; }
    public string ProtocolVersion { get; }
    public string CanonicalizationVersion { get; }
    public string ConfigurationHash { get; }
    public ReadOnlyMemory<byte> Payload => _payload;

    public static CanonicalRawBlock CopyFrom(
        string sourceId,
        string? deviceId,
        long connectionEpoch,
        long sequenceStart,
        int sequenceCount,
        int sampleCount,
        int channelCount,
        string channelLayoutId,
        RawNumericRepresentation numericRepresentation,
        RawByteOrder byteOrder,
        double sampleRateHz,
        long? deviceTimestamp,
        long hostMonotonicTimestamp,
        DateTimeOffset wallClockTimestamp,
        ulong qualityFlags,
        string protocolVersion,
        string canonicalizationVersion,
        string configurationHash,
        ReadOnlySpan<byte> payload) =>
        new(
            sourceId, deviceId, connectionEpoch, sequenceStart, sequenceCount,
            sampleCount, channelCount, channelLayoutId, numericRepresentation,
            byteOrder, sampleRateHz, deviceTimestamp, hostMonotonicTimestamp,
            wallClockTimestamp, qualityFlags, protocolVersion,
            canonicalizationVersion, configurationHash, payload.ToArray());

    public CanonicalRawBlock CloneOwned() =>
        CopyFrom(
            SourceId, DeviceId, ConnectionEpoch, SequenceStart, SequenceCount,
            SampleCount, ChannelCount, ChannelLayoutId, NumericRepresentation,
            ByteOrder, SampleRateHz, DeviceTimestamp, HostMonotonicTimestamp,
            WallClockTimestamp, QualityFlags, ProtocolVersion,
            CanonicalizationVersion, ConfigurationHash, _payload);
}

public interface IRawRecorderFaultObserver { void OnFault(RawRecorderFault fault); }

public interface IRawRecorder : IAsyncDisposable
{
    RawRecorderSnapshot Snapshot { get; }
    ValueTask PrepareAsync(RawRecordingSessionDescriptor descriptor, IRawRecorderFaultObserver? faultObserver = null, CancellationToken cancellationToken = default);
    ValueTask<RawRecorderAcceptResult> AcceptAsync(CanonicalRawBlock block, CancellationToken cancellationToken = default);
    RawRecorderAcceptResult TryAccept(CanonicalRawBlock block);
    ValueTask StopAsync(CancellationToken cancellationToken = default);
    ValueTask FinalizeAsync(CancellationToken cancellationToken = default);
    ValueTask AbortAsync(string reason, CancellationToken cancellationToken = default);
}

public interface IRawRecorderFactory
{
    bool IsEnabled { get; }
    string? DisabledReason { get; }
    IRawRecorder Create();
}
