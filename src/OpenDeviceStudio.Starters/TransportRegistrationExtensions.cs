using Microsoft.Extensions.DependencyInjection;
using OpenDeviceStudio.Abstractions.Connections;
using OpenDeviceStudio.Abstractions.Transports;
using OpenDeviceStudio.Connections;
using OpenDeviceStudio.Hosting;
using OpenDeviceStudio.Observability;

namespace OpenDeviceStudio.Starters;

public static class TransportRegistrationExtensions
{
    public static OpenDeviceStudioApplicationBuilder AddOpenDeviceStudioTransport<TTransport>(
        this OpenDeviceStudioApplicationBuilder builder,
        Func<IServiceProvider, TTransport> factory,
        Func<ITransport, ITransport>? decorate = null,
        ConnectionSharingMode sharingMode = ConnectionSharingMode.Shared)
        where TTransport : class, ITransport
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(factory);

        builder.Services.AddOpenDeviceStudioConnections();
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
