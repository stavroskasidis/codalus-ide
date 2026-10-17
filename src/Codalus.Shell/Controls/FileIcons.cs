using Codalus.Projects.Protocol;
using Codalus.Workbench.Projects;

namespace Codalus.Shell.Controls;

/// <summary>
/// File-type, folder, project and solution icons (D24, M34a). The built-in icons are the classic ones (<c>cl:</c> names,
/// Icons/Classic): outline pages with a colored badge, tan outline folders and project boxes, as a classic IDE draws them.
/// Material Icon Theme's colored icons (<c>mi:</c> names, Icons/material-icons.txt) are the other built-in theme
/// (<c>workbench.iconTheme</c> <c>material</c>). Names are worked out once, as Material's (the finer set), and the classic
/// icons map those onto theirs. The Solution Explorer, document tabs, Go To and search use them.
/// </summary>
public static class FileIcons
{
    private const string Mi = IconSet.MaterialPrefix;
    private const string Cl = IconSet.ClassicPrefix;

    /// <summary>The classic icon for each Material name; any other file is a blank page.</summary>
    private static readonly Dictionary<string, string> s_classic = new(StringComparer.Ordinal)
    {
        ["csharp"] = "csharp", ["fsharp"] = "fsharp", ["razor"] = "razor", ["xaml"] = "xaml", ["xml"] = "xml", ["html"] = "html",
        ["tune"] = "msbuild", ["json"] = "json", ["yaml"] = "yaml", ["markdown"] = "markdown",
        ["css"] = "css", ["sass"] = "sass", ["javascript"] = "javascript", ["typescript"] = "typescript",
        ["powershell"] = "powershell", ["console"] = "console", ["database"] = "database",
        ["image"] = "image", ["svg"] = "image", ["http"] = "http", ["proto"] = "text", ["log"] = "text",
        ["certificate"] = "certificate", ["license"] = "certificate", ["key"] = "key", ["lock"] = "lock",
        ["dll"] = "binary", ["exe"] = "application", ["zip"] = "package", ["pdf"] = "pdf", ["table"] = "table", ["word"] = "word",
        ["template"] = "template", ["settings"] = "config", ["editorconfig"] = "config", ["git"] = "git", ["docker"] = "docker",
    };

