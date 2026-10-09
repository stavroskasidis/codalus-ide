using System.Text.Json;

namespace DaedalusStudio.Projects.Host;

/// <summary>
/// The parts of NuGet's <c>obj/project.assets.json</c> the Dependencies node needs: resolved package versions per
/// target framework. The file exists once the project has been restored.
/// </summary>
internal sealed class AssetsFile
{
    private readonly Dictionary<string, Dictionary<string, string>> _packages;

    private AssetsFile(Dictionary<string, Dictionary<string, string>> packages) => _packages = packages;

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
                    foreach (var library in target.Value.EnumerateObject())
                    {
                        if (library.Value.TryGetProperty("type", out var type) && type.GetString() == "package"
                            && library.Name.Split('/') is [var name, var version])
                        {
                            versions[name] = version;
                        }
                    }

                    packages[target.Name] = versions;
                }
            }

            return new AssetsFile(packages);
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

    private static bool FrameworkMatches(string assetsKey, string alias) =>
        assetsKey.Replace(".", string.Empty, StringComparison.Ordinal)
            .Equals(alias.Replace(".", string.Empty, StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase);
}
