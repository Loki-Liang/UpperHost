using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using UpperHost.Abstractions.Transports;
using UpperHost.Hosting;
using UpperHost.Starters;
using UpperHost.Transport.Serial;
using UpperHost.Transport.Tcp;

namespace UpperHost.App;

public partial class App : Application
{
    private UpperHostApplication? _host;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var builder = UpperHostApplication.CreateBuilder(e.Args).AddUpperHostDefaults();
        ConfigureTransport(builder);
        builder.Services.AddSingleton<MainWindow>();

        _host = builder.Build();
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

    private static void ConfigureTransport(UpperHostApplicationBuilder builder)
    {
        const string selected = "__TRANSPORT__";
        switch (selected)
        {
            case "serial":
                builder.AddSerialTransport(new SerialTransportOptions(
                    builder.Configuration["UpperHost:Transport:Serial:PortName"] ?? "COM1",
                    builder.Configuration.GetValue("UpperHost:Transport:Serial:BaudRate", 115200)));
                break;
            case "tcp":
                builder.AddTcpTransport(new TcpTransportOptions(
                    builder.Configuration["UpperHost:Transport:Tcp:Host"] ?? "127.0.0.1",
                    builder.Configuration.GetValue("UpperHost:Transport:Tcp:Port", 9000)));
                break;
            default:
                builder.AddSimulatorTransport();
                break;
        }
    }
}
