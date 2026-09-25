using Microsoft.Extensions.DependencyInjection;
using UpperHost.Abstractions.Connections;
using UpperHost.Abstractions.Transports;
using UpperHost.Connections;
using UpperHost.Hosting;
using UpperHost.Observability;

namespace UpperHost.Starters;

public static class TransportRegistrationExtensions
{
    public static UpperHostApplicationBuilder AddUpperHostTransport<TTransport>(
        this UpperHostApplicationBuilder builder,
        Func<IServiceProvider, TTransport> factory,
        Func<ITransport, ITransport>? decorate = null,
        ConnectionSharingMode sharingMode = ConnectionSharingMode.Shared)
        where TTransport : class, ITransport
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(factory);

        builder.Services.AddUpperHostConnections();
        builder.Services.AddSingleton(factory);
        builder.Services.AddSingleton<ITransport>(services =>
        {
            var raw = services.GetRequiredService<TTransport>();
            var manager = services.GetRequiredService<IConnectionManager>();

            ITransport transport = new ConnectionManagedTransport(
                manager,
                ConnectionDefinition.FromEndpoint(raw.Endpoint, sharingMode),
                raw);

            if (decorate is not null)
                transport = decorate(transport);

            return new ObservedTransport(transport);
        });

        return builder;
    }
}
