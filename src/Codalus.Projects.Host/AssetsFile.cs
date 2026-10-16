using System.Text.Json;

namespace Codalus.Projects.Host;

/// <summary>
/// The parts of NuGet's <c>obj/project.assets.json</c> the Dependencies node needs: resolved package versions per
/// target framework, and each package's own dependencies (the transitive packages). The file exists once the project
/// has been restored.
/// </summary>
internal sealed class AssetsFile
{
    private readonly Dictionary<string, Dictionary<string, string>> _packages;
    private readonly Dictionary<string, Dictionary<string, string[]>> _dependencies;

    private AssetsFile(Dictionary<string, Dictionary<string, string>> packages, Dictionary<string, Dictionary<string, string[]>> dependencies) =>
        (_packages, _dependencies) = (packages, dependencies);

    public static AssetsFile? TryLoad(string? path, ICollection<string> diagnostics)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            using var stream = File.OpenRead(path);
            using var document = JsonDocument.Parse(stream);
            var packages = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            var dependencies = new Dictionary<string, Dictionary<string, string[]>>(StringComparer.OrdinalIgnoreCase);
            if (document.RootElement.TryGetProperty("targets", out var targets))
            {
                foreach (var target in targets.EnumerateObject())
                {
                    // Keys are "net10.0" or "net10.0/win-x64" (runtime-specific graphs); the plain one is enough here.
                    if (target.Name.Contains('/', StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var versions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    var edges = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
                    foreach (var library in target.Value.EnumerateObject())
                    {
                        if (library.Value.TryGetProperty("type", out var type) && type.GetString() == "package"
                            && library.Name.Split('/') is [var name, var version])
                        {
                            versions[name] = version;
                            edges[name] = library.Value.TryGetProperty("dependencies", out var needs) && needs.ValueKind == JsonValueKind.Object
                                ? [.. needs.EnumerateObject().Select(d => d.Name)]
                                : [];
                        }
                    }

                    packages[target.Name] = versions;
                    dependencies[target.Name] = edges;
                }
            }

            return new AssetsFile(packages, dependencies);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            diagnostics.Add($"Could not read {path}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// The resolved version of <paramref name="package"/> for <paramref name="framework"/> (the TFM alias used in
    /// the project). Assets files key targets by normalized framework names, so an alias lookup falls back to the
    /// only target when the project has one.
    /// </summary>
    public string? ResolvedPackageVersion(string framework, string package)
    {
        var target = _packages.GetValueOrDefault(framework)
                     ?? _packages.FirstOrDefault(p => FrameworkMatches(p.Key, framework)).Value
                     ?? (_packages.Count == 1 ? _packages.Values.First() : null);
        return target?.GetValueOrDefault(package);
    }

    /// <summary>
    /// Every package resolved for <paramref name="framework"/>, with the packages it depends on (those that are packages
    /// of the graph: framework references and projects aren't).
    /// </summary>
    public IEnumerable<Protocol.PackageDependencyInfo> Graph(string framework)
    {
        var key = _packages.ContainsKey(framework) ? framework
                  : _packages.Keys.FirstOrDefault(k => FrameworkMatches(k, framework))
                  ?? (_packages.Count == 1 ? _packages.Keys.First() : null);
        if (key is null)
        {
            yield break;
        }

        var versions = _packages[key];
        foreach (var (name, needs) in _dependencies[key])
        {
            yield return new Protocol.PackageDependencyInfo(framework, name, versions[name], [.. needs.Where(versions.ContainsKey)]);
        }
    }

    private static bool FrameworkMatches(string assetsKey, string alias) =>
        assetsKey.Replace(".", string.Empty, StringComparison.Ordinal)
            .Equals(alias.Replace(".", string.Empty, StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase);
}
