using System.Text.Json;
using System.Text.RegularExpressions;
using Codalus.Platform;
using Codalus.Plugins.Protocol;
using Codalus.Workbench.Commands;
using Codalus.Workbench.Keybindings;
using Codalus.Workbench.Menus;

namespace Codalus.Workbench.Plugins;

public enum PluginState
{
    /// <summary>Valid and compatible; contributions applied; not running yet (lazy).</summary>
    Ready,

    /// <summary>The manifest is malformed or refers to missing files.</summary>
    Invalid,

    /// <summary><c>engines.codalus</c> does not include the running API version.</summary>
    Incompatible,

    Active,

    /// <summary>Loading or activation failed, or its host crashed repeatedly.</summary>
    Failed,

    /// <summary>Turned off by the user (<c>plugins.disabled</c>, M23b): discovered, but nothing it contributes is applied.</summary>
    Disabled,
}

/// <summary>A discovered plugin: its manifest, where it lives, and its state in this session.</summary>
public sealed partial class PluginDescription
{
    internal PluginDescription(PluginManifest? manifest, string installPath, bool isBuiltIn)
    {
        Manifest = manifest;
        InstallPath = installPath;
        IsBuiltIn = isBuiltIn;
    }

    public PluginManifest? Manifest { get; }

    public string InstallPath { get; }

    public bool IsBuiltIn { get; }

    public string Id => Manifest?.Id ?? Path.GetFileName(InstallPath);

    public string DisplayName => Manifest?.DisplayName ?? Id;

    public ContributionSource Source => ContributionSource.Plugin(Id, IsBuiltIn);

    /// <summary>
    /// <c>builtin</c>, <c>default</c>, or <c>dedicated:&lt;id&gt;</c> (ADR 0003); with <c>workspace:</c> before it for a
    /// plugin that runs on the workspace's machine (ADR 0057), which in a remote window is the server.
    /// </summary>
    public string HostGroup => IsDebugged ? DebugGroup : (RunsOnWorkspace ? WorkspaceGroupPrefix : string.Empty)
        + (Manifest?.Isolation == "dedicated" ? "dedicated:" + Id : IsBuiltIn ? "builtin" : "default");

    /// <summary>The host group of plugins under development with a debugger (<see cref="PluginManager.DebugHostFile"/>).</summary>
    public const string DebugGroup = "debug";

    /// <summary>A plugin under development in a debugged host of its own (M22b).</summary>
    public bool IsDebugged { get; internal set; }

    /// <summary>How long its last activation took (M22d, performance attribution).</summary>
    public TimeSpan? ActivationTime { get; internal set; }

    private readonly Lock _commandsGate = new();
    private int _commandCalls;
    private TimeSpan _commandTime;
    private TimeSpan _slowestCommand;

    /// <summary>Its commands run this session: how many, how long in all, and the slowest (M22d).</summary>
    public (int Calls, TimeSpan Total, TimeSpan Slowest) Commands
    {
        get
        {
            lock (_commandsGate)
            {
                return (_commandCalls, _commandTime, _slowestCommand);
            }
        }
    }

    internal void RecordCommand(TimeSpan elapsed)
    {
        lock (_commandsGate)
        {
            _commandCalls++;
            _commandTime += elapsed;
            if (elapsed > _slowestCommand)
            {
                _slowestCommand = elapsed;
            }
        }
    }

    public const string WorkspaceGroupPrefix = "workspace:";

    /// <summary>The manifest's <c>runsOn</c> is <c>workspace</c>.</summary>
    public bool RunsOnWorkspace => Manifest?.RunsOn == "workspace";

    public PluginState State { get; internal set; }

    public string? Error { get; internal set; }

    public IReadOnlyList<string> Warnings { get; private set; } = [];

    public IReadOnlySet<string> ActivationEvents { get; private set; } = new HashSet<string>();

    public bool IsUsable => State is PluginState.Ready or PluginState.Active;

    /// <summary>No code: its contributions apply at discovery and it never loads in a plugin host.</summary>
    public bool IsDeclarative => Manifest is { Entry: null };

    public override string ToString() => $"{Id} ({State})";

    /// <summary>
    /// Reads and validates <c>codalus-plugin.json</c> in <paramref name="directory"/>. Never throws: problems
    /// become <see cref="PluginState.Invalid"/> or <see cref="PluginState.Incompatible"/> with an <see cref="Error"/>.
    /// </summary>
    public static PluginDescription Read(string directory, bool isBuiltIn, PlatformKind platform, bool allowProposedApi = false)
    {
        var path = Path.Combine(directory, PluginManifest.FileName);
        PluginManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize(ManifestLocalization.Apply(File.ReadAllText(path), directory, Localization.Localizer.Current), PluginJsonContext.Default.PluginManifest);
        }
        catch (Exception ex) when (ex is JsonException or IOException or NotSupportedException)
        {
            return new PluginDescription(null, directory, isBuiltIn) { State = PluginState.Invalid, Error = $"Cannot read {PluginManifest.FileName}: {ex.Message}" };
        }

