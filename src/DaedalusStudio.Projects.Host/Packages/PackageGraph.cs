using System.Text.Json;

namespace DaedalusStudio.Projects.Host.Packages;

/// <summary>
/// The restored dependency graph from <c>obj/project.assets.json</c>: per target framework, every library (package or
/// project) with its resolved version and dependencies, and the project's top-level package dependencies. Used for
/// transitive packages and "why is this here".
/// </summary>
internal sealed class PackageGraph
{
    private readonly Dictionary<string, FrameworkGraph> _frameworks;

    private PackageGraph(Dictionary<string, FrameworkGraph> frameworks) => _frameworks = frameworks;

    public sealed record Library(string Id, string Version, string Type, string[] Dependencies);

    public sealed record FrameworkGraph(string Name, Dictionary<string, Library> Libraries, HashSet<string> TopLevel);

    public IEnumerable<FrameworkGraph> Frameworks => _frameworks.Values;

    public static PackageGraph? TryLoad(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            using var stream = File.OpenRead(path);
            using var document = JsonDocument.Parse(stream);
            var root = document.RootElement;
            var topLevel = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            if (root.TryGetProperty("project", out var project) && project.TryGetProperty("frameworks", out var projectFrameworks))
            {
                foreach (var framework in projectFrameworks.EnumerateObject())
                {
                    var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    if (framework.Value.TryGetProperty("dependencies", out var dependencies))
                    {
                        foreach (var dependency in dependencies.EnumerateObject())
                        {
                            set.Add(dependency.Name);
                        }
                    }

                    topLevel[framework.Name] = set;
                }
            }

            var frameworks = new Dictionary<string, FrameworkGraph>(StringComparer.OrdinalIgnoreCase);
            if (root.TryGetProperty("targets", out var targets))
            {
                foreach (var target in targets.EnumerateObject().Where(t => !t.Name.Contains('/', StringComparison.Ordinal)))
                {
                    var libraries = new Dictionary<string, Library>(StringComparer.OrdinalIgnoreCase);
                    foreach (var library in target.Value.EnumerateObject())
                    {
                        if (library.Name.Split('/') is not [var id, var version])
                        {
                            continue;
                        }

                        var type = library.Value.TryGetProperty("type", out var t) ? t.GetString() ?? "package" : "package";
                        var dependencies = library.Value.TryGetProperty("dependencies", out var d)
                            ? d.EnumerateObject().Select(p => p.Name).ToArray()
                            : [];
                        libraries[id] = new Library(id, version, type, dependencies);
                    }

                    var name = topLevel.Keys.FirstOrDefault(k => FrameworkMatches(target.Name, k)) ?? target.Name;
                    frameworks[name] = new FrameworkGraph(name, libraries, topLevel.GetValueOrDefault(name) ?? []);
                }
            }

            return new PackageGraph(frameworks);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public FrameworkGraph? Framework(string alias) =>
        _frameworks.GetValueOrDefault(alias)
        ?? _frameworks.Values.FirstOrDefault(f => FrameworkMatches(f.Name, alias))
        ?? (_frameworks.Count == 1 ? _frameworks.Values.First() : null);

    /// <summary>
    /// Every chain from a top-level dependency (a package the project references, or a referenced project) to
    /// <paramref name="id"/>, as <c>id/version</c> steps. Bounded, since large graphs have many paths.
    /// </summary>
    public static IReadOnlyList<string[]> Paths(FrameworkGraph graph, string id, int limit = 100)
    {
        var paths = new List<string[]>();
        if (!graph.Libraries.ContainsKey(id))
        {
            return paths;
        }

        var starts = graph.Libraries.Values
            .Where(l => graph.TopLevel.Contains(l.Id) || l.Type == "project")
            .Select(l => l.Id)
            .ToList();
        var stack = new List<string>();
        var onPath = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Walk(string current)
        {
            if (paths.Count >= limit || stack.Count > 32 || !onPath.Add(current))
            {
                return;
            }

            stack.Add(current);
            if (string.Equals(current, id, StringComparison.OrdinalIgnoreCase))
            {
                paths.Add([.. stack.Select(s => graph.Libraries.TryGetValue(s, out var l) ? $"{l.Id}/{l.Version}" : s)]);
            }
            else if (graph.Libraries.TryGetValue(current, out var library))
            {
                foreach (var dependency in library.Dependencies.Where(graph.Libraries.ContainsKey))
                {
                    Walk(dependency);
                }
            }

            stack.RemoveAt(stack.Count - 1);
            onPath.Remove(current);
        }

        foreach (var start in starts)
        {
            Walk(start);
        }

        // A top-level reference is its own (one-step) path; keep the longer ones only when that is all there is.
        return paths.Distinct(new ChainComparer()).OrderBy(p => p.Length).ToList();
    }

    internal static bool FrameworkMatches(string assetsKey, string alias) =>
        assetsKey.Replace(".", string.Empty, StringComparison.Ordinal)
            .Equals(alias.Replace(".", string.Empty, StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase);

    private sealed class ChainComparer : IEqualityComparer<string[]>
    {
        public bool Equals(string[]? x, string[]? y) => x is not null && y is not null && x.SequenceEqual(y, StringComparer.OrdinalIgnoreCase);

        public int GetHashCode(string[] obj) => obj.Aggregate(0, (h, s) => HashCode.Combine(h, StringComparer.OrdinalIgnoreCase.GetHashCode(s)));
    }
}
