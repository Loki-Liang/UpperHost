using Microsoft.Extensions.DependencyInjection;
using OpenDeviceStudio.Abstractions.Devices;

namespace OpenDeviceStudio.Hosting;

public static class DevicePackageBuilderExtensions
{
    public static OpenDeviceStudioApplicationBuilder AddDevicePackage<TConfiguration>(
        this OpenDeviceStudioApplicationBuilder builder,
        DevicePackageDescriptor descriptor,
        TConfiguration configuration)
        where TConfiguration : class
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(configuration);

        descriptor.ConfigurationSchema.ValidateAndThrow(configuration);
        builder.Services.AddSingleton(descriptor);
        builder.Services.AddSingleton(configuration);
        return builder;
    }

    public static OpenDeviceStudioApplicationBuilder AddDevicePackageDescriptor(
        this OpenDeviceStudioApplicationBuilder builder,
        DevicePackageDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(descriptor);

        builder.Services.AddSingleton(descriptor);
        return builder;
    }
}
