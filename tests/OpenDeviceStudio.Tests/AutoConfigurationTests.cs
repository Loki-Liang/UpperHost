using Microsoft.Extensions.DependencyInjection;
using OpenDeviceStudio.Abstractions.Connections;
using OpenDeviceStudio.Abstractions.Diagnostics;
using OpenDeviceStudio.Abstractions.Events;
using OpenDeviceStudio.Abstractions.Transports;
using OpenDeviceStudio.Hosting;
using OpenDeviceStudio.Observability;
using OpenDeviceStudio.Starters;
using OpenDeviceStudio.Transport.Simulator;

namespace OpenDeviceStudio.Tests;

public sealed class AutoConfigurationTests
{
    [Fact]
    public async Task Defaults_auto_configure_simulator_and_platform_services()
    {
        var builder = OpenDeviceStudioApplication.CreateBuilder();
        builder.Configuration["OpenDeviceStudio:Transport:Type"] = "Simulator";
        builder.AddOpenDeviceStudioApplication();

        await using var app = builder.Build();
        var transport = app.Services.GetRequiredService<ITransport>();
        var simulator = app.Services.GetRequiredService<SimulatorTransport>();

        Assert.IsType<ObservedTransport>(transport);
        Assert.Equal(simulator.Endpoint, transport.Endpoint);
        Assert.NotNull(app.Services.GetRequiredService<IEventBus>());
        Assert.NotNull(app.Services.GetRequiredService<IAlarmService>());
        Assert.NotNull(app.Services.GetRequiredService<IConnectionManager>());
    }

    [Fact]
    public void Invalid_tcp_port_fails_fast_during_configuration()
    {
        var builder = OpenDeviceStudioApplication.CreateBuilder();
        builder.Configuration["OpenDeviceStudio:Transport:Type"] = "Tcp";
        builder.Configuration["OpenDeviceStudio:Transport:Tcp:Host"] = "127.0.0.1";
        builder.Configuration["OpenDeviceStudio:Transport:Tcp:Port"] = "70000";

        var error = Assert.Throws<InvalidOperationException>(() => builder.AddOpenDeviceStudioApplication());
        Assert.Contains("1..65535", error.Message);
    }
}
