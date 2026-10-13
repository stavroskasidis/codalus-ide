using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows.Input;
using DaedalusStudio.Plugins.Protocol;
using DaedalusStudio.Shell.Infrastructure;
using DaedalusStudio.Workbench.Output;
using DaedalusStudio.Workbench.Plugins;

namespace DaedalusStudio.Shell.ViewModels;

/// <summary>The Output tool window: one channel at a time, with clear and scroll lock.</summary>
public sealed class OutputViewModel : ObservableObject
{
    private OutputChannel? _selected;
    private bool _scrollLock;
    private bool _wordWrap;

    public OutputViewModel(OutputService output, EditorFont font)
    {
        Font = font;
        foreach (var channel in output.Channels)
        {
            Channels.Add(channel);
        }

        output.ChannelAdded += (_, channel) => Ui.Post(() =>
        {
            if (!Channels.Contains(channel))
            {
                Channels.Add(channel);
            }

            Selected ??= channel;
        });
        _selected = Channels.FirstOrDefault();
        ClearCommand = new RelayCommand(() => Selected?.Clear());
    }

    public EditorFont Font { get; }

    public ObservableCollection<OutputChannel> Channels { get; } = [];

    public OutputChannel? Selected
    {
        get => _selected;
        set => SetField(ref _selected, value);
    }

    public bool ScrollLock
    {
        get => _scrollLock;
        set => SetField(ref _scrollLock, value);
    }

    public bool WordWrap
    {
        get => _wordWrap;
        set => SetField(ref _wordWrap, value);
    }

    public ICommand ClearCommand { get; }
}

public sealed class PluginRowViewModel(PluginDescription plugin, bool turnedOff, Action<string, bool>? setTurnedOff)
{
    public string Name => plugin.DisplayName;

    public string Id => plugin.Id;

    public string Version => plugin.Manifest?.Version ?? "?";

    /// <summary>The state, and what changes at the next start when the user turned the plugin off or on (M23b).</summary>
    public string State => turnedOff == (plugin.State == PluginState.Disabled)
        ? plugin.State.ToString()
        : $"{plugin.State}, turned {(turnedOff ? "off" : "on")} from the next start";

    public string ToggleTitle => turnedOff ? "Turn On" : "Turn Off";

    public bool CanToggle => setTurnedOff is not null && plugin.State is not (PluginState.Invalid or PluginState.Incompatible);

    public ICommand ToggleCommand => new RelayCommand(_ => setTurnedOff?.Invoke(plugin.Id, !turnedOff));

    public string? Error => plugin.Error;

    public bool HasError => !string.IsNullOrEmpty(plugin.Error) && plugin.State is PluginState.Failed or PluginState.Invalid or PluginState.Incompatible;

    public string Details => $"{plugin.Id} {Version} · {(plugin.IsBuiltIn ? "built-in" : "installed")} · {plugin.HostGroup} host";

    public string AccessibleName => $"{Name}, {State}";
}

/// <summary>The Plugins tool window: installed plugins, their state and why a plugin was not loaded.</summary>
public sealed class PluginsViewModel : ObservableObject
{
    private readonly PluginManager _manager;
    private readonly Action<string, bool>? _setTurnedOff;

    public PluginsViewModel(PluginManager manager, Action<string, bool>? setTurnedOff = null)
    {
        _manager = manager;
        _setTurnedOff = setTurnedOff;
        manager.PluginsChanged += (_, _) => Ui.Post(Refresh);
        Refresh();
    }

    public ObservableCollection<PluginRowViewModel> Plugins { get; } = [];

    public bool IsEmpty => Plugins.Count == 0;

    public void Refresh()
    {
        Plugins.Clear();
        foreach (var plugin in _manager.Plugins.OrderBy(p => p.DisplayName, StringComparer.CurrentCultureIgnoreCase))
        {
            Plugins.Add(new PluginRowViewModel(plugin, _manager.IsTurnedOff(plugin.Id), _setTurnedOff));
        }

        OnPropertyChanged(nameof(IsEmpty));
    }
}

