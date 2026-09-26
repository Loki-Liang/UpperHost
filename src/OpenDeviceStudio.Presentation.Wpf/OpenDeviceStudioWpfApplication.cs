using System.Windows;
using OpenDeviceStudio.Hosting;

namespace OpenDeviceStudio.Presentation.Wpf;

public abstract class OpenDeviceStudioWpfApplication : Application
{
    private OpenDeviceStudioApplication? _host;

    protected abstract void ConfigureOpenDeviceStudio(OpenDeviceStudioApplicationBuilder builder);
    protected abstract Window CreateMainWindow(IServiceProvider services);

    protected sealed override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var builder = OpenDeviceStudioApplication.CreateBuilder(e.Args).AddOpenDeviceStudio();
        ConfigureOpenDeviceStudio(builder);

        _host = builder.Build();
        await _host.StartAsync().ConfigureAwait(true);

        MainWindow = CreateMainWindow(_host.Services);
        MainWindow.Show();
    }

    protected sealed override async void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            await _host.StopAsync().ConfigureAwait(true);
            await _host.DisposeAsync().ConfigureAwait(true);
        }

        base.OnExit(e);
    }
}
