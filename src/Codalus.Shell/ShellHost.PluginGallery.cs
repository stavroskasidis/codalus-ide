using System.Globalization;
using System.Text.Json;
using Avalonia.Controls;
using Codalus.Plugins.Protocol;
using Codalus.Shell.Controls;
using Codalus.Shell.Infrastructure;
using Codalus.Workbench.Commands;
using Codalus.Workbench.Menus;
using Codalus.Workbench.Plugins;
using Codalus.Workbench.Settings;
using Codalus.Workbench.ToolWindows;

namespace Codalus.Shell;

/// <summary>
/// The Plugin Gallery (M22c, ADR 0060): plugins from NuGet sources, searched, installed with their permissions shown
/// first, updated (automatically once a day with <c>plugins.autoUpdate</c>) and uninstalled. A declarative view, like
/// the Source Control Providers window. Updates and uninstalls apply at the next start.
/// </summary>
public sealed partial class ShellHost
{
    public const string PluginGalleryWindow = "plugins.gallery";
    public const string PluginSourcesSetting = "plugins.sources";
    public const string PluginSignatureSetting = "plugins.requireSignature";
    public const string PluginAutoUpdateSetting = "plugins.autoUpdate";
    public const string PluginPrereleaseSetting = "plugins.prerelease";

    private static readonly HttpClient s_galleryHttp = new() { Timeout = TimeSpan.FromSeconds(100), DefaultRequestHeaders = { { "User-Agent", "Codalus-IDE/" + ShellInfo.Version } } };

    private PluginInstaller? _pluginInstaller;
    private PluginStore? _pluginStore;
    private readonly GalleryState _gallery = new();

    /// <summary>What the gallery shows: the query, results, selection, updates and messages.</summary>
    private sealed class GalleryState
    {
        public string Query = string.Empty;
        public IReadOnlyList<GalleryPlugin> Results = [];
        public int Selected = -1;
        public string? Version;
        public IReadOnlyList<PluginUpdate> Updates = [];
        public int SelectedInstalled = -1;
        public int SelectedPending = -1;
        public string? Status;
        public bool Busy;
        public Action? Changed;
    }

    public sealed class PluginGalleryContent
    {
        internal PluginGalleryContent(ShellHost shell) => Shell = shell;

        internal ShellHost Shell { get; }
    }

    private PluginGalleryContent? _galleryContent;

    private PluginGalleryContent PluginGallery() => _galleryContent ??= new PluginGalleryContent(this);

    /// <summary>The installer and store (before plugins are discovered: pending changes apply first), settings, commands and the window.</summary>
    private void InitializePluginGallery()
    {
        _pluginInstaller = new PluginInstaller(Paths.PluginsDirectory, Workbench.Platform);
        _pluginStore = new PluginStore(Plugins, new PluginGallery(s_galleryHttp), _pluginInstaller, Paths.PluginsDirectory, Workbench.Platform)
        {
            Sources = () => Workbench.Settings.GetValue(PluginSourcesSetting) is { ValueKind: JsonValueKind.Array } sources
                ? [.. sources.EnumerateArray().Where(s => s.ValueKind == JsonValueKind.String).Select(s => s.GetString()!)]
                : [Codalus.Workbench.Plugins.PluginGallery.NuGetOrg],
            SignaturePolicy = () => Workbench.Settings.GetString(PluginSignatureSetting, "nugetOrg"),
            Prerelease = () => Workbench.Settings.GetBoolean(PluginPrereleaseSetting),
        };
        _pluginStore.Changed += (_, _) => Ui.Post(() => _gallery.Changed?.Invoke());

        Workbench.Settings.Register(
        [
            SettingDescriptor.Create(PluginSourcesSetting, $"[\"{Codalus.Workbench.Plugins.PluginGallery.NuGetOrg}\"]", SettingType.StringArray, ContributionSource.Shell) with
            {
                Title = "Plugin Sources", Category = "Plugins", Scope = SettingScope.User,
                Description = "NuGet v3 sources (service index URLs) the Plugin Gallery searches for plugins, in order.",
            },
            SettingDescriptor.Create(PluginSignatureSetting, "\"nugetOrg\"", SettingType.String, ContributionSource.Shell) with
            {
                Title = "Require Signed Plugins", Category = "Plugins", Scope = SettingScope.User,
                Description = "Which sources' plugin packages must be signed (checked with dotnet nuget verify).",
                Enum = ["nugetOrg", "all", "none"],
                EnumDescriptions = ["Packages from nuget.org", "Packages from every source", "None"],
            },
            SettingDescriptor.Create(PluginAutoUpdateSetting, "true", SettingType.Boolean, ContributionSource.Shell) with
            {
                Title = "Update Plugins Automatically", Category = "Plugins", Scope = SettingScope.User,
                Description = "Once a day, download the newer versions of plugins installed from the gallery; they apply at the next start.",
            },
            SettingDescriptor.Create(PluginPrereleaseSetting, "false", SettingType.Boolean, ContributionSource.Shell) with
            {
                Title = "Include Prerelease Plugins", Category = "Plugins", Scope = SettingScope.User,
                Description = "Offer and update to prerelease versions of plugins.",
            },
        ]);

        Workbench.ToolWindows.Register(new ToolWindowDescriptor(PluginGalleryWindow, "Plugin Gallery", ToolWindowLocation.Bottom, ToolWindowKind.BuiltIn, ContributionSource.Shell) { Icon = "extensions" });
        Workbench.Commands.Register(
            new CommandDescriptor("plugins.browse", "Browse Plugins…", ContributionSource.Shell) { Category = "Plugins" },
            (_, _) =>
            {
                Ui.Post(() => Layout.ShowToolWindow(PluginGalleryWindow, focus: true));
                return Task.FromResult<JsonElement?>(null);
            });
        Workbench.Commands.Register(
            new CommandDescriptor("plugins.checkUpdates", "Check for Plugin Updates", ContributionSource.Shell) { Category = "Plugins" },
            async (_, _) =>
            {
                await Ui.RunAsync(() => CheckPluginUpdatesAsync(interactive: true)).ConfigureAwait(false);
                return null;
            });
        Workbench.Menus.Add([new MenuItemRule(MenuLocations.MenuBar("tools"), "plugins.browse", ContributionSource.Shell) { Group = "plugins", Order = 99 }]);
    }

