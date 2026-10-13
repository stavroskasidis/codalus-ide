using System.Text.Json;

namespace Codalus.Shell.Theming;

/// <summary>
/// A file icon theme from a plugin (plugin API 1.0, M22a), in VS Code's format: <c>iconDefinitions</c> (each an
/// <c>iconPath</c> next to the theme file), the defaults <c>file</c>, <c>folder</c> and <c>folderExpanded</c>, and
/// <c>fileExtensions</c>, <c>fileNames</c>, <c>folderNames</c> and <c>folderNamesExpanded</c>. The shell draws raster
/// icons (PNG, ICO, BMP) as they are. SVG icons (M22b) are converted outside the shell into the drawing operations of
/// its own compiled icons (<c>codalus-pluginhost --compile-icons</c>) and drawn as vectors (<c>vec:&lt;path&gt;</c>)
/// once they are; until then, or when one uses SVG the converter doesn't support, the built-in icon shows.
/// </summary>
public sealed class IconTheme
{
    private static readonly HashSet<string> s_raster = new(StringComparer.OrdinalIgnoreCase) { ".png", ".ico", ".bmp", ".jpg", ".jpeg", ".webp" };

    private readonly Dictionary<string, string> _icons = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _extensions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _fileNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _folderNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _folderNamesExpanded = new(StringComparer.OrdinalIgnoreCase);
    private string? _file;
    private string? _folder;
    private string? _folderExpanded;

    /// <summary>Icons skipped (missing, of another format, or SVG not converted), for the log.</summary>
    public List<string> Skipped { get; } = [];

    /// <summary>The theme's SVG icons, to convert.</summary>
    public List<string> SvgFiles { get; } = [];

    /// <param name="themeFile">The theme's JSON file.</param>
    /// <param name="vector">The name of an SVG icon's converted drawing (<c>vec:&lt;path&gt;</c>), or null when it has none yet.</param>
    public static IconTheme Read(string themeFile, Func<string, string?>? vector = null)
    {
        var theme = new IconTheme();
        var folder = Path.GetDirectoryName(Path.GetFullPath(themeFile))!;
        using var document = JsonDocument.Parse(File.ReadAllText(themeFile), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var root = document.RootElement;
        if (root.TryGetProperty("iconDefinitions", out var definitions) && definitions.ValueKind == JsonValueKind.Object)
        {
            foreach (var definition in definitions.EnumerateObject())
            {
                if (definition.Value.TryGetProperty("iconPath", out var pathValue) && pathValue.GetString() is { } relative)
                {
                    var path = Path.GetFullPath(Path.Combine(folder, relative));
                    if (s_raster.Contains(Path.GetExtension(path)) && File.Exists(path))
                    {
                        theme._icons[definition.Name] = "img:" + path;
                    }
                    else if (Path.GetExtension(path).Equals(".svg", StringComparison.OrdinalIgnoreCase) && File.Exists(path))
                    {
                        theme.SvgFiles.Add(path);
                        if (vector?.Invoke(path) is { } drawing)
                        {
                            theme._icons[definition.Name] = drawing;
                        }
                        else
                        {
                            theme.Skipped.Add($"{definition.Name} ({relative})");
                        }
                    }
                    else
                    {
                        theme.Skipped.Add($"{definition.Name} ({relative})");
                    }
                }
            }
        }

        string? Icon(string property) => root.TryGetProperty(property, out var id) && id.GetString() is { } name ? theme._icons.GetValueOrDefault(name) : null;
        void Map(string property, Dictionary<string, string> into)
        {
            if (root.TryGetProperty(property, out var map) && map.ValueKind == JsonValueKind.Object)
            {
                foreach (var entry in map.EnumerateObject())
                {
                    if (entry.Value.GetString() is { } name && theme._icons.TryGetValue(name, out var icon))
                    {
                        into[entry.Name] = icon;
                    }
                }
            }
        }

        theme._file = Icon("file");
        theme._folder = Icon("folder");
        theme._folderExpanded = Icon("folderExpanded");
        Map("fileExtensions", theme._extensions);
        Map("fileNames", theme._fileNames);
        Map("folderNames", theme._folderNames);
        Map("folderNamesExpanded", theme._folderNamesExpanded);
        return theme;
    }

    /// <summary>A file's icon: its name, then its extensions from the longest (<c>d.ts</c> before <c>ts</c>), then the default.</summary>
    public string? ForFile(string name)
    {
        if (_fileNames.TryGetValue(name, out var byName))
        {
            return byName;
        }

        for (var dot = name.IndexOf('.', StringComparison.Ordinal); dot >= 0; dot = name.IndexOf('.', dot + 1))
        {
            if (_extensions.TryGetValue(name[(dot + 1)..], out var byExtension))
            {
                return byExtension;
            }
        }

        return _file;
    }

    public string? ForFolder(string name, bool open) =>
        open
            ? _folderNamesExpanded.GetValueOrDefault(name) ?? _folderNames.GetValueOrDefault(name) ?? _folderExpanded ?? _folder
            : _folderNames.GetValueOrDefault(name) ?? _folder;
}
