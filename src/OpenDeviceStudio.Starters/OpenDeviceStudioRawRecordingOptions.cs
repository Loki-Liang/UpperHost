using Microsoft.Extensions.Options;
using OpenDeviceStudio.Abstractions.Storage;

namespace OpenDeviceStudio.Starters;

public sealed class OpenDeviceStudioRawRecordingOptions
{
    public const string SectionName = "OpenDeviceStudio:Acquisition:RawRecording";

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

internal sealed class OpenDeviceStudioRawRecordingOptionsValidator :
    IValidateOptions<OpenDeviceStudioRawRecordingOptions>
{
    public ValidateOptionsResult Validate(
        string? name,
        OpenDeviceStudioRawRecordingOptions options)
    {
        var failures = new List<string>();

        if (!options.Enabled && string.IsNullOrWhiteSpace(options.DisabledReason))
            failures.Add("OpenDeviceStudio:Acquisition:RawRecording:DisabledReason is required when recording is disabled.");

        if (options.Enabled && string.IsNullOrWhiteSpace(options.RootDirectory))
            failures.Add("OpenDeviceStudio:Acquisition:RawRecording:RootDirectory is required.");

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

    public static void ValidateAndThrow(OpenDeviceStudioRawRecordingOptions options)
    {
        var result = new OpenDeviceStudioRawRecordingOptionsValidator().Validate(null, options);
        if (result.Failed)
            throw new OptionsValidationException(
                OpenDeviceStudioRawRecordingOptions.SectionName,
                typeof(OpenDeviceStudioRawRecordingOptions),
                result.Failures);
    }
}
