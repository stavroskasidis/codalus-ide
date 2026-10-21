using System.IO.Compression;

namespace Codalus.Workbench.Projects;

/// <summary>
/// A template's own files in its package, next to its <c>template.json</c>: the IDE host file, the template definition,
/// the localized strings, and the longest path of a file it writes (for the Windows path-length check).
/// </summary>
public sealed record TemplatePackageFiles(string? IdeHost, string? Definition, string? Strings, string? LongestContentPath)
{
    public static TemplatePackageFiles Empty { get; } = new(null, null, null, null);

    /// <summary>Reads one template's files (<see cref="TemplatePackageReader"/> reads many from one package).</summary>
    public static TemplatePackageFiles Read(string? mountPoint, string? configPlace, string? culture)
    {
        if (mountPoint is null || configPlace is null)
        {
            return Empty;
        }

        using var package = TemplatePackageReader.Open(mountPoint);
        return package?.Files(configPlace, culture) ?? Empty;
    }
}

/// <summary>
/// A template package (<c>MountPointUri</c>: a <c>.nupkg</c>, read as a zip, or a folder), opened once to read the files
/// of each of its templates. A template's folder is its <c>ConfigPlace</c>'s, which is under <c>/content/…</c> in some
/// packages and at the root in others.
/// </summary>
public sealed class TemplatePackageReader : IDisposable
{
    private readonly string? _folder;
    private readonly ZipArchive? _zip;
    private readonly Dictionary<string, ZipArchiveEntry>? _entries;

    private TemplatePackageReader(string? folder, ZipArchive? zip)
    {
        _folder = folder;
        _zip = zip;
        _entries = zip?.Entries.GroupBy(e => e.FullName.Replace('\\', '/'), StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The package at <paramref name="mountPoint"/>, or null when it isn't there or can't be read.</summary>
    public static TemplatePackageReader? Open(string mountPoint)
    {
        try
        {
            if (Directory.Exists(mountPoint))
            {
                return new TemplatePackageReader(mountPoint, null);
            }

            if (File.Exists(mountPoint))
            {
                return new TemplatePackageReader(null, ZipFile.OpenRead(mountPoint));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // A package that can't be read: its templates show from the cache alone.
        }

        return null;
    }

    public void Dispose() => _zip?.Dispose();

    /// <summary>
    /// The files of the template at <paramref name="configPlace"/>. <paramref name="culture"/> picks the strings
    /// (<c>templatestrings.fr.json</c>, then the language's: <c>pt-BR</c> → <c>pt</c>); English needs none (the cache is English).
    /// </summary>
    public TemplatePackageFiles Files(string configPlace, string? culture)
    {
        var config = configPlace.Replace('\\', '/').TrimStart('/');
        var configFolder = config.Contains('/', StringComparison.Ordinal) ? config[..config.LastIndexOf('/')] : string.Empty;
        var templateRoot = configFolder.Contains('/', StringComparison.Ordinal) ? configFolder[..configFolder.LastIndexOf('/')] : string.Empty;
        string[] cultures = culture is null or "" or "en" ? [] : culture.Contains('-', StringComparison.Ordinal) ? [culture, culture.Split('-')[0]] : [culture];
        try
        {
            return new TemplatePackageFiles(
                Text(configFolder + "/ide.host.json"),
                Text(config),
                cultures.Select(c => Text($"{configFolder}/localize/templatestrings.{c}.json")).FirstOrDefault(t => t is not null),
                Longest(templateRoot, configFolder));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return TemplatePackageFiles.Empty;
        }
    }

    private string? Text(string relative)
    {
        if (_entries is not null)
        {
            if (!_entries.TryGetValue(relative.TrimStart('/'), out var entry))
            {
                return null;
            }

            using var reader = new StreamReader(entry.Open(), detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }

        var path = Path.Combine(_folder!, relative.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    /// <summary>The longest path of a file under the template's folder (not its <c>.template.config</c>), relative to it.</summary>
    private string? Longest(string templateRoot, string configFolder)
    {
        if (_entries is not null)
        {
            var prefix = templateRoot.Length == 0 ? string.Empty : templateRoot + "/";
            return _entries.Keys
                .Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && !k.EndsWith('/') && !k.StartsWith(configFolder + "/", StringComparison.OrdinalIgnoreCase))
                .Select(k => Uri.UnescapeDataString(k[prefix.Length..]))
                .MaxBy(k => k.Length);
        }

        var root = Path.Combine(_folder!, templateRoot.Replace('/', Path.DirectorySeparatorChar));
        var configPath = Path.Combine(_folder!, configFolder.Replace('/', Path.DirectorySeparatorChar));
        return Directory.Exists(root)
            ? Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Where(f => !f.StartsWith(configPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
                .MaxBy(f => f.Length)
            : null;
    }
}
