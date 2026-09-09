using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using UpperHost.Abstractions.Devices;
using UpperHost.Abstractions.Protocols;
using UpperHost.Abstractions.Transports;
using UpperHost.Hosting;
using UpperHost.Protocols;
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
