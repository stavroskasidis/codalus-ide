using Codalus.Projects.Protocol;
using Codalus.Workbench.Projects;

namespace Codalus.Shell.Controls;

/// <summary>
/// File-type, folder, project and solution icons (D24, M34a): Material Icon Theme's colored icons for files and folders
/// (<c>mi:</c> names, Icons/material-icons.txt), and command-family colors for projects and the solution
/// (<c>kind@family</c>, see <see cref="Icon"/>). The Solution Explorer, document tabs, Go To and search use them.
/// </summary>
public static class FileIcons
{
    private const string Mi = IconSet.MaterialPrefix;

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

    /// <summary>The solution's icon (a classic IDE's purple solution glyph).</summary>
    public const string Solution = "solution@Build";

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

        return Mi + (icon ?? s_byExtension.GetValueOrDefault(Path.GetExtension(lower), "document"));
    }

    /// <summary>A folder's icon by its name, open or closed.</summary>
    public static string ForFolder(string name, bool open) =>
        Theme?.ForFolder(name, open) ?? Mi + s_byFolder.GetValueOrDefault(name, "folder") + (open ? "-open" : string.Empty);

    /// <summary>A project's icon from what the build host knows about it (test, web, Aspire, library, app).</summary>
    public static string ForProject(string path, ProjectInfo? info)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension == ".dcproj")
        {
            return Mi + "docker";
        }

        if (extension == ".sqlproj")
        {
            return Mi + "database";
        }

        if (extension == ".fsproj")
        {
            return Mi + "fsharp";
        }

        if (info is null)
        {
            return Mi + "csharp";
        }

        if (info.IsTestProject)
        {
            return "beaker@Test";
        }

        if (info.IsAspireHost)
        {
            return "cloud@Navigate";
        }

        if (info.Items.Any(i => i.Path.Contains("wwwroot", StringComparison.OrdinalIgnoreCase)
            || i.Path.EndsWith(".razor", StringComparison.OrdinalIgnoreCase) || i.Path.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase)))
        {
            return "globe@Navigate";
        }

        return info.OutputType is "Library" or null ? Mi + "lib" : Mi + "console";
    }

    /// <summary>A Solution Explorer node's icon (the workbench's generic name for anything else).</summary>
    public static string ForNode(ExplorerNode node, bool expanded, Func<string, ProjectInfo?> project) => node.Kind switch
    {
        ExplorerNodeKind.Solution => node.Icon == "folder" ? ForFolder(node.Name, expanded) : Solution,
        ExplorerNodeKind.SolutionFolder => ForFolder(node.Name, expanded),
        ExplorerNodeKind.Folder => ForFolder(node.Name, expanded),
        ExplorerNodeKind.Project => node.Path is { } path ? ForProject(path, project(path)) : node.Icon,
        ExplorerNodeKind.File or ExplorerNodeKind.SolutionItem => node.Icon == "project" && node.Path is { } projectFile ? ForProject(projectFile, project(projectFile)) : ForFile(node.Name),
        ExplorerNodeKind.Dependencies => Mi + "folder-packages" + (expanded ? "-open" : string.Empty),
        _ => node.Icon,
    };
}
