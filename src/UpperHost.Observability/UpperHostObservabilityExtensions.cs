using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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

        var options = new UpperHostObservabilityOptions();
        builder.Configuration
            .GetSection(UpperHostObservabilityOptions.SectionName)
            .Bind(options);

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
        builder.Services.TryAddSingleton<IOptions<UpperHostObservabilityOptions>>(
            Options.Create(options));
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
        var monitor = new AsyncLogBufferMonitor();
        builder.Services.AddSingleton(monitor);
        builder.Services.AddSingleton<IHealthProbe>(monitor);

        var logger = new LoggerConfiguration()
            .MinimumLevel.Is(ToSerilogLevel(options.MinimumLevel))
            .Enrich.FromLogContext()
            .Enrich.With<SensitiveDataRedactionEnricher>()
            .WriteTo.Async(
                sink => sink.File(
                    new JsonFormatter(renderMessage: true),
                    options.Path,
                    rollingInterval: RollingInterval.Day,
                    fileSizeLimitBytes: options.FileSizeLimitBytes,
                    rollOnFileSizeLimit: true,
                    retainedFileCountLimit: options.RetainedFileCountLimit,
                    shared: false),
                bufferSize: options.AsyncBufferSize,
                blockWhenFull: options.BlockWhenFull,
                monitor: monitor)
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
                .SetSampler(new ParentBasedSampler(
                    new TraceIdRatioBasedSampler(options.Otlp.TraceSampleRatio)))
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
}
