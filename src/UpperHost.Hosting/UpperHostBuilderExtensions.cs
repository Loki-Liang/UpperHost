using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using UpperHost.Abstractions.Devices;
using UpperHost.Hosting.Devices;

namespace UpperHost.Hosting;

public static class UpperHostBuilderExtensions
{
    public static UpperHostApplicationBuilder AddUpperHost(this UpperHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddSingleton<IDeviceRegistry, DeviceRegistry>();
        builder.Services.AddLogging(logging => logging.AddConsole());
        return builder;
    }
}
