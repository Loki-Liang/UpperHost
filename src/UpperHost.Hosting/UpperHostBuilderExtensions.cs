using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using UpperHost.Abstractions.Devices;
using UpperHost.Hosting.Devices;

namespace UpperHost.Hosting;

public static class UpperHostBuilderExtensions
{
    public static UpperHostApplicationBuilder AddUpperHost(this UpperHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.TryAddSingleton<IDeviceRegistry, DeviceRegistry>();
        builder.Services.TryAddSingleton<IDeviceDiscoveryService, DeviceDiscoveryService>();
        builder.Services.TryAddSingleton<IDeviceManager, DeviceManager>();
        builder.Services.AddHostedService<DeviceRegistrationHostedService>();
        builder.Services.AddLogging(logging => logging.AddConsole());
        return builder;
    }
}
