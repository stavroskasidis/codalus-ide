using System.Text.Json;
using Codalus.Plugins.Host.Proposed;
using Codalus.Plugins.Proposed;
using Codalus.Plugins.Protocol;

namespace Codalus.Plugins.Host;

/// <summary>The <see cref="IPluginContext"/> handed to one plugin; tracks everything it registers.</summary>
internal sealed class PluginContext : IPluginContext, ICommands, IWindow, IPluginLog, IProposedApiProvider, IDisposable
{
    private readonly PluginHostServer _server;
    private readonly List<IDisposable> _owned = [];
    private readonly PluginProposedApi _services;
    private readonly bool _enableProposedApi;

    public PluginContext(PluginHostServer server, PluginHostServer.LoadedPlugin plugin)
    {
        _server = server;
        Plugin = plugin.Info;
        Settings = new PluginSettings(server.Settings);
        _services = new PluginProposedApi(server.Proposed, plugin.Info.Id);
        _enableProposedApi = plugin.EnableProposedApi;
    }

    // Stable since API 1.0 (proposed in 0.2.x): every plugin has them.
    public ITests Tests => _services;

    public IWorkspace Workspace => _services;

    public IBuild Build => _services;

    public IDebug Debug => _services;

    public ITerminals Terminals => _services;

    public IHosting Hosting => _services;

    public ISecrets Secrets => _services.Secrets;

    public IFileSystems FileSystems => _fileSystems ??= new PluginFileSystems(_server, Id, Own);

    private PluginFileSystems? _fileSystems;

    /// <summary>The plugin's file system providers, registered with the host and announced to the shell.</summary>
    private sealed class PluginFileSystems(PluginHostServer server, string pluginId, Func<IDisposable, IDisposable> own) : IFileSystems
    {
        public IDisposable RegisterProvider(string scheme, IFileSystemProvider provider)
        {
            if (scheme.Length == 0 || !scheme.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.'))
            {
                throw new ArgumentException($"'{scheme}' isn't a scheme: letters, digits, '-' and '.'.", nameof(scheme));
            }

            return own(server.RegisterFileSystem(pluginId, scheme, provider));
        }

        public string RootOf(string scheme) => FileSystemRoots.RootOf(scheme);

        public void NotifyChanged(string scheme, IReadOnlyList<string> paths, string kind = "changed") =>
            server.Post(PluginMethods.FsChanged, new FsChangedParams(scheme, [.. paths], kind), PluginJsonContext.Default.FsChangedParams);
    }

    /// <summary>New proposals (none at 1.0), for plugins that enable them.</summary>
    public IProposedApi? Proposed => _enableProposedApi ? _services : null;

    public PluginInfo Plugin { get; }

    public ICommands Commands => this;

    public IWindow Window => this;

    public ISettings Settings { get; }

    public IPluginLog Log => this;

    public IList<IDisposable> Subscriptions { get; } = [];

    private string Id => Plugin.Id;

    private T Own<T>(T disposable)
        where T : IDisposable
    {
        lock (_owned)
        {
            _owned.Add(disposable);
        }

        return disposable;
    }

    public void Dispose() => DisposeAll();

    /// <summary>Disposes the plugin's subscriptions and everything it registered (deactivation).</summary>
    public void DisposeAll()
    {
        foreach (var subscription in Subscriptions.Reverse())
        {
            subscription.Dispose();
        }

        Subscriptions.Clear();
        lock (_owned)
        {
            foreach (var item in Enumerable.Reverse(_owned))
            {
                item.Dispose();
            }

            _owned.Clear();
        }

        ((PluginSettings)Settings).Detach();
        _services.Dispose();
    }

    // ------------------------------------------------------------------ commands

    public IDisposable Register(string commandId, Func<CommandInvocation, CancellationToken, Task<object?>> handler) =>
        Own(_server.RegisterCommand(Id, commandId, handler));

    public IDisposable Register(string commandId, Action handler) =>
        Register(commandId, (_, _) =>
        {
            handler();
            return Task.FromResult<object?>(null);
        });

    public async Task<JsonElement?> ExecuteAsync(string commandId, CancellationToken cancellationToken, params object?[] args)
    {
        var result = await _server.RequestAsync(
            PluginMethods.ExecuteCommandInShell,
            new ExecuteCommandParams(commandId, [.. args.Select(PluginHostServer.ToJson)]),
            PluginJsonContext.Default.ExecuteCommandParams,
            PluginJsonContext.Default.ExecuteCommandResult,
            cancellationToken).ConfigureAwait(false);
        return result.Result;
    }

    // ------------------------------------------------------------------ window

