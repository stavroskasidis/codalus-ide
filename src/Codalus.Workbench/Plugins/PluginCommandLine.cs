using System.Text.Json;
using Codalus.Platform;

namespace Codalus.Workbench.Plugins;

/// <summary>
/// <c>codalus --list-plugins</c> and <c>codalus --install-plugin</c> (the release track's command line), without a window:
/// the user plugins folder's plugins, and an install from a <c>.nupkg</c> file or the plugin galleries with the same
/// rules as the Plugin Gallery (ADR 0060): the <c>plugins.sources</c>, <c>plugins.requireSignature</c> and
/// <c>plugins.prerelease</c> settings, the signature check, and a running window's plugin replaced at its next start.
/// </summary>
public sealed class PluginCommandLine(string pluginsDirectory, PlatformKind platform, PluginGallery gallery)
{
    /// <summary>The user settings: sources, signature policy and prerelease are read from it.</summary>
    public string? SettingsFile { get; init; }

    /// <summary>Checks signatures (tests use a stand-in); the default is the installer's.</summary>
    public Func<string, CancellationToken, Task<PackageSignature>>? Verify { get; init; }

    /// <summary>The installed plugins, <c>id@version</c>, sorted by id (without <paramref name="versions"/>: just the ids).</summary>
    public IReadOnlyList<string> List(bool versions)
    {
        if (!Directory.Exists(pluginsDirectory))
        {
            return [];
        }

        return
        [
            .. PluginDiscovery.Scan([(pluginsDirectory, false)], platform)
                .OrderBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
                .Select(p => versions && p.Manifest?.Version is { } version ? $"{p.Id}@{version}" : p.Id),
        ];
    }

    /// <summary>
    /// Installs <paramref name="specification"/>: a <c>.nupkg</c> file, or a plugin id with an optional <c>@version</c>
    /// from the first gallery that has it. Writes what it did to <paramref name="output"/>; returns 0, or 1 on failure.
    /// </summary>
    public async Task<int> InstallAsync(string specification, TextWriter output, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(specification);
        ArgumentNullException.ThrowIfNull(output);
        var installer = Verify is { } verify ? new PluginInstaller(pluginsDirectory, platform) { Verify = verify } : new PluginInstaller(pluginsDirectory, platform);
        Directory.CreateDirectory(pluginsDirectory);
        try
        {
            PluginPackage package;
            if (specification.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase) && File.Exists(specification))
            {
                // A file the user names: installed as it is (a gallery's signature rules are for its packages).
                package = await installer.OpenAsync(Path.GetFullPath(specification), requireSignature: false, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var at = specification.LastIndexOf('@');
                var id = at > 0 ? specification[..at] : specification;
                var requested = at > 0 ? specification[(at + 1)..] : null;
                if (await FindAsync(id, requested, cancellationToken).ConfigureAwait(false) is not { } found)
                {
                    await output.WriteLineAsync(requested is null
                        ? $"No plugin '{id}' was found on {string.Join(", ", Sources())}."
                        : $"Version {requested} of '{id}' was not found on {string.Join(", ", Sources())}.").ConfigureAwait(false);
                    return 1;
                }

                var downloaded = await gallery.DownloadAsync(found.Source, id, found.Version, Path.Combine(pluginsDirectory, ".downloads"), cancellationToken).ConfigureAwait(false);
                try
                {
                    package = await installer.OpenAsync(downloaded, RequiresSignature(found.Source), cancellationToken).ConfigureAwait(false);
                    await File.WriteAllTextAsync(Path.Combine(package.Folder, PluginStore.SourceFile), found.Source, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    File.Delete(downloaded);
                }
            }

            var name = $"{package.Plugin.Id} {package.Plugin.Manifest?.Version}".TrimEnd();
            await output.WriteLineAsync(installer.Install(package) is not null
                ? $"Installed {name}."
                : $"{name} replaces the installed version when Codalus IDE next starts.").ConfigureAwait(false);
            return 0;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or HttpRequestException or InvalidOperationException or JsonException or UnauthorizedAccessException)
        {
            await output.WriteLineAsync($"Could not install {specification}: {ex.Message}").ConfigureAwait(false);
            return 1;
        }
    }

    /// <summary>The first source with the plugin, and the version: the one asked for, or the newest (stable unless the setting allows prereleases).</summary>
    private async Task<(string Source, string Version)?> FindAsync(string id, string? requested, CancellationToken cancellationToken)
    {
        foreach (var source in Sources())
        {
            var versions = await gallery.VersionsAsync(source, id, cancellationToken).ConfigureAwait(false);
            if (requested is not null)
            {
                if (versions.FirstOrDefault(v => string.Equals(v, requested, StringComparison.OrdinalIgnoreCase)) is { } exact)
                {
                    return (source, exact);
                }

                continue;
            }

            string? newest = null;
            SemanticVersion? best = null;
            foreach (var version in versions)
            {
                if (SemanticVersion.TryParse(version, out var candidate) && (Prerelease() || candidate.Prerelease is null) && (best is null || candidate > best))
                {
                    (best, newest) = (candidate, version);
                }
            }

            if (newest is not null)
            {
                return (source, newest);
            }
        }

        return null;
    }

    private bool RequiresSignature(string source) => Setting("plugins.requireSignature")?.GetString() switch
    {
        "none" => false,
        "all" => true,
        _ => Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.Host.EndsWith("nuget.org", StringComparison.OrdinalIgnoreCase),
    };

    private List<string> Sources() =>
        Setting("plugins.sources") is { ValueKind: JsonValueKind.Array } sources && sources.EnumerateArray().Select(s => s.GetString()).OfType<string>().ToList() is [_, ..] list
            ? list
            : [PluginGallery.NuGetOrg];

    private bool Prerelease() => Setting("plugins.prerelease") is { ValueKind: JsonValueKind.True };

    private JsonElement? Setting(string key)
    {
        if (SettingsFile is null || !File.Exists(SettingsFile))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(SettingsFile), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty(key, out var value) ? value.Clone() : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return null;
        }
    }
}
