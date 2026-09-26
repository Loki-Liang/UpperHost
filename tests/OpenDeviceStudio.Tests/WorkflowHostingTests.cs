using Microsoft.Extensions.DependencyInjection;
using OpenDeviceStudio.Abstractions.Workflows;
using OpenDeviceStudio.Control.Scheduling;
using OpenDeviceStudio.Hosting;
using OpenDeviceStudio.Starters;
using OpenDeviceStudio.Workflows;

namespace OpenDeviceStudio.Tests;

public sealed class WorkflowHostingTests
{
    [Fact]
    public async Task Starter_registers_workflow_coordinator_on_shared_control_arbiter()
    {
        var builder = OpenDeviceStudioApplication
            .CreateBuilder()
            .AddOpenDeviceStudio()
            .AddWorkflowExecutionRuntime(maxSharedReadersPerResource: 2);

        await using var app = builder.Build();
        var coordinator =
            app.Services.GetRequiredService<WorkflowExecutionCoordinator>();
        var arbiter =
            app.Services.GetRequiredService<ICommandResourceArbiter>();

        Assert.NotNull(coordinator);
        Assert.NotNull(arbiter);

        await app.StartAsync();

        var result = await coordinator.Start(
            new WorkflowExecutionRequest(
                new WorkflowExecutionDefinition(
                    "hosting",
                    "1",
                    new WorkflowActionNode(
                        "action",
                        static (_, _) =>
                            Task.FromResult(WorkflowStepResult.Success()))),
                WorkflowRecipeSnapshot.Create(
                    "recipe",
                    "1",
                    """{"value":1}"""),
                "station-hosting"))
            .Completion;

        Assert.Equal(WorkflowExecutionStatus.Completed, result.Status);

        await app.StopAsync();
    }

    [Fact]
    public void Starter_rejects_conflicting_shared_arbiter_configuration()
    {
        var builder = OpenDeviceStudioApplication
            .CreateBuilder()
            .AddOpenDeviceStudio()
            .AddWorkflowExecutionRuntime(maxSharedReadersPerResource: 2);

        Assert.Throws<InvalidOperationException>(() =>
            builder.AddControlResourceArbitration(
                maxSharedReadersPerResource: 4));
    }
}
