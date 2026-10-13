using Codalus.Platform;

namespace Codalus.Workbench.Plugins;

/// <summary>An installed gallery plugin with a newer version on its source.</summary>
/// <param name="Id">The plugin.</param>
/// <param name="Installed">The version installed.</param>
/// <param name="Available">The newer version.</param>
/// <param name="Source">Its source.</param>
public sealed record PluginUpdate(string Id, string Installed, string Available, string Source);

/// <summary>
/// The plugin browser's model (M22c, ADR 0060): searching the sources, installing, updating and uninstalling. Plugins
/// installed from a gallery remember their source (<see cref="SourceFile"/> in their folder), which updates are checked
/// against. A new plugin loads at once; updates and uninstalls apply at the next start (<see cref="PluginInstaller"/>).
/// </summary>
public sealed class PluginStore(PluginManager manager, PluginGallery gallery, PluginInstaller installer, string pluginsDirectory, PlatformKind platform)
{
    /// <summary>The file in an installed plugin's folder that names the source it came from.</summary>
    public const string SourceFile = ".codalus-source";

    /// <summary>The sources to search (the setting <c>plugins.sources</c>).</summary>
    public Func<IReadOnlyList<string>> Sources { get; init; } = () => [PluginGallery.NuGetOrg];

    /// <summary>Which sources must sign their packages: <c>nugetOrg</c> (default), <c>all</c> or <c>none</c>.</summary>
    public Func<string> SignaturePolicy { get; init; } = () => "nugetOrg";

    /// <summary>Whether prerelease versions are offered and updated to.</summary>
    public Func<bool> Prerelease { get; init; } = () => false;

    /// <summary>Raised when an install, update or uninstall changes what is installed or waiting.</summary>
    public event EventHandler? Changed;

    /// <summary>Whether a package from <paramref name="source"/> must be signed.</summary>
    public bool RequiresSignature(string source) => SignaturePolicy() switch
    {
        "none" => false,
        "all" => true,
        _ => Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.Host.EndsWith("nuget.org", StringComparison.OrdinalIgnoreCase),
    };

    /// <summary>Plugins on every source that match <paramref name="query"/>, the first source's when two have one id.</summary>
    public async Task<IReadOnlyList<GalleryPlugin>> SearchAsync(string query, int take, CancellationToken cancellationToken)
    {
        var sources = Sources();
        var results = await Task.WhenAll(sources.Select(async source =>
        {
            try
            {
                return await gallery.SearchAsync(source, query, Prerelease(), 0, take, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or System.Text.Json.JsonException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                manager.PluginLog.AppendLine($"[gallery] {source}: {ex.Message}");
                return [];
            }
        })).ConfigureAwait(false);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return [.. results.SelectMany(r => r).Where(p => seen.Add(p.Id))];
    }

    /// <summary>Downloads and opens a version of a gallery plugin, checking its signature as its source requires.</summary>
    public async Task<PluginPackage> DownloadAsync(GalleryPlugin plugin, string version, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        var downloads = Path.Combine(pluginsDirectory, ".downloads");
        var package = await gallery.DownloadAsync(plugin.Source, plugin.Id, version, downloads, cancellationToken).ConfigureAwait(false);
        try
        {
            var opened = await installer.OpenAsync(package, RequiresSignature(plugin.Source), cancellationToken).ConfigureAwait(false);
            File.WriteAllText(Path.Combine(opened.Folder, SourceFile), plugin.Source);
            return opened;
        }
        finally
        {
            File.Delete(package);
        }
    }

    /// <summary>
    /// Installs a downloaded package: a new plugin is added to the IDE now (true); an update waits for the next start
    /// (false).
    /// </summary>
    public bool Install(PluginPackage package)
    {
        var folder = installer.Install(package);
        if (folder is not null)
        {
            manager.AddPlugins(PluginDiscovery.Scan([(folder, false)], platform));
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return folder is not null;
    }

    /// <summary>Uninstalls a plugin from the user plugins folder at the next start.</summary>
    public void Uninstall(string id)
    {
        installer.Uninstall(id);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Drops a waiting update or uninstall.</summary>
    public void CancelPending(string id)
    {
        installer.Cancel(id);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Updates and uninstalls waiting for the next start.</summary>
    public IReadOnlyDictionary<string, string?> Pending => installer.Pending;

    /// <summary>The installed plugins that came from a gallery, with their source.</summary>
    public IReadOnlyList<(PluginDescription Plugin, string Source)> FromGalleries =>
        [.. manager.Plugins.Where(p => !p.IsBuiltIn && p.Manifest is not null)
            .Select(p => (Plugin: p, File: Path.Combine(p.InstallPath, SourceFile)))
            .Where(p => File.Exists(p.File) && IsUnder(p.Plugin.InstallPath, pluginsDirectory))
            .Select(p => (p.Plugin, File.ReadAllText(p.File).Trim()))];

    /// <summary>The installed gallery plugins that have a newer version on their source.</summary>
    public async Task<IReadOnlyList<PluginUpdate>> CheckUpdatesAsync(CancellationToken cancellationToken)
    {
        var updates = new List<PluginUpdate>();
        foreach (var (plugin, source) in FromGalleries)
        {
            try
            {
                if (await gallery.NewerVersionAsync(source, plugin.Id, plugin.Manifest!.Version, Prerelease(), cancellationToken).ConfigureAwait(false) is { } newer)
                {
                    updates.Add(new PluginUpdate(plugin.Id, plugin.Manifest.Version, newer, source));
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or System.Text.Json.JsonException)
            {
                manager.PluginLog.AppendLine($"[gallery] Checking {plugin.Id} for updates: {ex.Message}");
            }
        }

        return updates;
    }

    /// <summary>Downloads, checks and stages <paramref name="updates"/> for the next start; returns those staged.</summary>
    public async Task<IReadOnlyList<PluginUpdate>> StageUpdatesAsync(IReadOnlyList<PluginUpdate> updates, CancellationToken cancellationToken)
    {
        var staged = new List<PluginUpdate>();
        foreach (var update in updates)
        {
            try
            {
                var package = await DownloadAsync(new GalleryPlugin(update.Id, update.Available, null, null, null, 0, null, [], update.Source), update.Available, cancellationToken).ConfigureAwait(false);
                installer.Install(package);
                staged.Add(update);
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or IOException or InvalidOperationException)
            {
                manager.PluginLog.AppendLine($"[gallery] Updating {update.Id} to {update.Available}: {ex.Message}");
            }
        }

        if (staged.Count > 0)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return staged;
    }

    private static bool IsUnder(string path, string folder)
    {
        var comparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        return Path.GetFullPath(path).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar, comparison);
    }
}
