using System.Windows;
using OpenDeviceStudio.Hosting;

namespace OpenDeviceStudio.Presentation.Wpf;

public abstract class OpenDeviceStudioWpfApplication : Application
{
    private OpenDeviceStudioApplication? _upperHost;

    protected abstract void ConfigureOpenDeviceStudio(OpenDeviceStudioApplicationBuilder builder);
    protected abstract Window CreateMainWindow(IServiceProvider services);

    protected sealed override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var builder = OpenDeviceStudioApplication.CreateBuilder(e.Args).AddOpenDeviceStudio();
        ConfigureOpenDeviceStudio(builder);

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
