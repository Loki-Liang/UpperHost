using Microsoft.Extensions.DependencyInjection;
using UpperHost.Abstractions.Connections;
using UpperHost.Abstractions.Diagnostics;
using UpperHost.Abstractions.Events;
using UpperHost.Abstractions.Transports;
using UpperHost.Hosting;
using UpperHost.Observability;
using UpperHost.Starters;
using UpperHost.Transport.Simulator;
using UpperHost.Workflows;

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
        var simulator = app.Services.GetRequiredService<SimulatorTransport>();

        Assert.IsType<ObservedTransport>(transport);
        Assert.Equal(simulator.Endpoint, transport.Endpoint);
        Assert.NotNull(app.Services.GetRequiredService<IEventBus>());
        Assert.NotNull(app.Services.GetRequiredService<IAlarmService>());
        Assert.NotNull(app.Services.GetRequiredService<IConnectionManager>());
        Assert.NotNull(app.Services.GetRequiredService<AutomationExecutionCoordinator>());
        Assert.NotNull(app.Services.GetRequiredService<IAutomationExecutionJournal>());
    }


    [Fact]
    public async Task Host_stop_converges_active_automation_before_journal_shutdown()
    {
        var builder = UpperHostApplication.CreateBuilder();
        builder.Configuration["UpperHost:Transport:Type"] = "Simulator";
        builder.AddUpperHostApplication();

        await using var app = builder.Build();
        await app.StartAsync();

        var entered = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var plan = AutomationExecutionPlan.Compile(
            new AutomationWorkflowDefinition(
                "shutdown-smoke",
                "1",
                new AutomationActionNode(
                    "wait",
                    new AutomationStepDescriptor(
                        async (_, cancellationToken) =>
                        {
                            entered.TrySetResult(true);
                            await Task.Delay(
                                Timeout.InfiniteTimeSpan,
                                cancellationToken);
                            return AutomationStepResult.Success();
                        }))));

        var coordinator = app.Services.GetRequiredService<AutomationExecutionCoordinator>();
        var start = coordinator.TryStart(
            plan,
            AutomationRecipeSnapshot.Create("shutdown", "1", new { Value = 1 }));

        Assert.True(start.Accepted);
        await entered.Task;

        await app.StopAsync();
        var result = await start.Execution!.Completion;

        Assert.Equal(AutomationExecutionState.Aborted, result.State);
        Assert.Equal(AutomationStationState.Faulted, result.StationState);
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
