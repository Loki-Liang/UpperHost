using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using OpenDeviceStudio.Abstractions.Transports;
using OpenDeviceStudio.Hosting;
using OpenDeviceStudio.Resilience;
using OpenDeviceStudio.Transport.Serial;
using OpenDeviceStudio.Transport.Tcp;

namespace OpenDeviceStudio.Starters;

public static class ConfiguredTransportExtensions
{
    public static OpenDeviceStudioApplicationBuilder AddConfiguredTransport(this OpenDeviceStudioApplicationBuilder builder)
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
                "OpenDeviceStudio:Transport:Type must be one of Simulator, Serial or Tcp.")
        };
    }

    public static OpenDeviceStudioApplicationBuilder AddOpenDeviceStudioApplication(this OpenDeviceStudioApplicationBuilder builder) =>
        builder.AddOpenDeviceStudioDefaults().AddConfiguredTransport();

    private static OpenDeviceStudioTransportOptions BindConfiguredTransportOptions(
        OpenDeviceStudioApplicationBuilder builder)
    {
        var section = builder.Configuration.GetSection(OpenDeviceStudioTransportOptions.SectionName);

        builder.Services
            .AddOptions<OpenDeviceStudioTransportOptions>()
            .Bind(section)
            .ValidateOnStart();
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<OpenDeviceStudioTransportOptions>,
                OpenDeviceStudioTransportOptionsValidator>());

        var options = section.Get<OpenDeviceStudioTransportOptions>() ?? new OpenDeviceStudioTransportOptions();
        OpenDeviceStudioTransportOptionsValidator.ValidateAndThrow(options);
        return options;
    }

    private static OpenDeviceStudioApplicationBuilder AddConfiguredSerial(
        OpenDeviceStudioApplicationBuilder builder,
        OpenDeviceStudioTransportOptions configuration)
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
        return builder.AddOpenDeviceStudioTransport(
            _ => new SerialTransport(options),
            transport => WrapIfEnabled(transport, resilience));
    }

    private static OpenDeviceStudioApplicationBuilder AddConfiguredTcp(
        OpenDeviceStudioApplicationBuilder builder,
        OpenDeviceStudioTransportOptions configuration)
    {
        var configured = configuration.Tcp;
        var options = new TcpTransportOptions(
            configured.Host!,
            configured.Port,
            configured.ReadBufferSize);
        var resilience = CreateReconnectConfiguration(configuration.Resilience);

        builder.Services.AddSingleton(options);
        return builder.AddOpenDeviceStudioTransport(
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
        OpenDeviceStudioTransportResilienceOptions options)
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
