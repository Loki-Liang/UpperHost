using System.Windows;
using UpperHost.Hosting;

namespace UpperHost.Presentation.Wpf;

public abstract class UpperHostWpfApplication : Application
{
    private UpperHostApplication? _upperHost;

    protected abstract void ConfigureUpperHost(UpperHostApplicationBuilder builder);
    protected abstract Window CreateMainWindow(IServiceProvider services);

    protected sealed override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var builder = UpperHostApplication.CreateBuilder(e.Args).AddUpperHost();
        ConfigureUpperHost(builder);

        _upperHost = builder.Build();
        await _upperHost.StartAsync().ConfigureAwait(true);

        MainWindow = CreateMainWindow(_upperHost.Services);
        MainWindow.Show();
    }

    protected sealed override async void OnExit(ExitEventArgs e)
    {
        if (_upperHost is not null)
        {
            await _upperHost.StopAsync().ConfigureAwait(true);
            await _upperHost.DisposeAsync().ConfigureAwait(true);
        }

        base.OnExit(e);
    }
}
