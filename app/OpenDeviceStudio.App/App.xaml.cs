using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using OpenDeviceStudio.Hosting;
using OpenDeviceStudio.Starters;

namespace OpenDeviceStudio.App;

public partial class App : Application
{
    private OpenDeviceStudioApplication? _host;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var builder = OpenDeviceStudioApplication.CreateBuilder(e.Args).AddOpenDeviceStudioApplication();
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
}
