using Microsoft.Extensions.DependencyInjection;
using UpperHost.Abstractions.Devices;

namespace UpperHost.Hosting;

public static class DevicePackageBuilderExtensions
{
    public static UpperHostApplicationBuilder AddDevicePackage<TConfiguration>(
        this UpperHostApplicationBuilder builder,
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

    public static UpperHostApplicationBuilder AddDevicePackageDescriptor(
        this UpperHostApplicationBuilder builder,
        DevicePackageDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(descriptor);

        builder.Services.AddSingleton(descriptor);
        return builder;
    }
}
