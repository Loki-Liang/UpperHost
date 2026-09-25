using Microsoft.Extensions.DependencyInjection;
using UpperHost.Abstractions.Transports;
using UpperHost.Hosting;
using UpperHost.Observability;

namespace UpperHost.Starters;

public static class TransportRegistrationExtensions
{
    public static UpperHostApplicationBuilder AddUpperHostTransport<TTransport>(
        this UpperHostApplicationBuilder builder,
        Func<IServiceProvider, TTransport> factory)
        where TTransport : class, ITransport
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(factory);

        builder.Services.AddSingleton(factory);
        builder.Services.AddSingleton<ITransport>(services =>
            new ObservedTransport(services.GetRequiredService<TTransport>()));
        return builder;
    }
}
