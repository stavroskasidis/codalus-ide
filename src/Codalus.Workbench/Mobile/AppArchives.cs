using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Codalus.Workbench.Mobile;

/// <summary>
/// An archive of a .NET MAUI app (ADR 0114): one Release build for one platform, kept so it can be distributed (signed
/// for a channel) more than once. Android's and Windows' are folders of the IDE's (<see cref="AppArchives.Root"/>)
/// with an <c>archive.json</c>; Apple's are Xcode's own <c>.xcarchive</c>s, which Xcode's Organizer lists too.
/// </summary>
public sealed record AppArchive(string Path, MobilePlatform Platform, string Name, DateTimeOffset Created)
{
    public string? ApplicationId { get; init; }

    /// <summary><c>ApplicationDisplayVersion</c> (<c>CFBundleShortVersionString</c>).</summary>
    public string? DisplayVersion { get; init; }

    /// <summary><c>ApplicationVersion</c>: the build number (<c>CFBundleVersion</c>, Android's version code).</summary>
    public string? Version { get; init; }

    /// <summary>The project it was built from, when the IDE built it.</summary>
    public string? Project { get; init; }

    public string? Framework { get; init; }

    /// <summary>
    /// What the build made: Android's <c>.aab</c> and <c>.apk</c>, Windows' <c>.msix</c> or the unpackaged app's folder;
    /// for an <c>.xcarchive</c>, its app bundle.
    /// </summary>
    public IReadOnlyList<string> Packages { get; init; } = [];

    /// <summary>For a Windows archive: whether it is MSIX-packaged (false: an unpackaged app's folder).</summary>
    public bool IsPackaged { get; init; }

    /// <summary>For a Windows MSIX archive: the manifest's <c>Identity/@Publisher</c> a signing certificate's subject must match.</summary>
    public string? Publisher { get; init; }

    public bool IsXcodeArchive => Path.EndsWith(".xcarchive", StringComparison.OrdinalIgnoreCase);

    /// <summary>"1.2 (7)", or what of it is known.</summary>
    public string VersionLabel =>
        DisplayVersion is { Length: > 0 } display ? (Version is { Length: > 0 } build ? $"{display} ({build})" : display) : Version ?? string.Empty;
}

/// <summary>Where archives are kept and how they are read, listed, written and deleted (ADR 0114).</summary>
public sealed class AppArchives(string root, string? xcodeArchives)
{
    public const string ManifestName = "archive.json";

    /// <summary>The IDE's archives folder: <c>&lt;data&gt;/archives</c>.</summary>
    public string Root => root;

    /// <summary>Xcode's archives folder (<c>~/Library/Developer/Xcode/Archives</c>) on macOS, else null.</summary>
    public string? XcodeArchives => xcodeArchives;

    public static string? XcodeArchivesFolder(Codalus.Platform.PlatformKind platform, string home) =>
        platform == Codalus.Platform.PlatformKind.MacOS ? System.IO.Path.Combine(home, "Library", "Developer", "Xcode", "Archives") : null;

    /// <summary>Every archive, newest first: the IDE's, then Xcode's (any app's: the caller keeps its solution's).</summary>
    public IReadOnlyList<AppArchive> List()
    {
        var result = new List<AppArchive>();
        if (Directory.Exists(root))
        {
            foreach (var manifest in SafeFiles(root, ManifestName, SearchOption.AllDirectories))
            {
                if (Read(System.IO.Path.GetDirectoryName(manifest)!) is { } archive)
                {
                    result.Add(archive);
                }
            }
        }

        if (xcodeArchives is not null && Directory.Exists(xcodeArchives))
        {
            foreach (var day in SafeDirectories(xcodeArchives))
            {
                foreach (var folder in SafeDirectories(day).Where(d => d.EndsWith(".xcarchive", StringComparison.OrdinalIgnoreCase)))
                {
                    if (ReadXcodeArchive(folder) is { } archive)
                    {
                        result.Add(archive);
                    }
                }
            }
        }

        return [.. result.OrderByDescending(a => a.Created)];
    }

