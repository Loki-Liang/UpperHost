using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Json;
using UpperHost.Abstractions.Diagnostics;
using UpperHost.Abstractions.Observability;
using UpperHost.Hosting;

namespace UpperHost.Observability;

public static class UpperHostObservabilityExtensions
{
    public static UpperHostApplicationBuilder AddConfiguredUpperHostObservability(
        this UpperHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = new UpperHostObservabilityOptions
        {
            ServiceName = Value(builder, "UpperHost:Observability:ServiceName", "UpperHost.Application"),
            ServiceVersion = Value(
                builder,
                "UpperHost:Observability:ServiceVersion",
                UpperHostTelemetry.InstrumentationVersion)
        };

        options.FileLogging.Enabled =
            Boolean(builder, "UpperHost:Observability:Logging:File:Enabled", false);
        options.FileLogging.Path =
            Value(builder, "UpperHost:Observability:Logging:File:Path", "logs/upperhost-.json");
        options.FileLogging.FileSizeLimitBytes =
            PositiveLong(
                builder,
                "UpperHost:Observability:Logging:File:FileSizeLimitBytes",
                50 * 1024 * 1024);
        options.FileLogging.RetainedFileCountLimit =
            PositiveInt(
                builder,
                "UpperHost:Observability:Logging:File:RetainedFileCountLimit",
                14);
        options.FileLogging.MinimumLevel =
            LogLevelValue(
                builder,
                "UpperHost:Observability:Logging:File:MinimumLevel",
                LogLevel.Information);

        options.Otlp.Enabled =
            Boolean(builder, "UpperHost:Observability:Otlp:Enabled", false);
        options.Otlp.Endpoint =
            builder.Configuration["UpperHost:Observability:Otlp:Endpoint"];

        return builder.AddUpperHostObservability(options);
    }

    public static UpperHostApplicationBuilder AddUpperHostObservability(
        this UpperHostApplicationBuilder builder,
        UpperHostObservabilityOptions options)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        builder.Services.TryAddSingleton(options);
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IHealthProbe, TransportHealthProbe>());

        if (options.FileLogging.Enabled)
            AddFileLogging(builder, options.FileLogging);

        if (options.Otlp.Enabled)
            AddOpenTelemetry(builder, options);

        return builder;
    }

    private static void AddFileLogging(
        UpperHostApplicationBuilder builder,
        UpperHostFileLoggingOptions options)
    {
        var logger = new LoggerConfiguration()
            .MinimumLevel.Is(ToSerilogLevel(options.MinimumLevel))
            .Enrich.FromLogContext()
            .WriteTo.File(
                new JsonFormatter(renderMessage: true),
                options.Path,
                rollingInterval: RollingInterval.Day,
                fileSizeLimitBytes: options.FileSizeLimitBytes,
                rollOnFileSizeLimit: true,
                retainedFileCountLimit: options.RetainedFileCountLimit,
                shared: false)
            .CreateLogger();

        builder.Services.AddLogging(logging => logging.AddSerilog(logger, dispose: true));
    }

    private static void AddOpenTelemetry(
        UpperHostApplicationBuilder builder,
        UpperHostObservabilityOptions options)
    {
        var endpoint = new Uri(options.Otlp.Endpoint!, UriKind.Absolute);

        builder.Services
            .AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(
                options.ServiceName,
                serviceVersion: options.ServiceVersion))
            .WithTracing(tracing => tracing
                .AddSource(UpperHostTelemetry.InstrumentationName)
                .AddOtlpExporter(exporter => exporter.Endpoint = endpoint))
            .WithMetrics(metrics => metrics
                .AddMeter(UpperHostTelemetry.InstrumentationName)
                .AddOtlpExporter(exporter => exporter.Endpoint = endpoint));
    }

    private static LogEventLevel ToSerilogLevel(LogLevel level) => level switch
    {
        LogLevel.Trace => LogEventLevel.Verbose,
        LogLevel.Debug => LogEventLevel.Debug,
        LogLevel.Information => LogEventLevel.Information,
        LogLevel.Warning => LogEventLevel.Warning,
        LogLevel.Error => LogEventLevel.Error,
        LogLevel.Critical => LogEventLevel.Fatal,
        LogLevel.None => LogEventLevel.Fatal,
        _ => LogEventLevel.Information
    };

    private static string Value(
        UpperHostApplicationBuilder builder,
        string key,
        string fallback)
    {
        var raw = builder.Configuration[key];
        return string.IsNullOrWhiteSpace(raw) ? fallback : raw.Trim();
    }

    private static bool Boolean(
        UpperHostApplicationBuilder builder,
        string key,
        bool fallback)
    {
        var raw = builder.Configuration[key];
        if (string.IsNullOrWhiteSpace(raw))
            return fallback;
        return bool.TryParse(raw, out var value)
            ? value
            : throw new InvalidOperationException(
                $"UpperHost configuration '{key}' must be true or false.");
    }

    private static int PositiveInt(
        UpperHostApplicationBuilder builder,
        string key,
        int fallback)
    {
        var raw = builder.Configuration[key];
        if (string.IsNullOrWhiteSpace(raw))
            return fallback;
        return int.TryParse(raw, out var value) && value > 0
            ? value
            : throw new InvalidOperationException(
                $"UpperHost configuration '{key}' must be a positive integer.");
    }

    private static long PositiveLong(
        UpperHostApplicationBuilder builder,
        string key,
        long fallback)
    {
        var raw = builder.Configuration[key];
        if (string.IsNullOrWhiteSpace(raw))
            return fallback;
        return long.TryParse(raw, out var value) && value > 0
            ? value
            : throw new InvalidOperationException(
                $"UpperHost configuration '{key}' must be a positive integer.");
    }

    private static LogLevel LogLevelValue(
        UpperHostApplicationBuilder builder,
        string key,
        LogLevel fallback)
    {
        var raw = builder.Configuration[key];
        if (string.IsNullOrWhiteSpace(raw))
            return fallback;
        return Enum.TryParse<LogLevel>(raw, ignoreCase: true, out var value)
            ? value
            : throw new InvalidOperationException(
                $"UpperHost configuration '{key}' is not a valid Microsoft.Extensions.Logging.LogLevel.");
    }
}
