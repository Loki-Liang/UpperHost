using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using UpperHost.Hosting;
using UpperHost.Starters;

namespace UpperHost.App;

public partial class App : Application
{
    private UpperHostApplication? _host;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var builder = UpperHostApplication.CreateBuilder(e.Args).AddUpperHostApplication();
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