    public async Task<string?> ShowNotificationAsync(NotificationSeverity severity, string message, IReadOnlyList<string>? actions = null, CancellationToken cancellationToken = default)
    {
        var result = await _server.RequestAsync(
            PluginMethods.ShowNotification,
            new ShowNotificationParams(Id, severity.ToString().ToLowerInvariant(), message, [.. actions ?? []]),
            PluginJsonContext.Default.ShowNotificationParams,
            PluginJsonContext.Default.ShowNotificationResult,
            cancellationToken).ConfigureAwait(false);
        return result.Action;
    }

    public async Task<QuickPickItem?> ShowQuickPickAsync(IReadOnlyList<QuickPickItem> items, string? placeholder = null, CancellationToken cancellationToken = default)
    {
        var result = await _server.RequestAsync(
            PluginMethods.ShowQuickPick,
            new ShowQuickPickParams(Id, [.. items.Select(i => new QuickPickItemDto(i.Label, i.Description, i.Detail))], placeholder),
            PluginJsonContext.Default.ShowQuickPickParams,
            PluginJsonContext.Default.ShowQuickPickResult,
            cancellationToken).ConfigureAwait(false);
        return result.Index is int index && index >= 0 && index < items.Count ? items[index] : null;
    }

    public async Task<string?> ShowInputBoxAsync(InputBoxOptions options, CancellationToken cancellationToken = default)
    {
        var result = await _server.RequestAsync(
            PluginMethods.ShowInputBox,
            new ShowInputBoxParams(Id, options.Title, options.Prompt, options.Value),
            PluginJsonContext.Default.ShowInputBoxParams,
            PluginJsonContext.Default.ShowInputBoxResult,
            cancellationToken).ConfigureAwait(false);
        return result.Value;
    }

    public async Task<T> WithProgressAsync<T>(string title, Func<IProgress<ProgressReport>, CancellationToken, Task<T>> work, bool cancellable = false, CancellationToken cancellationToken = default)
    {
        var json = PluginJsonContext.Default;
        var id = _server.NewProgressId();
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var registration = _server.RegisterProgress(id, cancel);
        _server.Post(PluginMethods.ProgressStart, new ProgressStartParams(Id, id, title, cancellable), json.ProgressStartParams);
        try
        {
            var progress = new Progress(report => _server.Post(PluginMethods.ProgressReport, new ProgressReportParams(Id, id, report.Message, report.Percent), json.ProgressReportParams));
            return await work(progress, cancel.Token).ConfigureAwait(false);
        }
        finally
        {
            _server.Post(PluginMethods.ProgressEnd, new ProgressIdParams(Id, id), json.ProgressIdParams);
        }
    }

    /// <summary>Reports straight away, in order (<see cref="Progress{T}"/> would post to a synchronization context).</summary>
    private sealed class Progress(Action<ProgressReport> report) : IProgress<ProgressReport>
    {
        public void Report(ProgressReport value) => report(value);
    }

    public IOutputChannel CreateOutputChannel(string name) => new OutputChannel(_server, Id, name);

    public void SetView(string viewId, ViewNode root) =>
        _server.Post(PluginMethods.ViewSet, new ViewSetParams(Id, viewId, ToDto(root)), PluginJsonContext.Default.ViewSetParams);

    public IDisposable OnViewEvent(string viewId, Func<ViewEvent, CancellationToken, Task> handler) =>
        Own(_server.RegisterViewHandler(viewId, handler));

    public IDisposable RegisterCustomEditor(string viewType, ICustomEditorProvider provider) =>
        Own(_server.RegisterCustomEditor(viewType, provider));

    public void SetCustomEditorText(string viewId, string text) =>
        _server.Post(PluginMethods.CustomEditorSetText, new CustomEditorTextParams(Id, viewId, text), PluginJsonContext.Default.CustomEditorTextParams);

    private static ViewNodeDto ToDto(ViewNode node) => new(node.Kind)
    {
        Id = node.Id,
        Text = node.Text,
        Style = node.Style,
        Value = node.Value,
        Placeholder = node.Placeholder,
        Checked = node.Checked,
        Disabled = node.Disabled,
        Options = node.Options is { } options ? [.. options] : null,
        Columns = node.Columns is { } columns ? [.. columns] : null,
        Rows = node.Rows is { } rows ? [.. rows.Select(r => r.ToArray())] : null,
        Children = node.Children is { } children ? [.. children.Select(ToDto)] : null,
        Orientation = node.Orientation,
    };

    public IStatusBarItem CreateStatusBarItem(string id, StatusBarAlignment alignment = StatusBarAlignment.Left, int priority = 0) =>
        Own(new StatusBarItem(_server, Id, id, alignment, priority));

