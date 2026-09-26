using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using UpperHost.Abstractions.Devices;
using UpperHost.Abstractions.Protocols;
using UpperHost.Abstractions.Transports;
using UpperHost.Control.Parameters;
using UpperHost.Control.Scheduling;
using UpperHost.Control.State;
using UpperHost.Hosting;
using UpperHost.Protocols;
using UpperHost.Starters;
using UpperHost.Sample.DeviceControl.Device;
using UpperHost.Sample.DeviceControl.Domain;
using UpperHost.Sample.DeviceControl.Protocol;
using UpperHost.Sample.DeviceControl.Simulator;
using UpperHost.Transport.Simulator;

namespace UpperHost.Sample.DeviceControl;

public partial class App : Application
{
    private UpperHostApplication? _host;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var builder = UpperHostApplication.CreateBuilder(e.Args).AddUpperHost();
        builder.AddDevicePackage(
            TemperatureControllerPackage.Descriptor,
            new TemperatureControllerPackageOptions());

        builder.Services.AddSingleton(_ => new SimulatorTransport("temperature-controller"));
        builder.Services.AddSingleton<ITransport>(sp => sp.GetRequiredService<SimulatorTransport>());

        builder.Services.AddSingleton<TemperatureControllerProtocol>();
        builder.Services.AddSingleton<ICommandEncoder<TemperatureControllerCommand>>(sp =>
            sp.GetRequiredService<TemperatureControllerProtocol>());
        builder.Services.AddSingleton<IMessageDecoder<TemperatureControllerResponse>>(sp =>
            sp.GetRequiredService<TemperatureControllerProtocol>());
        builder.Services.AddSingleton<RequestResponseClient<TemperatureControllerCommand, TemperatureControllerResponse>>();

        builder.Services.AddSingleton<TemperatureControllerDevice>();
        builder.Services.AddSingleton<IDevice>(sp => sp.GetRequiredService<TemperatureControllerDevice>());
        builder.Services.AddSingleton<ICommandable<TemperatureControllerCommand, TemperatureControllerResponse>>(
            sp => sp.GetRequiredService<TemperatureControllerDevice>());
        builder.AddCommandDispatcher<TemperatureControllerCommand, TemperatureControllerResponse>(
            new BoundedCommandDispatcherOptions(
                Capacity: 64,
                PerPriorityCapacity: 32,
                MaxConcurrency: 4,
                MaxSharedReadersPerResource: 4));

        builder.AddDeviceState<TemperatureControllerResponse>();
        builder.AddDeviceState<double>();
        builder.AddDevicePolling<TemperatureControllerResponse>(
            sp =>
            {
                var dispatcher = sp.GetRequiredService<
                    BoundedCommandDispatcher<TemperatureControllerCommand, TemperatureControllerResponse>>();
                var operation = DevicePollOperations.FromCommandDispatcher<
                    TemperatureControllerCommand,
                    TemperatureControllerResponse,
                    TemperatureControllerResponse>(
                    dispatcher,
                    _ => new ReadStatusCommand(),
                    result => new DevicePollSample<TemperatureControllerResponse>(result.Value!),
                    [
                        new CommandResourceClaim(
                            new CommandResourceKey("device", TemperatureControllerDevice.DeviceId),
                            CommandResourceAccess.SharedRead)
                    ],
                    queueTimeout: TimeSpan.FromSeconds(1));

                return
                [
                    new DevicePollGroup<TemperatureControllerResponse>(
                        "temperature-status",
                        TemperatureControllerDevice.DeviceId,
                        new DeviceStatePartitionKey("status"),
                        Interval: TimeSpan.FromSeconds(1),
                        Timeout: TimeSpan.FromSeconds(2),
                        StaleAfter: TimeSpan.FromSeconds(3),
                        Operation: operation)
                ];
            },
            new DevicePollRuntimeOptions(WorkCapacity: 8, MaxConcurrency: 1));

        builder.Services.AddSingleton(sp =>
            new TypedParameterRuntime<double>(
                TemperatureControllerDevice.DeviceId,
                sp.GetRequiredService<TemperatureControllerDevice>(),
                sp.GetRequiredService<ICommandResourceArbiter>(),
                sp.GetRequiredService<DeviceSnapshotStore<double>>(),
                sp.GetRequiredService<IDeviceConnectionEpochSource>(),
                sp.GetService<TimeProvider>() ?? TimeProvider.System));

        builder.Services.AddSingleton<TemperatureControllerSimulator>();
        builder.Services.AddSingleton<MainWindow>();

        _host = builder.Build();

        // Resolve the simulated hardware before the first command so it can answer transport requests.
        _ = _host.Services.GetRequiredService<TemperatureControllerSimulator>();
        await _host.StartAsync();

        MainWindow = _host.Services.GetRequiredService<MainWindow>();
        MainWindow.Show();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
            await _host.DisposeAsync();
        base.OnExit(e);
    }
}
