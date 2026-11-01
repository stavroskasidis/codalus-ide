using System.Collections.Concurrent;
using System.Text.Json;
using Codalus.Plugins.Protocol;
using Codalus.Rpc;
using Codalus.Workbench.Commands;
using Codalus.Workbench.Context;
using Codalus.Workbench.Keybindings;
using Codalus.Workbench.Menus;
using Codalus.Workbench.Notifications;
using Codalus.Workbench.Settings;
using Codalus.Workbench.StatusBar;
using Codalus.Workbench.ToolWindows;
using Codalus.Workbench.Localization;

namespace Codalus.Workbench.Plugins;

public sealed class TreeViewRefreshEventArgs(string viewId, string? handle) : EventArgs
{
    public string ViewId { get; } = viewId;

    /// <summary>The element whose children changed, or null for the whole view.</summary>
    public string? Handle { get; } = handle;
}

/// <summary>UI prompts plugins can request; the shell implements them (quick pick, input box).</summary>
public interface IPluginPrompts
{
    /// <summary>Returns the index of the chosen item, or null when cancelled.</summary>
    Task<int?> PickAsync(IReadOnlyList<QuickPickItemDto> items, string? placeholder, string source, CancellationToken cancellationToken);

    /// <summary>Returns the entered text, or null when cancelled. <paramref name="browse"/>: <c>folder</c> or <c>file</c> for a path's Browse button, or null.</summary>
    Task<string?> InputAsync(string title, string? prompt, string? value, string? browse, string source, CancellationToken cancellationToken);
}

/// <summary>
/// Where <c>runsOn: workspace</c> plugins run in a remote window (ADR 0057): <see cref="Place"/> turns this machine's
/// plugin host into the server's (null when it can't run there), and <see cref="PluginPath"/> maps a plugin's folder
/// to its copy on the server.
/// </summary>
public sealed record WorkspacePlacement(Func<ProcessLaunchInfo, ProcessLaunchInfo?> Place, Func<string, string> PluginPath);

/// <summary>A plugin's source control provider and what it shows.</summary>
public sealed record SourceControlInfo(string Id, string Label, string Root, string PluginId)
{
    public IReadOnlyList<ScmGroupDto> Groups { get; init; } = [];

    public string? InputPlaceholder { get; init; }

    public string? AcceptLabel { get; init; }

    public string? Status { get; init; }
}

/// <summary>A plugin's custom editor: the files it opens, by name pattern (<c>*.todo.json</c>, <c>TODO</c>).</summary>
public sealed record ContributedCustomEditor(string ViewType, string DisplayName, IReadOnlyList<string> FilenamePatterns, string PluginId)
{
    public bool Matches(string path)
    {
        var name = Path.GetFileName(path);
        return FilenamePatterns.Any(pattern => System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(pattern, name, ignoreCase: true));
    }
}

/// <summary>A plugin's debug adapter: the launch configuration type it serves, and how to start it (<c>dotnet</c> for a <c>.dll</c>).</summary>
public sealed record ContributedDebugger(string Type, string Label, string Command, IReadOnlyList<string> Args, IReadOnlyList<string> Languages, string PluginId)
{
    /// <summary>The adapter's process: a <c>.dll</c> runs with <c>dotnet</c>.</summary>
    public (string Program, IReadOnlyList<string> Arguments) Start() =>
        Command.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? ("dotnet", [Command, .. Args]) : (Command, Args);
}

/// <summary>A plugin's color theme: its id (the setting's value), label, base theme (<c>light</c>, <c>dark</c> or <c>highContrast</c>) and file.</summary>
public sealed record ContributedTheme(string Id, string Label, string Base, string File, string PluginId)
{
    /// <summary>VS Code's <c>uiTheme</c> as our base theme; null for an unknown one.</summary>
    public static string? BaseOf(string? uiTheme) => uiTheme switch
    {
        "vs" or "hc-light" => "light",
        "vs-dark" => "dark",
        "hc-black" => "highContrast",
        _ => null,
    };
}

/// <summary>Locates <c>codalus-pluginhost</c>: <c>CODALUS_PLUGINHOST</c>, then <c>pluginhost/</c> next to the shell.</summary>
public static class PluginHostLocator
{
    public const string EnvironmentVariable = "CODALUS_PLUGINHOST";

    public static ProcessLaunchInfo? Resolve()
    {
        var candidates = new List<string>();
        if (Environment.GetEnvironmentVariable(EnvironmentVariable) is { Length: > 0 } fromEnv)
        {
            candidates.Add(fromEnv);
        }

        var exe = OperatingSystem.IsWindows() ? "codalus-pluginhost.exe" : "codalus-pluginhost";
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "pluginhost", exe));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "pluginhost", "codalus-pluginhost.dll"));
        return candidates.FirstOrDefault(File.Exists) is { } path ? ProcessLaunchInfo.FromPath(path) : null;
    }
}

/// <summary>
/// Runs plugin host v0 (ADR 0003) on the shell side. Contributions are applied declaratively when plugins are
/// added; host processes start lazily per host group on the first activation event; contributed commands are
/// proxied to the plugin (activating it first); host crashes are reported and the host is restarted with its
/// active plugins re-activated.
/// </summary>
public sealed class PluginManager : IAsyncDisposable
{
    // A guard against a hung plugin, generous enough for a first activation on a busy machine (a build, a CI runner).
    private static readonly TimeSpan s_activationTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Debug Plugin in Development Instance (M22b, <c>--debug-plugin-host</c>): the plugins in <see cref="DevelopmentDirs"/>
    /// run in a host of their own (<see cref="PluginDescription.DebugGroup"/>) that writes its process id to this file and
    /// waits for the debugger; their activation has no time limit, since the developer may be stopped at a breakpoint.
    /// </summary>
    public string? DebugHostFile { get; init; }

    /// <summary>The folders of the plugins under development (<c>--plugin-dir</c>).</summary>
    public IReadOnlyList<string> DevelopmentDirs { get; init; } = [];

    /// <summary>How long a tree view waits for its plugin's answer; tests on loaded machines wait longer.</summary>
    internal TimeSpan TreeTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>The setting listing the ids of plugins the user turned off (M23b); it applies when the IDE starts.</summary>
    public const string DisabledSetting = "plugins.disabled";

    private readonly WorkbenchServices _workbench;
    private readonly Func<string, ProcessLaunchInfo?> _resolveHost;
    private readonly string _shellVersion;
    private readonly SupervisionPolicy? _hostPolicy;
    private readonly List<PluginDescription> _plugins = [];
    private readonly ConcurrentDictionary<string, HostConnection> _hosts = new(StringComparer.Ordinal);
    private WorkspacePlacement? _workspacePlacement;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _activationLocks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _markdown = new(StringComparer.Ordinal);

    // Host groups whose failure to start was reported (M49): once each, not once per plugin, until a retry.
    private readonly ConcurrentDictionary<string, byte> _hostStartReported = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private IProposedApiHost? _proposedApi;

    public PluginManager(WorkbenchServices workbench, Func<string, ProcessLaunchInfo?> resolveHost, string shellVersion, SupervisionPolicy? hostPolicy = null)
    {
        _hostPolicy = hostPolicy;
        _workbench = workbench;
        _resolveHost = resolveHost;
        _shellVersion = shellVersion;
        _workbench.Settings.Changed += OnSettingsChanged;
        _workbench.Commands.Executed += OnCommandExecuted;
    }

    public IReadOnlyList<PluginDescription> Plugins
    {
        get
        {
            lock (_gate)
            {
                return [.. _plugins];
            }
        }
    }

    public event EventHandler? PluginsChanged;

    /// <summary>
    /// A plugin host wasn't started because the .NET runtime it needs isn't installed (M49). Called once per host group
    /// (until <see cref="RetryFailedHostsAsync"/>), on any thread; it returns whether it told the user (the shell's one
    /// notification that offers the runtime). When it didn't, or there's no handler, the host's failure is shown as an
    /// error.
    /// </summary>
    public Func<HostRuntimeMissingException, bool>? HostRuntimeMissing { get; set; }

    /// <summary>The shell's prompt UI; without it, plugin prompts are cancelled.</summary>
    public IPluginPrompts? Prompts { get; set; }

    /// <summary>The workspace, build and debug services behind the proposed APIs (ADR 0010).</summary>
    public IProposedApiHost? ProposedApi
    {
        get => _proposedApi;
        set
        {
            if (_proposedApi is not null)
            {
                _proposedApi.Notification -= OnProposedNotification;
            }

            _proposedApi = value;
            if (value is not null)
            {
                value.Notification += OnProposedNotification;
            }
        }
    }

    /// <summary>Receives test controllers (the Test Explorer's model).</summary>
    public ITestSink? TestSink { get; set; }

    /// <summary>Receives hosting providers (ADR 0031).</summary>
    public IHostingSink? HostingSink { get; set; }

    /// <summary>Receives language model providers (ADR 0062).</summary>
    public ILanguageModelSink? LanguageModelSink { get; set; }

    /// <summary>The language model providers the usable plugins declare (<c>contributes.languageModelProviders</c>), with their plugin.</summary>
    public IReadOnlyList<(PluginDescription Plugin, ManifestLanguageModelProvider Provider)> LanguageModelProviderDeclarations =>
        [.. Plugins.Where(p => p.IsUsable && !p.IsDeclarative)
            .SelectMany(p => (p.Manifest!.Contributes?.LanguageModelProviders ?? []).Where(m => p.ActivationEvents.Contains("onLanguageModelProvider:" + m.Id)).Select(m => (p, m)))];

    /// <summary>Where plugins' secrets go (the OS credential store); without it, secret requests fail.</summary>
    public Secrets.SecretStorage? Secrets { get; set; }

    /// <summary>The hosting providers the usable plugins declare (<c>contributes.hostingProviders</c>), with their plugin.</summary>
    public IReadOnlyList<(PluginDescription Plugin, ManifestHostingProvider Provider)> HostingProviderDeclarations =>
        [.. Plugins.Where(p => p.IsUsable && !p.IsDeclarative)
            .SelectMany(p => (p.Manifest!.Contributes?.HostingProviders ?? []).Where(h => p.ActivationEvents.Contains("onHostingProvider:" + h.Id)).Select(h => (p, h)))];

    /// <summary>The plugins' debugger visualizers (proposed API 0.2.7).</summary>
    public Debugging.DebugVisualizerRegistry DebugVisualizers { get; } = new();

    public event EventHandler<TreeViewRefreshEventArgs>? TreeViewRefreshed;

    public event EventHandler<string>? MarkdownChanged;

    /// <summary>Output → Plugins: activations, warnings, the gallery.</summary>
    public Output.OutputChannel PluginLog => _workbench.Output.GetOrCreate("codalus.plugins", "Plugins");

