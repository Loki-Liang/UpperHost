using Microsoft.Extensions.DependencyInjection;
using UpperHost.Abstractions.Devices;
using UpperHost.Control.Commands;
using UpperHost.Control.Interlocks;
using UpperHost.Hosting;
using UpperHost.Starters;
using UpperHost.Workflows;

namespace UpperHost.Sample.AutomationStation;

internal static class Program
{
    public static async Task Main(string[] args)
    {
        var builder = UpperHostApplication.CreateBuilder(args).AddUpperHostDefaults();

        builder.Services.AddSingleton<SafetyDoorDevice>();
        builder.Services.AddSingleton<AxisDevice>();
        builder.Services.AddSingleton<CameraDevice>();
        builder.Services.AddSingleton<IDevice>(sp => sp.GetRequiredService<SafetyDoorDevice>());
        builder.Services.AddSingleton<IDevice>(sp => sp.GetRequiredService<AxisDevice>());
        builder.Services.AddSingleton<IDevice>(sp => sp.GetRequiredService<CameraDevice>());

        builder.Services.AddSingleton<ICommandable<AxisCommand, AxisResult>>(sp =>
            sp.GetRequiredService<AxisDevice>());
        builder.Services.AddSingleton<ICommandable<CaptureCommand, CaptureResult>>(sp =>
            sp.GetRequiredService<CameraDevice>());
        builder.Services.AddSingleton<IInterlock<AxisCommand>, DoorClosedInterlock>();
        builder.Services.AddSingleton<ICommandGuard<AxisCommand>>(sp =>
            new InterlockCommandGuard<AxisCommand>(
                sp.GetServices<IInterlock<AxisCommand>>()));

        builder.AddCommandDispatcher<AxisCommand, AxisResult>();
        builder.AddCommandDispatcher<CaptureCommand, CaptureResult>();
        builder.Services.AddSingleton<AutomationStation>();

        await using var app = builder.Build();
        await app.StartAsync();

        var registry = app.Services.GetRequiredService<IDeviceRegistry>();
        Console.WriteLine(
            $"Registered devices: {string.Join(", ", registry.Devices.Select(device => device.Descriptor.DisplayName))}");

        var station = app.Services.GetRequiredService<AutomationStation>();
        var door = app.Services.GetRequiredService<SafetyDoorDevice>();
        var recipe = new InspectionRecipe(
            Version: "2026.09",
            InspectionPosition: 100,
            MinimumQuality: 0.95);

        Console.WriteLine("\nCycle 1: door closed; expected success through authoritative Automation runtime.");
        await PrintCycleAsync(station, recipe);

        var reset = station.Reset();
        Console.WriteLine($"Reset accepted={reset.Accepted}, station={station.State}");
        door.Open();

        Console.WriteLine("\nCycle 2: door open; axis command must be rejected by #60 interlock/dispatcher.");
        await PrintCycleAsync(station, recipe);

        await app.StopAsync();
    }

    private static async Task PrintCycleAsync(
        AutomationStation station,
        InspectionRecipe recipe)
    {
        var result = await station.RunCycleAsync(recipe);
        foreach (var step in result.Steps)
        {
            Console.WriteLine(
                $"  #{step.Sequence} {step.NodeId}: {step.Status} attempts={step.Attempts} - {step.Message}");
        }

        Console.WriteLine(
            $"Execution={result.ExecutionId}, Outcome={result.Outcome}, State={result.State}, Station={station.State}, RecipeHash={result.RecipeHash}");
    }
}
