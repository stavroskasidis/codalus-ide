using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;

namespace Daedalus.LanguageServices.Host;

/// <summary>
/// Loads analyzer and source generator assemblies, one <see cref="AssemblyLoadContext"/> per directory (a package's
/// analyzer folder, or a project's output), so different versions of the same dependency can coexist. Assemblies the
/// host already has (Roslyn, the runtime) come from the default context. Files are loaded from memory, so analyzers
/// built by projects in the solution stay writable for the next build.
/// </summary>
internal sealed class AnalyzerLoader : IAnalyzerAssemblyLoader
{
    private static readonly HashSet<string> s_hostAssemblies = new(
        ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(Path.GetFileNameWithoutExtension)
            .OfType<string>(),
        StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<string, DirectoryContext> _contexts = new(StringComparer.OrdinalIgnoreCase);

    public void AddDependencyLocation(string fullPath)
    {
    }

    public Assembly LoadFromPath(string fullPath)
    {
        var directory = Path.GetDirectoryName(fullPath) ?? string.Empty;
        return _contexts.GetOrAdd(directory, d => new DirectoryContext(d)).LoadFile(fullPath);
    }

    private sealed class DirectoryContext(string directory) : AssemblyLoadContext("analyzers: " + directory)
    {
        private readonly Lock _gate = new();
        private readonly Dictionary<string, Assembly> _loaded = new(StringComparer.OrdinalIgnoreCase);

        public Assembly LoadFile(string path)
        {
            lock (_gate)
            {
                if (_loaded.TryGetValue(path, out var existing))
                {
                    return existing;
                }

                using var stream = File.OpenRead(path);
                var assembly = LoadFromStream(stream);
                _loaded[path] = assembly;
                return assembly;
            }
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (assemblyName.Name is not { } name || s_hostAssemblies.Contains(name))
            {
                return null; // the default context (Roslyn, System.*)
            }

            var candidate = Path.Combine(directory, name + ".dll");
            return File.Exists(candidate) ? LoadFile(candidate) : null;
        }
    }
}
