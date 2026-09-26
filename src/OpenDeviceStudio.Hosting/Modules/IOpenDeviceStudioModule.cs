using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace OpenDeviceStudio.Hosting.Modules;

public interface IOpenDeviceStudioModule
{
    string Name { get; }
    void ConfigureServices(IServiceCollection services, IConfiguration configuration);
}

public static class OpenDeviceStudioModuleExtensions
{
    public static OpenDeviceStudioApplicationBuilder AddModule<TModule>(this OpenDeviceStudioApplicationBuilder builder)
        where TModule : IOpenDeviceStudioModule, new()
    {
        var module = new TModule();
        module.ConfigureServices(builder.Services, builder.Configuration);
        return builder;
    }

    public static OpenDeviceStudioApplicationBuilder AddModule(this OpenDeviceStudioApplicationBuilder builder, IOpenDeviceStudioModule module)
    {
        ArgumentNullException.ThrowIfNull(module);
        module.ConfigureServices(builder.Services, builder.Configuration);
        return builder;
    }
}
