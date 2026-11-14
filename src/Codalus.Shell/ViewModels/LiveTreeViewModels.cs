using System.Collections.ObjectModel;
using Codalus.Shell.Infrastructure;
using Codalus.Workbench.Localization;
using Codalus.Workbench.Previewer;

namespace Codalus.Shell.ViewModels;

/// <summary>An element of the running app in the Live Visual Tree (M19b, ADR 0052).</summary>
public sealed class LiveNodeViewModel(int id, string type, string name, bool templated, bool isWindow) : ObservableObject
{
    private bool _isExpanded;

    public int Id { get; } = id;

    public string Type { get; } = type;

    public string Name { get; } = name;

    /// <summary>A template made it (the visual tree only): shown muted.</summary>
    public bool IsTemplated { get; } = templated;

    public bool IsWindow { get; } = isWindow;

    public string Label => Name.Length > 0 ? $"{Type}  {Name}" : Type;

    public string AccessibleName => (IsTemplated ? "template part " : string.Empty) + Label;

    public ObservableCollection<LiveNodeViewModel> Children { get; } = [];

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetField(ref _isExpanded, value);
    }

    public IEnumerable<LiveNodeViewModel> SelfAndDescendants() => Children.SelectMany(c => c.SelfAndDescendants()).Prepend(this);
}

/// <summary>
/// Debug → Windows → Live Visual Tree (M19b, ADR 0052; WPF apps in M40b, ADR 0072): the running app's windows and
/// elements, the logical tree by default (a classic IDE's "Just My XAML"), the visual tree with templates' parts on
/// request. Selecting an element highlights it in the app and shows its properties; Select Element in the App picks one
/// by clicking it.
/// </summary>
public sealed class LiveTreeViewModel : ObservableObject
{
    public const string ToolWindowId = "debug.liveVisualTree";

    private readonly Func<LiveSession?> _session;
    private bool _showVisualTree;
    private bool _isPicking;
    private LiveNodeViewModel? _selected;
    private string _message = NotRunning;
    private int _generation;

    /// <summary>What the tree says before an app runs: WPF apps run (and get the live tools) on Windows only (D31).</summary>
    internal static string NotRunning => OperatingSystem.IsWindows()
        ? Localizer.Get("msg.liveTree.notRunningWindows", "Start an Avalonia or WPF app (F5 or Ctrl+F5) to see its live visual tree.")
        : Localizer.Get("msg.liveTree.notRunning", "Start an Avalonia app (F5 or Ctrl+F5) to see its live visual tree. A WPF app's live visual tree is Windows only.");

    public LiveTreeViewModel(Func<LiveSession?> session, Func<LiveNodeViewModel, Task> goToXaml)
    {
        _session = session;
        RefreshCommand = new RelayCommand(_ => _ = RefreshAsync());
        GoToXamlCommand = new RelayCommand(_ =>
        {
            if (Selected is { } node)
            {
                _ = goToXaml(node);
            }
        });
    }

    public ObservableCollection<LiveNodeViewModel> Roots { get; } = [];

    public RelayCommand RefreshCommand { get; }

    /// <summary>Opens the XAML that declares the selected element, at its line.</summary>
    public RelayCommand GoToXamlCommand { get; }

    /// <summary>The selection changed (the Live Property Explorer follows it).</summary>
    public event EventHandler<LiveNodeViewModel?>? SelectionChanged;

    public LiveNodeViewModel? Selected
    {
        get => _selected;
        set
        {
            if (SetField(ref _selected, value))
            {
                SelectionChanged?.Invoke(this, value);
                if (_session() is { IsConnected: true } session)
                {
                    _ = Quietly(session.HighlightAsync(value?.Id, CancellationToken.None));
                }
            }
        }
    }

    public bool ShowVisualTree
    {
        get => _showVisualTree;
        set
        {
            if (SetField(ref _showVisualTree, value))
            {
                _ = RefreshAsync();
            }
        }
    }

    /// <summary>Select Element in the App: the next click in the app selects its element here.</summary>
    public bool IsPicking
    {
        get => _isPicking;
        set
        {
            if (SetField(ref _isPicking, value) && _session() is { IsConnected: true } session)
            {
                _ = Quietly(session.PickAsync(value, CancellationToken.None));
            }
        }
    }

    public string Message
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

    public bool HasMessage => Message.Length > 0;

    public bool IsConnected => _session() is { IsConnected: true };

