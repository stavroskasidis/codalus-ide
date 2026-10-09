using System.Collections.ObjectModel;
using DaedalusStudio.Shell.Infrastructure;
using DaedalusStudio.Workbench.Search;

namespace DaedalusStudio.Shell.ViewModels;

/// <summary>An entry offered by the quick pick (a command, a theme…).</summary>
public sealed record QuickPickItem(string Label)
{
    /// <summary>Muted text after the label, e.g. "recently used".</summary>
    public string? Detail { get; init; }

    /// <summary>Key binding shown on the right.</summary>
    public string? KeyLabel { get; init; }

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

    /// <summary>What screen readers announce for the row.</summary>
    public string AccessibleName => HasKeyLabel ? $"{Label}, {Item.KeyLabel}" : Label;
}

/// <summary>
/// The command palette overlay, also used as a generic picker (<see cref="ShowAsync"/>). Filtering is fuzzy;
/// with an empty query the items keep the order they were given in (most recently used first for commands).
/// </summary>
public sealed class QuickPickViewModel : ObservableObject, IDisposable
{
    private IReadOnlyList<QuickPickItem> _items = [];
    private Func<string, CancellationToken, Task<IReadOnlyList<QuickPickItem>>>? _provider;
    private CancellationTokenSource? _providerCts;
    private TaskCompletionSource<QuickPickItem?>? _pending;
    private Action<QuickPickItem?>? _onHighlight;
    private string _query = string.Empty;
    private string _placeholder = string.Empty;
    private bool _isOpen;
    private int _selectedIndex = -1;

    public ObservableCollection<QuickPickRow> Rows { get; } = [];

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
            Rows.Clear();
            foreach (var item in items)
            {
                Rows.Add(new QuickPickRow(item, FuzzyMatcher.Match(_query.Trim(), item.Label)?.Positions ?? []));
            }

            OnPropertyChanged(nameof(IsEmpty));
            _selectedIndex = -1;
            SelectedIndex = Rows.Count == 0 ? -1 : 0;
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
        IEnumerable<QuickPickRow> rows;
        if (query.Length == 0)
        {
            rows = _items.Select(i => new QuickPickRow(i, []));
        }
        else
        {
            rows = _items
                .Select((item, index) => (item, index, match: FuzzyMatcher.Match(query, item.Label)))
                .Where(m => m.match is not null)
                .OrderByDescending(m => m.match!.Score)
                .ThenBy(m => m.index)
                .Select(m => new QuickPickRow(m.item, m.match!.Positions));
        }

        Rows.Clear();
        foreach (var row in rows)
        {
            Rows.Add(row);
        }

        OnPropertyChanged(nameof(IsEmpty));
        _selectedIndex = -1;
        SelectedIndex = Rows.Count == 0 ? -1 : Math.Clamp(query.Length == 0 ? activeIndex : 0, 0, Rows.Count - 1);
    }
}
