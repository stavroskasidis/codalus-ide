using Codalus.Shell.Infrastructure;
using Codalus.Workbench.Localization;
using Codalus.Workbench.Search;

namespace Codalus.Shell.ViewModels;

/// <summary>An entry offered by the quick pick (a command, a theme…).</summary>
public sealed record QuickPickItem(string Label)
{
    /// <summary>Muted text after the label, e.g. "recently used".</summary>
    public string? Detail { get; init; }

    /// <summary>Key binding shown on the right.</summary>
    public string? KeyLabel { get; init; }

    /// <summary>An icon before the label (a file's type, a symbol's kind; M34a).</summary>
    public string? Icon { get; init; }

    public object? Tag { get; init; }
}

public sealed class QuickPickRow(QuickPickItem item, IReadOnlyList<int> highlights)
{
    public QuickPickItem Item { get; } = item;

    public string Label => Item.Label;

    public IReadOnlyList<int> Highlights { get; } = highlights;

    public string? Detail => Item.Detail;

    public string? KeyLabel => Item.KeyLabel;

    public bool HasDetail => !string.IsNullOrEmpty(Item.Detail);

    public bool HasKeyLabel => !string.IsNullOrEmpty(Item.KeyLabel);

    public string? Icon => Item.Icon;

    public bool HasIcon => Item.Icon is not null;

    /// <summary>What screen readers announce for the row: the label, its detail and what's on the right.</summary>
    public string AccessibleName => string.Join(", ", new[] { Label, Item.Detail, Item.KeyLabel }.Where(s => !string.IsNullOrEmpty(s)));

    /// <summary>The right column's widest: half the row (a row's width in, the most its key label or detail may take out).</summary>
    public static Avalonia.Data.Converters.IValueConverter HalfWidth { get; } =
        new Avalonia.Data.Converters.FuncValueConverter<double, double>(width => width > 0 ? width / 2 : double.PositiveInfinity);
}

/// <summary>
/// The command palette overlay, also used as a generic picker (<see cref="ShowAsync"/>). Filtering is fuzzy;
/// with an empty query the items keep the order they were given in (most recently used first for commands). It shows
/// the <see cref="MaxRows"/> best matches, and how many more there are.
/// </summary>
public sealed class QuickPickViewModel : ObservableObject, IDisposable
{
    /// <summary>
    /// The most rows shown: the best matches, the others counted (M56: with 20,000 branches each keystroke added every
    /// match to the list, a change at a time). Typing narrows them down.
    /// </summary>
    public const int MaxRows = 1000;

    private IReadOnlyList<QuickPickItem> _items = [];
    private Func<string, CancellationToken, Task<IReadOnlyList<QuickPickItem>>>? _provider;
    private CancellationTokenSource? _providerCts;
    private TaskCompletionSource<QuickPickItem?>? _pending;
    private Action<QuickPickItem?>? _onHighlight;
    private string _query = string.Empty;
    private string _placeholder = string.Empty;
    private bool _isOpen;
    private int _selectedIndex = -1;
    private int _hidden;

    /// <summary>The rows shown, replaced with one reset per query.</summary>
    public ResettableCollection<QuickPickRow> Rows { get; } = [];

    /// <summary>Whether more items match than <see cref="MaxRows"/> shows.</summary>
    public bool HasMore => _hidden > 0;

    /// <summary>How many more match than are shown.</summary>
    public string MoreText => Localizer.Format("quickPick.moreMatches", "{0} more match: type to narrow the list down", _hidden.ToString("N0", System.Globalization.CultureInfo.CurrentCulture));

    public string Query
    {
        get => _query;
        set
        {
            if (SetField(ref _query, value ?? string.Empty))
            {
                if (_provider is null)
                {
                    Filter();
                }
                else
                {
                    _ = QueryProviderAsync();
                }
            }
        }
    }

    public string Placeholder
    {
        get => _placeholder;
        private set => SetField(ref _placeholder, value);
    }

