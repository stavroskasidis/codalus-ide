using System.Reflection;
using System.Runtime.Loader;

namespace Codalus.Plugins.Host;

/// <summary>
/// One collectible load context per plugin. The plugin's own dependencies resolve from its folder (via its
/// .deps.json); the plugin API assembly always comes from the host so that plugin and host agree on its types.
/// </summary>
internal sealed class PluginLoadContext(string mainAssemblyPath) : AssemblyLoadContext(Path.GetFileNameWithoutExtension(mainAssemblyPath), isCollectible: true)
{
    private static readonly HashSet<string> s_shared = new(StringComparer.OrdinalIgnoreCase)
    {
        typeof(ICodalusPlugin).Assembly.GetName().Name!,
    };

    private readonly AssemblyDependencyResolver _resolver = new(mainAssemblyPath);

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (assemblyName.Name is { } name && s_shared.Contains(name))
        {
            return null; // fall back to the default context (the host's copy)
        }

        return _resolver.ResolveAssemblyToPath(assemblyName) is { } path ? LoadFromAssemblyPath(path) : null;
    }

    protected override nint LoadUnmanagedDll(string unmanagedDllName) =>
        _resolver.ResolveUnmanagedDllToPath(unmanagedDllName) is { } path ? LoadUnmanagedDllFromPath(path) : 0;
}
