using System.Collections.ObjectModel;
using DaedalusStudio.Shell.Infrastructure;
using DaedalusStudio.Workbench.Previewer;

namespace DaedalusStudio.Shell.ViewModels;

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
/// Debug → Windows → Live Visual Tree (M19b, ADR 0052): the running Avalonia app's windows and elements, the logical
/// tree by default (a classic IDE's "Just My XAML"), the visual tree with templates' parts on request. Selecting an
/// element highlights it in the app and shows its properties; Select Element in the App picks one by clicking it.
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

    private const string NotRunning = "Start an Avalonia app (F5 or Ctrl+F5) to see its live visual tree.";

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

            Message = roots.Count == 0 ? "The app has no window open." : string.Empty;
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
public sealed class LivePropertyRow(LiveProperty property, Func<LivePropertyRow, string, Task> commit) : ObservableObject
{
    private string _value = property.Value;
    private string _error = string.Empty;

    public string Name { get; } = property.Name;

    public string Type { get; } = property.Type;

    public string Source { get; } = property.Source;

    public bool IsReadOnly { get; } = property.IsReadOnly;

    public bool IsEditable => !IsReadOnly;

    /// <summary>A local value stands out, as in a classic IDE (it's the one an edit changes).</summary>
    public bool IsLocal => Source == "Local";

    public Avalonia.Media.FontWeight NameWeight => IsLocal ? Avalonia.Media.FontWeight.SemiBold : Avalonia.Media.FontWeight.Normal;

    public string AccessibleName => $"{Name}: {Value} ({Source})";

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
/// Debug → Windows → Live Property Explorer (M19b, ADR 0052): the selected element's Avalonia properties, their values
/// and where they come from (local, style, template, inherited, default); editing a value sets it as a local value in
/// the running app (not in the XAML).
/// </summary>
public sealed class LivePropertiesViewModel : ObservableObject
{
    public const string ToolWindowId = "debug.livePropertyExplorer";

    private readonly Func<LiveSession?> _session;
    private LiveNodeViewModel? _element;
    private string _filter = string.Empty;
    private List<LiveProperty> _all = [];
    private int _generation;

    public LivePropertiesViewModel(Func<LiveSession?> session) => _session = session;

    public ObservableCollection<LivePropertyRow> Rows { get; } = [];

    public string Title => _element is null ? "Select an element in the Live Visual Tree." : _element.Label;

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
        _element = element;
        OnPropertyChanged(nameof(Title));
        var generation = ++_generation;
        if (element is null || _session() is not { IsConnected: true } session)
        {
            _all = [];
            Fill();
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
    }

    private void Fill()
    {
        Rows.Clear();
        foreach (var property in _all.Where(p => Filter.Length == 0 || p.Name.Contains(Filter, StringComparison.OrdinalIgnoreCase)))
        {
            Rows.Add(new LivePropertyRow(property, CommitAsync));
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
