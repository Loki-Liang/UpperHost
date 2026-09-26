using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using UpperHost.Hosting;
using UpperHost.Storage.FileSystem;

namespace UpperHost.Starters;

public static class ConfiguredRawRecordingExtensions
{
    public static UpperHostApplicationBuilder AddConfiguredRawRecording(
        this UpperHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var section = builder.Configuration.GetSection(UpperHostRawRecordingOptions.SectionName);
        builder.Services
            .AddOptions<UpperHostRawRecordingOptions>()
            .Bind(section)
            .ValidateOnStart();
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<
                IValidateOptions<UpperHostRawRecordingOptions>,
                UpperHostRawRecordingOptionsValidator>());

        var configuration =
            section.Get<UpperHostRawRecordingOptions>() ??
            new UpperHostRawRecordingOptions();
        UpperHostRawRecordingOptionsValidator.ValidateAndThrow(configuration);

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