    private static readonly Dictionary<string, string> s_byExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".cs"] = "csharp", [".csx"] = "csharp",
        [".razor"] = "razor", [".cshtml"] = "razor",
        [".xaml"] = "xaml", [".axaml"] = "xaml",
        [".xml"] = "xml", [".resx"] = "xml", [".xsd"] = "xml", [".nuspec"] = "xml", [".manifest"] = "xml", [".xlf"] = "xml",
        [".props"] = "tune", [".targets"] = "tune",
        [".json"] = "json", [".jsonc"] = "json",
        [".yml"] = "yaml", [".yaml"] = "yaml",
        [".md"] = "markdown", [".markdown"] = "markdown",
        [".html"] = "html", [".htm"] = "html",
        [".css"] = "css", [".scss"] = "sass", [".sass"] = "sass",
        [".js"] = "javascript", [".mjs"] = "javascript", [".cjs"] = "javascript",
        [".ts"] = "typescript", [".mts"] = "typescript",
        [".ps1"] = "powershell", [".psm1"] = "powershell", [".psd1"] = "powershell",
        [".sh"] = "console", [".cmd"] = "console", [".bat"] = "console",
        [".sql"] = "database", [".db"] = "database", [".sqlite"] = "database",
        [".png"] = "image", [".jpg"] = "image", [".jpeg"] = "image", [".gif"] = "image", [".bmp"] = "image", [".ico"] = "image", [".webp"] = "image",
        [".svg"] = "svg",
        [".http"] = "http", [".rest"] = "http",
        [".proto"] = "proto",
        [".pfx"] = "certificate", [".cer"] = "certificate", [".crt"] = "certificate", [".pem"] = "certificate",
        [".snk"] = "key", [".key"] = "key",
        [".log"] = "log",
        [".txt"] = "document",
        [".fs"] = "fsharp", [".fsx"] = "fsharp", [".fsi"] = "fsharp",
        [".dll"] = "dll", [".exe"] = "exe",
        [".zip"] = "zip", [".nupkg"] = "zip", [".snupkg"] = "zip",
        [".pdf"] = "pdf",
        [".csv"] = "table", [".tsv"] = "table",
        [".docx"] = "word",
        [".tt"] = "template", [".t4"] = "template",
        [".config"] = "settings", [".runsettings"] = "settings", [".editorconfig"] = "editorconfig",
        [".lock"] = "lock",
    };

    private static readonly Dictionary<string, string> s_byFolder = new(StringComparer.OrdinalIgnoreCase)
    {
        ["properties"] = "folder-config", ["config"] = "folder-config", ["configuration"] = "folder-config",
        ["wwwroot"] = "folder-public", ["public"] = "folder-public",
        ["src"] = "folder-src", ["source"] = "folder-src",
        ["test"] = "folder-test", ["tests"] = "folder-test",
        ["docs"] = "folder-docs", ["doc"] = "folder-docs",
        ["images"] = "folder-images", ["img"] = "folder-images", ["assets"] = "folder-images", ["icons"] = "folder-images",
        ["lib"] = "folder-lib", ["libs"] = "folder-lib",
        ["components"] = "folder-components",
        ["views"] = "folder-views", ["pages"] = "folder-views",
        ["controllers"] = "folder-controller",
        ["scripts"] = "folder-scripts", ["js"] = "folder-javascript",
        ["shared"] = "folder-shared",
        ["resources"] = "folder-resource",
        ["tools"] = "folder-tools",
        [".github"] = "folder-github",
        ["bin"] = "folder-dist", ["obj"] = "folder-dist", ["dist"] = "folder-dist", ["publish"] = "folder-dist",
        ["data"] = "folder-database", ["database"] = "folder-database",
        ["api"] = "folder-api",
        ["packages"] = "folder-packages",
        ["migrations"] = "folder-migrations",
        ["server"] = "folder-server",
        ["client"] = "folder-client",
        ["core"] = "folder-core",
        ["app"] = "folder-app",
        ["protos"] = "folder-proto", ["proto"] = "folder-proto",
        [".vscode"] = "folder-vscode",
        ["helpers"] = "folder-helper",
        ["utils"] = "folder-utils", ["utilities"] = "folder-utils",
        ["docker"] = "folder-docker",
        ["models"] = "folder-class",
        ["json"] = "folder-json",
        ["middleware"] = "folder-middleware",
        ["temp"] = "folder-temp", ["tmp"] = "folder-temp",
        ["content"] = "folder-content",
        ["layout"] = "folder-layout", ["layouts"] = "folder-layout",
        ["plugins"] = "folder-plugin",
        ["security"] = "folder-secure",
        ["logs"] = "folder-log",
    };

    /// <summary>A Windows Forms form's or user control's icon (M41a): a window with a C# badge (Material has none: its C# file).</summary>
    public static string Form => Material ? Mi + "csharp" : Cl + "form";

    /// <summary>
    /// Whether a Solution Explorer node is a form or user control of a Windows Forms project: a <c>.cs</c> file with its
    /// <c>.Designer.cs</c> nested under it.
    /// </summary>
    public static bool IsForm(ExplorerNode node, Func<string, ProjectInfo?> project) =>
        node is { Kind: ExplorerNodeKind.File, ProjectPath: { } owner } && node.Name.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) && !node.Name.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase)
        && node.Children.Any(c => string.Equals(c.Name, node.Name[..^3] + ".Designer.cs", StringComparison.OrdinalIgnoreCase))
        && project(owner) is { UsesWindowsForms: true };

    /// <summary>The solution's icon (a classic IDE's purple solution glyph).</summary>
    public static string Solution => Material ? "solution@Build" : Cl + "solution";

    /// <summary>Whether the built-in icons are Material Icon Theme's (<c>workbench.iconTheme</c> <c>material</c>) instead of the classic ones.</summary>
    public static bool Material { get; set; }

    /// <summary>A plugin's file icon theme in use (API 1.0, M22a); null for the built-in icons.</summary>
    public static Theming.IconTheme? Theme { get; set; }

    /// <summary>A file's icon by its name (special names first, then the extension).</summary>
    public static string ForFile(string name)
    {
        if (Theme?.ForFile(name) is { } themed)
        {
            return themed;
        }

        var lower = name.ToLowerInvariant();
        string? icon = lower switch
        {
            _ when lower.StartsWith("readme", StringComparison.Ordinal) => "readme",
            "license" or "license.txt" or "license.md" or "licence" => "license",
            _ when lower.StartsWith("changelog", StringComparison.Ordinal) => "changelog",
            ".gitignore" or ".gitattributes" or ".gitmodules" => "g" + "it",
            ".editorconfig" => "editorconfig",
            "dockerfile" or ".dockerignore" => "docker",
            _ when lower.StartsWith("dockerfile.", StringComparison.Ordinal) => "docker",
            _ when (lower.StartsWith("docker-compose", StringComparison.Ordinal) || lower.StartsWith("compose.", StringComparison.Ordinal)) && (lower.EndsWith(".yml", StringComparison.Ordinal) || lower.EndsWith(".yaml", StringComparison.Ordinal)) => "docker",
            "packages.lock.json" => "lock",
            _ when lower.StartsWith("appsettings", StringComparison.Ordinal) && lower.EndsWith(".json", StringComparison.Ordinal) => "settings",
            "launchsettings.json" or "global.json" or "nuget.config" => "settings",
            _ when lower.StartsWith("directory.", StringComparison.Ordinal) && (lower.EndsWith(".props", StringComparison.Ordinal) || lower.EndsWith(".targets", StringComparison.Ordinal)) => "tune",
            _ => null,
        };

        if (icon is null && Path.GetExtension(lower) is ".sln" or ".slnx" or ".slnf")
        {
            return Solution;
        }

        icon ??= s_byExtension.GetValueOrDefault(Path.GetExtension(lower), "document");
        if (Material)
        {
            return Mi + icon;
        }

        // Settings in JSON show braces, and text files their lines, as a classic IDE's do.
        var extension = Path.GetExtension(lower);
        return Cl + icon switch
        {
            "settings" when extension == ".json" => "json",
            "readme" or "changelog" => extension == ".md" ? "markdown" : "text",
            "document" when extension == ".txt" => "text",
            _ => s_classic.GetValueOrDefault(icon, "document"),
        };
    }

    /// <summary>A folder's icon by its name, open or closed.</summary>
    public static string ForFolder(string name, bool open)
    {
        if (Theme?.ForFolder(name, open) is { } themed)
        {
            return themed;
        }

        if (Material)
        {
            return Mi + s_byFolder.GetValueOrDefault(name, "folder") + (open ? "-open" : string.Empty);
        }

        // A classic IDE draws Properties as a wrench and wwwroot as a globe, and every other folder plain.
        return Cl + name.ToLowerInvariant() switch
        {
            "properties" => "folder-properties",
            "wwwroot" => "folder-web",
            _ => open ? "folder-open" : "folder",
        };
    }

    /// <summary>A project's icon from what the build host knows about it (test, web, Aspire, library, app).</summary>
    public static string ForProject(string path, ProjectInfo? info)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension == ".dcproj")
        {
            return (Material ? Mi : Cl) + "docker";
        }

        if (extension == ".sqlproj")
        {
            return (Material ? Mi : Cl) + "database";
        }

        if (extension == ".fsproj")
        {
            return Material ? Mi + "fsharp" : Cl + "project-fsharp";
        }

        if (info is null)
        {
            return Material ? Mi + "csharp" : Cl + "project-csharp";
        }

        if (info.IsTestProject)
        {
            return Material ? "beaker@Test" : Cl + "project-test";
        }

        if (info.IsAspireHost)
        {
            return "cloud@Navigate";
        }

        if (info.Items.Any(i => i.Path.Contains("wwwroot", StringComparison.OrdinalIgnoreCase)
            || i.Path.EndsWith(".razor", StringComparison.OrdinalIgnoreCase) || i.Path.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase)))
        {
            return Material ? "globe@Navigate" : Cl + "project-web";
        }

        // A classic IDE draws applications and libraries alike.
        return !Material ? Cl + "project-csharp" : info.OutputType is "Library" or null ? Mi + "lib" : Mi + "console";
    }

    /// <summary>A Solution Explorer node's icon (the workbench's generic name for anything else).</summary>
    public static string ForNode(ExplorerNode node, bool expanded, Func<string, ProjectInfo?> project) => node.Kind switch
    {
        ExplorerNodeKind.Solution => node.Icon == "folder" ? ForFolder(node.Name, expanded) : Solution,
        ExplorerNodeKind.SolutionFolder => ForFolder(node.Name, expanded && Material), // classic solution folders stay closed
        ExplorerNodeKind.Folder => ForFolder(node.Name, expanded),
        ExplorerNodeKind.Project => node.Path is { } path ? ForProject(path, project(path)) : node.Icon,
        ExplorerNodeKind.File or ExplorerNodeKind.SolutionItem => node.Icon == "project" && node.Path is { } projectFile ? ForProject(projectFile, project(projectFile))
            : Theme is null && IsForm(node, project) ? Form : ForFile(node.Name),
        ExplorerNodeKind.Dependencies => Material ? Mi + "folder-packages" + (expanded ? "-open" : string.Empty) : Cl + "dependencies",
        _ => node.Icon,
    };
}
