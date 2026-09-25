using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using UpperHost.Abstractions.Observability;

namespace UpperHost.Observability;

public sealed class UpperHostObservabilityOptions
{
    public const string SectionName = "UpperHost:Observability";

    public string ServiceName { get; set; } = "UpperHost.Application";
    public string ServiceVersion { get; set; } =
        Assembly.GetEntryAssembly()?.GetName().Version?.ToString()
        ?? UpperHostTelemetry.InstrumentationVersion;

    public UpperHostLoggingOptions Logging { get; set; } = new();
    public UpperHostOtlpOptions Otlp { get; set; } = new();

    // Compatibility alias for existing programmatic configuration.
    public UpperHostFileLoggingOptions FileLogging
    {
        get => Logging.File;
        set => Logging.File = value ?? new UpperHostFileLoggingOptions();
    }

    public void Validate()
    {
        var result = new UpperHostObservabilityOptionsValidator()
            .Validate(Options.DefaultName, this);
        if (!result.Failed)
            return;

        throw new OptionsValidationException(
            Options.DefaultName,
            typeof(UpperHostObservabilityOptions),
            result.Failures);
    }
}

public sealed class UpperHostLoggingOptions
{
    public UpperHostFileLoggingOptions File { get; set; } = new();
}

public sealed class UpperHostFileLoggingOptions
{
    public bool Enabled { get; set; }
    public string Path { get; set; } = "logs/upperhost-.json";
    public LogLevel MinimumLevel { get; set; } = LogLevel.Information;
    public long FileSizeLimitBytes { get; set; } = 50 * 1024 * 1024;
    public int RetainedFileCountLimit { get; set; } = 14;
    public int AsyncBufferSize { get; set; } = 10_000;
    public bool BlockWhenFull { get; set; }
}

public sealed class UpperHostOtlpOptions
{
    public bool Enabled { get; set; }
    public string? Endpoint { get; set; }
    public double TraceSampleRatio { get; set; } = 1.0;
}
