using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace UpperHost.Hosting.Modules;

public interface IUpperHostModule
{
    string Name { get; }
    void ConfigureServices(IServiceCollection services, IConfiguration configuration);
}

public static class UpperHostModuleExtensions
{
    public static UpperHostApplicationBuilder AddModule<TModule>(this UpperHostApplicationBuilder builder)
        where TModule : IUpperHostModule, new()
    {
        var module = new TModule();
        module.ConfigureServices(builder.Services, builder.Configuration);
        return builder;
    }

    public static UpperHostApplicationBuilder AddModule(this UpperHostApplicationBuilder builder, IUpperHostModule module)
    {
        ArgumentNullException.ThrowIfNull(module);
        module.ConfigureServices(builder.Services, builder.Configuration);
        return builder;
    }
}