    /// <summary>Applies updates and uninstalls that waited for this start. Called first in plugin discovery.</summary>
    private void ApplyPendingPluginChanges()
    {
        if (_pluginInstaller?.ApplyPending() is { Count: > 0 } applied)
        {
            Workbench.Log.AppendLine($"[plugins] Applied the waiting changes of {string.Join(", ", applied)}.");
        }
    }

    // ------------------------------------------------------------------ updates

    private string UpdateStampFile => Path.Combine(Paths.State, "plugin-updates.txt");

    /// <summary>At startup: at most once a day, checks the gallery plugins and (with auto-update) stages newer versions.</summary>
    private async Task CheckPluginUpdatesOnStartupAsync()
    {
        if (_pluginStore is null || _pluginStore.FromGalleries.Count == 0)
        {
            return;
        }

        try
        {
            if (File.Exists(UpdateStampFile) && DateTime.TryParse(await File.ReadAllTextAsync(UpdateStampFile).ConfigureAwait(true), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var last) && DateTime.UtcNow - last < TimeSpan.FromDays(1))
            {
                return;
            }
        }
        catch (IOException)
        {
        }

        await CheckPluginUpdatesAsync(interactive: false).ConfigureAwait(true);
    }

    /// <summary>Checks for updates; with auto-update (or when asked), downloads and stages them for the next start.</summary>
    public async Task CheckPluginUpdatesAsync(bool interactive)
    {
        if (_pluginStore is not { } store)
        {
            return;
        }

        var updates = await Task.Run(() => store.CheckUpdatesAsync(CancellationToken.None)).ConfigureAwait(true);
        try
        {
            Directory.CreateDirectory(Paths.State);
            await File.WriteAllTextAsync(UpdateStampFile, DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)).ConfigureAwait(true);
        }
        catch (IOException)
        {
        }

        _gallery.Updates = [.. updates.Where(u => !store.Pending.ContainsKey(u.Id))];
        _gallery.Changed?.Invoke();
        if (updates.Count == 0)
        {
            if (interactive)
            {
                StatusBar.ShowMessage("Your gallery plugins are up to date.", TimeSpan.FromSeconds(5));
            }

            return;
        }

