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
using OpenDeviceStudio.Abstractions.Diagnostics;
using OpenDeviceStudio.Abstractions.Observability;
using OpenDeviceStudio.Hosting;

namespace OpenDeviceStudio.Observability;

public static class OpenDeviceStudioObservabilityExtensions
{
    public static OpenDeviceStudioApplicationBuilder AddConfiguredOpenDeviceStudioObservability(
        this OpenDeviceStudioApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var section = builder.Configuration
            .GetSection(OpenDeviceStudioObservabilityOptions.SectionName);

        builder.Services
            .AddOptions<OpenDeviceStudioObservabilityOptions>()
            .Bind(section)
            .ValidateOnStart();
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<OpenDeviceStudioObservabilityOptions>,
                OpenDeviceStudioObservabilityOptionsValidator>());

        var options = section.Get<OpenDeviceStudioObservabilityOptions>()
            ?? new OpenDeviceStudioObservabilityOptions();
        OpenDeviceStudioObservabilityOptionsValidator.ValidateAndThrow(options);

        builder.Services.TryAddSingleton(options);
        ConfigureObservabilityInfrastructure(builder, options);
        return builder;
    }

    public static OpenDeviceStudioApplicationBuilder AddOpenDeviceStudioObservability(
        this OpenDeviceStudioApplicationBuilder builder,
        OpenDeviceStudioObservabilityOptions options)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(options);
        OpenDeviceStudioObservabilityOptionsValidator.ValidateAndThrow(options);

        builder.Services.TryAddSingleton(options);
        builder.Services.TryAddSingleton<IOptions<OpenDeviceStudioObservabilityOptions>>(
            Options.Create(options));

        ConfigureObservabilityInfrastructure(builder, options);
        return builder;
    }

    private static void ConfigureObservabilityInfrastructure(
        OpenDeviceStudioApplicationBuilder builder,
        OpenDeviceStudioObservabilityOptions options)
    {
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IHealthProbe, TransportHealthProbe>());

        if (options.FileLogging.Enabled)
            AddFileLogging(builder, options.FileLogging);

        if (options.Otlp.Enabled)
            AddOpenTelemetry(builder, options);
    }

    private static void AddFileLogging(
        OpenDeviceStudioApplicationBuilder builder,
        OpenDeviceStudioFileLoggingOptions options)
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
        OpenDeviceStudioApplicationBuilder builder,
        OpenDeviceStudioObservabilityOptions options)
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
                .AddSource(OpenDeviceStudioTelemetry.InstrumentationName)
                .AddOtlpExporter(exporter => exporter.Endpoint = endpoint))
            .WithMetrics(metrics => metrics
                .AddMeter(OpenDeviceStudioTelemetry.InstrumentationName)
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
