using System.Reflection;
using Microsoft.Extensions.Logging;
using OpenDeviceStudio.Abstractions.Observability;

namespace OpenDeviceStudio.Observability;

public sealed class OpenDeviceStudioObservabilityOptions
{
    public const string SectionName = "OpenDeviceStudio:Observability";

    public string ServiceName { get; set; } = "OpenDeviceStudio.Application";
    public string ServiceVersion { get; set; } =
        Assembly.GetEntryAssembly()?.GetName().Version?.ToString()
        ?? OpenDeviceStudioTelemetry.InstrumentationVersion;

    public OpenDeviceStudioLoggingOptions Logging { get; set; } = new();
    public OpenDeviceStudioOtlpOptions Otlp { get; set; } = new();

    public OpenDeviceStudioFileLoggingOptions FileLogging
    {
        get => Logging.File;
        set => Logging.File = value ?? new OpenDeviceStudioFileLoggingOptions();
    }

    public void Validate() => OpenDeviceStudioObservabilityOptionsValidator.ValidateAndThrow(this);
}

public sealed class OpenDeviceStudioLoggingOptions
{
    public OpenDeviceStudioFileLoggingOptions File { get; set; } = new();
}

public sealed class OpenDeviceStudioFileLoggingOptions
{
    public bool Enabled { get; set; }
    public string Path { get; set; } = "logs/opendevicestudio-.json";
    public LogLevel MinimumLevel { get; set; } = LogLevel.Information;
    public long FileSizeLimitBytes { get; set; } = 50 * 1024 * 1024;
    public int RetainedFileCountLimit { get; set; } = 14;
    public int AsyncBufferSize { get; set; } = 10_000;
    public bool BlockWhenFull { get; set; }
}

public sealed class OpenDeviceStudioOtlpOptions
{
    public bool Enabled { get; set; }
    public string? Endpoint { get; set; }
    public double TraceSampleRatio { get; set; } = 1.0;
}
