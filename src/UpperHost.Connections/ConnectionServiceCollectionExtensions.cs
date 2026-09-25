using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using UpperHost.Abstractions.Connections;
using UpperHost.Abstractions.Diagnostics;

namespace UpperHost.Connections;

public static class ConnectionServiceCollectionExtensions
{
    public static IServiceCollection AddUpperHostConnections(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IConnectionManager, ConnectionManager>();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IHealthProbe, ConnectionHealthProbe>());
        return services;
    }
}
