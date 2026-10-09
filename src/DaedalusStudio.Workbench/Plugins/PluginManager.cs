using System.Collections.Concurrent;
using System.Text.Json;
using DaedalusStudio.Plugins.Protocol;
using DaedalusStudio.Rpc;
using DaedalusStudio.Workbench.Commands;
using DaedalusStudio.Workbench.Context;
using DaedalusStudio.Workbench.Keybindings;
using DaedalusStudio.Workbench.Menus;
using DaedalusStudio.Workbench.Notifications;
using DaedalusStudio.Workbench.Settings;
using DaedalusStudio.Workbench.StatusBar;
using DaedalusStudio.Workbench.ToolWindows;

namespace DaedalusStudio.Workbench.Plugins;

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

    /// <summary>Returns the entered text, or null when cancelled.</summary>
    Task<string?> InputAsync(string title, string? prompt, string? value, string source, CancellationToken cancellationToken);
}

/// <summary>Locates <c>daedalus-pluginhost</c>: <c>DAEDALUS_PLUGINHOST</c>, then <c>pluginhost/</c> next to the shell.</summary>
public static class PluginHostLocator
{
    public const string EnvironmentVariable = "DAEDALUS_PLUGINHOST";

    public static ProcessLaunchInfo? Resolve()
    {
        var candidates = new List<string>();
        if (Environment.GetEnvironmentVariable(EnvironmentVariable) is { Length: > 0 } fromEnv)
        {
            candidates.Add(fromEnv);
        }

        var exe = OperatingSystem.IsWindows() ? "daedalus-pluginhost.exe" : "daedalus-pluginhost";
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "pluginhost", exe));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "pluginhost", "daedalus-pluginhost.dll"));
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
    private static readonly TimeSpan s_activationTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan s_treeTimeout = TimeSpan.FromSeconds(5);

    private readonly WorkbenchServices _workbench;
    private readonly Func<string, ProcessLaunchInfo?> _resolveHost;
    private readonly string _shellVersion;
    private readonly SupervisionPolicy? _hostPolicy;
    private readonly List<PluginDescription> _plugins = [];
    private readonly ConcurrentDictionary<string, HostConnection> _hosts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _activationLocks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _markdown = new(StringComparer.Ordinal);
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

    public event EventHandler<TreeViewRefreshEventArgs>? TreeViewRefreshed;

    public event EventHandler<string>? MarkdownChanged;

    private Output.OutputChannel PluginLog => _workbench.Output.GetOrCreate("daedalus.plugins", "Plugins");

    /// <summary>Registers plugins and applies their declarative contributions. Problems are logged and notified once.</summary>
    public void AddPlugins(IEnumerable<PluginDescription> plugins)
    {
        var problems = new List<string>();
        foreach (var plugin in plugins)
        {
            lock (_gate)
            {
                _plugins.Add(plugin);
            }

            foreach (var warning in plugin.Warnings)
            {
                PluginLog.AppendLine($"[{plugin.Id}] warning: {warning}");
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
                window.Kind == "markdown" ? ToolWindowKind.Markdown : ToolWindowKind.Tree,
                source)
            {
                Icon = window.Icon,
            });
        }

        if (contributions?.Settings?.Properties is { } properties)
        {
            var descriptors = new List<SettingDescriptor>();
            foreach (var (key, setting) in properties)
            {
                if (SettingTypes.Parse(setting.Type) is not { } type)
                {
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
                return Fail(plugin, "The plugin host (daedalus-pluginhost) was not found.");
            }

            await host.EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
            if (!plugin.IsUsable)
            {
                return false; // loading failed
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(s_activationTimeout);
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
            PluginLog.AppendLine($"[{plugin.Id}] activated ({reason}) in {System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:0} ms");
            PluginsChanged?.Invoke(this, EventArgs.Empty);
            return true;
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

    private bool Fail(PluginDescription plugin, string error)
    {
        plugin.State = PluginState.Failed;
        plugin.Error = error;
        PluginLog.AppendLine($"[{plugin.Id}] failed: {error}");
        _ = ShowAndHandleAsync(_workbench.Notifications.Error($"Plugin '{plugin.DisplayName}' failed: {error}", "Show Log"));
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
        var result = await host.Connection.InvokeAsync(
            PluginMethods.ExecuteCommand, new ExecuteCommandParams(commandId, [.. args]),
            PluginJsonContext.Default.ExecuteCommandParams, PluginJsonContext.Default.ExecuteCommandResult,
            cancellationToken).ConfigureAwait(false);
        return result.Result;
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
        timeout.CancelAfter(s_treeTimeout);
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
        foreach (var host in _hosts.Values.Where(h => h.IsRunning && h.UsesProposedApi))
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

        return _hosts.GetOrAdd(group, g => new HostConnection(this, g, launch));
    }

    private PluginDescription? Find(string id) => Plugins.FirstOrDefault(p => p.Id == id);

    public async ValueTask DisposeAsync()
    {
        _workbench.Settings.Changed -= OnSettingsChanged;
        _workbench.Commands.Executed -= OnCommandExecuted;
        foreach (var host in _hosts.Values)
        {
            await host.DisposeAsync().ConfigureAwait(false);
        }

        foreach (var gate in _activationLocks.Values)
        {
            gate.Dispose();
        }
    }

    /// <summary>One plugin host process (a host group) and the shell-side handlers for its requests.</summary>
    private sealed class HostConnection : IAsyncDisposable
    {
        private readonly PluginManager _manager;
        private readonly string _group;
        private readonly SupervisedProcess _process;

        // Test controllers created by this host's plugins (disposed when the host crashes).
        private readonly ConcurrentDictionary<string, byte> _controllers = new(StringComparer.Ordinal);

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
            _process.Restarted += (_, _) => _manager._workbench.Notifications.Info($"Plugin host '{_group}' restarted.");
            _process.Log += (_, line) => _manager._workbench.Output.GetOrCreate("daedalus.pluginhost." + group, $"Plugin Host ({group})").AppendLine(line);
        }

        /// <summary>Plugins active in this host (re-activated after a restart).</summary>
        public ConcurrentBag<string> Active { get; private set; } = [];

        public bool IsRunning => _process.IsRunning;

        /// <summary>Whether a plugin of this host may use proposed APIs (and so wants workspace notifications).</summary>
        public bool UsesProposedApi => Members.Any(p => p.Manifest?.EnableProposedApi == true);

        public JsonRpcConnection Connection => _process.Connection;

        private IEnumerable<PluginDescription> Members => _manager.Plugins.Where(p => p.HostGroup == _group);

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
                new LoadPluginsParams([.. members.Select(p => new PluginLoadEntry(p.Id, p.Manifest!.Version, p.InstallPath, p.Manifest.Entry.Assembly, p.Manifest.Entry.Type, p.Manifest.EnableProposedApi))]),
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
            foreach (var controller in _controllers.Keys)
            {
                _controllers.TryRemove(controller, out _);
                _manager.TestSink?.ControllerDisposed(controller);
            }

            foreach (var plugin in Members.Where(p => p.State == PluginState.Active))
            {
                plugin.State = PluginState.Ready;
            }

            var exit = e.ExitCode is { } code ? $" (exit code {code})" : string.Empty;
            if (e.WillRestart)
            {
                _manager._workbench.Notifications.Warning($"Plugin host '{_group}' stopped unexpectedly{exit}. Restarting…");
            }
            else
            {
                foreach (var plugin in Members.Where(p => p.IsUsable))
                {
                    plugin.State = PluginState.Failed;
                    plugin.Error = "Its plugin host crashed repeatedly and was stopped for this session.";
                }

                _ = _manager.ShowAndHandleAsync(_manager._workbench.Notifications.Error(
                    $"Plugin host '{_group}' crashed repeatedly and was stopped for this session{exit}.", "Show Log"));
            }

            _manager.PluginsChanged?.Invoke(_manager, EventArgs.Empty);
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
                    ? await prompts.InputAsync(p.Title, p.Prompt, p.Value, _manager.Find(p.PluginId)?.DisplayName ?? p.PluginId, ct).ConfigureAwait(false)
                    : null));
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
