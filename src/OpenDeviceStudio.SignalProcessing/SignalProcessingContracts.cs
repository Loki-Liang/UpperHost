using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using OpenDeviceStudio.Dataflow;

namespace OpenDeviceStudio.SignalProcessing;

public static class SignalProcessingStageIds
{
    public const string RawInput = "$raw";
}

public enum SignalSampleFormat
{
    Int16,
    Int32,
    Float32,
    Float64
}

public enum SignalGapPolicy
{
    Fault,
    ResetAndMarkQuality,
    DropUntilReinitialized
}

public enum SignalTimestampAnchor
{
    Start,
    Center,
    End
}

public enum SignalTailPolicy
{
    DropIncomplete,
    EmitPartial
}

public enum SignalResetReason
{
    Explicit,
    Gap,
    Reconnect,
    ProcessingEpochChanged
}

public enum SignalProcessingRuntimeState
{
    Created,
    Running,
    Completing,
    Completed,
    Faulted,
    Disposed
}

[Flags]
public enum SignalQualityFlags : ulong
{
    None = 0,
    GapDetected = 1UL << 0,
    ResetAfterGap = 1UL << 1,
    Discontinuous = 1UL << 2,
    Warmup = 1UL << 3,
    Tail = 1UL << 4,
    StageFault = 1UL << 5
}

public readonly record struct SignalPartitionKey(
    string SourceId,
    string ChannelLayoutId)
{
    public override string ToString() => $"{SourceId}:{ChannelLayoutId}";
}

public sealed record SignalDescriptor(
    SignalSampleFormat SampleFormat,
    int ChannelCount,
    string ChannelLayoutId,
    double SampleRateHz,
    string Units,
    string ClockDomain)
{
    public void Validate()
    {
        if (ChannelCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(ChannelCount));
        ArgumentException.ThrowIfNullOrWhiteSpace(ChannelLayoutId);
        if (SampleRateHz <= 0 || double.IsNaN(SampleRateHz) || double.IsInfinity(SampleRateHz))
            throw new ArgumentOutOfRangeException(nameof(SampleRateHz));
        ArgumentException.ThrowIfNullOrWhiteSpace(Units);
        ArgumentException.ThrowIfNullOrWhiteSpace(ClockDomain);
    }

    internal string Canonical() =>
        $"{SampleFormat}|{ChannelCount}|{ChannelLayoutId}|{SampleRateHz:R}|{Units}|{ClockDomain}";
}

public sealed record SignalInputRange(
    long SequenceStart,
    long SequenceEndExclusive,
    DateTimeOffset TimeStart,
    DateTimeOffset TimeEnd)
{
    public SignalInputRange
    {
        if (SequenceEndExclusive <= SequenceStart)
            throw new ArgumentOutOfRangeException(nameof(SequenceEndExclusive));
        if (TimeEnd < TimeStart)
            throw new ArgumentOutOfRangeException(nameof(TimeEnd));
    }
}

public sealed record SignalWindowContract(
    int LengthSamples,
    int StrideSamples,
    SignalTimestampAnchor TimestampAnchor,
    SignalTailPolicy TailPolicy)
{
    public SignalWindowContract
    {
        if (LengthSamples <= 0)
            throw new ArgumentOutOfRangeException(nameof(LengthSamples));
        if (StrideSamples <= 0)
            throw new ArgumentOutOfRangeException(nameof(StrideSamples));
    }
}

public sealed record SignalStageLineage(
    string StageId,
    string StageVersion,
    string ConfigurationHash,
    SignalInputRange InputRange,
    SignalDescriptor OutputDescriptor,
    TimeSpan AlgorithmicDelay);

public sealed record SignalLineage(
    string SessionId,
    string ProcessingEpoch,
    string SourceId,
    long ConnectionEpoch,
    SignalInputRange OriginRange,
    IReadOnlyList<SignalStageLineage> Stages);

