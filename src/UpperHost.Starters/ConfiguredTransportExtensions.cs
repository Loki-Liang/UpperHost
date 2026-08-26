using UpperHost.Hosting;
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

            "serial" => builder.AddSerialTransport(new SerialTransportOptions(
                Required(builder, "UpperHost:Transport:Serial:PortName"),
                PositiveInt(builder, "UpperHost:Transport:Serial:BaudRate", 115200))),

            "tcp" => builder.AddTcpTransport(new TcpTransportOptions(
                Required(builder, "UpperHost:Transport:Tcp:Host"),
                Port(builder, "UpperHost:Transport:Tcp:Port", 9000))),

            _ => throw new InvalidOperationException(
                $"Unsupported UpperHost transport type '{transportType}'. Registered baseline types are Simulator, Serial and Tcp. Add a provider starter for custom transports.")
        };
    }

    public static UpperHostApplicationBuilder AddUpperHostApplication(this UpperHostApplicationBuilder builder) =>
        builder.AddUpperHostDefaults().AddConfiguredTransport();

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

    private static int Port(UpperHostApplicationBuilder builder, string key, int fallback)
    {
        var value = PositiveInt(builder, key, fallback);
        return value <= 65535
            ? value
            : throw new InvalidOperationException($"UpperHost configuration '{key}' must be in range 1..65535.");
    }
}
