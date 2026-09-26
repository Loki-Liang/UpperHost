using Microsoft.Extensions.DependencyInjection;
using OpenDeviceStudio.Abstractions.Devices;
using OpenDeviceStudio.Control.Commands;
using OpenDeviceStudio.Control.Interlocks;
using OpenDeviceStudio.Hosting;
using OpenDeviceStudio.Starters;

namespace OpenDeviceStudio.Sample.AutomationStation;

internal static class Program
{
    public static async Task Main(string[] args)
    {
        var builder = OpenDeviceStudioApplication
            .CreateBuilder(args)
            .AddOpenDeviceStudioDefaults()
            .AddWorkflowExecutionRuntime()
            .AddCommandDispatcher<AxisCommand, AxisResult>()
            .AddCommandDispatcher<CaptureCommand, CaptureResult>();

        builder.Services.AddSingleton<SafetyDoorDevice>();
        builder.Services.AddSingleton<AxisDevice>();
        builder.Services.AddSingleton<CameraDevice>();

        builder.Services.AddSingleton<IDevice>(
            sp => sp.GetRequiredService<SafetyDoorDevice>());
        builder.Services.AddSingleton<IDevice>(
            sp => sp.GetRequiredService<AxisDevice>());
        builder.Services.AddSingleton<IDevice>(
            sp => sp.GetRequiredService<CameraDevice>());

        builder.Services.AddSingleton<ICommandable<AxisCommand, AxisResult>>(
            sp => sp.GetRequiredService<AxisDevice>());
        builder.Services.AddSingleton<ICommandable<CaptureCommand, CaptureResult>>(
            sp => sp.GetRequiredService<CameraDevice>());

        builder.Services.AddSingleton<IInterlock<AxisCommand>, DoorClosedInterlock>();
        builder.Services.AddSingleton<ICommandGuard<AxisCommand>>(sp =>
            new InterlockCommandGuard<AxisCommand>(
                sp.GetServices<IInterlock<AxisCommand>>()));

        builder.Services.AddSingleton<AutomationStation>();

        await using var app = builder.Build();
        await app.StartAsync();

        var registry = app.Services.GetRequiredService<IDeviceRegistry>();
        Console.WriteLine(
            $"Registered devices: {string.Join(", ", registry.Devices.Select(device => device.Descriptor.DisplayName))}");

        var station = app.Services.GetRequiredService<AutomationStation>();
        var door = app.Services.GetRequiredService<SafetyDoorDevice>();

        Console.WriteLine("\nCycle 1: door closed; expected success.");
        await PrintCycleAsync(station);

        await station.ResetAsync();
        door.Open();

        Console.WriteLine(
            "\nCycle 2: door open; axis command must be rejected by the shared Control dispatcher/interlock path.");
        await PrintCycleAsync(station);
    }

    private static async Task PrintCycleAsync(AutomationStation station)
    {
        var result = await station.RunCycleAsync();

        foreach (var node in result.Nodes)
        {
            Console.WriteLine(
                $"  {node.NodeId}: {node.Status} attempts={node.Attempts} - {node.Message}");
        }

        Console.WriteLine(
            $"Workflow={result.Status}, StationState={station.State}, RecipeHash={result.RecipeSnapshot.Hash[..12]}");
    }
}
