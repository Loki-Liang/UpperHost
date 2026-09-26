using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace OpenDeviceStudio.Hosting;

public sealed class OpenDeviceStudioApplication : IAsyncDisposable
{
    private readonly IHost _host;

    internal OpenDeviceStudioApplication(IHost host) => _host = host;

    public IServiceProvider Services => _host.Services;

    public static OpenDeviceStudioApplicationBuilder CreateBuilder(string[]? args = null) => new(args ?? []);

    public Task StartAsync(CancellationToken cancellationToken = default) => _host.StartAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken = default) => _host.StopAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync().ConfigureAwait(false);
        _host.Dispose();
    }
}

public sealed class OpenDeviceStudioApplicationBuilder
{
    internal HostApplicationBuilder HostBuilder { get; }

    internal OpenDeviceStudioApplicationBuilder(string[] args) => HostBuilder = Host.CreateApplicationBuilder(args);

    public IServiceCollection Services => HostBuilder.Services;
    public IConfigurationManager Configuration => HostBuilder.Configuration;

    public OpenDeviceStudioApplication Build() => new(HostBuilder.Build());
}
