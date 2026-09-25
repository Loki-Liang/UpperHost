using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using UpperHost.Abstractions.Transports;
using UpperHost.Hosting;
using UpperHost.Observability;
using UpperHost.Resilience;
using UpperHost.Transport.Serial;
using UpperHost.Transport.Tcp;

namespace UpperHost.Starters;

public static class ConfiguredTransportExtensions
{
    public static UpperHostApplicationBuilder AddConfiguredTransport(this UpperHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var section = builder.Configuration.GetSection(UpperHostTransportOptions.SectionName);

        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<UpperHostTransportOptions>, UpperHostTransportOptionsValidator>());
        builder.Services
            .AddOptions<UpperHostTransportOptions>()
            .Bind(section)
            .ValidateOnStart();
        builder.Services.TryAddSingleton<UpperHostTransportOptions>(services =>
            services.GetRequiredService<IOptions<UpperHostTransportOptions>>().Value);

        var options = section.Get<UpperHostTransportOptions>() ?? new UpperHostTransportOptions();
        EnsureValid(options);

        return options.Type switch
        {
            UpperHostTransportType.Simulator => builder.AddSimulatorTransport(options.Simulator.Name),
            UpperHostTransportType.Serial => AddConfiguredSerial(builder, options),
            UpperHostTransportType.Tcp => AddConfiguredTcp(builder, options),
            _ => throw new OptionsValidationException(
                nameof(UpperHostTransportOptions),
                typeof(UpperHostTransportOptions),
                [$"{UpperHostTransportOptions.SectionName}:Type is unsupported."])
        };
    }

    public static UpperHostApplicationBuilder AddUpperHostApplication(this UpperHostApplicationBuilder builder) =>
        builder.AddUpperHostDefaults().AddConfiguredTransport();

    private static UpperHostApplicationBuilder AddConfiguredSerial(
        UpperHostApplicationBuilder builder,
        UpperHostTransportOptions options)
    {
        var serial = new SerialTransportOptions(
            options.Serial.PortName!,
            options.Serial.BaudRate);
        var resilience = ToReconnectPolicy(options.Resilience);

        builder.Services.AddSingleton(serial);
        return builder.AddUpperHostTransport(
            _ => new SerialTransport(serial),
            transport => WrapIfEnabled(transport, options.Resilience.Enabled, resilience));
    }

    private static UpperHostApplicationBuilder AddConfiguredTcp(
        UpperHostApplicationBuilder builder,
        UpperHostTransportOptions options)
    {
        var tcp = new TcpTransportOptions(
            options.Tcp.Host!,
            options.Tcp.Port);
        var resilience = ToReconnectPolicy(options.Resilience);

        builder.Services.AddSingleton(tcp);
        return builder.AddUpperHostTransport(
            _ => new TcpTransport(tcp),
            transport => WrapIfEnabled(transport, options.Resilience.Enabled, resilience));
    }

    private static ITransport WrapIfEnabled(
        ITransport inner,
        bool enabled,
        ReconnectPolicy policy) =>
        enabled ? new ReconnectingTransport(inner, policy) : inner;

    private static ReconnectPolicy ToReconnectPolicy(UpperHostResilienceOptions options)
    {
        var policy = new ReconnectPolicy(
            options.MaxAttempts,
            TimeSpan.FromMilliseconds(options.InitialDelayMs),
            options.BackoffFactor,
            TimeSpan.FromMilliseconds(options.MaximumDelayMs),
            options.ReconnectOnEndOfStream);
        policy.Validate();
        return policy;
    }

    private static void EnsureValid(UpperHostTransportOptions options)
    {
        var result = new UpperHostTransportOptionsValidator().Validate(Options.DefaultName, options);
        if (!result.Failed)
            return;

        throw new OptionsValidationException(
            Options.DefaultName,
            typeof(UpperHostTransportOptions),
            result.Failures);
    }
}
