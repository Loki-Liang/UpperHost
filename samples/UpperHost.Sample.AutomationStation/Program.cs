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

        builder.Services.AddSingleton<ICommandable<AxisCommand, AxisResult>>(sp => sp.GetRequiredService<AxisDevice>());
        builder.Services.AddSingleton<IInterlock<AxisCommand>, DoorClosedInterlock>();
        builder.Services.AddSingleton<ICommandGuard<AxisCommand>>(sp =>
            new InterlockCommandGuard<AxisCommand>(sp.GetServices<IInterlock<AxisCommand>>()));
        builder.Services.AddSingleton<CommandRuntime<AxisCommand, AxisResult>>();
        builder.Services.AddSingleton<AutomationStation>();

        await using var app = builder.Build();
        await app.StartAsync();

        var registry = app.Services.GetRequiredService<IDeviceRegistry>();
        Console.WriteLine($"Registered devices: {string.Join(", ", registry.Devices.Select(device => device.Descriptor.DisplayName))}");

        var station = app.Services.GetRequiredService<AutomationStation>();
        var door = app.Services.GetRequiredService<SafetyDoorDevice>();

        Console.WriteLine("\nCycle 1: door closed; expected success.");
        await PrintCycleAsync(station);

        await station.ResetAsync();
        door.Open();

        Console.WriteLine("\nCycle 2: door open; axis command must be rejected by software interlock.");
        await PrintCycleAsync(station);
    }

    private static async Task PrintCycleAsync(AutomationStation station)
    {
        var result = await station.RunCycleAsync();
        foreach (var step in result.Steps)
            Console.WriteLine($"  {step.Step}: {step.Result.Status} - {step.Result.Message}");
        Console.WriteLine($"Workflow={result.Status}, StationState={station.State}");
    }
}
