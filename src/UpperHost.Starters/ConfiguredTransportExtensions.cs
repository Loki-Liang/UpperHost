using Microsoft.Extensions.DependencyInjection;
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

        var transportType = builder.Configuration["UpperHost:Transport:Type"]?.Trim();
        if (string.IsNullOrWhiteSpace(transportType))
            transportType = "Simulator";

        return transportType.ToLowerInvariant() switch
        {
            "simulator" => builder.AddSimulatorTransport(
                builder.Configuration["UpperHost:Transport:Simulator:Name"] ?? "default"),
            "serial" => AddConfiguredSerial(builder),
            "tcp" => AddConfiguredTcp(builder),
            _ => throw new InvalidOperationException(
                $"Unsupported UpperHost transport type '{transportType}'. Registered baseline types are Simulator, Serial and Tcp. Add a provider starter for custom transports.")
        };
    }

    public static UpperHostApplicationBuilder AddUpperHostApplication(this UpperHostApplicationBuilder builder) =>
        builder.AddUpperHostDefaults().AddConfiguredTransport();

    private static UpperHostApplicationBuilder AddConfiguredSerial(UpperHostApplicationBuilder builder)
    {
        var options = new SerialTransportOptions(
            Required(builder, "UpperHost:Transport:Serial:PortName"),
            PositiveInt(builder, "UpperHost:Transport:Serial:BaudRate", 115200));
        var resilience = ReadReconnectPolicy(builder);

        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<ITransport>(_ => WrapIfEnabled(new SerialTransport(options), resilience));
        return builder;
    }

    private static UpperHostApplicationBuilder AddConfiguredTcp(UpperHostApplicationBuilder builder)
    {
        var options = new TcpTransportOptions(
            Required(builder, "UpperHost:Transport:Tcp:Host"),
            Port(builder, "UpperHost:Transport:Tcp:Port", 9000));
        var resilience = ReadReconnectPolicy(builder);

        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<ITransport>(_ => WrapIfEnabled(new TcpTransport(options), resilience));
        return builder;
    }

    private static ITransport WrapIfEnabled(ITransport inner, ReconnectConfiguration configuration) =>
        configuration.Enabled
            ? new ReconnectingTransport(inner, configuration.Policy)
            : inner;

    private static ReconnectConfiguration ReadReconnectPolicy(UpperHostApplicationBuilder builder)
    {
        var enabled = Boolean(builder, "UpperHost:Transport:Resilience:Enabled", true);
        var maxAttempts = NonNegativeInt(builder, "UpperHost:Transport:Resilience:MaxAttempts", 5);
        var initialDelayMs = NonNegativeInt(builder, "UpperHost:Transport:Resilience:InitialDelayMs", 250);
        var maxDelayMs = NonNegativeInt(builder, "UpperHost:Transport:Resilience:MaximumDelayMs", 5000);
        var backoff = PositiveDouble(builder, "UpperHost:Transport:Resilience:BackoffFactor", 2.0);
        var reconnectOnEnd = Boolean(builder, "UpperHost:Transport:Resilience:ReconnectOnEndOfStream", true);

        var policy = new ReconnectPolicy(
            maxAttempts,
            TimeSpan.FromMilliseconds(initialDelayMs),
            backoff,
            TimeSpan.FromMilliseconds(maxDelayMs),
            reconnectOnEnd);
        policy.Validate();
        return new ReconnectConfiguration(enabled, policy);
    }

    private static string Required(UpperHostApplicationBuilder builder, string key)
    {
        var value = builder.Configuration[key];
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"Required UpperHost configuration '{key}' is missing.");
        return value;
    }

    private static int PositiveInt(UpperHostApplicationBuilder builder, string key, int fallback)
    {
        var raw = builder.Configuration[key];
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        return int.TryParse(raw, out var value) && value > 0
            ? value
            : throw new InvalidOperationException($"UpperHost configuration '{key}' must be a positive integer.");
    }

    private static int NonNegativeInt(UpperHostApplicationBuilder builder, string key, int fallback)
    {
        var raw = builder.Configuration[key];
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        return int.TryParse(raw, out var value) && value >= 0
            ? value
            : throw new InvalidOperationException($"UpperHost configuration '{key}' must be a non-negative integer.");
    }

    private static double PositiveDouble(UpperHostApplicationBuilder builder, string key, double fallback)
    {
        var raw = builder.Configuration[key];
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        return double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value) && value >= 1
            ? value
            : throw new InvalidOperationException($"UpperHost configuration '{key}' must be a number greater than or equal to 1.");
    }

    private static bool Boolean(UpperHostApplicationBuilder builder, string key, bool fallback)
    {
        var raw = builder.Configuration[key];
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        return bool.TryParse(raw, out var value)
            ? value
            : throw new InvalidOperationException($"UpperHost configuration '{key}' must be true or false.");
    }

    private static int Port(UpperHostApplicationBuilder builder, string key, int fallback)
    {
        var value = PositiveInt(builder, key, fallback);
        return value <= 65535
            ? value
            : throw new InvalidOperationException($"UpperHost configuration '{key}' must be in range 1..65535.");
    }

    private sealed record ReconnectConfiguration(bool Enabled, ReconnectPolicy Policy);
}