        if (Workbench.Settings.GetBoolean(PluginAutoUpdateSetting, true))
        {
            await StagePluginUpdatesAsync(updates).ConfigureAwait(true);
        }
        else if (!interactive)
        {
            var notification = Workbench.Notifications.Info($"Plugin updates are available: {string.Join(", ", updates.Select(u => $"{u.Id} {u.Available}"))}.", "Show Updates");
            if (await notification.Result.ConfigureAwait(true) == "Show Updates")
            {
                Layout.ShowToolWindow(PluginGalleryWindow, focus: true);
            }
        }
    }

    private async Task StagePluginUpdatesAsync(IReadOnlyList<PluginUpdate> updates)
    {
        var task = Workbench.Progress.Begin("Updating plugins", "plugins");
        IReadOnlyList<PluginUpdate> staged;
        try
        {
            staged = await Task.Run(() => _pluginStore!.StageUpdatesAsync(updates, CancellationToken.None)).ConfigureAwait(true);
        }
        finally
        {
            task.End();
        }

        _gallery.Updates = [.. _gallery.Updates.Where(u => !staged.Contains(u))];
        _gallery.Changed?.Invoke();
        if (staged.Count > 0 && await Workbench.Notifications.Info($"Plugin updates are ready and apply at the next start: {string.Join(", ", staged.Select(u => $"{u.Id} {u.Available}"))}.", "Restart Now").Result.ConfigureAwait(true) == "Restart Now")
        {
            Restart();
        }
    }

    // ------------------------------------------------------------------ the view

    /// <summary>The gallery as a declarative view.</summary>
    internal ViewNodeDto GalleryRoot()
    {
        var store = _pluginStore!;
        var children = new List<ViewNodeDto>
        {
            new("text") { Text = "Sources: " + string.Join(", ", store.Sources()), Style = "muted" },
            new("stack")
            {
                Orientation = "horizontal",
                Children =
                [
                    new ViewNodeDto("textbox") { Id = "query", Value = _gallery.Query, Placeholder = "Search plugins (Enter)" },
                    new ViewNodeDto("button") { Id = "search", Text = "Search", Style = "primary", Disabled = _gallery.Busy },
                    new ViewNodeDto("button") { Id = "checkUpdates", Text = "Check for Updates", Disabled = _gallery.Busy },
                ],
            },
        };
        if (_gallery.Status is { } status)
        {
            children.Add(new ViewNodeDto("text") { Text = status, Style = "muted" });
        }

        var installed = Plugins.Plugins.ToDictionary(p => p.Id, p => p.Manifest?.Version, StringComparer.OrdinalIgnoreCase);
        if (_gallery.Results.Count > 0)
        {
            children.Add(new ViewNodeDto("table")
            {
                Id = "results",
                Columns = ["Plugin", "Version", "Installed", "Publisher", "Downloads"],
                Rows = [.. _gallery.Results.Select(p => new[]
                {
                    p.Title ?? p.Id, p.Version, installed.GetValueOrDefault(p.Id) ?? string.Empty, p.Authors ?? string.Empty,
                    p.TotalDownloads.ToString("N0", CultureInfo.CurrentCulture),
                })],
            });
        }

        if (_gallery.Selected >= 0 && _gallery.Selected < _gallery.Results.Count)
        {
            var plugin = _gallery.Results[_gallery.Selected];
            var versions = plugin.Versions.Count > 0 ? plugin.Versions.Reverse().ToArray() : [plugin.Version];
            var current = installed.GetValueOrDefault(plugin.Id);
            var pending = store.Pending.ContainsKey(plugin.Id);
            children.Add(new ViewNodeDto("separator"));
            children.Add(new ViewNodeDto("text") { Text = plugin.Title ?? plugin.Id, Style = "heading" });
            if (plugin.Description is { Length: > 0 } description)
            {
                children.Add(new ViewNodeDto("text") { Text = description });
            }

            children.Add(new ViewNodeDto("text") { Text = $"{plugin.Id} · {plugin.Authors} · {plugin.Source}", Style = "muted" });
            children.Add(new ViewNodeDto("stack")
            {
                Orientation = "horizontal",
                Children =
                [
                    new ViewNodeDto("dropdown") { Id = "version", Options = versions, Value = _gallery.Version ?? versions[0] },
                    new ViewNodeDto("button")
                    {
                        Id = "install",
                        Text = pending ? "Waiting for a restart" : current is null ? "Install" : current == (_gallery.Version ?? versions[0]) ? "Installed" : "Install This Version",
                        Style = "primary",
                        Disabled = _gallery.Busy || pending || current == (_gallery.Version ?? versions[0]),
                    },
                ],
            });
            children.Add(new ViewNodeDto("text") { Text = "Its permissions and signature are shown before it installs.", Style = "muted" });
        }

        if (_gallery.Updates.Count > 0)
        {
            children.Add(new ViewNodeDto("separator"));
            children.Add(new ViewNodeDto("text") { Text = $"Updates ({_gallery.Updates.Count})", Style = "heading" });
            children.Add(new ViewNodeDto("table") { Id = "updates", Columns = ["Plugin", "Installed", "Available"], Rows = [.. _gallery.Updates.Select(u => new[] { u.Id, u.Installed, u.Available })] });
            children.Add(new ViewNodeDto("button") { Id = "updateAll", Text = "Update All", Disabled = _gallery.Busy });
        }

        var fromGalleries = store.FromGalleries;
        if (fromGalleries.Count > 0)
        {
            children.Add(new ViewNodeDto("separator"));
            children.Add(new ViewNodeDto("text") { Text = "Installed from a gallery", Style = "heading" });
            children.Add(new ViewNodeDto("table") { Id = "installed", Columns = ["Plugin", "Version", "Source"], Rows = [.. fromGalleries.Select(p => new[] { p.Plugin.DisplayName, p.Plugin.Manifest!.Version, p.Source })] });
            children.Add(new ViewNodeDto("button") { Id = "uninstall", Text = "Uninstall", Disabled = _gallery.SelectedInstalled < 0 || _gallery.SelectedInstalled >= fromGalleries.Count });
        }

        var waiting = store.Pending;
        if (waiting.Count > 0)
        {
            children.Add(new ViewNodeDto("separator"));
            children.Add(new ViewNodeDto("text") { Text = "Waiting for a restart", Style = "heading" });
            children.Add(new ViewNodeDto("table") { Id = "pending", Columns = ["Plugin", "Change"], Rows = [.. waiting.Select(p => new[] { p.Key, p.Value is null ? "uninstall" : "update" })] });
            children.Add(new ViewNodeDto("stack")
            {
                Orientation = "horizontal",
                Children =
                [
                    new ViewNodeDto("button") { Id = "restart", Text = "Restart Now", Style = "primary" },
                    new ViewNodeDto("button") { Id = "cancelPending", Text = "Cancel This Change", Disabled = _gallery.SelectedPending < 0 || _gallery.SelectedPending >= waiting.Count },
                ],
            });
        }

        return new ViewNodeDto("stack") { Children = [.. children] };
    }

    /// <summary>What the user did in the gallery.</summary>
    internal async Task OnPluginGalleryEventAsync(string id, string kind, string? value, int? row)
    {
        var store = _pluginStore!;
        switch (id, kind)
        {
            case ("query", "change"):
                _gallery.Query = value ?? string.Empty;
                await SearchPluginGalleryAsync().ConfigureAwait(true);
                break;
            case ("search", "click"):
                await SearchPluginGalleryAsync().ConfigureAwait(true);
                break;
            case ("checkUpdates", "click"):
                await CheckPluginUpdatesAsync(interactive: true).ConfigureAwait(true);
                break;
            case ("results", "select" or "activate"):
                _gallery.Selected = row ?? -1;
                _gallery.Version = null;
                break;
            case ("version", "change"):
                _gallery.Version = value;
                break;
            case ("install", "click") when _gallery.Selected >= 0 && _gallery.Selected < _gallery.Results.Count:
                var plugin = _gallery.Results[_gallery.Selected];
                await InstallFromGalleryAsync(plugin, _gallery.Version ?? (plugin.Versions.Count > 0 ? plugin.Versions[^1] : plugin.Version)).ConfigureAwait(true);
                break;
            case ("updateAll", "click"):
                await StagePluginUpdatesAsync(_gallery.Updates).ConfigureAwait(true);
                break;
            case ("installed", "select" or "activate"):
                _gallery.SelectedInstalled = row ?? -1;
                break;
            case ("uninstall", "click") when store.FromGalleries is var installedPlugins && _gallery.SelectedInstalled >= 0 && _gallery.SelectedInstalled < installedPlugins.Count:
                var target = installedPlugins[_gallery.SelectedInstalled].Plugin;
                if (await Dialogs.ConfirmAsync($"Uninstall {target.DisplayName}?", "It is removed at the next start.", "Uninstall", danger: true).ConfigureAwait(true))
                {
                    store.Uninstall(target.Id);
                    _gallery.SelectedInstalled = -1;
                }

                break;
            case ("pending", "select" or "activate"):
                _gallery.SelectedPending = row ?? -1;
                break;
            case ("cancelPending", "click") when store.Pending.Keys.ToList() is var ids && _gallery.SelectedPending >= 0 && _gallery.SelectedPending < ids.Count:
                store.CancelPending(ids[_gallery.SelectedPending]);
                _gallery.SelectedPending = -1;
                break;
            case ("restart", "click"):
                Restart();
                return;
            default:
                return;
        }

        _gallery.Changed?.Invoke();
    }

    private async Task SearchPluginGalleryAsync()
    {
        _gallery.Busy = true;
        _gallery.Status = "Searching…";
        _gallery.Changed?.Invoke();
        try
        {
            var query = _gallery.Query;
            _gallery.Results = await Task.Run(() => _pluginStore!.SearchAsync(query, 50, CancellationToken.None)).ConfigureAwait(true);
            _gallery.Selected = -1;
            _gallery.Status = _gallery.Results.Count == 0 ? "No plugins found." : null;
        }
        finally
        {
            _gallery.Busy = false;
        }
    }

    /// <summary>Downloads and checks a plugin, shows its permissions and publisher, and installs it when the user agrees.</summary>
    public async Task<bool> InstallFromGalleryAsync(GalleryPlugin plugin, string version)
    {
        var store = _pluginStore!;
        _gallery.Busy = true;
        _gallery.Status = $"Downloading {plugin.Id} {version}…";
        _gallery.Changed?.Invoke();
        PluginPackage package;
        try
        {
            package = await Task.Run(() => store.DownloadAsync(plugin, version, CancellationToken.None)).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or IOException or InvalidOperationException or System.IO.InvalidDataException)
        {
            _gallery.Status = null;
            Workbench.Notifications.Error($"{plugin.Id} {version} can't be installed: {ex.Message}");
            return false;
        }
        finally
        {
            _gallery.Busy = false;
            _gallery.Changed?.Invoke();
        }

        var permissions = package.Permissions.Count == 0
            ? "It declares no permissions."
            : $"It declares these permissions: {string.Join(", ", package.Permissions.Select(PermissionText))}. They say what it does; they aren't enforced, since a .NET plugin runs with your rights.";
        var publisher = package.Signature.Publisher is { } signer ? $"Signed by {signer}." : package.Signature.Signed ? "Signed by its source." : "Not signed.";
        var problem = package.Plugin.State is PluginState.Invalid or PluginState.Incompatible ? $"\n\nIt can't load: {package.Plugin.Error}" : string.Empty;
        if (problem.Length > 0 || !await Dialogs.ConfirmAsync($"Install {package.Plugin.DisplayName} {version}?", $"From {plugin.Source}, by {package.Authors ?? "unknown"}. {publisher}\n\n{permissions}", "Install").ConfigureAwait(true))
        {
            if (problem.Length > 0)
            {
                Workbench.Notifications.Error($"{plugin.Id} {version} can't be installed.{problem}");
            }

            try
            {
                Directory.Delete(package.Folder, recursive: true);
            }
            catch (IOException)
            {
            }

            _gallery.Status = null;
            _gallery.Changed?.Invoke();
            return false;
        }

        var now = store.Install(package);
        _gallery.Status = now ? $"{package.Plugin.DisplayName} {version} is installed." : $"{package.Plugin.DisplayName} {version} is installed at the next start.";
        _gallery.Changed?.Invoke();
        PluginList.Refresh();
        return true;
    }

    private static string PermissionText(string permission) => permission switch
    {
        "network" => "the network",
        "processes" => "starting programs",
        "fileSystem" => "files outside the workspace",
        "secrets" => "stored secrets",
        _ => permission,
    };

    /// <summary>The window: a declarative view rebuilt when the gallery's state changes.</summary>
    internal sealed class PluginGalleryView : UserControl
    {
        private readonly DeclarativeView _view = new();

        public PluginGalleryView(ShellHost shell)
        {
            Content = new ScrollViewer { Content = _view };
            shell._gallery.Changed = () => _view.Root = shell.GalleryRoot();
            shell.Plugins.PluginsChanged += (_, _) => Ui.Post(() => _view.Root = shell.GalleryRoot());
            _view.Raised += (id, kind, value, row) => _ = shell.OnPluginGalleryEventAsync(id, kind, value, row);
            _view.Root = shell.GalleryRoot();
        }

        /// <summary>The renderer (tests).</summary>
        public DeclarativeView View => _view;
    }
}