public sealed class SignalBlock<T>
    where T : unmanaged
{
    private readonly T[] _samples;

    private SignalBlock(
        string sessionId,
        string processingEpoch,
        string sourceId,
        long connectionEpoch,
        long sequenceStart,
        int sequenceCount,
        int sampleCount,
        DateTimeOffset effectiveTimestamp,
        SignalQualityFlags quality,
        SignalDescriptor descriptor,
        SignalLineage lineage,
        T[] samples)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(processingEpoch);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        if (sequenceCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(sequenceCount));
        if (sampleCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(sampleCount));
        descriptor.Validate();
        if (samples.Length != checked(sampleCount * descriptor.ChannelCount))
        {
            throw new ArgumentException(
                "Signal sample payload length must equal SampleCount * ChannelCount.",
                nameof(samples));
        }

        SessionId = sessionId;
        ProcessingEpoch = processingEpoch;
        SourceId = sourceId;
        ConnectionEpoch = connectionEpoch;
        SequenceStart = sequenceStart;
        SequenceCount = sequenceCount;
        SampleCount = sampleCount;
        EffectiveTimestamp = effectiveTimestamp;
        Quality = quality;
        Descriptor = descriptor;
        Lineage = lineage;
        _samples = samples;
    }

    public string SessionId { get; }
    public string ProcessingEpoch { get; }
    public string SourceId { get; }
    public long ConnectionEpoch { get; }
    public long SequenceStart { get; }
    public long SequenceEndExclusive => checked(SequenceStart + SequenceCount);
    public int SequenceCount { get; }
    public int SampleCount { get; }
    public DateTimeOffset EffectiveTimestamp { get; }
    public SignalQualityFlags Quality { get; }
    public SignalDescriptor Descriptor { get; }
    public SignalLineage Lineage { get; }
    public ReadOnlyMemory<T> Samples => _samples;
    public SignalPartitionKey PartitionKey => new(SourceId, Descriptor.ChannelLayoutId);

    public static SignalBlock<T> CreateInput(
        string sessionId,
        string processingEpoch,
        string sourceId,
        long connectionEpoch,
        long sequenceStart,
        int sequenceCount,
        int sampleCount,
        DateTimeOffset effectiveTimestamp,
        SignalQualityFlags quality,
        SignalDescriptor descriptor,
        ReadOnlySpan<T> samples,
        DateTimeOffset? inputRangeEnd = null)
    {
        descriptor.Validate();
        var durationSeconds = sampleCount / descriptor.SampleRateHz;
        var end = inputRangeEnd ??
            effectiveTimestamp + TimeSpan.FromSeconds(durationSeconds);
        var range = new SignalInputRange(
            sequenceStart,
            checked(sequenceStart + sequenceCount),
            effectiveTimestamp,
            end);
        var lineage = new SignalLineage(
            sessionId,
            processingEpoch,
            sourceId,
            connectionEpoch,
            range,
            Array.Empty<SignalStageLineage>());

        return new SignalBlock<T>(
            sessionId,
            processingEpoch,
            sourceId,
            connectionEpoch,
            sequenceStart,
            sequenceCount,
            sampleCount,
            effectiveTimestamp,
            quality,
            descriptor,
            lineage,
            samples.ToArray());
    }

    internal SignalBlock<T> WithQuality(SignalQualityFlags quality) =>
        new(
            SessionId,
            ProcessingEpoch,
            SourceId,
            ConnectionEpoch,
            SequenceStart,
            SequenceCount,
            SampleCount,
            EffectiveTimestamp,
            quality,
            Descriptor,
            Lineage,
            _samples);

    internal static SignalBlock<T> FromStageOutput(
        SignalBlock<T> input,
        SignalStageOutput<T> output,
        string processingEpoch,
        ISignalStageFactory<T> factory)
    {
        var stage = new SignalStageLineage(
            factory.StageId,
            factory.Version,
            factory.ConfigurationHash,
            output.InputRange,
            output.Descriptor,
            factory.AlgorithmicDelay);
        var stages = input.Lineage.Stages.Concat([stage]).ToArray();
        var lineage = input.Lineage with
        {
            ProcessingEpoch = processingEpoch,
            Stages = stages
        };

        return new SignalBlock<T>(
            input.SessionId,
            processingEpoch,
            input.SourceId,
            input.ConnectionEpoch,
            output.SequenceStart,
            output.SequenceCount,
            output.SampleCount,
            output.EffectiveTimestamp,
            output.Quality,
            output.Descriptor,
            lineage,
            output.OwnedSamples);
    }
}