        var plugin = new PluginDescription(manifest, directory, isBuiltIn);
        plugin.Validate(platform, allowProposedApi);
        return plugin;
    }

    private void Validate(PlatformKind platform, bool allowProposedApi)
    {
        var warnings = new List<string>();
        Warnings = warnings;
        var m = Manifest!;
        string? Fail(string error)
        {
            State = PluginState.Invalid;
            Error = error;
            return error;
        }

        if (!IdPattern().IsMatch(m.Id ?? string.Empty))
        {
            Fail($"Invalid id '{m.Id}': expected 'publisher.name' in lower case.");
            return;
        }

        if (!SemanticVersion.TryParse(m.Version, out _))
        {
            Fail($"Invalid version '{m.Version}': expected SemVer.");
            return;
        }

        if (m.Entry is null)
        {
            // Declarative: only contributions that need no plugin code (API 0.2.4, ADR 0016; snippets since 0.2.5).
            var contributes = m.Contributes;
            if (contributes is null || (contributes.Languages is null && contributes.Grammars is null && contributes.LanguageServers is null && contributes.Settings is null && contributes.Snippets is null && contributes.Themes is null && contributes.IconThemes is null && contributes.Debuggers is null))
            {
                Fail("A plugin without an entry must contribute languages, grammars, language servers, snippets, themes, debuggers or settings.");
                return;
            }
        }
        else if (m.Entry.Runtime != "dotnet" || string.IsNullOrWhiteSpace(m.Entry.Assembly))
        {
            Fail("entry must be { \"runtime\": \"dotnet\", \"assembly\": \"<file>.dll\" }.");
            return;
        }
        else if (!File.Exists(Path.Combine(InstallPath, m.Entry.Assembly)))
        {
            Fail($"Entry assembly not found: {m.Entry.Assembly}");
            return;
        }

        foreach (var permission in m.Permissions ?? [])
        {
            if (permission is not ("network" or "processes" or "fileSystem" or "secrets"))
            {
                warnings.Add($"Unknown permission '{permission}': expected network, processes, fileSystem or secrets.");
            }
        }

        if (m.Isolation is not (null or "shared" or "dedicated"))
        {
            Fail($"Invalid isolation '{m.Isolation}': expected 'shared' or 'dedicated'.");
            return;
        }

        if (m.RunsOn is not (null or "ui" or "workspace"))
        {
            Fail($"Invalid runsOn '{m.RunsOn}': expected 'ui' or 'workspace'.");
            return;
        }

        // Built-ins may use proposed APIs in every build (ADR 0010).
        if (m.EnableProposedApi && !allowProposedApi && !IsBuiltIn)
        {
            Fail("Uses proposed APIs, which are only available in development builds (--enable-proposed-api).");
            return;
        }

        if (!VersionRange.TryParse(m.Engines?.Codalus, out var range, out var rangeError))
        {
            Fail($"Invalid engines.codalus: {rangeError}");
            return;
        }

        if (!range!.Satisfies(SemanticVersion.Parse(PluginMethods.ApiVersion)))
        {
            State = PluginState.Incompatible;
            Error = $"Requires plugin API {range} but this version of Codalus IDE provides {PluginMethods.ApiVersion}.";
            return;
        }

        var events = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in m.ActivationEvents ?? [])
        {
            if (e is "onStartup" or "onSolutionOpened" or "onTestProjects" or "onDebug" || ActivationEventPattern().IsMatch(e))
            {
                events.Add(e);
            }
            else
            {
                warnings.Add($"Ignoring unknown activation event '{e}'.");
            }
        }

        var c = m.Contributes;
        var commandIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var command in c?.Commands ?? [])
        {
            if (string.IsNullOrWhiteSpace(command.Id) || string.IsNullOrWhiteSpace(command.Title) || !commandIds.Add(command.Id))
            {
                warnings.Add($"Ignoring invalid or duplicate command '{command.Id}'.");
            }
        }

        foreach (var (location, items) in c?.Menus ?? [])
        {
            if (!IsKnownMenuLocation(location))
            {
                warnings.Add($"Ignoring unknown menu location '{location}'.");
                continue;
            }

            foreach (var item in items.Where(i => !commandIds.Contains(i.Command)))
            {
                warnings.Add($"Menu '{location}' refers to '{item.Command}', which this plugin does not declare.");
            }
        }

        foreach (var binding in c?.Keybindings ?? [])
        {
            var problems = new List<string>();
            KeybindingService.TryParseRule(binding, KeybindingSource.Plugin, platform, Id, problems);
            warnings.AddRange(problems);
        }

        foreach (var window in c?.ToolWindows ?? [])
        {
            if (window.Location is not (null or "left" or "right" or "bottom") || window.Kind is not (null or "tree" or "markdown" or "view"))
            {
                warnings.Add($"Tool window '{window.Id}': location must be left/right/bottom and kind tree/markdown/view.");
            }

            // Opening a contributed view always activates its plugin.
            events.Add("onView:" + window.Id);
        }

        foreach (var (key, setting) in c?.Settings?.Properties ?? [])
        {
            if (Settings.SettingTypes.Parse(setting.Type) is null)
            {
                warnings.Add($"Setting '{key}': unknown type '{setting.Type}'.");
            }
        }

        // A session asked of a contributed authentication provider activates its plugin (API 1.0).
        foreach (var provider in c?.Authentication ?? [])
        {
            if (!string.IsNullOrWhiteSpace(provider.Id))
            {
                events.Add("onAuthenticationRequest:" + provider.Id);
            }
        }

        // Opening a file in a contributed custom editor activates its plugin (API 1.0).
        foreach (var editor in c?.CustomEditors ?? [])
        {
            if (!string.IsNullOrWhiteSpace(editor.ViewType))
            {
                events.Add("onCustomEditor:" + editor.ViewType);
            }
        }

        // Contributed commands implicitly activate their plugin.
        foreach (var id in commandIds)
        {
            events.Add("onCommand:" + id);
        }

        // Hosting providers (API 0.2.6): the shell activates the plugin when it needs the provider (ADR 0031).
        foreach (var provider in c?.HostingProviders ?? [])
        {
            if (string.IsNullOrWhiteSpace(provider.Id) || string.IsNullOrWhiteSpace(provider.Label) || !HostingIdPattern().IsMatch(provider.Id))
            {
                warnings.Add($"Ignoring hosting provider '{provider.Id}': it needs an id (lower case letters, digits and dashes) and a label.");
                continue;
            }

            if (m.Entry is null)
            {
                warnings.Add($"Hosting provider '{provider.Id}' needs plugin code (an entry).");
                continue;
            }

            events.Add("onHostingProvider:" + provider.Id);
        }

        ActivationEvents = events;
        State = PluginState.Ready;
    }

    /// <summary>
    /// The locations plugins contribute to, and since M34a their submenus (<c>menuBar/edit/advanced</c>,
    /// <c>editor/context/git</c>…); an unknown submenu simply never shows.
    /// </summary>
    private static bool IsKnownMenuLocation(string location)
    {
        if (location is MenuLocations.CommandPalette or MenuLocations.ToolWindowTitle)
        {
            return true;
        }

        if (location.StartsWith(MenuLocations.EditorContext, StringComparison.Ordinal) || location.StartsWith(MenuLocations.SolutionExplorerContext, StringComparison.Ordinal))
        {
            var rest = location.StartsWith(MenuLocations.EditorContext, StringComparison.Ordinal) ? location[MenuLocations.EditorContext.Length..] : location[MenuLocations.SolutionExplorerContext.Length..];
            return rest.Length == 0 || SubmenuPath().IsMatch(rest);
        }

        if (!location.StartsWith(MenuLocations.MenuBarPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var path = location[MenuLocations.MenuBarPrefix.Length..];
        var slash = path.IndexOf('/', StringComparison.Ordinal);
        var top = slash < 0 ? path : path[..slash];
        return top is "file" or "edit" or "view" or "git" or "project" or "build" or "debug" or "test" or "analyze" or "tools" or "window" or "help"
            && (slash < 0 || SubmenuPath().IsMatch(path[slash..]));
    }

    [GeneratedRegex("^(/[A-Za-z][A-Za-z0-9]*)+$")]
    private static partial Regex SubmenuPath();

    [GeneratedRegex("^[a-z0-9][a-z0-9-]*\\.[a-z0-9][a-z0-9.-]*$")]
    private static partial Regex IdPattern();

    [GeneratedRegex("^(onCommand|onView|onSettingChanged|onHostingProvider):[A-Za-z0-9._/-]+$")]
    private static partial Regex ActivationEventPattern();

    [GeneratedRegex("^[a-z0-9][a-z0-9-]*$")]
    private static partial Regex HostingIdPattern();
}

/// <summary>Finds plugins in folders: each subfolder (or the folder itself) with a codalus-plugin.json.</summary>
public static class PluginDiscovery
{
    /// <summary>
    /// Scans <paramref name="roots"/> in order; when two folders declare the same id, the first wins
    /// (so <c>--plugin-dir</c> development copies override installed ones).
    /// </summary>
    public static IReadOnlyList<PluginDescription> Scan(IEnumerable<(string Directory, bool IsBuiltIn)> roots, PlatformKind platform, bool allowProposedApi = false)
    {
        var result = new List<PluginDescription>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (root, isBuiltIn) in roots)
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            IEnumerable<string> candidates = File.Exists(Path.Combine(root, PluginManifest.FileName))
                ? [root]
                : Directory.EnumerateDirectories(root).Where(d => File.Exists(Path.Combine(d, PluginManifest.FileName))).Order(StringComparer.Ordinal);
            foreach (var directory in candidates)
            {
                var plugin = PluginDescription.Read(directory, isBuiltIn, platform, allowProposedApi);
                if (seen.Add(plugin.Id))
                {
                    result.Add(plugin);
                }
            }
        }

        return result;
    }
}