    public bool IsOpen
    {
        get => _isOpen;
        private set => SetField(ref _isOpen, value);
    }

    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            if (SetField(ref _selectedIndex, value))
            {
                _onHighlight?.Invoke(Selected?.Item);
            }
        }
    }

    public QuickPickRow? Selected => _selectedIndex >= 0 && _selectedIndex < Rows.Count ? Rows[_selectedIndex] : null;

    public bool IsEmpty => Rows.Count == 0;

    public event EventHandler? Opened;

    public event EventHandler? Closed;

    /// <summary>Opens the picker. Completes with the accepted item, or null when cancelled (or replaced by another pick).</summary>
    public Task<QuickPickItem?> ShowAsync(IReadOnlyList<QuickPickItem> items, string placeholder, int activeIndex = 0, Action<QuickPickItem?>? onHighlight = null)
    {
        Close(null);
        _pending = new TaskCompletionSource<QuickPickItem?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _items = items;
        Placeholder = placeholder;
        _query = string.Empty;
        OnPropertyChanged(nameof(Query));
        Filter(activeIndex);
        _onHighlight = onHighlight;
        IsOpen = true;
        Opened?.Invoke(this, EventArgs.Empty);
        return _pending.Task;
    }

    /// <summary>
    /// Opens the picker with items that <paramref name="provider"/> computes for each query (Go To All); they are shown
    /// in the given order, the first selected. Queries are debounced and the outdated ones cancelled.
    /// </summary>
    public Task<QuickPickItem?> ShowAsync(Func<string, CancellationToken, Task<IReadOnlyList<QuickPickItem>>> provider, string placeholder)
    {
        var task = ShowAsync([], placeholder);
        _provider = provider;
        return task;
    }

    private async Task QueryProviderAsync()
    {
        _providerCts?.Cancel();
        _providerCts?.Dispose();
        var cts = _providerCts = new CancellationTokenSource();
        var provider = _provider;
        try
        {
            await Task.Delay(80, cts.Token).ConfigureAwait(true);
            var items = await provider!(_query, cts.Token).ConfigureAwait(true);
            if (cts.IsCancellationRequested || _provider != provider)
            {
                return;
            }

            _items = items;
            var query = _query.Trim();
            Show([.. items.Take(MaxRows).Select(item => new QuickPickRow(item, FuzzyMatcher.Match(query, item.Label)?.Positions ?? []))], items.Count, 0);
        }
        catch (OperationCanceledException)
        {
        }
    }

    public void Dispose()
    {
        _providerCts?.Cancel();
        _providerCts?.Dispose();
    }

    public void Accept() => Close(Selected?.Item);

    public void Accept(QuickPickRow row) => Close(row.Item);

    public void Cancel() => Close(null);

    public void Move(int delta)
    {
        if (Rows.Count == 0)
        {
            return;
        }

        SelectedIndex = Math.Clamp(_selectedIndex < 0 ? 0 : _selectedIndex + delta, 0, Rows.Count - 1);
    }

    public void MoveToEdge(bool last) => SelectedIndex = Rows.Count == 0 ? -1 : last ? Rows.Count - 1 : 0;

    private void Close(QuickPickItem? result)
    {
        var pending = _pending;
        if (pending is null)
        {
            return;
        }

        _pending = null;
        _onHighlight = null;
        _provider = null;
        _providerCts?.Cancel();
        IsOpen = false;
        pending.TrySetResult(result);
        Closed?.Invoke(this, EventArgs.Empty);
    }

    private void Filter(int activeIndex = 0)
    {
        var query = _query.Trim();
        if (query.Length == 0)
        {
            // In the given order; the active item shows even past the first rows.
            List<QuickPickRow> rows = [.. _items.Take(MaxRows).Select(i => new QuickPickRow(i, []))];
            if (activeIndex >= MaxRows && activeIndex < _items.Count)
            {
                rows.Add(new QuickPickRow(_items[activeIndex], []));
                activeIndex = rows.Count - 1;
            }

            Show(rows, _items.Count, activeIndex);
            return;
        }

        var matches = _items
            .Select((item, index) => (item, index, match: FuzzyMatcher.Match(query, item.Label)))
            .Where(m => m.match is not null)
            .ToList();
        Show([.. matches.OrderByDescending(m => m.match!.Score).ThenBy(m => m.index).Take(MaxRows).Select(m => new QuickPickRow(m.item, m.match!.Positions))], matches.Count, 0);
    }

    /// <summary>Shows <paramref name="rows"/> of <paramref name="total"/> matches with one reset, <paramref name="selected"/> selected.</summary>
    private void Show(List<QuickPickRow> rows, int total, int selected)
    {
        Rows.Reset(rows);
        _hidden = total - rows.Count;
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasMore));
        OnPropertyChanged(nameof(MoreText));
        _selectedIndex = -1;
        SelectedIndex = rows.Count == 0 ? -1 : Math.Clamp(selected, 0, rows.Count - 1);
    }
}