/// <summary>A toolbar button of a tool window (contributed to <c>toolWindow/title</c>).</summary>
public sealed class ToolbarActionViewModel(string title, string? keyLabel, ICommand command)
{
    public string Title { get; } = title;

    public string Tooltip { get; } = keyLabel is null ? title : $"{title} ({keyLabel})";

    public ICommand Command { get; } = command;
}

/// <summary>A plugin-provided tree view (ADR 0003). Children load lazily from the plugin host.</summary>
public sealed class TreeToolViewModel : ObservableObject
{
    private readonly PluginManager _plugins;
    private readonly Action<string, IReadOnlyList<JsonElement>> _run;
    private readonly Dictionary<string, TreeNodeViewModel> _byHandle = new(StringComparer.Ordinal);
    private readonly Func<IReadOnlyList<ToolbarActionViewModel>> _actions;
    private IReadOnlyList<ToolbarActionViewModel> _currentActions = [];
    private int _generation;
    private bool _loaded;
    private string? _message;

    public TreeToolViewModel(string viewId, PluginManager plugins, Action<string, IReadOnlyList<JsonElement>> run, Func<IReadOnlyList<ToolbarActionViewModel>> actions)
    {
        ViewId = viewId;
        _plugins = plugins;
        _run = run;
        _actions = actions;
        plugins.TreeViewRefreshed += (_, e) =>
        {
            if (e.ViewId == viewId)
            {
                Ui.Post(() => Refresh(e.Handle));
            }
        };
    }

    public string ViewId { get; }

    /// <summary>Title bar commands (<c>toolWindow/title</c>), re-evaluated by <see cref="RefreshActions"/>.</summary>
    public IReadOnlyList<ToolbarActionViewModel> Actions
    {
        get => _currentActions;
        private set
        {
            if (SetField(ref _currentActions, value))
            {
                OnPropertyChanged(nameof(HasActions));
            }
        }
    }

    public bool HasActions => _currentActions.Count > 0;

    public void RefreshActions() => Actions = _actions();

    public ObservableCollection<TreeNodeViewModel> Roots { get; } = [];

    /// <summary>Shown instead of the tree while loading, when empty or when the plugin failed.</summary>
    public string? Message
    {
        get => _message;
        private set
        {
            if (SetField(ref _message, value))
            {
                OnPropertyChanged(nameof(HasMessage));
            }
        }
    }

    public bool HasMessage => _message is not null;

    /// <summary>Called when the view appears; loads the roots once.</summary>
    public void EnsureLoaded()
    {
        if (!_loaded)
        {
            _loaded = true;
            _ = LoadRootsAsync();
        }
    }

    public void Activate(TreeNodeViewModel node)
    {
        if (node.Item?.Command is { } command)
        {
            _run(command.Id, command.Args ?? []);
        }
    }

    internal async Task LoadChildrenAsync(TreeNodeViewModel node)
    {
        var generation = _generation;
        var children = await FetchAsync(node.Item!.Handle).ConfigureAwait(true);
        if (generation == _generation)
        {
            node.SetChildren(children.Select(Create));
        }
    }

    private void Refresh(string? handle)
    {
        if (handle is not null && _byHandle.TryGetValue(handle, out var node))
        {
            if (node.IsExpanded)
            {
                _ = LoadChildrenAsync(node);
            }
            else
            {
                node.ResetChildren();
            }

            return;
        }

        if (_loaded)
        {
            _ = LoadRootsAsync();
        }
    }

    private async Task LoadRootsAsync()
    {
        var generation = ++_generation;
        if (Roots.Count == 0)
        {
            Message = "Loading…";
        }

        var roots = await FetchAsync(null).ConfigureAwait(true);
        if (generation != _generation)
        {
            return;
        }

        _byHandle.Clear();
        Roots.Clear();
        foreach (var root in roots)
        {
            Roots.Add(Create(root));
        }

        if (Roots.Count > 0)
        {
            Message = null;
        }
        else if (_plugins.Plugins.FirstOrDefault(p => p.ActivationEvents.Contains("onView:" + ViewId)) is { State: PluginState.Failed or PluginState.Invalid or PluginState.Incompatible } owner)
        {
            Message = $"{owner.DisplayName} is not available: {owner.Error}";
        }
        else
        {
            Message = "Nothing to show.";
        }
    }

