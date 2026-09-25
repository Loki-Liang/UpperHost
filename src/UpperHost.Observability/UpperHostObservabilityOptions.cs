using System.Reflection;
using Microsoft.Extensions.Logging;
using UpperHost.Abstractions.Observability;

namespace UpperHost.Observability;

public sealed class UpperHostObservabilityOptions
{
    public const string SectionName = "UpperHost:Observability";

    public string ServiceName { get; set; } = "UpperHost.Application";
    public string ServiceVersion { get; set; } =
        Assembly.GetEntryAssembly()?.GetName().Version?.ToString()
        ?? UpperHostTelemetry.InstrumentationVersion;

    public UpperHostFileLoggingOptions FileLogging { get; set; } = new();
    public UpperHostOtlpOptions Otlp { get; set; } = new();

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ServiceName))
            throw new InvalidOperationException("UpperHost observability ServiceName is required.");
        if (string.IsNullOrWhiteSpace(ServiceVersion))
            throw new InvalidOperationException("UpperHost observability ServiceVersion is required.");
        FileLogging.Validate();
        Otlp.Validate();
    }
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

    internal void Validate()
    {
        if (!Enabled)
            return;
        if (string.IsNullOrWhiteSpace(Path))
            throw new InvalidOperationException("UpperHost file log path is required when file logging is enabled.");
        if (FileSizeLimitBytes <= 0)
            throw new InvalidOperationException("UpperHost file log size limit must be greater than zero.");
        if (RetainedFileCountLimit <= 0)
            throw new InvalidOperationException("UpperHost retained file count must be greater than zero.");
        if (AsyncBufferSize <= 0)
            throw new InvalidOperationException("UpperHost async log buffer size must be greater than zero.");
    }
}

public sealed class UpperHostOtlpOptions
{
    public bool Enabled { get; set; }
    public string? Endpoint { get; set; }
    public double TraceSampleRatio { get; set; } = 1.0;

    internal void Validate()
    {
        if (TraceSampleRatio <= 0 || TraceSampleRatio > 1)
            throw new InvalidOperationException("UpperHost OTLP trace sample ratio must be greater than 0 and at most 1.");

        if (!Enabled)
            return;
        if (string.IsNullOrWhiteSpace(Endpoint) ||
            !Uri.TryCreate(Endpoint, UriKind.Absolute, out _))
        {
            throw new InvalidOperationException(
                "UpperHost OTLP endpoint must be an absolute URI when OTLP export is enabled.");
        }
    }
}
