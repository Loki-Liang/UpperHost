using OpenDeviceStudio.Abstractions.Storage;

namespace OpenDeviceStudio.Storage.FileSystem;

public sealed record FileSystemRawRecorderOptions(
    string RootDirectory,
    int QueueCapacity = 256,
    long MaxSegmentBytes = 268_435_456,
    TimeSpan? MaxSegmentDuration = null,
    long HardMinimumFreeBytes = 67_108_864,
    long WarningFreeBytes = 268_435_456,
    long MaxSessionBytes = 10_737_418_240,
    RawDurabilityLevel Durability = RawDurabilityLevel.FlushOnFinalize,
    int FreeSpaceCheckIntervalBlocks = 64)
{
    public TimeSpan EffectiveMaxSegmentDuration =>
        MaxSegmentDuration ?? TimeSpan.FromMinutes(10);

    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(RootDirectory);
        if (QueueCapacity <= 0) throw new ArgumentOutOfRangeException(nameof(QueueCapacity));
        if (MaxSegmentBytes <= 0) throw new ArgumentOutOfRangeException(nameof(MaxSegmentBytes));
        if (EffectiveMaxSegmentDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(MaxSegmentDuration));
        if (HardMinimumFreeBytes < 0) throw new ArgumentOutOfRangeException(nameof(HardMinimumFreeBytes));
        if (WarningFreeBytes < HardMinimumFreeBytes)
            throw new ArgumentOutOfRangeException(nameof(WarningFreeBytes));
        if (MaxSessionBytes <= 0) throw new ArgumentOutOfRangeException(nameof(MaxSessionBytes));
        if (FreeSpaceCheckIntervalBlocks <= 0)
            throw new ArgumentOutOfRangeException(nameof(FreeSpaceCheckIntervalBlocks));
    }
}

public sealed class FileSystemRawRecorderFactory : IRawRecorderFactory
{
    private readonly FileSystemRawRecorderOptions _options;
    private readonly TimeProvider _timeProvider;

    public FileSystemRawRecorderFactory(
        FileSystemRawRecorderOptions options,
        TimeProvider? timeProvider = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public bool IsEnabled => true;
    public string? DisabledReason => null;

    public IRawRecorder Create() =>
        new FileSystemRawRecorder(_options, _timeProvider);
}