    private async Task<IReadOnlyList<TreeItemDto>> FetchAsync(string? handle)
    {
        try
        {
            return await _plugins.GetTreeChildrenAsync(ViewId, handle, CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Message = $"Could not load this view: {ex.Message}";
            return [];
        }
        catch (OperationCanceledException)
        {
            Message = "The plugin did not respond in time.";
            return [];
        }
    }

    private TreeNodeViewModel Create(TreeItemDto item)
    {
        var node = new TreeNodeViewModel(this, item);
        _byHandle[item.Handle] = node;
        return node;
    }
}

public sealed class TreeNodeViewModel : ObservableObject
{
    private readonly TreeToolViewModel? _owner;
    private bool _isExpanded;
    private bool _childrenLoaded;

    internal TreeNodeViewModel(TreeToolViewModel? owner, TreeItemDto? item, string? placeholder = null)
    {
        _owner = owner;
        Item = item;
        Label = item?.Label ?? placeholder ?? string.Empty;
        if (item is { Collapsible: "collapsed" or "expanded" })
        {
            Children.Add(Placeholder());
            if (item.Collapsible == "expanded")
            {
                IsExpanded = true;
            }
        }
    }

    public TreeItemDto? Item { get; }

    public string Label { get; }

    public string? Description => Item?.Description;

    public bool HasDescription => !string.IsNullOrEmpty(Item?.Description);

    public string? Tooltip => Item?.Tooltip ?? Item?.Label;

    public string? Icon => Item?.Icon;

    public bool HasIcon => Item?.Icon is not null;

    public bool IsPlaceholder => Item is null;

    public string AccessibleName => HasDescription ? $"{Label}, {Description}" : Label;

    public ObservableCollection<TreeNodeViewModel> Children { get; } = [];

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (SetField(ref _isExpanded, value) && value && !_childrenLoaded && _owner is not null && Item is not null)
            {
                _childrenLoaded = true;
                _ = _owner.LoadChildrenAsync(this);
            }
        }
    }

    internal void SetChildren(IEnumerable<TreeNodeViewModel> children)
    {
        _childrenLoaded = true;
        Children.Clear();
        foreach (var child in children)
        {
            Children.Add(child);
        }
    }

    internal void ResetChildren()
    {
        _childrenLoaded = false;
        Children.Clear();
        Children.Add(Placeholder());
    }

    private static TreeNodeViewModel Placeholder() => new(null, null, "Loading…");
}

/// <summary>A plugin-provided Markdown view (ADR 0003).</summary>
public sealed class MarkdownToolViewModel : ObservableObject
{
    private readonly PluginManager _plugins;
    private string _markdown;
    private bool _activated;

    public MarkdownToolViewModel(string viewId, PluginManager plugins, Action<string, IReadOnlyList<JsonElement>> run)
    {
        ViewId = viewId;
        _plugins = plugins;
        Run = run;
        _markdown = plugins.GetMarkdown(viewId) ?? string.Empty;
        plugins.MarkdownChanged += (_, id) =>
        {
            if (id == viewId)
            {
                Ui.Post(() => Markdown = plugins.GetMarkdown(viewId) ?? string.Empty);
            }
        };
    }

    public string ViewId { get; }

    public Action<string, IReadOnlyList<JsonElement>> Run { get; }

    public string Markdown
    {
        get => _markdown;
        private set => SetField(ref _markdown, value);
    }

    public void EnsureActivated()
    {
        if (!_activated)
        {
            _activated = true;
            _ = _plugins.OnViewOpenedAsync(ViewId, CancellationToken.None);
        }
    }
}
