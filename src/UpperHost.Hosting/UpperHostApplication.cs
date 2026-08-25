using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace UpperHost.Hosting;

public sealed class UpperHostApplication : IAsyncDisposable
{
    private readonly IHost _host;

    internal UpperHostApplication(IHost host) => _host = host;

    public IServiceProvider Services => _host.Services;

    public static UpperHostApplicationBuilder CreateBuilder(string[]? args = null) => new(args ?? []);

    public Task StartAsync(CancellationToken cancellationToken = default) => _host.StartAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken = default) => _host.StopAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync().ConfigureAwait(false);
        _host.Dispose();
    }
}

public sealed class UpperHostApplicationBuilder
{
    internal HostApplicationBuilder HostBuilder { get; }

    internal UpperHostApplicationBuilder(string[] args) => HostBuilder = Host.CreateApplicationBuilder(args);

    public IServiceCollection Services => HostBuilder.Services;
    public IConfigurationManager Configuration => HostBuilder.Configuration;

    public UpperHostApplication Build() => new(HostBuilder.Build());
}
