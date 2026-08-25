using System.Reflection;
using System.Runtime.Loader;
using UpperHost.Hosting;
using UpperHost.Hosting.Modules;

namespace UpperHost.Modules;

public static class PluginLoader
{
    public static IReadOnlyList<IUpperHostModule> LoadModules(string directory)
    {
        if (!Directory.Exists(directory))
            return [];

        var modules = new List<IUpperHostModule>();
        foreach (var path in Directory.EnumerateFiles(directory, "*.dll", SearchOption.TopDirectoryOnly))
        {
            var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(path));
            foreach (var type in GetLoadableTypes(assembly))
            {
                if (type is { IsAbstract: false, IsInterface: false } &&
                    typeof(IUpperHostModule).IsAssignableFrom(type) &&
                    Activator.CreateInstance(type) is IUpperHostModule module)
                {
                    modules.Add(module);
                }
            }
        }

        return modules;
    }

    public static UpperHostApplicationBuilder AddModulesFromDirectory(this UpperHostApplicationBuilder builder, string directory)
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
