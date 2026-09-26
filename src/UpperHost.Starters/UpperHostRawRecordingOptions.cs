using Microsoft.Extensions.Options;
using UpperHost.Abstractions.Storage;

namespace UpperHost.Starters;

public sealed class UpperHostRawRecordingOptions
{
    public const string SectionName = "UpperHost:Acquisition:RawRecording";

    public bool Enabled { get; set; } = true;
    public string RootDirectory { get; set; } = Path.Combine("data", "raw");
    public int QueueCapacity { get; set; } = 256;
    public long MaxSegmentBytes { get; set; } = 268_435_456;
    public int MaxSegmentMinutes { get; set; } = 10;
    public long HardMinimumFreeBytes { get; set; } = 67_108_864;
    public long WarningFreeBytes { get; set; } = 268_435_456;
    public long MaxSessionBytes { get; set; } = 10_737_418_240;
    public RawDurabilityLevel Durability { get; set; } = RawDurabilityLevel.FlushOnFinalize;
    public int FreeSpaceCheckIntervalBlocks { get; set; } = 64;
    public string? DisabledReason { get; set; }
}

internal sealed class UpperHostRawRecordingOptionsValidator :
    IValidateOptions<UpperHostRawRecordingOptions>
{
    public ValidateOptionsResult Validate(
        string? name,
        UpperHostRawRecordingOptions options)
    {
        var failures = new List<string>();

        if (!options.Enabled && string.IsNullOrWhiteSpace(options.DisabledReason))
            failures.Add("UpperHost:Acquisition:RawRecording:DisabledReason is required when recording is disabled.");

        if (options.Enabled && string.IsNullOrWhiteSpace(options.RootDirectory))
            failures.Add("UpperHost:Acquisition:RawRecording:RootDirectory is required.");

        if (options.QueueCapacity <= 0)
            failures.Add("RawRecording QueueCapacity must be greater than zero.");
        if (options.MaxSegmentBytes <= 0)
            failures.Add("RawRecording MaxSegmentBytes must be greater than zero.");
        if (options.MaxSegmentMinutes <= 0)
            failures.Add("RawRecording MaxSegmentMinutes must be greater than zero.");
        if (options.HardMinimumFreeBytes < 0)
            failures.Add("RawRecording HardMinimumFreeBytes must not be negative.");
        if (options.WarningFreeBytes < options.HardMinimumFreeBytes)
            failures.Add("RawRecording WarningFreeBytes must be >= HardMinimumFreeBytes.");
        if (options.MaxSessionBytes <= 0)
            failures.Add("RawRecording MaxSessionBytes must be greater than zero.");
        if (options.FreeSpaceCheckIntervalBlocks <= 0)
            failures.Add("RawRecording FreeSpaceCheckIntervalBlocks must be greater than zero.");

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    public static void ValidateAndThrow(UpperHostRawRecordingOptions options)
    {
        var result = new UpperHostRawRecordingOptionsValidator().Validate(null, options);
        if (result.Failed)
            throw new OptionsValidationException(
                UpperHostRawRecordingOptions.SectionName,
                typeof(UpperHostRawRecordingOptions),
                result.Failures);
    }
}
