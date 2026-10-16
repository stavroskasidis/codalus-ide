using Microsoft.CodeAnalysis;

namespace Codalus.LanguageServices.Host.Xaml;

/// <summary>A resource key a XAML file can use: the key, the element that defines it, and its file.</summary>
internal sealed record XamlResourceKey(string Key, string Kind, string File);

/// <summary>
/// The resource keys a WPF XAML file sees (M40a, ADR 0069), and whether that list is complete: when every merged
/// dictionary on the way was found in the solution and the project has an <c>App.xaml</c>, a <c>StaticResource</c> key
/// outside it is reported; otherwise (a library, a dictionary from another assembly) nothing is.
/// </summary>
internal sealed record XamlResourceScope(IReadOnlyList<XamlResourceKey> Keys, bool Complete)
{
    public bool Contains(string key) => Keys.Any(k => k.Key == key);
}

/// <summary>
/// WPF's resource lookup as the editor approximates it: the file's own resources, the dictionaries it merges
/// (<c>&lt;ResourceDictionary Source="…"/&gt;</c>, relative, rooted or pack URIs), the application's <c>App.xaml</c> with
/// its merged dictionaries, and the resources of the files that merge this one.
/// </summary>
internal static class XamlResources
{
    public static async Task<XamlResourceScope> ScopeAsync(XamlContext context, CancellationToken cancellationToken)
    {
        var all = await context.ProjectXamlAsync(cancellationToken).ConfigureAwait(false);
        var documents = new Dictionary<string, XamlDocument>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, document) in all)
        {
            documents.TryAdd(Path.GetFullPath(path), document);
        }

        var complete = true;
        var projects = Projects(context.Project);
        var self = Path.GetFullPath(context.Path);

        HashSet<string> Closure(string start)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { start };
            var queue = new Queue<string>([start]);
            while (queue.Count > 0)
            {
                var path = queue.Dequeue();
                if (!documents.TryGetValue(path, out var document))
                {
                    continue;
                }

                foreach (var source in MergedSources(document))
                {
                    if (ResolveSource(path, source, projects) is { } target && documents.ContainsKey(target))
                    {
                        if (seen.Add(target))
                        {
                            queue.Enqueue(target);
                        }
                    }
                    else
                    {
                        complete = false;
                    }
                }
            }

            return seen;
        }

        var visible = Closure(self);
        var projectDirectory = context.Project?.FilePath is { } projectFile ? Path.GetDirectoryName(projectFile) : null;
        var applications = documents
            .Where(d => d.Value.Root?.LocalName == "Application" && projectDirectory is not null && d.Key.StartsWith(projectDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            .Select(d => d.Key)
            .ToList();
        if (applications.Count == 0)
        {
            complete = false; // a library: its resources come from the application that uses it
        }

        foreach (var application in applications)
        {
            visible.UnionWith(Closure(application));
        }

        foreach (var owner in documents.Keys.Where(p => !visible.Contains(p)).ToList())
        {
            if (Closure(owner).Contains(self))
            {
                visible.UnionWith(Closure(owner));
            }
        }

        var keys = new List<XamlResourceKey>();
        foreach (var path in visible)
        {
            if (!documents.TryGetValue(path, out var document))
            {
                continue;
            }

            foreach (var element in document.Elements)
            {
                if (element.Attribute("x:Key")?.Value is { Length: > 0 } key && !key.TrimStart().StartsWith('{'))
                {
                    keys.Add(new XamlResourceKey(key.Trim(), element.LocalName, path));
                }
            }
        }

        return new XamlResourceScope([.. keys.DistinctBy(k => k.Key)], complete);
    }

    /// <summary>The <c>Source</c> of every <c>ResourceDictionary</c> a document merges.</summary>
    public static IEnumerable<string> MergedSources(XamlDocument document) =>
        document.Elements
            .Where(e => e.LocalName == "ResourceDictionary" && e.Parent is { } parent && parent.LocalName.EndsWith(".MergedDictionaries", StringComparison.Ordinal))
            .Select(e => e.Attribute("Source")?.Value?.Trim())
            .OfType<string>()
            .Where(s => s.Length > 0 && !s.StartsWith('{'));

    /// <summary>
    /// The file a dictionary's <c>Source</c> names, from the file that merges it: relative to that file, rooted at its
    /// project (<c>/Themes/Colors.xaml</c>), or in a project of the solution by assembly name
    /// (<c>pack://application:,,,/Lib;component/Themes/Colors.xaml</c>, <c>/Lib;component/…</c>). Null when it is
    /// another assembly's.
    /// </summary>
    public static string? ResolveSource(string from, string source, IReadOnlyList<(string AssemblyName, string Directory)> projects)
    {
        var value = source.Replace('\\', '/');
        const string Pack = "pack://application:,,,";
        if (value.StartsWith(Pack, StringComparison.OrdinalIgnoreCase))
        {
            value = value[Pack.Length..];
        }

        if (value.Contains("://", StringComparison.Ordinal))
        {
            return null;
        }

        var component = value.IndexOf(";component/", StringComparison.OrdinalIgnoreCase);
        if (component >= 0)
        {
            var assembly = value[..component].TrimStart('/').Split(';')[0];
            var project = projects.FirstOrDefault(p => string.Equals(p.AssemblyName, assembly, StringComparison.OrdinalIgnoreCase));
            return project.Directory is null ? null : Path.GetFullPath(Path.Combine(project.Directory, value[(component + ";component/".Length)..]));
        }

        if (value.StartsWith('/'))
        {
            var owner = projects.Where(p => from.StartsWith(p.Directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)).MaxBy(p => p.Directory.Length);
            return owner.Directory is null ? null : Path.GetFullPath(Path.Combine(owner.Directory, value.TrimStart('/')));
        }

        return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(from)!, value));
    }

    private static List<(string AssemblyName, string Directory)> Projects(Project? project)
    {
        var result = new List<(string, string)>();
        if (project is null)
        {
            return result;
        }

        foreach (var candidate in project.ProjectReferences.Select(r => project.Solution.GetProject(r.ProjectId)).Prepend(project).OfType<Project>())
        {
            if (candidate.FilePath is { } file)
            {
                result.Add((candidate.AssemblyName, Path.GetDirectoryName(file)!));
            }
        }

        return result;
    }
}