    /// <summary>Reads the windows and their trees again, keeping the selection by id.</summary>
    public async Task RefreshAsync()
    {
        var generation = ++_generation;
        OnPropertyChanged(nameof(IsConnected));
        if (_session() is not { IsConnected: true } session)
        {
            Roots.Clear();
            Selected = null;
            Message = NotRunning;
            return;
        }

        try
        {
            var selectedId = Selected?.Id;
            var roots = new List<LiveNodeViewModel>();
            foreach (var window in await session.WindowsAsync(CancellationToken.None).ConfigureAwait(true))
            {
                var nodes = await session.TreeAsync(window.Id, ShowVisualTree, CancellationToken.None).ConfigureAwait(true);
                var byId = new Dictionary<int, LiveNodeViewModel>();
                LiveNodeViewModel? root = null;
                foreach (var node in nodes)
                {
                    var item = new LiveNodeViewModel(node.Id, node.Type, node.Name, node.Templated, node.ParentId == 0);
                    byId[node.Id] = item;
                    if (node.ParentId != 0 && byId.TryGetValue(node.ParentId, out var parent))
                    {
                        parent.Children.Add(item);
                    }
                    else
                    {
                        root ??= item;
                    }
                }

                if (root is not null)
                {
                    root.IsExpanded = true;
                    roots.Add(root);
                }
            }

            if (generation != _generation)
            {
                return;
            }

            Roots.Clear();
            foreach (var root in roots)
            {
                Roots.Add(root);
            }

            Message = roots.Count == 0 ? Localizer.Get("msg.liveTree.noWindow", "The app has no window open.") : string.Empty;
            Selected = selectedId is { } id ? Find(id) : null;
            if (roots.Count == 0)
            {
                RetryLater(generation);
            }
        }
        catch (PreviewerException ex)
        {
            Message = ex.Message;
            RetryLater(generation);
        }
    }

    /// <summary>
    /// The agent connects when the app starts, before Avalonia is up and its first window opens: while connected with
    /// nothing to show, ask again every second.
    /// </summary>
    private void RetryLater(int generation)
    {
        _ = Task.Delay(TimeSpan.FromSeconds(1)).ContinueWith(_ => Ui.Post(() =>
        {
            if (generation == _generation && _session() is { IsConnected: true })
            {
                _ = RefreshAsync();
            }
        }), TaskScheduler.Default);
    }

    /// <summary>Selects the element with <paramref name="id"/> (a pick in the app), expanding its ancestors.</summary>
    public async Task SelectAsync(int id)
    {
        IsPicking = false;
        if (Find(id) is null)
        {
            await RefreshAsync().ConfigureAwait(true);
        }

        if (Find(id) is { } node)
        {
            foreach (var root in Roots)
            {
                Expand(root, node);
            }

            Selected = node;
        }
    }

    private static bool Expand(LiveNodeViewModel current, LiveNodeViewModel target)
    {
        if (current == target)
        {
            return true;
        }

        foreach (var child in current.Children)
        {
            if (Expand(child, target))
            {
                current.IsExpanded = true;
                return true;
            }
        }

        return false;
    }

    public LiveNodeViewModel? Find(int id) => Roots.SelectMany(r => r.SelfAndDescendants()).FirstOrDefault(n => n.Id == id);

    private static async Task Quietly(Task task)
    {
        try
        {
            await task.ConfigureAwait(true);
        }
        catch (PreviewerException)
        {
            // The app ended meanwhile: the next refresh says so.
        }
    }
}

/// <summary>A property in the Live Property Explorer; <see cref="Value"/> is editable unless it's read-only.</summary>
public sealed class LivePropertyRow(LiveProperty property, Func<LivePropertyRow, string, Task> commit, Func<LivePropertyRow, Task>? toggleWatch = null, bool watched = false) : ObservableObject
{
    private string _value = property.Value;
    private string _error = string.Empty;
    private bool _isWatched = watched;

    public string Name { get; } = property.Name;

    /// <summary>Break When Value Changes is on for this property (M64b): the debugger stops when it changes.</summary>
    public bool IsWatched
    {
        get => _isWatched;
        set
        {
            if (SetField(ref _isWatched, value))
            {
                OnPropertyChanged(nameof(WatchHeader));
            }
        }
    }

    /// <summary>The context menu's item for Break When Value Changes (checked when on).</summary>
    public string WatchHeader => (IsWatched ? "✓ " : string.Empty) + Localizer.Get("liveProperties.breakWhenValueChanges", "Break When Value Changes");

    /// <summary>Break When Value Changes on or off (M64b).</summary>
    public RelayCommand ToggleWatchCommand => new(() =>
    {
        if (toggleWatch is not null)
        {
            _ = toggleWatch(this);
        }
    });

