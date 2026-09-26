using System.Reflection;
using System.Runtime.Loader;
using OpenDeviceStudio.Hosting;
using OpenDeviceStudio.Hosting.Modules;

namespace OpenDeviceStudio.Modules;

public static class PluginLoader
{
    public static IReadOnlyList<IOpenDeviceStudioModule> LoadModules(string directory)
    {
        if (!Directory.Exists(directory))
            return [];

        var modules = new List<IOpenDeviceStudioModule>();
        foreach (var path in Directory.EnumerateFiles(directory, "*.dll", SearchOption.TopDirectoryOnly))
        {
            var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(path));
            foreach (var type in GetLoadableTypes(assembly))
            {
                if (type is { IsAbstract: false, IsInterface: false } &&
                    typeof(IOpenDeviceStudioModule).IsAssignableFrom(type) &&
                    Activator.CreateInstance(type) is IOpenDeviceStudioModule module)
                {
                    modules.Add(module);
                }
            }
        }

        return modules;
    }

    public static OpenDeviceStudioApplicationBuilder AddModulesFromDirectory(this OpenDeviceStudioApplicationBuilder builder, string directory)
    {
        foreach (var module in LoadModules(directory))
            builder.AddModule(module);
        return builder;
    }

    private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.OfType<Type>();
        }
    }
}
