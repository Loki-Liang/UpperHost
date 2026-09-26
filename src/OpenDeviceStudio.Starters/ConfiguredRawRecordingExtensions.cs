using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using OpenDeviceStudio.Hosting;
using OpenDeviceStudio.Storage.FileSystem;

namespace OpenDeviceStudio.Starters;

public static class ConfiguredRawRecordingExtensions
{
    public static OpenDeviceStudioApplicationBuilder AddConfiguredRawRecording(
        this OpenDeviceStudioApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var section = builder.Configuration.GetSection(OpenDeviceStudioRawRecordingOptions.SectionName);
        builder.Services
            .AddOptions<OpenDeviceStudioRawRecordingOptions>()
            .Bind(section)
            .ValidateOnStart();
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<
                IValidateOptions<OpenDeviceStudioRawRecordingOptions>,
                OpenDeviceStudioRawRecordingOptionsValidator>());

        var configuration =
            section.Get<OpenDeviceStudioRawRecordingOptions>() ??
            new OpenDeviceStudioRawRecordingOptions();
        OpenDeviceStudioRawRecordingOptionsValidator.ValidateAndThrow(configuration);

        if (!configuration.Enabled)
            return builder.DisableRawRecording(configuration.DisabledReason!);

        return builder.AddFileSystemRawRecording(
            new FileSystemRawRecorderOptions(
                configuration.RootDirectory,
                configuration.QueueCapacity,
                configuration.MaxSegmentBytes,
                TimeSpan.FromMinutes(configuration.MaxSegmentMinutes),
                configuration.HardMinimumFreeBytes,
                configuration.WarningFreeBytes,
                configuration.MaxSessionBytes,
                configuration.Durability,
                configuration.FreeSpaceCheckIntervalBlocks));
    }
}
