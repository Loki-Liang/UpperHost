using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using UpperHost.Abstractions.Connections;
using UpperHost.Abstractions.Diagnostics;
using UpperHost.Abstractions.Events;
using UpperHost.Abstractions.Transports;
using UpperHost.Hosting;
using UpperHost.Observability;
using UpperHost.Starters;
using UpperHost.Transport.Simulator;

namespace UpperHost.Tests;

public sealed class AutoConfigurationTests
{
    [Fact]
    public async Task Defaults_auto_configure_simulator_and_platform_services()
    {
        var builder = UpperHostApplication.CreateBuilder();
        builder.AddUpperHostApplication();

        await using var app = builder.Build();
        var transport = app.Services.GetRequiredService<ITransport>();
        var simulator = app.Services.GetRequiredService<SimulatorTransport>();

        Assert.IsType<ObservedTransport>(transport);
        Assert.Equal(simulator.Endpoint, transport.Endpoint);
        Assert.NotNull(app.Services.GetRequiredService<IEventBus>());
        Assert.NotNull(app.Services.GetRequiredService<IAlarmService>());
        Assert.NotNull(app.Services.GetRequiredService<IConnectionManager>());

        var options = app.Services
            .GetRequiredService<IOptions<UpperHostTransportOptions>>()
            .Value;
        Assert.Equal(UpperHostTransportType.Simulator, options.Type);
        Assert.Equal("default", options.Simulator.Name);
        Assert.Equal(5, options.Resilience.MaxAttempts);
        Assert.Equal(250, options.Resilience.InitialDelayMs);
    }

    [Fact]
    public void Invalid_tcp_port_fails_fast_with_canonical_path()
    {
        var builder = UpperHostApplication.CreateBuilder();
        builder.Configuration["UpperHost:Transport:Type"] = "Tcp";
        builder.Configuration["UpperHost:Transport:Tcp:Host"] = "127.0.0.1";
        builder.Configuration["UpperHost:Transport:Tcp:Port"] = "70000";

        var error = Assert.Throws<OptionsValidationException>(() =>
            builder.AddUpperHostApplication());

        Assert.Contains("UpperHost:Transport:Tcp:Port", error.Message);
        Assert.Contains("1..65535", error.Message);
    }

    [Fact]
    public void Missing_selected_serial_port_fails_fast_with_canonical_path()
    {
        var builder = UpperHostApplication.CreateBuilder();
        builder.Configuration["UpperHost:Transport:Type"] = "Serial";

        var error = Assert.Throws<OptionsValidationException>(() =>
            builder.AddUpperHostApplication());

        Assert.Contains("UpperHost:Transport:Serial:PortName", error.Message);
    }

    [Fact]
    public void Invalid_resilience_cross_field_combination_fails_fast()
    {
        var builder = UpperHostApplication.CreateBuilder();
        builder.Configuration["UpperHost:Transport:Resilience:InitialDelayMs"] = "5000";
        builder.Configuration["UpperHost:Transport:Resilience:MaximumDelayMs"] = "1000";

        var error = Assert.Throws<OptionsValidationException>(() =>
            builder.AddUpperHostApplication());

        Assert.Contains("UpperHost:Transport:Resilience:MaximumDelayMs", error.Message);
        Assert.Contains("UpperHost:Transport:Resilience:InitialDelayMs", error.Message);
    }

    [Fact]
    public async Task ValidateOnStart_rechecks_transport_configuration_before_runtime_io()
    {
        var builder = UpperHostApplication.CreateBuilder();
        builder.AddUpperHostApplication();

        builder.Configuration["UpperHost:Transport:Resilience:MaxAttempts"] = "-1";

        await using var app = builder.Build();
        var error = await Assert.ThrowsAsync<OptionsValidationException>(() =>
            app.StartAsync());

        Assert.Contains("UpperHost:Transport:Resilience:MaxAttempts", error.Message);
    }
}