    /// <summary>Registers plugins and applies their declarative contributions. Problems are logged and notified once.</summary>
    private static bool IsUnder(string path, string folder)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        var comparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        return full.Equals(root, comparison) || full.StartsWith(root + Path.DirectorySeparatorChar, comparison);
    }

    /// <summary>Says in the plugin log that copies of retired built-in plugins were left out (<see cref="PluginDiscovery.RetiredIds"/>).</summary>
    public void ReportRetired(IEnumerable<(string Id, string Directory)> retired)
    {
        foreach (var (id, directory) in retired)
        {
            PluginLog.AppendLine(id.Equals("codalus.templates", StringComparison.OrdinalIgnoreCase)
                ? $"An old copy of the Templates plugin at {directory} is ignored: New Project is part of Codalus IDE since 0.6.0."
                : $"An old copy of the plugin {id} at {directory} is ignored: its features are part of Codalus IDE.");
        }
    }

    public void AddPlugins(IEnumerable<PluginDescription> plugins)
    {
        var problems = new List<string>();
        foreach (var plugin in plugins)
        {
            plugin.IsDebugged = DebugHostFile is not null && !plugin.IsBuiltIn && DevelopmentDirs.Any(dir => IsUnder(plugin.InstallPath, dir));
            lock (_gate)
            {
                _plugins.Add(plugin);
            }

            foreach (var warning in plugin.Warnings)
            {
                PluginLog.AppendLine($"[{plugin.Id}] warning: {warning}");
            }

            if (plugin.IsUsable && IsTurnedOff(plugin.Id))
            {
                plugin.State = PluginState.Disabled;
                PluginLog.AppendLine($"[{plugin.Id}] turned off ({DisabledSetting})");
                continue;
            }

            if (!plugin.IsUsable)
            {
                PluginLog.AppendLine($"[{plugin.Id}] {plugin.State}: {plugin.Error}");
                problems.Add($"{plugin.DisplayName}: {plugin.Error}");
                continue;
            }

            ApplyContributions(plugin);
            PluginLog.AppendLine($"[{plugin.Id}] {plugin.Manifest!.Version} ready ({plugin.HostGroup} host; activation: {string.Join(", ", plugin.ActivationEvents.Order(StringComparer.Ordinal))})");
        }

        if (problems.Count > 0)
        {
            var message = problems.Count == 1 ? $"A plugin was not loaded. {problems[0]}" : $"{problems.Count} plugins were not loaded. See Output → Plugins.";
            _ = ShowAndHandleAsync(_workbench.Notifications.Warning(message, "Show Log"));
        }

        PluginsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Whether the user turned the plugin off (<see cref="DisabledSetting"/>): in their settings, or for the open
    /// workspace (M22c). The two lists add up.
    /// </summary>
    public bool IsTurnedOff(string pluginId) => TurnedOff(SettingScope.User).Contains(pluginId) || TurnedOff(SettingScope.Workspace).Contains(pluginId);

    /// <summary>The plugins turned off in one settings layer.</summary>
    public IReadOnlySet<string> TurnedOff(SettingScope scope) =>
        _workbench.Settings.GetLayerValue(DisabledSetting, scope) is { ValueKind: System.Text.Json.JsonValueKind.Array } ids
            ? ids.EnumerateArray().Where(id => id.ValueKind == System.Text.Json.JsonValueKind.String).Select(id => id.GetString()!).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    private async Task ShowAndHandleAsync(Notification notification)
    {
        if (await notification.Result.ConfigureAwait(false) == "Show Log")
        {
            _workbench.Output.Show(PluginLog);
        }
    }

    // ------------------------------------------------------------------ contributions

    private void ApplyContributions(PluginDescription plugin)
    {
        var manifest = plugin.Manifest!;
        var source = plugin.Source;
        var contributions = manifest.Contributes;

        foreach (var command in contributions?.Commands ?? [])
        {
            if (string.IsNullOrWhiteSpace(command.Id) || string.IsNullOrWhiteSpace(command.Title))
            {
                continue;
            }

            // The IDE's own commands stay its own (ADR 0082): a plugin's menus and keys may run them, not replace them.
            if (_workbench.Commands.Find(command.Id) is { } existing && existing.Source == ContributionSource.Shell)
            {
                PluginLog.AppendLine($"[{plugin.Id}] declares {command.Id}, which Codalus IDE owns; ignored.");
                continue;
            }

            var descriptor = new CommandDescriptor(command.Id, command.Title, source)
            {
                Category = command.Category ?? plugin.DisplayName,
                Enablement = WhenClause.TryParse(command.Enablement, out var enablement, out _) ? enablement : WhenClause.Always,
            };
            _workbench.Commands.Register(descriptor, (args, ct) => ExecutePluginCommandAsync(plugin, command.Id, args, ct));
        }

        var menuItems = new List<MenuItemRule>();
        foreach (var (location, items) in contributions?.Menus ?? [])
        {
            foreach (var item in items)
            {
                menuItems.Add(new MenuItemRule(location, item.Command, source)
                {
                    Group = item.Group ?? "plugins",
                    Order = item.Order,
                    When = WhenClause.TryParse(item.When, out var when, out _) ? when : WhenClause.Always,
                });
            }
        }

        _workbench.Menus.Add(menuItems);

        var keybindings = (contributions?.Keybindings ?? [])
            .Select(k => KeybindingService.TryParseRule(k, KeybindingSource.Plugin, _workbench.Platform, plugin.Id, null))
            .OfType<KeybindingRule>()
            .ToList();
        _workbench.Keybindings.SetPluginRules(plugin.Id, keybindings);

        foreach (var window in contributions?.ToolWindows ?? [])
        {
            _workbench.ToolWindows.Register(new ToolWindowDescriptor(
                window.Id,
                window.Title,
                window.Location switch { "left" => ToolWindowLocation.Left, "bottom" => ToolWindowLocation.Bottom, _ => ToolWindowLocation.Right },
                window.Kind switch { "markdown" => ToolWindowKind.Markdown, "view" => ToolWindowKind.View, _ => ToolWindowKind.Tree },
                source)
            {
                Icon = window.Icon,
            });
        }

        ApplyLanguageContributions(plugin);

        if (contributions?.Settings?.Properties is { } properties)
        {
            var descriptors = new List<SettingDescriptor>();
            foreach (var (key, setting) in properties)
            {
                if (SettingTypes.Parse(setting.Type) is not { } type)
                {
                    continue;
                }

                if (_workbench.Settings.Find(key) is { } owned && owned.Source == ContributionSource.Shell)
                {
                    PluginLog.AppendLine($"[{plugin.Id}] declares the setting {key}, which Codalus IDE owns; ignored.");
                    continue;
                }

                var defaultValue = setting.Default ?? DefaultFor(type);
                descriptors.Add(new SettingDescriptor(key, type, defaultValue, source)
                {
                    Category = contributions.Settings.Title ?? plugin.DisplayName,
                    Description = setting.Description,
                    Enum = setting.Enum,
                    EnumDescriptions = setting.EnumDescriptions,
                    Minimum = setting.Minimum,
                    Maximum = setting.Maximum,
                    Scope = setting.Scope == "user" ? SettingScope.User : SettingScope.Workspace,
                    Order = setting.Order,
                });
            }

            _workbench.Settings.Register(descriptors);
        }
    }

    /// <summary>The Properties window's providers of the plugins that load (API 0.2.8, ADR 0033).</summary>
    public IReadOnlyList<(string PluginId, ManifestPropertyProvider Provider)> PropertyProviders =>
        [.. Plugins.Where(p => p.State is PluginState.Ready or PluginState.Active)
            .SelectMany(p => (p.Manifest?.Contributes?.PropertyProviders ?? []).Where(x => !string.IsNullOrWhiteSpace(x.Command)).Select(x => (p.Id, x)))];

    /// <summary>External Tools' macros of the plugins that load (API 0.2.8, ADR 0033).</summary>
    public IReadOnlyList<(string PluginId, ManifestToolMacro Macro)> ToolMacros =>
        [.. Plugins.Where(p => p.State is PluginState.Ready or PluginState.Active)
            .SelectMany(p => (p.Manifest?.Contributes?.ExternalToolMacros ?? []).Where(x => !string.IsNullOrWhiteSpace(x.Name) && !string.IsNullOrWhiteSpace(x.Command)).Select(x => (p.Id, x)))];

    /// <summary>
    /// Color themes of the plugins that load (API 1.0, M22a): those with an id, a known base and a file that exists.
    /// The shell offers them with its own.
    /// </summary>
    public IReadOnlyList<ContributedTheme> Themes =>
        [.. Plugins.Where(p => p.State is PluginState.Ready or PluginState.Active)
            .SelectMany(p => (p.Manifest?.Contributes?.Themes ?? [])
                .Where(t => !string.IsNullOrWhiteSpace(t.Id) && !string.IsNullOrWhiteSpace(t.Path) && ContributedTheme.BaseOf(t.UiTheme) is not null)
                .Select(t => new ContributedTheme(t.Id, string.IsNullOrWhiteSpace(t.Label) ? t.Id : t.Label, ContributedTheme.BaseOf(t.UiTheme)!, Path.Combine(p.InstallPath, t.Path), p.Id))
                .Where(t => File.Exists(t.File)))];

    /// <summary>File icon themes of the plugins that load (API 1.0, M22a): id, label and file.</summary>
    public IReadOnlyList<(string Id, string Label, string File, string PluginId)> IconThemes =>
        [.. Plugins.Where(p => p.State is PluginState.Ready or PluginState.Active)
            .SelectMany(p => (p.Manifest?.Contributes?.IconThemes ?? [])
                .Where(t => !string.IsNullOrWhiteSpace(t.Id) && !string.IsNullOrWhiteSpace(t.Path) && File.Exists(Path.Combine(p.InstallPath, t.Path)))
                .Select(t => (t.Id, string.IsNullOrWhiteSpace(t.Label) ? t.Id : t.Label, Path.Combine(p.InstallPath, t.Path), p.Id)))];

    /// <summary>Debug adapters of the plugins that load (API 1.0, M22a), by the launch configuration type they serve.</summary>
    public IReadOnlyList<ContributedDebugger> Debuggers =>
        [.. Plugins.Where(p => p.State is PluginState.Ready or PluginState.Active)
            .SelectMany(p => (p.Manifest?.Contributes?.Debuggers ?? [])
                .Where(d => !string.IsNullOrWhiteSpace(d.Type) && !string.IsNullOrWhiteSpace(d.Command))
                .Select(d =>
                {
                    string Resolve(string text) => text.Replace("${pluginDir}", p.InstallPath, StringComparison.Ordinal);
                    var command = Resolve(d.Command);
                    var local = Path.Combine(p.InstallPath, command);
                    return new ContributedDebugger(d.Type, string.IsNullOrWhiteSpace(d.Label) ? d.Type : d.Label, File.Exists(local) ? local : command, [.. (d.Args ?? []).Select(Resolve)], d.Languages ?? [], p.Id);
                }))];

    /// <summary>Custom tools of the plugins that load (API 0.2.8, ADR 0035).</summary>
    public IReadOnlyList<(string PluginId, ManifestCustomTool Tool)> CustomTools =>
        [.. Plugins.Where(p => p.State is PluginState.Ready or PluginState.Active)
            .SelectMany(p => (p.Manifest?.Contributes?.CustomTools ?? []).Where(x => !string.IsNullOrWhiteSpace(x.Name) && !string.IsNullOrWhiteSpace(x.Command)).Select(x => (p.Id, x)))];

    /// <summary>Languages, grammars and language servers (ADR 0016). They need no plugin code, so they apply at discovery.</summary>
    private void ApplyLanguageContributions(PluginDescription plugin)
    {
        var contributions = plugin.Manifest!.Contributes;
        var folder = plugin.InstallPath;
        var source = plugin.Id;
        string Resolve(string path) => path.Replace("${pluginDir}", folder, StringComparison.Ordinal);
        foreach (var language in contributions?.Languages ?? [])
        {
            if (string.IsNullOrWhiteSpace(language.Id))
            {
                continue;
            }

            Languages.LanguageConfiguration? configuration = null;
            if (language.Configuration is { Length: > 0 } configurationPath)
            {
                try
                {
                    configuration = Languages.LanguageConfiguration.Parse(File.ReadAllText(Path.Combine(folder, Resolve(configurationPath))));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
                {
                    PluginLog.AppendLine($"[{plugin.Id}] language '{language.Id}': {ex.Message}");
                }
            }

            _workbench.LanguageDefinitions.Register(new Languages.LanguageDefinition(language.Id, language.Aliases?.FirstOrDefault() ?? language.Id)
            {
                Aliases = language.Aliases ?? [],
                Extensions = language.Extensions ?? [],
                FileNames = language.Filenames ?? [],
                FileNamePatterns = language.FilenamePatterns ?? [],
                FirstLine = language.FirstLine,
                Configuration = configuration,
                Source = source,
            });
        }

        foreach (var grammar in contributions?.Grammars ?? [])
        {
            if (string.IsNullOrWhiteSpace(grammar.ScopeName) || string.IsNullOrWhiteSpace(grammar.Path))
            {
                continue;
            }

            _workbench.LanguageDefinitions.RegisterGrammar(new Languages.GrammarDefinition(grammar.ScopeName, grammar.Language, Path.Combine(folder, Resolve(grammar.Path)))
            {
                EmbeddedLanguages = grammar.EmbeddedLanguages,
                Source = source,
            });
        }

        var snippets = new List<Snippets.Snippet>();
        foreach (var file in contributions?.Snippets ?? [])
        {
            try
            {
                snippets.AddRange(Snippets.SnippetRegistry.ParseJson(File.ReadAllText(Path.Combine(folder, Resolve(file.Path))), file.Language, plugin.Id));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                PluginLog.AppendLine($"[{plugin.Id}] snippets '{file.Path}': {ex.Message}");
            }
        }

        if (snippets.Count > 0)
        {
            _workbench.Snippets.Set(plugin.Id, snippets);
        }

        foreach (var server in contributions?.LanguageServers ?? [])
        {
            if (string.IsNullOrWhiteSpace(server.Id) || server.Languages is not { Length: > 0 } || string.IsNullOrWhiteSpace(server.Command)
                || server.Transport is not (null or "stdio"))
            {
                PluginLog.AppendLine($"[{plugin.Id}] language server '{server.Id}': needs an id, languages and a command over stdio.");
                continue;
            }

            var command = Resolve(server.Command);
            var local = Path.Combine(folder, command);
            var arguments = (server.Args ?? []).Select(Resolve).ToArray();
            ProcessLaunchInfo Launch() => File.Exists(local)
                ? ProcessLaunchInfo.FromPath(local, arguments)
                : new ProcessLaunchInfo(command, arguments);
            _workbench.LanguageServers.Register(new Languages.LanguageServiceDescriptor(server.Id, server.Name ?? plugin.DisplayName, [], null, Launch)
            {
                Languages = server.Languages,
                InitializationOptions = server.InitializationOptions,
                SettingsSection = server.SettingsSection,
                Source = source,
            });
        }
    }

    private static JsonElement DefaultFor(SettingType type)
    {
        using var doc = JsonDocument.Parse(type switch
        {
            SettingType.Boolean => "false",
            SettingType.Integer or SettingType.Number => "0",
            SettingType.StringArray => "[]",
            _ => "\"\"",
        });
        return doc.RootElement.Clone();
    }

    // ------------------------------------------------------------------ activation

    /// <summary>Activates plugins at startup that declare <c>onStartup</c>. Call after the window is interactive.</summary>
    public Task ActivateStartupPluginsAsync(CancellationToken cancellationToken) =>
        ActivateByEventAsync("onStartup", cancellationToken);

    /// <summary>
    /// Activates the plugins under development with a debugger (<see cref="DebugHostFile"/>) at once, so their host
    /// starts and the debugger can attach before the developer uses them.
    /// </summary>
    public async Task ActivateDebuggedPluginsAsync(CancellationToken cancellationToken)
    {
        var targets = Plugins.Where(p => p.IsUsable && p.IsDebugged && !p.IsDeclarative).ToList();
        await Task.WhenAll(targets.Select(p => ActivateAsync(p, "onDebugPlugin", cancellationToken))).ConfigureAwait(false);
    }

    /// <summary>A plugin-contributed tool window was opened.</summary>
    public Task OnViewOpenedAsync(string viewId, CancellationToken cancellationToken) =>
        ActivateByEventAsync("onView:" + viewId, cancellationToken);

    public async Task ActivateByEventAsync(string activationEvent, CancellationToken cancellationToken)
    {
        var targets = Plugins.Where(p => p.IsUsable && p.ActivationEvents.Contains(activationEvent)).ToList();
        await Task.WhenAll(targets.Select(p => ActivateAsync(p, activationEvent, cancellationToken))).ConfigureAwait(false);
    }

    /// <summary>Activates <paramref name="plugin"/> once (concurrent callers share the activation). Returns false on failure.</summary>
    public async Task<bool> ActivateAsync(PluginDescription plugin, string reason, CancellationToken cancellationToken)
    {
        if (plugin.State == PluginState.Active)
        {
            return true;
        }

        if (!plugin.IsUsable)
        {
            return false;
        }

        // Turned off for this workspace (M22c): discovered before the workspace's settings were read, so it is checked here.
        if (!plugin.IsBuiltIn && TurnedOff(SettingScope.Workspace).Contains(plugin.Id))
        {
            PluginLog.AppendLine($"[{plugin.Id}] turned off for this workspace ({DisabledSetting}); not activated");
            return false;
        }

        if (plugin.IsDeclarative)
        {
            plugin.State = PluginState.Active;
            return true;
        }

        var gate = _activationLocks.GetOrAdd(plugin.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (plugin.State == PluginState.Active)
            {
                return true;
            }

            if (!plugin.IsUsable)
            {
                return false;
            }

            var host = GetHost(plugin.HostGroup);
            if (host is null)
            {
                return Fail(plugin, "The plugin host (codalus-pluginhost) was not found.");
            }

            await host.EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
            if (!plugin.IsUsable)
            {
                return false; // loading failed
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(plugin.IsDebugged ? Timeout.InfiniteTimeSpan : s_activationTimeout);
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            ActivatePluginResult result;
            try
            {
                result = await host.Connection.InvokeAsync(
                    PluginMethods.ActivatePlugin, new ActivatePluginParams(plugin.Id, reason),
                    PluginJsonContext.Default.ActivatePluginParams, PluginJsonContext.Default.ActivatePluginResult,
                    timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return Fail(plugin, $"Activation did not finish within {s_activationTimeout.TotalSeconds:0} seconds.");
            }

            if (!result.Activated)
            {
                return Fail(plugin, result.Error ?? "Activation failed.");
            }

            plugin.State = PluginState.Active;
            host.Active.Add(plugin.Id);
            var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started);
            plugin.ActivationTime = elapsed;
            PluginLog.AppendLine($"[{plugin.Id}] activated ({reason}) in {elapsed.TotalMilliseconds:0} ms");
            PluginsChanged?.Invoke(this, EventArgs.Empty);
            if (elapsed >= SlowActivation && !plugin.IsDebugged)
            {
                SlowPlugin?.Invoke(this, plugin);
            }

            return true;
        }
        catch (JsonRpcConnectionLostException ex) when (ex is HostRuntimeMissingException or HostExitedException { DuringStartup: true })
        {
            return FailWithHost(plugin, ex);
        }
        catch (Exception ex) when (ex is JsonRpcConnectionLostException or JsonRpcRemoteException or InvalidOperationException
                                       or System.ComponentModel.Win32Exception or IOException)
        {
            return Fail(plugin, ex.Message);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// The plugin's host couldn't start (M49): the plugin fails with the host's reason, and the host's failure is reported
    /// once for its group rather than once per plugin.
    /// </summary>
    private bool FailWithHost(PluginDescription plugin, JsonRpcConnectionLostException error)
    {
        plugin.Error = error.Message;
        plugin.FailedWithItsHost = true;
        plugin.ActivationFailedWithItsHost = true; // its activation was asked for: a retry activates it again
        plugin.State = PluginState.Failed;
        PluginLog.AppendLine($"[{plugin.Id}] failed: its plugin host couldn't start: {error.Message}");
        ReportHostStartFailure(plugin.HostGroup, error);
        PluginsChanged?.Invoke(this, EventArgs.Empty);
        return false;
    }

    private void ReportHostStartFailure(string group, JsonRpcConnectionLostException error)
    {
        if (!_hostStartReported.TryAdd(group, 0))
        {
            return;
        }

        if (error is HostRuntimeMissingException missing && HostRuntimeMissing is { } handler && handler(missing))
        {
            return;
        }

        _ = ShowAndHandleAsync(_workbench.Notifications.Error(Localizer.Format("msg.pluginManager.pluginHostCouldntStart", "Plugin host '{0}' couldn't start: {1}", group, error.Message), "Show Log"));
    }

    /// <summary>
    /// Makes the plugins that failed because their host couldn't start (M49) usable again, once what was missing is
    /// there (the .NET runtime was installed), and activates again those whose activation was asked for: their hosts
    /// start again with the IDE's environment as it is now. The others (failed with their host's crash while their own
    /// activation events hadn't fired) wait for their events.
    /// </summary>
    public async Task RetryFailedHostsAsync(CancellationToken cancellationToken)
    {
        _hostStartReported.Clear();
        var failed = Plugins.Where(p => p is { State: PluginState.Failed, FailedWithItsHost: true }).ToList();
        if (failed.Count == 0)
        {
            return;
        }

        var activate = failed.Where(p => p.ActivationFailedWithItsHost).ToList();
        foreach (var plugin in failed)
        {
            plugin.State = PluginState.Ready;
            plugin.Error = null;
            plugin.FailedWithItsHost = false;
            plugin.ActivationFailedWithItsHost = false;
        }

        PluginLog.AppendLine($"Starting the plugin hosts again for {string.Join(", ", activate.Select(p => p.Id))}; ready again: {string.Join(", ", failed.Except(activate).Select(p => p.Id))}");
        PluginsChanged?.Invoke(this, EventArgs.Empty);
        await Task.WhenAll(activate.Select(p => ActivateAsync(p, "retry", cancellationToken))).ConfigureAwait(false);
    }

    private bool Fail(PluginDescription plugin, string error)
    {
        plugin.Error = error;
        plugin.State = PluginState.Failed;
        PluginLog.AppendLine($"[{plugin.Id}] failed: {error}");
        _ = ShowAndHandleAsync(_workbench.Notifications.Error(Localizer.Format("msg.pluginManager.pluginFailed", "Plugin '{0}' failed: {1}", plugin.DisplayName, error), "Show Log"));
        PluginsChanged?.Invoke(this, EventArgs.Empty);
        return false;
    }

    private async Task<JsonElement?> ExecutePluginCommandAsync(PluginDescription plugin, string commandId, IReadOnlyList<JsonElement> args, CancellationToken cancellationToken)
    {
        if (!await ActivateAsync(plugin, "onCommand:" + commandId, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException($"Plugin '{plugin.DisplayName}' is not available: {plugin.Error}");
        }

        var host = GetHost(plugin.HostGroup)!;
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            var result = await host.Connection.InvokeAsync(
                PluginMethods.ExecuteCommand, new ExecuteCommandParams(commandId, [.. args]),
                PluginJsonContext.Default.ExecuteCommandParams, PluginJsonContext.Default.ExecuteCommandResult,
                cancellationToken).ConfigureAwait(false);
            return result.Result;
        }
        finally
        {
            plugin.RecordCommand(System.Diagnostics.Stopwatch.GetElapsedTime(started));
        }
    }

    // ------------------------------------------------------------------ views

    /// <summary>Children of a tree item (null handle = roots); activates the contributing plugin first.</summary>
    public async Task<IReadOnlyList<TreeItemDto>> GetTreeChildrenAsync(string viewId, string? handle, CancellationToken cancellationToken)
    {
        await OnViewOpenedAsync(viewId, cancellationToken).ConfigureAwait(false);
        var owner = Plugins.FirstOrDefault(p => p.State == PluginState.Active && p.ActivationEvents.Contains("onView:" + viewId));
        if (owner is null || GetHost(owner.HostGroup) is not { IsRunning: true } host)
        {
            return [];
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TreeTimeout);
        try
        {
            var result = await host.Connection.InvokeAsync(
                PluginMethods.GetTreeChildren, new GetTreeChildrenParams(viewId, handle),
                PluginJsonContext.Default.GetTreeChildrenParams, PluginJsonContext.Default.GetTreeChildrenResult,
                timeout.Token).ConfigureAwait(false);
            return result.Items;
        }
        catch (JsonRpcRemoteException ex) when (ex.Code == PluginErrorCodes.ViewNotFound)
        {
            return []; // the plugin has not registered a provider (yet)
        }
    }

    public string? GetMarkdown(string viewId) => _markdown.GetValueOrDefault(viewId);

    // ------------------------------------------------------------------ declarative views and custom editors (API 1.0)

    private readonly ConcurrentDictionary<string, (string PluginId, ViewNodeDto Root)> _views = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _customEditorOwners = new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, (string PluginId, PluginFileSystem FileSystem, IDisposable Mount)> _fileSystems = new(StringComparer.Ordinal);

    /// <summary>The file system schemes plugins serve (API 1.0), with the root each shows under.</summary>
    public IReadOnlyList<(string Scheme, string Root, string PluginId)> FileSystems =>
        [.. _fileSystems.Select(f => (f.Key, f.Value.FileSystem.Root, f.Value.PluginId)).OrderBy(f => f.Key, StringComparer.Ordinal)];

    /// <summary>A plugin started or stopped serving a file system.</summary>
    public event EventHandler? FileSystemsChanged;

    private void RegisterFileSystem(string pluginId, string scheme)
    {
        var fileSystem = new PluginFileSystem(scheme, () => Find(pluginId) is { } plugin && GetHost(plugin.HostGroup) is { IsRunning: true } host ? host.Connection : null);
        var mount = Codalus.Workbench.FileSystem.FileSystems.Mount(fileSystem.Root, fileSystem);
        if (_fileSystems.TryRemove(scheme, out var previous))
        {
            previous.Mount.Dispose();
        }

        _fileSystems[scheme] = (pluginId, fileSystem, mount);
        PluginLog.AppendLine($"[{pluginId}] serves the file system '{scheme}' at {fileSystem.Root}");
        FileSystemsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void UnregisterFileSystems(Func<string, string, bool> which)
    {
        foreach (var (scheme, entry) in _fileSystems.Where(f => which(f.Key, f.Value.PluginId)).ToList())
        {
            // The mount goes first: once FileSystems no longer lists it, its root is the local disk again.
            entry.Mount.Dispose();
            _fileSystems.TryRemove(new KeyValuePair<string, (string, PluginFileSystem, IDisposable)>(scheme, entry));
        }

        FileSystemsChanged?.Invoke(this, EventArgs.Empty);
    }

    // ------------------------------------------------------------------ source control providers (API 1.0)

    private readonly ConcurrentDictionary<string, SourceControlInfo> _sourceControls = new(StringComparer.Ordinal);

    /// <summary>The source control providers plugins register, with their latest state.</summary>
    public IReadOnlyList<SourceControlInfo> SourceControls => [.. _sourceControls.Values.OrderBy(s => s.Label, StringComparer.CurrentCultureIgnoreCase)];

    /// <summary>A provider came, went or changed its state.</summary>
    public event EventHandler? SourceControlsChanged;

    private JsonRpcConnection? SourceControlConnection(string providerId) =>
        _sourceControls.TryGetValue(providerId, out var info) && Find(info.PluginId) is { } plugin && GetHost(plugin.HostGroup) is { IsRunning: true } host ? host.Connection : null;

    /// <summary>Commits with <paramref name="message"/> through the provider.</summary>
    public async Task AcceptSourceControlAsync(string providerId, string message, CancellationToken cancellationToken)
    {
        var connection = SourceControlConnection(providerId) ?? throw new InvalidOperationException($"The source control provider '{providerId}' isn't running.");
        await connection.InvokeAsync(PluginMethods.ScmAccept, new ScmRequestParams(providerId) { Message = message }, PluginJsonContext.Default.ScmRequestParams, PluginJsonContext.Default.EmptyResult, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A file as the provider's repository has it; null for a new one.</summary>
    public async Task<string?> GetSourceControlOriginalAsync(string providerId, string path, CancellationToken cancellationToken)
    {
        var connection = SourceControlConnection(providerId) ?? throw new InvalidOperationException($"The source control provider '{providerId}' isn't running.");
        return (await connection.InvokeAsync(PluginMethods.ScmOriginal, new ScmRequestParams(providerId) { Path = path }, PluginJsonContext.Default.ScmRequestParams, PluginJsonContext.Default.ScmOriginalResult, cancellationToken).ConfigureAwait(false)).Text;
    }

    // ------------------------------------------------------------------ authentication providers (API 1.0)

    private readonly ConcurrentDictionary<string, (string PluginId, string Label)> _authProviders = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _authConsents = new(StringComparer.Ordinal);

    /// <summary>The authentication providers plugins offer.</summary>
    public IReadOnlyList<(string Id, string Label, string PluginId)> AuthenticationProviders =>
        [.. _authProviders.Select(p => (p.Key, p.Value.Label, p.Value.PluginId)).OrderBy(p => p.Label, StringComparer.CurrentCultureIgnoreCase)];

    /// <summary>A provider came, went, or its sessions changed (its id).</summary>
    public event EventHandler<string>? AuthenticationChanged;

    /// <summary>Asks the user to allow what the message says; the shell sets it. Without it, another plugin's provider is refused.</summary>
    public Func<string, Task<bool>>? AskConsent { get; set; }

    private async Task<HostConnection?> AuthenticationHostAsync(string providerId, CancellationToken cancellationToken)
    {
        if (!_authProviders.ContainsKey(providerId))
        {
            // Its plugin activates on the request; the registration follows the activation.
            await ActivateByEventAsync("onAuthenticationRequest:" + providerId, cancellationToken).ConfigureAwait(false);
            for (var waited = 0; waited < 50 && !_authProviders.ContainsKey(providerId); waited++)
            {
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }
        }

        return _authProviders.TryGetValue(providerId, out var provider) && Find(provider.PluginId) is { } plugin && GetHost(plugin.HostGroup) is { IsRunning: true } host ? host : null;
    }

    /// <summary>The provider's sessions (the Accounts page).</summary>
    public async Task<IReadOnlyList<AuthSessionDto>> GetAuthenticationSessionsAsync(string providerId, CancellationToken cancellationToken) =>
        await AuthenticationHostAsync(providerId, cancellationToken).ConfigureAwait(false) is { } host
            ? (await host.Connection.InvokeAsync(PluginMethods.AuthGetSessions, new AuthRequestParams(providerId), PluginJsonContext.Default.AuthRequestParams, PluginJsonContext.Default.AuthSessionsResult, cancellationToken).ConfigureAwait(false)).Sessions
            : [];

    /// <summary>Signs a session out.</summary>
    public async Task SignOutAsync(string providerId, string sessionId, CancellationToken cancellationToken)
    {
        if (await AuthenticationHostAsync(providerId, cancellationToken).ConfigureAwait(false) is { } host)
        {
            await host.Connection.InvokeAsync(PluginMethods.AuthRemoveSession, new AuthRequestParams(providerId) { SessionId = sessionId }, PluginJsonContext.Default.AuthRequestParams, PluginJsonContext.Default.EmptyResult, cancellationToken).ConfigureAwait(false);
            AuthenticationChanged?.Invoke(this, providerId);
        }
    }

    /// <summary>
    /// A plugin's request for a session: an existing one with the scopes, or a new sign-in when asked. A plugin using
    /// another plugin's provider needs the user's consent, once per session of the IDE.
    /// </summary>
    internal async Task<AuthSessionDto?> GetSessionForAsync(AuthGetSessionParams request, CancellationToken cancellationToken)
    {
        if (await AuthenticationHostAsync(request.ProviderId, cancellationToken).ConfigureAwait(false) is not { } host)
        {
            return null;
        }

        var (providerPlugin, label) = _authProviders[request.ProviderId];
        var consumer = Find(request.PluginId)?.DisplayName ?? request.PluginId;
        var consentKey = request.PluginId + "|" + request.ProviderId;
        async Task<bool> ConsentAsync(string message)
        {
            if (request.PluginId == providerPlugin || _authConsents.ContainsKey(consentKey))
            {
                return true;
            }

            if (AskConsent is not { } ask || !await ask(message).ConfigureAwait(false))
            {
                return false;
            }

            _authConsents[consentKey] = 0;
            return true;
        }

        var sessions = (await host.Connection.InvokeAsync(PluginMethods.AuthGetSessions, new AuthRequestParams(request.ProviderId) { Scopes = request.Scopes },
            PluginJsonContext.Default.AuthRequestParams, PluginJsonContext.Default.AuthSessionsResult, cancellationToken).ConfigureAwait(false)).Sessions;
        if (sessions.FirstOrDefault(s => request.Scopes.All(scope => s.Scopes.Contains(scope, StringComparer.Ordinal))) is { } existing)
        {
            return await ConsentAsync($"'{consumer}' wants to use your {label} account ({existing.Account}).").ConfigureAwait(false) ? existing : null;
        }

        if (!request.CreateIfNone || !await ConsentAsync($"'{consumer}' wants to sign you in to {label}.").ConfigureAwait(false))
        {
            return null;
        }

        var created = (await host.Connection.InvokeAsync(PluginMethods.AuthCreateSession, new AuthRequestParams(request.ProviderId) { Scopes = request.Scopes },
            PluginJsonContext.Default.AuthRequestParams, PluginJsonContext.Default.AuthSessionResult, cancellationToken).ConfigureAwait(false)).Session;
        AuthenticationChanged?.Invoke(this, request.ProviderId);
        return created;
    }

    /// <summary>A declarative view changed (its id).</summary>
    public event EventHandler<string>? ViewChanged;

    /// <summary>A custom editor's plugin gave its file new text.</summary>
    public event EventHandler<(string ViewId, string Text)>? CustomEditorTextChanged;

    /// <summary>A declarative view's content, or null before its plugin set it.</summary>
    public ViewNodeDto? GetView(string viewId) => _views.TryGetValue(viewId, out var view) ? view.Root : null;

    /// <summary>Sends what the user did to the plugin that set the view. False when it isn't running.</summary>
    public async Task<bool> SendViewEventAsync(ViewEventParams viewEvent, CancellationToken cancellationToken)
    {
        if (!_views.TryGetValue(viewEvent.ViewId, out var view) || Find(view.PluginId) is not { } plugin || GetHost(plugin.HostGroup) is not { IsRunning: true } host)
        {
            return false;
        }

        try
        {
            await host.Connection.InvokeAsync(PluginMethods.ViewEvent, viewEvent, PluginJsonContext.Default.ViewEventParams, PluginJsonContext.Default.EmptyResult, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is JsonRpcRemoteException or JsonRpcConnectionLostException or IOException)
        {
            PluginLog.AppendLine($"[{plugin.Id}] view event {viewEvent.ElementId}/{viewEvent.Kind} failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>Custom editors of the plugins that load (API 1.0, M22a).</summary>
    public IReadOnlyList<ContributedCustomEditor> CustomEditors =>
        [.. Plugins.Where(p => p.IsUsable)
            .SelectMany(p => (p.Manifest?.Contributes?.CustomEditors ?? [])
                .Where(e => !string.IsNullOrWhiteSpace(e.ViewType) && e.FilenamePatterns is { Length: > 0 })
                .Select(e => new ContributedCustomEditor(e.ViewType, string.IsNullOrWhiteSpace(e.DisplayName) ? e.ViewType : e.DisplayName, e.FilenamePatterns, p.Id)))];

    /// <summary>The custom editor for a file, by its name; null when none claims it.</summary>
    public ContributedCustomEditor? CustomEditorFor(string path) => CustomEditors.FirstOrDefault(e => e.Matches(path));

    /// <summary>
    /// Opens <paramref name="path"/> in <paramref name="editor"/>: its plugin activates, gets the file, and fills the view
    /// whose id this returns. Throws when the plugin can't.
    /// </summary>
    public async Task<string> OpenCustomEditorAsync(ContributedCustomEditor editor, string path, string text, CancellationToken cancellationToken)
    {
        if (Find(editor.PluginId) is not { } plugin || !await ActivateAsync(plugin, "onCustomEditor:" + editor.ViewType, cancellationToken).ConfigureAwait(false)
            || GetHost(plugin.HostGroup) is not { IsRunning: true } host)
        {
            throw new InvalidOperationException($"The plugin of the custom editor '{editor.DisplayName}' didn't start.");
        }

        var viewId = "customEditor:" + Guid.NewGuid().ToString("N");
        _customEditorOwners[viewId] = plugin.Id;
        try
        {
            await host.Connection.InvokeAsync(PluginMethods.CustomEditorOpen, new CustomEditorOpenParams(editor.ViewType, viewId, path, text),
                PluginJsonContext.Default.CustomEditorOpenParams, PluginJsonContext.Default.EmptyResult, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonRpcRemoteException ex)
        {
            _customEditorOwners.TryRemove(viewId, out _);
            throw new InvalidOperationException($"The custom editor '{editor.DisplayName}' couldn't open {Path.GetFileName(path)}: {ex.Message}", ex);
        }

        return viewId;
    }

    /// <summary>A custom editor's document closed: its plugin hears of it, and its view goes.</summary>
    public void CloseCustomEditor(string viewId)
    {
        _views.TryRemove(viewId, out _);
        if (_customEditorOwners.TryRemove(viewId, out var pluginId) && Find(pluginId) is { } plugin && GetHost(plugin.HostGroup) is { IsRunning: true } host)
        {
            _ = host.NotifySafeAsync(PluginMethods.CustomEditorClose, new ViewIdParams(viewId), PluginJsonContext.Default.ViewIdParams);
        }
    }

    // ------------------------------------------------------------------ settings and command events

    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs e)
    {
        var changes = e.Keys.ToDictionary(k => k, k => _workbench.Settings.GetValue(k), StringComparer.Ordinal);
        foreach (var host in _hosts.Values.Where(h => h.IsRunning))
        {
            _ = host.NotifySafeAsync(PluginMethods.SettingsDidChange, new SettingsDidChangeParams(changes), PluginJsonContext.Default.SettingsDidChangeParams);
        }

        foreach (var key in e.Keys.Except(e.Registered))
        {
            _ = ActivateByEventAsync("onSettingChanged:" + key, CancellationToken.None);
        }
    }

    private void OnCommandExecuted(object? sender, CommandExecutedEventArgs e)
    {
        // Explicit onCommand events for commands owned by someone else (e.g. built-ins).
        if (_workbench.Commands.Find(e.CommandId)?.Source is { IsBuiltIn: true } && e.Error is null)
        {
            _ = ActivateByEventAsync("onCommand:" + e.CommandId, CancellationToken.None);
        }
    }

    // ------------------------------------------------------------------ proposed APIs

    private void OnProposedNotification(object? sender, ProposedNotification notification)
    {
        var json = PluginJsonContext.Default;
        foreach (var host in _hosts.Values.Where(h => h.IsRunning))
        {
            _ = notification switch
            {
                ProposedNotification.SolutionChanged => host.NotifySafeAsync(ProposedMethods.WorkspaceSolutionChanged, new EmptyParams(), json.EmptyParams),
                ProposedNotification.DocumentsChanged d => host.NotifySafeAsync(ProposedMethods.WorkspaceDocumentsChanged, new DocumentUrisParams(d.Uris), json.DocumentUrisParams),
                ProposedNotification.BuildFinished b => host.NotifySafeAsync(ProposedMethods.BuildFinished, b.Build, json.BuildFinishedParams),
                _ => Task.CompletedTask,
            };
        }
    }

    // ------------------------------------------------------------------ hosts

    private HostConnection? GetHost(string group)
    {
        if (_hosts.TryGetValue(group, out var existing))
        {
            return existing;
        }

        if (_resolveHost(group) is not { } launch)
        {
            return null;
        }

        // A workspace plugin in a remote window: its host runs on the server, its folder is the server's copy.
        Func<string, string>? pluginPath = null;
        if (group.StartsWith(PluginDescription.WorkspaceGroupPrefix, StringComparison.Ordinal) && Volatile.Read(ref _workspacePlacement) is { } placement)
        {
            if (placement.Place(launch) is not { } placed)
            {
                return null;
            }

            launch = placed;
            pluginPath = placement.PluginPath;
        }

        if (group == PluginDescription.DebugGroup && DebugHostFile is { } debugFile)
        {
            launch = launch with
            {
                Environment = new Dictionary<string, string>(launch.Environment ?? new Dictionary<string, string>(), StringComparer.Ordinal)
                {
                    ["CODALUS_PLUGINHOST_DEBUG_FILE"] = debugFile,
                },
            };
        }

        var remote = launch.Launcher is not null;
        return _hosts.GetOrAdd(group, g => new HostConnection(this, g, launch) { PluginPath = pluginPath, IsRemote = remote });
    }

    /// <summary>How long an activation may take before <see cref="SlowPlugin"/> is raised (M22d).</summary>
    public TimeSpan SlowActivation { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>A plugin took <see cref="SlowActivation"/> or more to activate (M22d, performance attribution).</summary>
    public event EventHandler<PluginDescription>? SlowPlugin;

    /// <summary>The memory a plugin host process uses on this machine (its working set), or null when it isn't running here.</summary>
    public long? HostMemory(string group)
    {
        if (!_hosts.TryGetValue(group, out var host) || host.IsRemote || host.ProcessId is not { } id)
        {
            return null;
        }

        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(id);
            return process.WorkingSet64;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>The plugin host groups started so far, and whether each runs on a remote window's server.</summary>
    public IReadOnlyList<(string Group, bool Running, bool Remote)> Hosts =>
        [.. _hosts.Select(h => (h.Key, h.Value.IsRunning, h.Value.IsRemote)).OrderBy(h => h.Key, StringComparer.Ordinal)];

    /// <summary>
    /// Where <c>runsOn: workspace</c> plugins run (M21b, ADR 0057): a remote window's server (<paramref name="placement"/>),
    /// or this machine (null). Their running hosts stop, and their plugins activate again in the new place on their
    /// next activation event; the returned task ends when the old hosts have stopped.
    /// </summary>
    public async Task SetWorkspacePlacementAsync(WorkspacePlacement? placement)
    {
        Volatile.Write(ref _workspacePlacement, placement);
        foreach (var (group, host) in _hosts.Where(h => h.Key.StartsWith(PluginDescription.WorkspaceGroupPrefix, StringComparison.Ordinal)).ToList())
        {
            if (_hosts.TryRemove(new KeyValuePair<string, HostConnection>(group, host)))
            {
                await host.RetireAsync().ConfigureAwait(false);
            }
        }

        PluginsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Whether a workspace plugin's host runs elsewhere now (a remote window).</summary>
    public bool WorkspacePluginsAreRemote => Volatile.Read(ref _workspacePlacement) is not null;

    private PluginDescription? Find(string id) => Plugins.FirstOrDefault(p => p.Id == id);

    public async ValueTask DisposeAsync()
    {
        _workbench.Settings.Changed -= OnSettingsChanged;
        _workbench.Commands.Executed -= OnCommandExecuted;

        // The hosts shut down together (M56): one after another, a few slow ones outlasted the time the IDE's exit gives them.
        await Task.WhenAll(_hosts.Values.Select(host => host.DisposeAsync().AsTask())).ConfigureAwait(false);
        foreach (var gate in _activationLocks.Values)
        {
            gate.Dispose();
        }
    }

    /// <summary>
    /// Why a plugin host may not use <paramref name="pluginId"/> for its secrets (ADR 0083), or null: it must be one of
    /// <paramref name="hostPlugins"/>, the plugins of that host, and never an id under the shell's own prefix.
    /// </summary>
    internal static string? SecretOwnerRefusal(string? pluginId, IEnumerable<string> hostPlugins) =>
        pluginId is not { Length: > 0 } ? "secrets/* needs the plugin's id."
        : pluginId.StartsWith(Ai.Acp.AcpAgentSecrets.ShellPrefix, StringComparison.OrdinalIgnoreCase) ? $"'{pluginId}' is the shell's own: a plugin can't use its secrets."
        : !hostPlugins.Contains(pluginId, StringComparer.Ordinal) ? $"'{pluginId}' isn't a plugin of this host, so it can't use that plugin's secrets."
        : null;

    /// <summary>One plugin host process (a host group) and the shell-side handlers for its requests.</summary>
    private sealed class HostConnection : IAsyncDisposable
    {
        private readonly PluginManager _manager;
        private readonly string _group;
        private readonly SupervisedProcess _process;

        // Tasks this host's plugins show (ended when the host goes).
        private readonly ConcurrentDictionary<string, Notifications.ProgressTask> _progress = new(StringComparer.Ordinal);

        // Test controllers created by this host's plugins (disposed when the host crashes).
        private readonly ConcurrentDictionary<string, byte> _controllers = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, byte> _visualizers = new(StringComparer.Ordinal);

        // Hosting providers registered by this host's plugins (unregistered when the host crashes).
        private readonly ConcurrentDictionary<string, byte> _providers = new(StringComparer.Ordinal);

        // Language model providers registered by this host's plugins (unregistered when the host crashes).
        private readonly ConcurrentDictionary<string, byte> _modelProviders = new(StringComparer.Ordinal);

        public HostConnection(PluginManager manager, string group, ProcessLaunchInfo launch)
        {
            _manager = manager;
            _group = group;
            _process = new SupervisedProcess($"plugin host '{group}'", launch, manager._hostPolicy)
            {
                ConfigureConnection = Configure,
                Initialize = InitializeAsync,
            };
            _process.Crashed += OnCrashed;
            _process.Restarted += (_, _) => _manager._workbench.Notifications.Info(Localizer.Format("msg.pluginManager.pluginHostRestarted", "Plugin host '{0}' restarted.", _group));
            _process.Log += (_, line) => _manager._workbench.Output.GetOrCreate("codalus.pluginhost." + group, $"Plugin Host ({group})").AppendLine(line);
        }

        /// <summary>Plugins active in this host (re-activated after a restart).</summary>
        public ConcurrentBag<string> Active { get; private set; } = [];

        /// <summary>A plugin folder as this host sees it (a remote window's server copy); null when it's this machine's.</summary>
        public Func<string, string>? PluginPath { get; init; }

        /// <summary>The host runs on a remote window's server.</summary>
        public bool IsRemote { get; init; }

        /// <summary>Stops the host for good (its place changed): what its plugins contributed at run time goes, and they can activate again elsewhere.</summary>
        public async Task RetireAsync()
        {
            ReleaseContributions();
            await _process.DisposeAsync().ConfigureAwait(false);
        }

        public bool IsRunning => _process.IsRunning;

        public int? ProcessId => _process.ProcessId;

        public JsonRpcConnection Connection => _process.Connection;

        private IEnumerable<PluginDescription> Members => _manager.Plugins.Where(p => p.HostGroup == _group && !p.IsDeclarative);

        public Task EnsureStartedAsync(CancellationToken cancellationToken) => _process.StartAsync(cancellationToken);

        public async Task NotifySafeAsync<T>(string method, T parameters, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info)
        {
            try
            {
                await _process.Connection.NotifyAsync(method, parameters, info, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is JsonRpcConnectionLostException or IOException or ObjectDisposedException)
            {
            }
        }

        private async Task InitializeAsync(JsonRpcConnection connection, bool isRestart, CancellationToken cancellationToken)
        {
            var json = PluginJsonContext.Default;
            var settings = new Dictionary<string, JsonElement>(_manager._workbench.Settings.Snapshot(), StringComparer.Ordinal);
            var init = await connection.InvokeAsync(
                PluginMethods.Initialize, new HostInitializeParams(PluginMethods.ProtocolVersion, _manager._shellVersion, _group, settings),
                json.HostInitializeParams, json.HostInitializeResult, cancellationToken).ConfigureAwait(false);
            if (init.ProtocolVersion.Split('.')[0] != PluginMethods.ProtocolVersion.Split('.')[0])
            {
                throw new InvalidOperationException($"Plugin host speaks protocol {init.ProtocolVersion}; the shell needs {PluginMethods.ProtocolVersion}.");
            }

            var members = Members.Where(p => p.IsUsable).ToList();
            var load = await connection.InvokeAsync(
                PluginMethods.LoadPlugins,
                new LoadPluginsParams([.. members.Select(p => new PluginLoadEntry(p.Id, p.Manifest!.Version, PluginPath is { } map ? map(p.InstallPath) : p.InstallPath, p.Manifest.Entry!.Assembly, p.Manifest.Entry.Type, p.Manifest.EnableProposedApi))]),
                json.LoadPluginsParams, json.LoadPluginsResult, cancellationToken).ConfigureAwait(false);
            foreach (var result in load.Results.Where(r => !r.Loaded))
            {
                if (_manager.Find(result.Id) is { } plugin)
                {
                    _manager.Fail(plugin, "Could not be loaded: " + result.Error);
                }
            }

            if (!isRestart)
            {
                return;
            }

            // Re-activate what was active before the crash.
            var previouslyActive = Active.Distinct().ToList();
            Active = [];
            foreach (var id in previouslyActive)
            {
                if (_manager.Find(id) is { State: PluginState.Ready } plugin)
                {
                    var result = await connection.InvokeAsync(
                        PluginMethods.ActivatePlugin, new ActivatePluginParams(id, "restart"),
                        json.ActivatePluginParams, json.ActivatePluginResult, cancellationToken).ConfigureAwait(false);
                    if (result.Activated)
                    {
                        plugin.State = PluginState.Active;
                        Active.Add(id);
                    }
                    else
                    {
                        _manager.Fail(plugin, result.Error ?? "Re-activation failed.");
                    }
                }
            }

            _manager.PluginsChanged?.Invoke(_manager, EventArgs.Empty);
        }

        private void OnCrashed(object? sender, ProcessCrashedEventArgs e)
        {
            ReleaseContributions();
            var exit = e.ExitCode is { } code ? $" (exit code {code})" : string.Empty;
            if (e is { DuringStartup: true, WillRestart: false })
            {
                // It couldn't start (M49): its plugins fail with its reason, reported once for the group.
                var error = e.Error as JsonRpcConnectionLostException ?? new JsonRpcConnectionLostException($"Plugin host '{_group}' exited during startup{exit}.", e.Error);
                foreach (var plugin in Members.Where(p => p.IsUsable))
                {
                    plugin.Error = error.Message;
                    plugin.FailedWithItsHost = true;
                    plugin.State = PluginState.Failed;
                }

                _manager.ReportHostStartFailure(_group, error);
            }
            else if (e.WillRestart)
            {
                _manager._workbench.Notifications.Warning(Localizer.Format("msg.pluginManager.pluginHostStoppedUnexpectedlyRestarting", "Plugin host '{0}' stopped unexpectedly{1}. Restarting…", _group, exit));
            }
            else
            {
                foreach (var plugin in Members.Where(p => p.IsUsable))
                {
                    plugin.Error = "Its plugin host crashed repeatedly and was stopped for this session.";
                    plugin.State = PluginState.Failed;
                }

                _ = _manager.ShowAndHandleAsync(_manager._workbench.Notifications.Error(
                    Localizer.Format("msg.pluginManager.pluginHostCrashedRepeatedlyAndWas", "Plugin host '{0}' crashed repeatedly and was stopped for this session{1}.", _group, exit), "Show Log"));
            }

            _manager.PluginsChanged?.Invoke(_manager, EventArgs.Empty);
        }

        /// <summary>Test controllers, hosting providers and visualizers the host's plugins registered go; its active plugins are ready again.</summary>
        private void ReleaseContributions()
        {
            var members = Members.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
            _manager.UnregisterFileSystems((_, plugin) => members.Contains(plugin));
            foreach (var id in _manager._sourceControls.Where(s => members.Contains(s.Value.PluginId)).Select(s => s.Key).ToList())
            {
                _manager._sourceControls.TryRemove(id, out _);
                _manager.SourceControlsChanged?.Invoke(_manager, EventArgs.Empty);
            }

            foreach (var (id, _) in _manager._authProviders.Where(p => members.Contains(p.Value.PluginId)).ToList())
            {
                _manager._authProviders.TryRemove(id, out _);
                _manager.AuthenticationChanged?.Invoke(_manager, id);
            }

            foreach (var id in _progress.Keys)
            {
                if (_progress.TryRemove(id, out var task))
                {
                    task.End();
                }
            }

            foreach (var controller in _controllers.Keys)
            {
                _controllers.TryRemove(controller, out _);
                _manager.TestSink?.ControllerDisposed(controller);
            }

            foreach (var provider in _providers.Keys)
            {
                _providers.TryRemove(provider, out _);
                _manager.HostingSink?.ProviderUnregistered(provider);
            }

            foreach (var provider in _modelProviders.Keys)
            {
                _modelProviders.TryRemove(provider, out _);
                _manager.LanguageModelSink?.ProviderUnregistered(provider);
            }

            foreach (var visualizer in _visualizers.Keys)
            {
                _visualizers.TryRemove(visualizer, out _);
                _manager.DebugVisualizers.Unregister(visualizer);
            }

            foreach (var plugin in Members.Where(p => p.State == PluginState.Active))
            {
                plugin.State = PluginState.Ready;
            }
        }

        private void Configure(JsonRpcConnection connection)
        {
            var json = PluginJsonContext.Default;
            var workbench = _manager._workbench;
            connection.HandlerFailed += (_, ex) => _manager.PluginLog.AppendLine($"[{_group}] a message from the plugin host failed in the shell: {ex}");

            connection.OnNotification(PluginMethods.RegisterCommand, json.CommandRegistrationParams, p =>
            {
                if (workbench.Commands.Find(p.Command) is null && _manager.Find(p.PluginId) is { } plugin)
                {
                    // An undeclared, runtime-registered command: hidden, but executable by id.
                    workbench.Commands.Register(
                        new CommandDescriptor(p.Command, p.Command, plugin.Source) { ShowInPalette = false },
                        (args, ct) => _manager.ExecutePluginCommandAsync(plugin, p.Command, args, ct));
                }
            });
            connection.OnNotification(PluginMethods.UnregisterCommand, json.CommandRegistrationParams, _ => { });
            connection.OnRequest(PluginMethods.ExecuteCommandInShell, json.ExecuteCommandParams, json.ExecuteCommandResult, async (p, ct) =>
            {
                try
                {
                    return new ExecuteCommandResult(await workbench.Commands.ExecuteAsync(p.Command, p.Args, ct).ConfigureAwait(false));
                }
                catch (CommandNotFoundException ex)
                {
                    throw new JsonRpcHandlerException(PluginErrorCodes.CommandFailed, ex.Message);
                }
                catch (CommandNotEnabledException ex)
                {
                    throw new JsonRpcHandlerException(PluginErrorCodes.CommandFailed, ex.Message);
                }
            });
            connection.OnRequest(PluginMethods.ShowNotification, json.ShowNotificationParams, json.ShowNotificationResult, async (p, ct) =>
            {
                var severity = p.Severity switch { "error" => NotificationSeverity.Error, "warning" => NotificationSeverity.Warning, _ => NotificationSeverity.Info };
                var source = _manager.Find(p.PluginId)?.DisplayName ?? p.PluginId;
                var notification = workbench.Notifications.Show(severity, p.Message, p.Actions, source);
                using var registration = ct.Register(() => workbench.Notifications.Dismiss(notification));
                return new ShowNotificationResult(await notification.Result.ConfigureAwait(false));
            });
            connection.OnRequest(PluginMethods.ShowQuickPick, json.ShowQuickPickParams, json.ShowQuickPickResult, async (p, ct) =>
                new ShowQuickPickResult(_manager.Prompts is { } prompts
                    ? await prompts.PickAsync(p.Items, p.Placeholder, _manager.Find(p.PluginId)?.DisplayName ?? p.PluginId, ct).ConfigureAwait(false)
                    : null));
            connection.OnRequest(PluginMethods.ShowInputBox, json.ShowInputBoxParams, json.ShowInputBoxResult, async (p, ct) =>
                new ShowInputBoxResult(_manager.Prompts is { } prompts
                    ? await prompts.InputAsync(p.Title, p.Prompt, p.Value, p.Browse, _manager.Find(p.PluginId)?.DisplayName ?? p.PluginId, ct).ConfigureAwait(false)
                    : null));
            connection.OnNotification(PluginMethods.ProgressStart, json.ProgressStartParams, p =>
            {
                var source = _manager.Find(p.PluginId)?.DisplayName ?? p.PluginId;
                Action? cancel = p.Cancellable
                    ? () => _ = NotifySafeAsync(PluginMethods.ProgressCancel, new ProgressIdParams(p.PluginId, p.Id), json.ProgressIdParams)
                    : null;
                _progress[p.Id] = workbench.Progress.Begin(p.Title, source, cancel);
            });
            connection.OnNotification(PluginMethods.ProgressReport, json.ProgressReportParams, p =>
            {
                if (_progress.TryGetValue(p.Id, out var task))
                {
                    task.Report(p.Message, p.Percent);
                }
            });
            connection.OnNotification(PluginMethods.ProgressEnd, json.ProgressIdParams, p =>
            {
                if (_progress.TryRemove(p.Id, out var task))
                {
                    task.End();
                }
            });
            connection.OnNotification(PluginMethods.OutputAppend, json.OutputParams, p => Channel(p).Append(p.Text ?? string.Empty));
            connection.OnNotification(PluginMethods.OutputClear, json.OutputParams, p => Channel(p).Clear());
            connection.OnNotification(PluginMethods.OutputShow, json.OutputParams, p => workbench.Output.Show(Channel(p)));
            connection.OnNotification(PluginMethods.StatusBarUpdate, json.StatusBarUpdateParams, p =>
                workbench.StatusBar.Set(new StatusBarItem(
                    $"{p.PluginId}/{p.ItemId}", p.Text, p.Alignment == "right" ? StatusBarAlignment.Right : StatusBarAlignment.Left, p.Priority, p.PluginId)
                {
                    Tooltip = p.Tooltip,
                    Command = p.Command,
                    Visible = p.Visible,
                }));
            connection.OnNotification(PluginMethods.StatusBarRemove, json.StatusBarRemoveParams, p => workbench.StatusBar.Remove($"{p.PluginId}/{p.ItemId}"));
            connection.OnNotification(PluginMethods.TreeViewRefresh, json.TreeViewRefreshParams, p =>
                _manager.TreeViewRefreshed?.Invoke(_manager, new TreeViewRefreshEventArgs(p.ViewId, p.Handle)));
            connection.OnNotification(PluginMethods.MarkdownViewSet, json.MarkdownViewSetParams, p =>
            {
                _manager._markdown[p.ViewId] = p.Markdown;
                _manager.MarkdownChanged?.Invoke(_manager, p.ViewId);
            });
            connection.OnNotification(PluginMethods.ScmRegister, json.ScmProviderParams, p =>
            {
                _manager._sourceControls[p.ProviderId] = new SourceControlInfo(p.ProviderId, p.Label, p.Root, p.PluginId);
                _manager.SourceControlsChanged?.Invoke(_manager, EventArgs.Empty);
            });
            connection.OnNotification(PluginMethods.ScmUpdate, json.ScmUpdateParams, p =>
            {
                if (_manager._sourceControls.TryGetValue(p.ProviderId, out var info))
                {
                    _manager._sourceControls[p.ProviderId] = info with { Groups = p.Groups, InputPlaceholder = p.InputPlaceholder, AcceptLabel = p.AcceptLabel, Status = p.Status };
                    _manager.SourceControlsChanged?.Invoke(_manager, EventArgs.Empty);
                }
            });
            connection.OnNotification(PluginMethods.ScmUnregister, json.ScmProviderParams, p =>
            {
                if (_manager._sourceControls.TryRemove(p.ProviderId, out _))
                {
                    _manager.SourceControlsChanged?.Invoke(_manager, EventArgs.Empty);
                }
            });
            connection.OnNotification(PluginMethods.AuthRegister, json.AuthProviderParams, p =>
            {
                _manager._authProviders[p.ProviderId] = (p.PluginId, p.Label);
                _manager.AuthenticationChanged?.Invoke(_manager, p.ProviderId);
            });
            connection.OnNotification(PluginMethods.AuthUnregister, json.AuthProviderParams, p =>
            {
                if (_manager._authProviders.TryRemove(p.ProviderId, out _))
                {
                    _manager.AuthenticationChanged?.Invoke(_manager, p.ProviderId);
                }
            });
            connection.OnNotification(PluginMethods.AuthSessionsChanged, json.AuthProviderParams, p => _manager.AuthenticationChanged?.Invoke(_manager, p.ProviderId));
            connection.OnRequest(PluginMethods.AuthGetSession, json.AuthGetSessionParams, json.AuthSessionResult, async (p, ct) =>
                new AuthSessionResult(await _manager.GetSessionForAsync(p, ct).ConfigureAwait(false)));
            connection.OnNotification(PluginMethods.FsRegister, json.FsSchemeParams, p => _manager.RegisterFileSystem(p.PluginId, p.Scheme));
            connection.OnNotification(PluginMethods.FsUnregister, json.FsSchemeParams, p => _manager.UnregisterFileSystems((scheme, plugin) => scheme == p.Scheme && plugin == p.PluginId));
            connection.OnNotification(PluginMethods.FsChanged, json.FsChangedParams, p =>
            {
                if (_manager._fileSystems.TryGetValue(p.Scheme, out var entry))
                {
                    entry.FileSystem.OnChanged(p.Paths, p.Kind);
                }
            });
            connection.OnNotification(PluginMethods.ViewSet, json.ViewSetParams, p =>
            {
                _manager._views[p.ViewId] = (p.PluginId, p.Root);
                _manager.ViewChanged?.Invoke(_manager, p.ViewId);
            });
            connection.OnNotification(PluginMethods.CustomEditorSetText, json.CustomEditorTextParams, p =>
                _manager.CustomEditorTextChanged?.Invoke(_manager, (p.ViewId, p.Text)));
            connection.OnNotification(PluginMethods.Log, json.LogParams, p =>
                _manager.PluginLog.AppendLine($"[{p.PluginId ?? _group}] {p.Level}: {p.Message}"));
            ConfigureProposed(connection);
        }

        private IProposedApiHost Proposed => _manager.ProposedApi ?? throw new JsonRpcHandlerException(PluginErrorCodes.ProposedApiUnavailable, "This shell doesn't offer the proposed workspace APIs.");

        private void ConfigureProposed(JsonRpcConnection connection)
        {
            var json = PluginJsonContext.Default;
            connection.OnRequest(ProposedMethods.WorkspaceGetSolution, json.EmptyParams, json.GetSolutionResult, async (_, ct) =>
                new GetSolutionResult(await Proposed.GetSolutionAsync(ct).ConfigureAwait(false)));
            connection.OnRequest(ProposedMethods.WorkspaceProjectOutput, json.ProjectOutputParams, json.ProjectOutputDto, (p, ct) =>
                Proposed.GetProjectOutputAsync(p.Path, p.TargetFramework, ct));
            connection.OnRequest(ProposedMethods.WorkspacePackages, json.WorkspacePackagesParams, json.WorkspacePackagesResult, (p, ct) =>
                Proposed.GetPackagesAsync(p.Path, ct));
            connection.OnRequest(ProposedMethods.WorkspaceProperties, json.WorkspacePropertiesParams, json.WorkspacePropertiesResult, (p, ct) =>
                Proposed.GetPropertiesAsync(p.Path, p.Names, ct));
            connection.OnRequest(ProposedMethods.WorkspaceTestCandidates, json.DocumentUrisParams, json.TestCandidatesResultDto, (p, ct) =>
                Proposed.FindTestCandidatesAsync(p.Uris, ct));
            connection.OnRequest(ProposedMethods.WorkspaceTypeOutline, json.TypeOutlineParamsDto, json.TypeOutlineResult, async (p, ct) =>
                new TypeOutlineResult(await Proposed.GetTypeOutlineAsync(p.Uri, p.Line, p.Character, ct).ConfigureAwait(false)));
            connection.OnRequest(ProposedMethods.BuildStart, json.BuildStartParams, json.BuildResultDto, (p, ct) =>
                Proposed.BuildAsync(p.Projects, ct));
            connection.OnRequest(ProposedMethods.DebugLaunch, json.DebugLaunchParams, json.DebugSessionDto, (p, ct) =>
                Proposed.DebugLaunchAsync(p, DebugEnded, ct));
            connection.OnRequest(ProposedMethods.DebugAttach, json.DebugAttachParams, json.DebugSessionDto, (p, ct) =>
                Proposed.DebugAttachAsync(p.ProcessId, DebugEnded, ct));
            connection.OnRequest(ProposedMethods.DebugEvaluate, json.DebugEvaluateParams, json.DebugValueDto, (p, ct) =>
                Proposed.DebugEvaluateAsync(p.Expression, ct));
            connection.OnNotification(ProposedMethods.DebugRegisterVisualizer, json.DebugVisualizerDto, p =>
            {
                _visualizers[p.VisualizerId] = 0;
                _manager.DebugVisualizers.Register(new Debugging.PluginDebugVisualizer(p.VisualizerId, p.PluginId, p.Title, p.Types, async (request, ct) =>
                    await Connection.InvokeAsync(ProposedMethods.DebugVisualize, request, PluginJsonContext.Default.DebugVisualizeParams, PluginJsonContext.Default.DebugVisualizerViewDto, ct).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("The visualizer returned nothing.")));
            });
            connection.OnNotification(ProposedMethods.DebugUnregisterVisualizer, json.DebugVisualizerIdParams, p =>
            {
                _visualizers.TryRemove(p.VisualizerId, out _);
                _manager.DebugVisualizers.Unregister(p.VisualizerId);
            });
            connection.OnRequest(ProposedMethods.DebugStop, json.DebugSessionParams, json.EmptyResult, async (p, ct) =>
            {
                await Proposed.DebugStopAsync(p.SessionId, ct).ConfigureAwait(false);
                return new EmptyResult();
            });
            connection.OnRequest(ProposedMethods.TerminalCreate, json.TerminalCreateParams, json.TerminalDto, (p, ct) =>
                Proposed.TerminalCreateAsync(p, TerminalOutput, TerminalExited, ct));
            connection.OnRequest(ProposedMethods.TerminalSendText, json.TerminalTextParams, json.EmptyResult, async (p, ct) =>
            {
                await Proposed.TerminalSendTextAsync(p.TerminalId, p.Text, ct).ConfigureAwait(false);
                return new EmptyResult();
            });
            connection.OnRequest(ProposedMethods.TerminalKill, json.TerminalIdParams, json.EmptyResult, async (p, ct) =>
            {
                await Proposed.TerminalKillAsync(p.TerminalId, ct).ConfigureAwait(false);
                return new EmptyResult();
            });

            connection.OnNotification(ProposedMethods.TestsCreateController, json.TestControllerParams, p =>
            {
                _controllers[p.ControllerId] = 0;
                _manager.TestSink?.ControllerCreated(p, new ControllerConnection(this, p.ControllerId));
            });
            connection.OnNotification(ProposedMethods.TestsDisposeController, json.TestControllerIdParams, p =>
            {
                _controllers.TryRemove(p.ControllerId, out _);
                _manager.TestSink?.ControllerDisposed(p.ControllerId);
            });
            connection.OnNotification(ProposedMethods.TestsUpdateItems, json.TestItemsUpdateParams, p => _manager.TestSink?.ItemsUpdated(p));
            connection.OnNotification(ProposedMethods.TestsUpdateProfiles, json.TestProfilesParams, p => _manager.TestSink?.ProfilesUpdated(p));
            connection.OnNotification(ProposedMethods.TestsRunStarted, json.TestRunStartedParams, p => _manager.TestSink?.RunStarted(p));
            connection.OnNotification(ProposedMethods.TestsRunUpdate, json.TestRunUpdateParams, p => _manager.TestSink?.RunUpdated(p));
            connection.OnNotification(ProposedMethods.TestsRunCoverage, json.TestRunCoverageParams, p => _manager.TestSink?.RunCoverage(p));
            connection.OnNotification(ProposedMethods.TestsRunEnded, json.TestRunIdParams, p => _manager.TestSink?.RunEnded(p.RunId));

            // Hosting providers and secrets (API 0.2.6, ADR 0031).
            connection.OnNotification(HostingMethods.RegisterProvider, json.HostingProviderParams, p =>
            {
                _providers[p.ProviderId] = 0;
                _manager.HostingSink?.ProviderRegistered(p.PluginId, p.ProviderId, new HostingConnection(this));
            });
            connection.OnNotification(HostingMethods.UnregisterProvider, json.HostingProviderParams, p =>
            {
                _providers.TryRemove(p.ProviderId, out _);
                _manager.HostingSink?.ProviderUnregistered(p.ProviderId);
            });
            connection.OnNotification(HostingMethods.DeviceCode, json.HostingDeviceCodeParams, p => _manager.HostingSink?.DeviceCodeReceived(p));

            // Language model providers (proposed, ADR 0062).
            connection.OnNotification(LanguageModelMethods.RegisterProvider, json.LanguageModelProviderParams, p =>
            {
                _modelProviders[p.ProviderId] = 0;
                _manager.LanguageModelSink?.ProviderRegistered(p.PluginId, p.ProviderId, new HostingConnection(this));
            });
            connection.OnNotification(LanguageModelMethods.UnregisterProvider, json.LanguageModelProviderParams, p =>
            {
                _modelProviders.TryRemove(p.ProviderId, out _);
                _manager.LanguageModelSink?.ProviderUnregistered(p.ProviderId);
            });
            connection.OnNotification(LanguageModelMethods.ChatPart, json.ChatPartParams, p => _manager.LanguageModelSink?.ChatPart(p));
            connection.OnRequest(HostingMethods.SecretsGet, json.SecretParams, json.SecretResult, async (p, ct) =>
                new SecretResult(await Secrets.GetAsync(SecretOwner(p.PluginId), p.Key, ct).ConfigureAwait(false)));
            connection.OnRequest(HostingMethods.SecretsStore, json.SecretParams, json.EmptyResult, async (p, ct) =>
            {
                await Secrets.StoreAsync(SecretOwner(p.PluginId), p.Key, p.Value ?? throw new JsonRpcHandlerException(PluginErrorCodes.CommandFailed, "secrets/store needs a value."), ct).ConfigureAwait(false);
                return new EmptyResult();
            });
            connection.OnRequest(HostingMethods.SecretsDelete, json.SecretParams, json.EmptyResult, async (p, ct) =>
            {
                await Secrets.DeleteAsync(SecretOwner(p.PluginId), p.Key, ct).ConfigureAwait(false);
                return new EmptyResult();
            });
        }

        /// <summary>
        /// The plugin id a <c>secrets/*</c> request may use (ADR 0083): one of this host's own plugins, never the shell's
        /// (<c>codalus.shell.</c>, where external agents' keys are); anything else is refused, so a plugin can't read another
        /// host's secrets or the agents' keys.
        /// </summary>
        private string SecretOwner(string pluginId) =>
            SecretOwnerRefusal(pluginId, Members.Select(p => p.Id)) is { } refusal
                ? throw new JsonRpcHandlerException(PluginErrorCodes.CommandFailed, refusal)
                : pluginId;

        private Secrets.SecretStorage Secrets => _manager.Secrets ?? throw new JsonRpcHandlerException(PluginErrorCodes.ProposedApiUnavailable, "This shell has no credential store for secrets.");

        private sealed class HostingConnection(HostConnection host) : IHostingConnection
        {
            public Task<TResult> InvokeAsync<TParams, TResult>(string method, TParams parameters, System.Text.Json.Serialization.Metadata.JsonTypeInfo<TParams> paramsInfo, System.Text.Json.Serialization.Metadata.JsonTypeInfo<TResult> resultInfo, CancellationToken cancellationToken) =>
                host.Connection.InvokeAsync(method, parameters, paramsInfo, resultInfo, cancellationToken);
        }

        private void DebugEnded(DebugEndedParams ended) =>
            _ = NotifySafeAsync(ProposedMethods.DebugEnded, ended, PluginJsonContext.Default.DebugEndedParams);

        private void TerminalOutput(TerminalOutputParams output) =>
            _ = NotifySafeAsync(ProposedMethods.TerminalOutput, output, PluginJsonContext.Default.TerminalOutputParams);

        private void TerminalExited(TerminalExitedParams exited) =>
            _ = NotifySafeAsync(ProposedMethods.TerminalExited, exited, PluginJsonContext.Default.TerminalExitedParams);

        private sealed class ControllerConnection(HostConnection host, string controllerId) : ITestControllerConnection
        {
            public async Task<IReadOnlyList<string>> RunAsync(TestRunParams request, CancellationToken cancellationToken) =>
                (await host.Connection.InvokeAsync(ProposedMethods.TestsRun, request, PluginJsonContext.Default.TestRunParams, PluginJsonContext.Default.TestRunResultDto, cancellationToken).ConfigureAwait(false))?.RunIds ?? [];

            public Task RefreshAsync(CancellationToken cancellationToken) =>
                host.Connection.InvokeAsync(ProposedMethods.TestsRefresh, new TestControllerIdParams(controllerId), PluginJsonContext.Default.TestControllerIdParams, PluginJsonContext.Default.EmptyResult, cancellationToken);
        }

        private Output.OutputChannel Channel(OutputParams p)
        {
            var name = _manager.Find(p.PluginId)?.DisplayName ?? p.PluginId;
            return _manager._workbench.Output.GetOrCreate($"plugin.{p.PluginId}.{p.Channel}", $"{name}: {p.Channel}");
        }

        public ValueTask DisposeAsync() => _process.DisposeAsync();
    }
}