public sealed class SignalStageOutput<T>
    where T : unmanaged
{
    private readonly T[] _samples;

    private SignalStageOutput(
        long sequenceStart,
        int sequenceCount,
        int sampleCount,
        DateTimeOffset effectiveTimestamp,
        SignalQualityFlags quality,
        SignalDescriptor descriptor,
        SignalInputRange inputRange,
        T[] samples)
    {
        if (sequenceCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(sequenceCount));
        if (sampleCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(sampleCount));
        descriptor.Validate();
        if (samples.Length != checked(sampleCount * descriptor.ChannelCount))
        {
            throw new ArgumentException(
                "Stage output payload length must equal SampleCount * ChannelCount.",
                nameof(samples));
        }

        SequenceStart = sequenceStart;
        SequenceCount = sequenceCount;
        SampleCount = sampleCount;
        EffectiveTimestamp = effectiveTimestamp;
        Quality = quality;
        Descriptor = descriptor;
        InputRange = inputRange;
        _samples = samples;
    }

    public long SequenceStart { get; }
    public int SequenceCount { get; }
    public int SampleCount { get; }
    public DateTimeOffset EffectiveTimestamp { get; }
    public SignalQualityFlags Quality { get; }
    public SignalDescriptor Descriptor { get; }
    public SignalInputRange InputRange { get; }
    public ReadOnlyMemory<T> Samples => _samples;
    internal T[] OwnedSamples => _samples;

    public static SignalStageOutput<T> CopyFrom(
        long sequenceStart,
        int sequenceCount,
        int sampleCount,
        DateTimeOffset effectiveTimestamp,
        SignalQualityFlags quality,
        SignalDescriptor descriptor,
        SignalInputRange inputRange,
        ReadOnlySpan<T> samples) =>
        new(
            sequenceStart,
            sequenceCount,
            sampleCount,
            effectiveTimestamp,
            quality,
            descriptor,
            inputRange,
            samples.ToArray());

    public static SignalStageOutput<T> CopyFromInput(
        SignalBlock<T> input,
        SignalDescriptor descriptor,
        ReadOnlySpan<T> samples,
        SignalQualityFlags? quality = null,
        DateTimeOffset? effectiveTimestamp = null) =>
        CopyFrom(
            input.SequenceStart,
            input.SequenceCount,
            input.SampleCount,
            effectiveTimestamp ?? input.EffectiveTimestamp,
            quality ?? input.Quality,
            descriptor,
            new SignalInputRange(
                input.SequenceStart,
                input.SequenceEndExclusive,
                input.EffectiveTimestamp,
                input.EffectiveTimestamp +
                    TimeSpan.FromSeconds(input.SampleCount / input.Descriptor.SampleRateHz)),
            samples);
}

public sealed record SignalStageResult<T>(IReadOnlyList<SignalStageOutput<T>> Outputs)
    where T : unmanaged
{
    public static SignalStageResult<T> Empty { get; } =
        new(Array.Empty<SignalStageOutput<T>>());

    public static SignalStageResult<T> One(SignalStageOutput<T> output) =>
        new([output]);
}

public sealed record SignalStageInstanceContext(
    string SessionId,
    string ProcessingEpoch,
    SignalPartitionKey PartitionKey,
    SignalDescriptor InputDescriptor,
    SignalDescriptor OutputDescriptor,
    TimeProvider TimeProvider);

