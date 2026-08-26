using Microsoft.Extensions.DependencyInjection;
using UpperHost.Abstractions.Diagnostics;
using UpperHost.Abstractions.Events;
using UpperHost.Abstractions.Transports;
using UpperHost.Hosting;
using UpperHost.Starters;
using UpperHost.Transport.Simulator;

namespace UpperHost.Tests;

public sealed class AutoConfigurationTests
{
    [Fact]
    public async Task Defaults_auto_configure_simulator_and_platform_services()
    {
        var builder = UpperHostApplication.CreateBuilder();
        builder.Configuration["UpperHost:Transport:Type"] = "Simulator";
        builder.AddUpperHostApplication();

        await using var app = builder.Build();
        var transport = app.Services.GetRequiredService<ITransport>();

        Assert.IsType<SimulatorTransport>(transport);
        Assert.NotNull(app.Services.GetRequiredService<IEventBus>());
        Assert.NotNull(app.Services.GetRequiredService<IAlarmService>());
    }

    [Fact]
    public void Invalid_tcp_port_fails_fast_during_configuration()
    {
        var builder = UpperHostApplication.CreateBuilder();
        builder.Configuration["UpperHost:Transport:Type"] = "Tcp";
        builder.Configuration["UpperHost:Transport:Tcp:Host"] = "127.0.0.1";
        builder.Configuration["UpperHost:Transport:Tcp:Port"] = "70000";

        var error = Assert.Throws<InvalidOperationException>(() => builder.AddUpperHostApplication());
        Assert.Contains("1..65535", error.Message);
    }
}