    public string Type { get; } = property.Type;

    public string Source { get; } = property.Source;

    public bool IsReadOnly { get; } = property.IsReadOnly;

    public bool IsEditable => !IsReadOnly;

    /// <summary>A local value stands out, as in a classic IDE (it's the one an edit changes).</summary>
    public bool IsLocal => Source == "Local";

    public Avalonia.Media.FontWeight NameWeight => IsLocal ? Avalonia.Media.FontWeight.SemiBold : Avalonia.Media.FontWeight.Normal;

    public string AccessibleName => $"{Name}: {Value} ({Source})";

    /// <summary>The name and type, for the trimmed name (M34g: the tooltip had the type alone).</summary>
    public string ToolTip => $"{Name}: {Type}";

    public string Value
    {
        get => _value;
        set
        {
            if (SetField(ref _value, value))
            {
                _ = commit(this, value);
            }
        }
    }

    public string Error
    {
        get => _error;
        set
        {
            if (SetField(ref _error, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => Error.Length > 0;
}

/// <summary>
/// Debug → Windows → Live Property Explorer (M19b, ADR 0052; M40b, ADR 0072): the selected element's Avalonia or WPF
/// dependency properties, their values and where they come from (local, style, template, inherited, default); editing
/// a value sets it as a local value in the running app (not in the XAML). A property's context menu has Break When
/// Value Changes (M64b), and Styles lists why each style of the element's scope applies to it or doesn't.
/// </summary>
public sealed class LivePropertiesViewModel : ObservableObject
{
    public const string ToolWindowId = "debug.livePropertyExplorer";

    private readonly Func<LiveSession?> _session;
    private readonly HashSet<string> _watched = new(StringComparer.Ordinal);
    private LiveSession? _watchedIn;
    private LiveNodeViewModel? _element;
    private string _filter = string.Empty;
    private List<LiveProperty> _all = [];
    private int _generation;
    private string _stylesMessage = string.Empty;

    public LivePropertiesViewModel(Func<LiveSession?> session) => _session = session;

    public ObservableCollection<LivePropertyRow> Rows { get; } = [];

    /// <summary>The element's styles and why each applies or doesn't (M64b).</summary>
    public ObservableCollection<LiveStyleRow> Styles { get; } = [];

    /// <summary>Opens a style's XAML at its line (the shell's).</summary>
    public Func<string, int, Task>? OpenStyle { get; set; }

    /// <summary>Why no style is listed (none in scope, or the agent couldn't say).</summary>
    public string StylesMessage
    {
        get => _stylesMessage;
        private set
        {
            if (SetField(ref _stylesMessage, value))
            {
                OnPropertyChanged(nameof(HasStylesMessage));
            }
        }
    }

    public bool HasStylesMessage => StylesMessage.Length > 0;

    /// <summary>Whether Break When Value Changes is on for the element's property in the current run.</summary>
    public bool IsWatched(int element, string property) => ReferenceEquals(_watchedIn, _session()) && _watched.Contains(WatchKey(element, property));

    private static string WatchKey(int element, string property) => element.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\u001f" + property;

    /// <summary>Break When Value Changes on the row's property, or off (M64b): the agent watches it in the app.</summary>
    public async Task ToggleWatchAsync(LivePropertyRow row)
    {
        if (_element is not { } element || _session() is not { IsConnected: true } session)
        {
            return;
        }

        if (!ReferenceEquals(_watchedIn, session))
        {
            _watched.Clear();
            _watchedIn = session;
        }

        var key = WatchKey(element.Id, row.Name);
        var on = !_watched.Contains(key);
        try
        {
            await session.WatchAsync(element.Id, row.Name, on, CancellationToken.None).ConfigureAwait(true);
            if (on)
            {
                _watched.Add(key);
            }
            else
            {
                _watched.Remove(key);
            }

            row.IsWatched = on;
            row.Error = string.Empty;
        }
        catch (PreviewerException ex)
        {
            row.Error = ex.Message;
        }
    }

    public string Title => _element is null ? Localizer.Get("msg.liveTree.selectElement", "Select an element in the Live Visual Tree.") : _element.Label;

    public string Filter
    {
        get => _filter;
        set
        {
            if (SetField(ref _filter, value))
            {
                Fill();
            }
        }
    }

    public async Task ShowAsync(LiveNodeViewModel? element)
    {
        if (element?.Id != _element?.Id)
        {
            Styles.Clear(); // another element's styles never show as this one's while its own come
        }

        _element = element;
        OnPropertyChanged(nameof(Title));
        var generation = ++_generation;
        if (element is null || _session() is not { IsConnected: true } session)
        {
            _all = [];
            Fill();
            Styles.Clear();
            StylesMessage = string.Empty;
            return;
        }

        try
        {
            var properties = await session.PropertiesAsync(element.Id, CancellationToken.None).ConfigureAwait(true);
            if (generation == _generation)
            {
                _all = [.. properties];
                Fill();
            }
        }
        catch (PreviewerException)
        {
            _all = [];
            Fill();
        }

        await ShowStylesAsync(session, element, generation).ConfigureAwait(true);
    }

    /// <summary>The element's styles and why each applies or doesn't (M64b).</summary>
    private async Task ShowStylesAsync(LiveSession session, LiveNodeViewModel element, int generation)
    {
        try
        {
            var styles = await session.StylesAsync(element.Id, CancellationToken.None).ConfigureAwait(true);
            if (generation != _generation)
            {
                return;
            }

            Styles.Clear();
            foreach (var style in styles)
            {
                Styles.Add(new LiveStyleRow(style, OpenStyle));
            }

            StylesMessage = styles.Count == 0 ? Localizer.Get("liveProperties.noStyles", "No style in the element's scope targets its type.") : string.Empty;
        }
        catch (PreviewerException ex)
        {
            if (generation == _generation)
            {
                Styles.Clear();
                StylesMessage = ex.Message;
            }
        }
    }

    private void Fill()
    {
        Rows.Clear();
        foreach (var property in _all.Where(p => Filter.Length == 0 || p.Name.Contains(Filter, StringComparison.OrdinalIgnoreCase)))
        {
            Rows.Add(new LivePropertyRow(property, CommitAsync, ToggleWatchAsync, _element is { } element && IsWatched(element.Id, property.Name)));
        }
    }

    private async Task CommitAsync(LivePropertyRow row, string value)
    {
        if (_element is not { } element || _session() is not { IsConnected: true } session)
        {
            return;
        }

        try
        {
            await session.SetAsync(element.Id, row.Name, value, CancellationToken.None).ConfigureAwait(true);
            row.Error = string.Empty;
            await ShowAsync(element).ConfigureAwait(true); // its source is Local now, and others may follow
        }
        catch (PreviewerException ex)
        {
            row.Error = ex.Message;
        }
    }
}

/// <summary>
/// A style in the Live Property Explorer's Styles (M64b): its selector (or WPF trigger), whether it applies to the
/// element, the part that fails when it doesn't, its setters, and Go to its XAML when the app knows where it is.
/// </summary>
public sealed class LiveStyleRow(LiveStyle style, Func<string, int, Task>? open)
{
    public string Selector { get; } = style.Selector;

    public LiveStyleState State { get; } = style.State;

    /// <summary>A mark for the state: applies, inactive now, doesn't apply.</summary>
    public string Glyph => State switch
    {
        LiveStyleState.Applies => "✓",
        LiveStyleState.Inactive => "◌",
        _ => "✗",
    };

    public string StateText => State switch
    {
        LiveStyleState.Applies => Localizer.Get("liveProperties.styleApplies", "Applies"),
        LiveStyleState.Inactive => Localizer.Get("liveProperties.styleInactive", "Inactive now"),
        _ => Localizer.Get("liveProperties.styleDoesntApply", "Doesn't apply"),
    };

    /// <summary>The part of the selector that fails, and why (empty when it applies).</summary>
    public string Reason { get; } = style.Reason;

    public bool HasReason => Reason.Length > 0;

    /// <summary>Its setters, one per line (a higher priority's override noted).</summary>
    public string Setters { get; } = string.Join("\n", style.Setters);

    public bool HasSetters => Setters.Length > 0;

    /// <summary>The kind: a style, a control theme, a theme's nested style; WPF's style, based-on style, trigger.</summary>
    public string Kind { get; } = style.Kind;

    public string Location { get; } = style.File.Length > 0 ? $"{Path.GetFileName(style.File)}:{style.Line.ToString(System.Globalization.CultureInfo.InvariantCulture)}" : string.Empty;

    public bool HasLocation => Location.Length > 0 && open is not null;

    public string AccessibleName => $"{Selector}: {StateText}. {Reason}";

    public RelayCommand GoToCommand => new(() =>
    {
        if (open is not null && Style.File.Length > 0)
        {
            _ = open(Style.File, Style.Line);
        }
    });

    /// <summary>The style's source, for tests.</summary>
    public LiveStyle Style { get; } = style;
}