public interface ISignalStage<T> : IAsyncDisposable
    where T : unmanaged
{
    ValueTask InitializeAsync(CancellationToken cancellationToken = default);

    ValueTask<SignalStageResult<T>> ProcessAsync(
        SignalBlock<T> input,
        CancellationToken cancellationToken = default);

    ValueTask<SignalStageResult<T>> CompleteAsync(
        CancellationToken cancellationToken = default);

    ValueTask ResetAsync(
        SignalResetReason reason,
        CancellationToken cancellationToken = default);
}

public interface ISignalStageFactory<T>
    where T : unmanaged
{
    string StageId { get; }
    string Version { get; }
    string ConfigurationHash { get; }
    bool IsStateful { get; }
    bool RequiresContinuity { get; }
    bool RequiresOrderedInput { get; }
    SignalGapPolicy GapPolicy { get; }
    TimeSpan AlgorithmicDelay { get; }
    SignalWindowContract? Window { get; }

    void ValidateInput(SignalDescriptor inputDescriptor);

    SignalDescriptor DescribeOutput(SignalDescriptor inputDescriptor);

    ISignalStage<T> Create(SignalStageInstanceContext context);
}

public sealed record SignalEdgeOptions(
    int Capacity = 64,
    StreamBranchDelivery Delivery = StreamBranchDelivery.Required,
    StreamOverflowPolicy Overflow = StreamOverflowPolicy.Wait,
    StreamBranchFailurePolicy FailurePolicy = StreamBranchFailurePolicy.Propagate)
{
    public SignalEdgeOptions
    {
        if (Capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(Capacity));
    }

    internal StreamBranchOptions ToRouterOptions(string stageId) =>
        new(
            $"stage:{stageId}",
            $"Signal stage {stageId}",
            Capacity,
            Delivery,
            Overflow,
            FailurePolicy,
            StreamOrderingPolicy.SerializedPublisherFifo);
}

public sealed record SignalStageRegistration<T>(
    string InputStageId,
    ISignalStageFactory<T> Factory,
    SignalEdgeOptions Edge)
    where T : unmanaged;

public sealed class SignalProcessingDefinition<T>
    where T : unmanaged
{
    public SignalProcessingDefinition(
        string graphVersion,
        SignalDescriptor inputDescriptor,
        IReadOnlyList<SignalStageRegistration<T>> stages)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(graphVersion);
        ArgumentNullException.ThrowIfNull(inputDescriptor);
        ArgumentNullException.ThrowIfNull(stages);

        GraphVersion = graphVersion;
        InputDescriptor = inputDescriptor;
        Stages = stages.ToArray();
    }

    public string GraphVersion { get; }
    public SignalDescriptor InputDescriptor { get; }
    public IReadOnlyList<SignalStageRegistration<T>> Stages { get; }
}

public sealed record SignalProcessingFault(
    string StageId,
    SignalPartitionKey? PartitionKey,
    string Message,
    Exception? Exception,
    DateTimeOffset OccurredAt);

public sealed record SignalStageRuntimeSnapshot(
    string StageId,
    int ActivePartitions,
    long BlocksIn,
    long BlocksOut,
    long SamplesIn,
    long SamplesOut,
    long GapCount,
    long ResetCount,
    long DroppedWhileBlocked,
    long FaultCount,
    SignalDescriptor OutputDescriptor);

public sealed record SignalProcessingSnapshot(
    SignalProcessingRuntimeState State,
    string SessionId,
    string ProcessingEpoch,
    string PlanHash,
    SignalProcessingFault? Fault,
    IReadOnlyList<SignalStageRuntimeSnapshot> Stages,
    IReadOnlyList<StreamBranchSnapshot> Edges);

public static class SignalConfigurationHash
{
    public static string Compute(IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var canonical = string.Join(
            "\n",
            values
                .OrderBy(static item => item.Key, StringComparer.Ordinal)
                .Select(static item => $"{item.Key}={item.Value}"));
        return Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}

public class SignalProcessingException(string message, Exception? inner = null)
    : InvalidOperationException(message, inner);

public sealed class SignalContinuityException(string message)
    : SignalProcessingException(message);
