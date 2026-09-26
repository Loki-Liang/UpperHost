using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using OpenDeviceStudio.Abstractions.Devices;
using OpenDeviceStudio.Hosting.Devices;

namespace OpenDeviceStudio.Hosting;

public static class OpenDeviceStudioBuilderExtensions
{
    public static OpenDeviceStudioApplicationBuilder AddOpenDeviceStudio(this OpenDeviceStudioApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.TryAddSingleton<IDevicePackageCatalog, DevicePackageCatalog>();
        builder.Services.TryAddSingleton<IDeviceRegistry, DeviceRegistry>();
        builder.Services.TryAddSingleton<IDeviceDiscoveryService, DeviceDiscoveryService>();
        builder.Services.TryAddSingleton<IDeviceManager, DeviceManager>();
        builder.Services.AddHostedService<DevicePackageRegistrationHostedService>();
        builder.Services.AddHostedService<DeviceRegistrationHostedService>();
        builder.Services.AddLogging(logging => logging.AddConsole());
        return builder;
    }
}