    public IDisposable RegisterTreeDataProvider<T>(string viewId, ITreeDataProvider<T> provider)
        where T : notnull
    {
        var adapter = new TreeAdapter<T>(_server, Id, viewId, provider);
        var registration = _server.RegisterTree(viewId, adapter);
        _server.Post(PluginMethods.TreeViewRefresh, new TreeViewRefreshParams(viewId, null), PluginJsonContext.Default.TreeViewRefreshParams);
        return Own(new PluginHostServer.Disposable(() =>
        {
            adapter.Dispose();
            registration.Dispose();
        }));
    }

    public void SetMarkdown(string viewId, string markdown) =>
        _server.Post(PluginMethods.MarkdownViewSet, new MarkdownViewSetParams(viewId, markdown), PluginJsonContext.Default.MarkdownViewSetParams);

    // ------------------------------------------------------------------ log

    public void Write(PluginLogLevel level, string message) => _server.Log(Id, level.ToString().ToLowerInvariant(), message);

    private sealed class OutputChannel(PluginHostServer server, string pluginId, string name) : IOutputChannel
    {
        public string Name { get; } = name;

        public void Append(string text) => Send(PluginMethods.OutputAppend, text);

        public void AppendLine(string line) => Send(PluginMethods.OutputAppend, line + "\n");

        public void Clear() => Send(PluginMethods.OutputClear, null);

        public void Show() => Send(PluginMethods.OutputShow, null);

        private void Send(string method, string? text) =>
            server.Post(method, new OutputParams(pluginId, Name, text), PluginJsonContext.Default.OutputParams);
    }

    private sealed class StatusBarItem(PluginHostServer server, string pluginId, string itemId, StatusBarAlignment alignment, int priority) : IStatusBarItem
    {
        private string _text = string.Empty;
        private string? _tooltip;
        private string? _command;
        private bool _visible;
        private bool _disposed;

        public string Text
        {
            get => _text;
            set
            {
                _text = value;
                Push();
            }
        }

        public string? Tooltip
        {
            get => _tooltip;
            set
            {
                _tooltip = value;
                Push();
            }
        }

        public string? Command
        {
            get => _command;
            set
            {
                _command = value;
                Push();
            }
        }

        public void Show()
        {
            _visible = true;
            Push(force: true);
        }

        public void Hide()
        {
            _visible = false;
            Push(force: true);
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                server.Post(PluginMethods.StatusBarRemove, new StatusBarRemoveParams(pluginId, itemId), PluginJsonContext.Default.StatusBarRemoveParams);
            }
        }

        private void Push(bool force = false)
        {
            if (_disposed || (!_visible && !force))
            {
                return;
            }

            server.Post(
                PluginMethods.StatusBarUpdate,
                new StatusBarUpdateParams(pluginId, itemId, _text, _tooltip, _command, alignment == StatusBarAlignment.Right ? "right" : "left", priority, _visible),
                PluginJsonContext.Default.StatusBarUpdateParams);
        }
    }
}

/// <summary>The host's copy of the effective settings, kept current by <c>settings/didChange</c>.</summary>
internal sealed class SettingsCache
{
    private readonly Lock _gate = new();
    private Dictionary<string, JsonElement> _values = new(StringComparer.Ordinal);

    public event EventHandler<IReadOnlyCollection<string>>? Changed;

    public void Replace(Dictionary<string, JsonElement> values)
    {
        lock (_gate)
        {
            _values = new Dictionary<string, JsonElement>(values, StringComparer.Ordinal);
        }
    }

    public void Apply(Dictionary<string, JsonElement> changes)
    {
        lock (_gate)
        {
            foreach (var (key, value) in changes)
            {
                if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                {
                    _values.Remove(key);
                }
                else
                {
                    _values[key] = value;
                }
            }
        }

        Changed?.Invoke(this, changes.Keys);
    }

    public bool TryGet(string key, out JsonElement value)
    {
        lock (_gate)
        {
            return _values.TryGetValue(key, out value);
        }
    }
}

internal sealed class PluginSettings : ISettings
{
    private readonly SettingsCache _cache;

    public PluginSettings(SettingsCache cache)
    {
        _cache = cache;
        _cache.Changed += OnChanged;
    }

    public event EventHandler<SettingsChangedEventArgs>? Changed;

    public T GetValue<T>(string key, T fallback)
    {
        if (!_cache.TryGet(key, out var value))
        {
            return fallback;
        }

        try
        {
            return value.Deserialize<T>(PluginHostServer.PluginValueOptions) ?? fallback;
        }
        catch (JsonException)
        {
            return fallback;
        }
    }

    public void Detach() => _cache.Changed -= OnChanged;

    private void OnChanged(object? sender, IReadOnlyCollection<string> keys) => Changed?.Invoke(this, new SettingsChangedEventArgs(keys));
}
