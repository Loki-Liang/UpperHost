using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenDeviceStudio.Abstractions.Connections;
using OpenDeviceStudio.Abstractions.Diagnostics;

namespace OpenDeviceStudio.Connections;

public static class ConnectionServiceCollectionExtensions
{
    public static IServiceCollection AddOpenDeviceStudioConnections(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IConnectionManager, ConnectionManager>();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IHealthProbe, ConnectionHealthProbe>());
        return services;
    }
}
