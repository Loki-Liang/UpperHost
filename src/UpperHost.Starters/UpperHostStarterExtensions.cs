using Microsoft.Extensions.DependencyInjection;
using UpperHost.Abstractions.Transports;
using UpperHost.Diagnostics;
using UpperHost.Hosting;
using UpperHost.Transport.Serial;
using UpperHost.Transport.Simulator;
using UpperHost.Transport.Tcp;
using UpperHost.Workflows;

namespace UpperHost.Starters;

public static class UpperHostStarterExtensions
{
    public static UpperHostApplicationBuilder AddUpperHostDefaults(this UpperHostApplicationBuilder builder)
    {
        builder.AddUpperHost();
        builder.Services.AddSingleton<WorkflowRunner>();
        builder.Services.AddSingleton<HealthService>();
        return builder;
    }

    public static UpperHostApplicationBuilder AddSimulatorTransport(this UpperHostApplicationBuilder builder, string name = "default")
    {
        builder.Services.AddSingleton<SimulatorTransport>(_ => new SimulatorTransport(name));
        builder.Services.AddSingleton<ITransport>(sp => sp.GetRequiredService<SimulatorTransport>());
        return builder;
    }

    public static UpperHostApplicationBuilder AddSerialTransport(this UpperHostApplicationBuilder builder, SerialTransportOptions options)
    {
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<ITransport, SerialTransport>();
        return builder;
    }

    public static UpperHostApplicationBuilder AddTcpTransport(this UpperHostApplicationBuilder builder, TcpTransportOptions options)
    {
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<ITransport, TcpTransport>();
        return builder;
    }
}