    /// <summary>A new archive's folder for <paramref name="name"/> on <paramref name="platform"/> (not created).</summary>
    public string NewFolder(string name, MobilePlatform platform, DateTimeOffset now)
    {
        var folder = System.IO.Path.Combine(root, Safe(name), now.ToLocalTime().ToString("yyyy-MM-dd HH-mm-ss", CultureInfo.InvariantCulture) + " " + MobileTargets.Title(platform));
        for (var i = 2; Directory.Exists(folder); i++)
        {
            folder = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(folder)!, System.IO.Path.GetFileName(folder) + " " + i.ToString(CultureInfo.InvariantCulture));
        }

        return folder;
    }

    /// <summary>Writes the archive's <c>archive.json</c> (its packages relative to its folder).</summary>
    public static void Save(AppArchive archive)
    {
        Directory.CreateDirectory(archive.Path);
        var manifest = new ArchiveManifest
        {
            Platform = archive.Platform.ToString(),
            Name = archive.Name,
            Created = archive.Created,
            ApplicationId = archive.ApplicationId,
            DisplayVersion = archive.DisplayVersion,
            Version = archive.Version,
            Project = archive.Project,
            Framework = archive.Framework,
            Packages = [.. archive.Packages.Select(p => System.IO.Path.GetRelativePath(archive.Path, p))],
            IsPackaged = archive.IsPackaged,
            Publisher = archive.Publisher,
        };
        File.WriteAllText(System.IO.Path.Combine(archive.Path, ManifestName), JsonSerializer.Serialize(manifest, ArchivesJsonContext.Default.ArchiveManifest));
    }

    /// <summary>The IDE's archive in <paramref name="folder"/>, or null when it has no readable <c>archive.json</c>.</summary>
    public static AppArchive? Read(string folder)
    {
        try
        {
            var manifest = JsonSerializer.Deserialize(File.ReadAllText(System.IO.Path.Combine(folder, ManifestName)), ArchivesJsonContext.Default.ArchiveManifest);
            if (manifest?.Name is not { Length: > 0 } name || !Enum.TryParse<MobilePlatform>(manifest.Platform, out var platform))
            {
                return null;
            }

            return new AppArchive(folder, platform, name, manifest.Created)
            {
                ApplicationId = manifest.ApplicationId,
                DisplayVersion = manifest.DisplayVersion,
                Version = manifest.Version,
                Project = manifest.Project,
                Framework = manifest.Framework,
                Packages = [.. (manifest.Packages ?? []).Select(p => System.IO.Path.GetFullPath(System.IO.Path.Combine(folder, p)))],
                IsPackaged = manifest.IsPackaged,
                Publisher = manifest.Publisher,
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// An <c>.xcarchive</c>'s app from its <c>Info.plist</c>: name, creation date, bundle id and versions; a Mac app
    /// (Mac Catalyst) when its bundle has <c>Contents</c>, else iOS. Null when it holds no application.
    /// </summary>
    public static AppArchive? ReadXcodeArchive(string folder)
    {
        string text;
        try
        {
            text = File.ReadAllText(System.IO.Path.Combine(folder, "Info.plist"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        if (PropertyList.ParseDictionary(text) is not { } info || PropertyList.String(info, "ApplicationProperties", "ApplicationPath") is not { Length: > 0 } application)
        {
            return null;
        }

        var app = System.IO.Path.Combine(folder, "Products", application.Replace('/', System.IO.Path.DirectorySeparatorChar));
        var mac = Directory.Exists(System.IO.Path.Combine(app, "Contents"));
        var name = info.GetValueOrDefault("Name") as string ?? System.IO.Path.GetFileNameWithoutExtension(application);
        var created = info.GetValueOrDefault("CreationDate") as DateTimeOffset? ?? new DateTimeOffset(Directory.GetCreationTimeUtc(folder), TimeSpan.Zero);
        return new AppArchive(folder, mac ? MobilePlatform.MacCatalyst : MobilePlatform.iOS, name, created)
        {
            ApplicationId = PropertyList.String(info, "ApplicationProperties", "CFBundleIdentifier"),
            DisplayVersion = PropertyList.String(info, "ApplicationProperties", "CFBundleShortVersionString"),
            Version = PropertyList.String(info, "ApplicationProperties", "CFBundleVersion"),
            Packages = [app],
        };
    }

    /// <summary>The newest Xcode archive created at or after <paramref name="since"/> for <paramref name="applicationId"/> (any, when null).</summary>
    public AppArchive? NewestXcodeArchive(DateTimeOffset since, string? applicationId) =>
        List().Where(a => a.IsXcodeArchive && a.Created >= since.AddSeconds(-2) && (applicationId is null || string.Equals(a.ApplicationId, applicationId, StringComparison.Ordinal)))
            .OrderByDescending(a => a.Created)
            .FirstOrDefault();

    /// <summary>
    /// Where a distribution of <paramref name="archive"/> for <paramref name="channel"/> is written: inside the IDE's
    /// archive, or for an Xcode archive beside the IDE's (Xcode's folder holds archives only).
    /// </summary>
    public string DistributionFolder(AppArchive archive, DistributionChannel channel, DateTimeOffset now)
    {
        var parent = archive.IsXcodeArchive
            ? System.IO.Path.Combine(root, Safe(archive.Name), System.IO.Path.GetFileNameWithoutExtension(archive.Path))
            : archive.Path;
        return System.IO.Path.Combine(parent, "Distribution", Distribution.Title(channel) + " " + now.ToLocalTime().ToString("yyyy-MM-dd HH-mm-ss", CultureInfo.InvariantCulture));
    }

    /// <summary>Deletes an archive's folder (and, for an Xcode archive, its distributions beside the IDE's).</summary>
    public void Delete(AppArchive archive)
    {
        Directory.Delete(archive.Path, recursive: true);
        if (archive.IsXcodeArchive)
        {
            var distributions = System.IO.Path.Combine(root, Safe(archive.Name), System.IO.Path.GetFileNameWithoutExtension(archive.Path));
            if (Directory.Exists(distributions))
            {
                Directory.Delete(distributions, recursive: true);
            }
        }
    }

    /// <summary>
    /// The next build number after <paramref name="version"/>: an integer plus one, else its last number plus one
    /// (<c>1.0.3</c> → <c>1.0.4</c>), else <c>1</c>.
    /// </summary>
    public static string NextBuildNumber(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return "1";
        }

        var text = version.Trim();
        var end = text.Length;
        while (end > 0 && char.IsAsciiDigit(text[end - 1]))
        {
            end--;
        }

        return end == text.Length
            ? text + ".1"
            : text[..end] + (long.Parse(text[end..], CultureInfo.InvariantCulture) + 1).ToString(CultureInfo.InvariantCulture);
    }

    private static string Safe(string name) => string.Concat(name.Select(c => System.IO.Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    private static IEnumerable<string> SafeDirectories(string folder)
    {
        try
        {
            return Directory.GetDirectories(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static IEnumerable<string> SafeFiles(string folder, string pattern, SearchOption option)
    {
        try
        {
            // An archive's manifest is two levels down (<app>/<archive>/archive.json); deeper ones are packages' own files.
            return option == SearchOption.AllDirectories
                ? SafeDirectories(folder).SelectMany(SafeDirectories).Select(d => System.IO.Path.Combine(d, pattern)).Where(File.Exists).ToList()
                : Directory.GetFiles(folder, pattern);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}

/// <summary>An archive's <c>archive.json</c>.</summary>
internal sealed class ArchiveManifest
{
    public string? Platform { get; set; }

    public string? Name { get; set; }

    public DateTimeOffset Created { get; set; }

    public string? ApplicationId { get; set; }

    public string? DisplayVersion { get; set; }

    public string? Version { get; set; }

    public string? Project { get; set; }

    public string? Framework { get; set; }

    public List<string>? Packages { get; set; }

    public bool IsPackaged { get; set; }

    public string? Publisher { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ArchiveManifest))]
[JsonSerializable(typeof(SigningIdentityFile))]
internal sealed partial class ArchivesJsonContext : JsonSerializerContext;
