using System.Text.Json;

namespace Codalus.Workbench.WinForms;

/// <summary>
/// The design folders of a project's packages (#461, ADR 0106): where a control library's package keeps the designers it
/// builds for the classic IDE's designer server, <c>lib/&lt;tfm&gt;/Design/WinForms/Server</c>, for the target framework
/// restore chose for it. Read from the project's <c>project.assets.json</c>: its package folders, each package's path in
/// them, and the <c>lib/&lt;tfm&gt;</c> folder its assemblies come from.
/// </summary>
public static class WinFormsDesignFolders
{
    /// <summary>The design folders that exist, in the assets file's order; empty when it can't be read.</summary>
    public static IReadOnlyList<string> Read(string assetsFile)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(assetsFile));
            return Read(document.RootElement, Directory.Exists);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    /// <summary>The design folders of an assets file's JSON, those <paramref name="exists"/> says are there (tests give their own).</summary>
    public static IReadOnlyList<string> Read(JsonElement assets, Func<string, bool> exists)
    {
        if (assets.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        var packageFolders = assets.TryGetProperty("packageFolders", out var folders) && folders.ValueKind == JsonValueKind.Object
            ? folders.EnumerateObject().Select(f => f.Name).ToList()
            : [];
        var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (assets.TryGetProperty("libraries", out var libraries) && libraries.ValueKind == JsonValueKind.Object)
        {
            foreach (var library in libraries.EnumerateObject())
            {
                if (library.Value.ValueKind == JsonValueKind.Object
                    && library.Value.TryGetProperty("type", out var type) && type.ValueEquals("package")
                    && library.Value.TryGetProperty("path", out var path) && path.GetString() is { Length: > 0 } relative)
                {
                    paths[library.Name] = relative;
                }
            }
        }

        var found = new List<string>();
        if (!assets.TryGetProperty("targets", out var targets) || targets.ValueKind != JsonValueKind.Object)
        {
            return found;
        }

        foreach (var target in targets.EnumerateObject().Where(t => t.Value.ValueKind == JsonValueKind.Object))
        {
            foreach (var package in target.Value.EnumerateObject())
            {
                if (!paths.TryGetValue(package.Name, out var relative) || LibFolder(package.Value) is not { } framework)
                {
                    continue;
                }

                foreach (var root in packageFolders)
                {
                    // The framework's folder as the package names it: net8.0-windows7.0 (as dotnet pack writes it) or net8.0-windows (DevExpress's).
                    var design = Frameworks(framework).Select(f => Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar), "lib", f, "Design", "WinForms", "Server")).FirstOrDefault(exists);
                    if (design is not null)
                    {
                        if (!found.Contains(design, StringComparer.OrdinalIgnoreCase))
                        {
                            found.Add(design);
                        }

                        break;
                    }
                }
            }
        }

        return found;
    }

    /// <summary><paramref name="framework"/>, then without its platform's version (<c>net8.0-windows7.0</c>, <c>net8.0-windows</c>).</summary>
    private static IEnumerable<string> Frameworks(string framework)
    {
        yield return framework;
        var dash = framework.IndexOf('-', StringComparison.Ordinal);
        var version = dash < 0 ? -1 : framework.IndexOfAny("0123456789".ToCharArray(), dash);
        if (version > 0)
        {
            yield return framework[..version];
        }
    }

    /// <summary>The <c>lib/&lt;tfm&gt;</c> folder a package's runtime (else compile) assemblies come from, or null.</summary>
    private static string? LibFolder(JsonElement package)
    {
        foreach (var kind in new[] { "runtime", "compile" })
        {
            if (!package.TryGetProperty(kind, out var files) || files.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            foreach (var file in files.EnumerateObject())
            {
                var parts = file.Name.Split('/');
                if (parts.Length >= 3 && parts[0] == "lib" && parts[^1] != "_._")
                {
                    return parts[1];
                }
            }
        }

        return null;
    }
}
