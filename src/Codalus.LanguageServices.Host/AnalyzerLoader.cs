using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;

namespace Codalus.LanguageServices.Host;

/// <summary>
/// Loads analyzer and source generator assemblies, one <see cref="AssemblyLoadContext"/> per directory (a package's
/// analyzer folder, or a project's output), so different versions of the same dependency can coexist. Assemblies the
/// host already has (Roslyn, the runtime) come from the default context. Files are loaded from memory, so analyzers
/// built by projects in the solution stay writable for the next build.
/// </summary>
/// <remarks>
/// An analyzer's dependency is found next to it, else among the analyzers the projects name
/// (<see cref="AddDependencyLocation"/>, as the compiler resolves them), loaded by its own directory's context so every
/// analyzer that uses it sees one assembly: Windows Forms' C# generator (<c>analyzers/dotnet/cs</c>) uses
/// <c>System.Windows.Forms.Analyzers</c> one folder up (<c>analyzers/dotnet</c>), and without it the generator failed
/// and <c>ApplicationConfiguration</c> was never generated.
/// </remarks>
internal sealed class AnalyzerLoader : IAnalyzerAssemblyLoader
{
    private static readonly HashSet<string> s_hostAssemblies = new(
        ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(Path.GetFileNameWithoutExtension)
            .OfType<string>(),
        StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<string, DirectoryContext> _contexts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The analyzer files the projects name, by assembly name: where a dependency not next to its analyzer is.</summary>
    private readonly ConcurrentDictionary<string, string> _dependencies = new(StringComparer.OrdinalIgnoreCase);

    public void AddDependencyLocation(string fullPath)
    {
        if (Path.GetFileNameWithoutExtension(fullPath) is { Length: > 0 } name)
        {
            _dependencies.TryAdd(name, fullPath);
        }
    }

    public Assembly LoadFromPath(string fullPath)
    {
        var directory = Path.GetDirectoryName(fullPath) ?? string.Empty;
        return _contexts.GetOrAdd(directory, d => new DirectoryContext(this, d)).LoadFile(fullPath);
    }

    private sealed class DirectoryContext(AnalyzerLoader loader, string directory) : AssemblyLoadContext("analyzers: " + directory)
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
            if (File.Exists(candidate))
            {
                return LoadFile(candidate);
            }

            // Another analyzer of the projects, in its own directory's context (the same assembly its other users get).
            return loader._dependencies.TryGetValue(name, out var elsewhere) && File.Exists(elsewhere) ? loader.LoadFromPath(elsewhere) : null;
        }
    }
}
