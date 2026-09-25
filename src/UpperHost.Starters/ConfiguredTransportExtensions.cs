using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using UpperHost.Abstractions.Transports;
using UpperHost.Hosting;
using UpperHost.Resilience;
using UpperHost.Transport.Serial;
using UpperHost.Transport.Tcp;

namespace UpperHost.Starters;

public static class ConfiguredTransportExtensions
{
    public static UpperHostApplicationBuilder AddConfiguredTransport(this UpperHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = BindConfiguredTransportOptions(builder);
        var transportType = (options.Type ?? string.Empty).Trim().ToLowerInvariant();

        return transportType switch
        {
            "simulator" => builder.AddSimulatorTransport(options.Simulator.Name),
            "serial" => AddConfiguredSerial(builder, options),
            "tcp" => AddConfiguredTcp(builder, options),
            _ => throw new InvalidOperationException(
                "UpperHost:Transport:Type must be one of Simulator, Serial or Tcp.")
        };
    }

    public static UpperHostApplicationBuilder AddUpperHostApplication(this UpperHostApplicationBuilder builder) =>
        builder.AddUpperHostDefaults().AddConfiguredTransport();

    private static UpperHostTransportOptions BindConfiguredTransportOptions(
        UpperHostApplicationBuilder builder)
    {
        var section = builder.Configuration.GetSection(UpperHostTransportOptions.SectionName);

        builder.Services
            .AddOptions<UpperHostTransportOptions>()
            .Bind(section)
            .ValidateOnStart();
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<UpperHostTransportOptions>,
                UpperHostTransportOptionsValidator>());

        var options = section.Get<UpperHostTransportOptions>() ?? new UpperHostTransportOptions();
        UpperHostTransportOptionsValidator.ValidateAndThrow(options);
        return options;
    }

    private static UpperHostApplicationBuilder AddConfiguredSerial(
        UpperHostApplicationBuilder builder,
        UpperHostTransportOptions configuration)
    {
        var configured = configuration.Serial;
        var options = new SerialTransportOptions(
            configured.PortName!,
            configured.BaudRate,
            configured.DataBits,
            configured.Parity,
            configured.StopBits,
            configured.ReadBufferSize);
        var resilience = CreateReconnectConfiguration(configuration.Resilience);

        builder.Services.AddSingleton(options);
        return builder.AddUpperHostTransport(
            _ => new SerialTransport(options),
            transport => WrapIfEnabled(transport, resilience));
    }

    private static UpperHostApplicationBuilder AddConfiguredTcp(
        UpperHostApplicationBuilder builder,
        UpperHostTransportOptions configuration)
    {
        var configured = configuration.Tcp;
        var options = new TcpTransportOptions(
            configured.Host!,
            configured.Port,
            configured.ReadBufferSize);
        var resilience = CreateReconnectConfiguration(configuration.Resilience);

        builder.Services.AddSingleton(options);
        return builder.AddUpperHostTransport(
            _ => new TcpTransport(options),
            transport => WrapIfEnabled(transport, resilience));
    }

    private static ITransport WrapIfEnabled(
        ITransport inner,
        ReconnectConfiguration configuration)
    {
        return configuration.Enabled
            ? new ReconnectingTransport(inner, configuration.Policy)
            : inner;
    }

    private static ReconnectConfiguration CreateReconnectConfiguration(
        UpperHostTransportResilienceOptions options)
    {
        var policy = new ReconnectPolicy(
            options.MaxAttempts,
            TimeSpan.FromMilliseconds(options.InitialDelayMs),
            options.BackoffFactor,
            TimeSpan.FromMilliseconds(options.MaximumDelayMs),
            options.ReconnectOnEndOfStream);

        return new ReconnectConfiguration(options.Enabled, policy);
    }

    private sealed record ReconnectConfiguration(bool Enabled, ReconnectPolicy Policy);
}
