using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using Codalus.Data.Protocol;
using Codalus.Shell.Docking;
using Codalus.Shell.Infrastructure;
using Codalus.Workbench.Data;

namespace Codalus.Shell.ViewModels;

// ------------------------------------------------------------------ the Database Explorer

/// <summary>Kinds of Database Explorer nodes (the <c>dataNodeKind</c> context key).</summary>
public static class DbNodeKinds
{
    public const string Group = "group";
    public const string Connection = "connection";
    public const string Database = "database";
    public const string Folder = "folder";
    public const string Table = "table";
    public const string View = "view";
    public const string Procedure = "procedure";
    public const string Function = "function";
    public const string Column = "column";
    public const string Key = "key";
    public const string Index = "index";
    public const string Message = "message";
}

/// <summary>A Database Explorer node. Children load when it is first expanded (<see cref="Loader"/>).</summary>
public sealed class DbNodeViewModel : ObservableObject
{
    private bool _isExpanded;
    private bool _loaded;
    private string _label;
    private string? _description;

    public DbNodeViewModel(string kind, string label, string icon, Func<DbNodeViewModel, Task>? loader = null)
    {
        Kind = kind;
        _label = label;
        Icon = icon;
        Loader = loader;
        if (loader is not null)
        {
            Children.Add(Placeholder("Loading…"));
        }
    }

    public string Kind { get; }

    public string Label
    {
        get => _label;
        set
        {
            if (SetField(ref _label, value))
            {
                OnPropertyChanged(nameof(AccessibleName));
            }
        }
    }

    public string Icon { get; }

    public string? Description
    {
        get => _description;
        set
        {
            if (SetField(ref _description, value))
            {
                OnPropertyChanged(nameof(HasDescription));
            }
        }
    }

    public bool HasDescription => _description is { Length: > 0 };

    public string? Tooltip { get; init; }

    public string AccessibleName => _description is { Length: > 0 } d ? $"{_label}, {d}" : _label;

    public bool IsPlaceholder => Kind == DbNodeKinds.Message;

    public string? ConnectionId { get; init; }

    public string? Database { get; init; }

    /// <summary>For tables, views and routines: the object.</summary>
    public DbObjectInfo? Object { get; init; }

    public DbTableRef? Table => Object is { Kind: DbObjectKinds.Table or DbObjectKinds.View } o ? new DbTableRef(o.Schema, o.Name) : null;

    public Func<DbNodeViewModel, Task>? Loader { get; set; }

    public ObservableCollection<DbNodeViewModel> Children { get; } = [];

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (SetField(ref _isExpanded, value) && value && !_loaded && Loader is not null)
            {
                _ = LoadAsync();
            }
        }
    }

    /// <summary>Loads (or reloads) the children.</summary>
    public async Task LoadAsync()
    {
        if (Loader is not { } loader)
        {
            return;
        }

        _loaded = true;
        try
        {
            await loader(this).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Rpc.JsonRpcRemoteException or Rpc.JsonRpcConnectionLostException or OperationCanceledException or DataPasswordRequiredException)
        {
            Children.Clear();
            Children.Add(Placeholder(ex is OperationCanceledException ? "Canceled." : ex.Message));
            _loaded = false;
        }
    }

    /// <summary>Forgets the children; the next expansion loads them again.</summary>
    public void Reset()
    {
        _loaded = false;
        Children.Clear();
        if (Loader is not null)
        {
            Children.Add(Placeholder("Loading…"));
        }

        if (_isExpanded)
        {
            _ = LoadAsync();
        }
    }

    public static DbNodeViewModel Placeholder(string text) => new(DbNodeKinds.Message, text, "info");
}

/// <summary>
/// The Database Explorer (ADR 0037): the user's connections and the solution's, each with its databases, schema folders,
/// tables, views, routines, columns, keys and indexes. The shell supplies the loaders and the commands.
/// </summary>
public sealed class DatabaseExplorerViewModel : ObservableObject
{
    public const string ToolWindowId = "data.explorer";

    private DbNodeViewModel? _selected;
    private string? _message;

    public DatabaseExplorerViewModel(Func<string, ICommand> command)
    {
        AddCommand = command("data.addConnection");
        RefreshCommand = command("data.refresh");
        NewQueryCommand = command("data.newQuery");
        ViewDataCommand = command("data.viewData");
    }

    public ObservableCollection<DbNodeViewModel> Roots { get; } = [];

    public DbNodeViewModel? Selected
    {
        get => _selected;
        set
        {
            if (SetField(ref _selected, value))
            {
                SelectionChanged?.Invoke(this, value);
            }
        }
    }

    public event EventHandler<DbNodeViewModel?>? SelectionChanged;

    public string? Message
    {
        get => _message;
        set
        {
            if (SetField(ref _message, value))
            {
                OnPropertyChanged(nameof(HasMessage));
            }
        }
    }

    public bool HasMessage => _message is not null;

    public ICommand AddCommand { get; }

    public ICommand RefreshCommand { get; }

    public ICommand NewQueryCommand { get; }

    public ICommand ViewDataCommand { get; }

    /// <summary>The connection node a node belongs to.</summary>
    public DbNodeViewModel? ConnectionOf(DbNodeViewModel? node)
    {
        if (node?.ConnectionId is not { } id)
        {
            return null;
        }

        return Roots.SelectMany(r => r.Kind == DbNodeKinds.Group ? r.Children : [r]).FirstOrDefault(c => c.Kind == DbNodeKinds.Connection && c.ConnectionId == id);
    }
}

// ------------------------------------------------------------------ grids

/// <summary>A row of a grid: its values now and as read (null is NULL), and what the user did to it.</summary>
public sealed class DataRowViewModel : ObservableObject
{
    private string _state;

    public DataRowViewModel(string?[] values, string state = DataRowStates.Unchanged)
    {
        Original = values;
        Values = [.. values];
        _state = state;
    }

    public string?[] Original { get; }

    public string?[] Values { get; }

    public string State
    {
        get => _state;
        set
        {
            if (SetField(ref _state, value))
            {
                OnPropertyChanged(nameof(Marker));
            }
        }
    }

    /// <summary>The row header: <c>*</c> changed, <c>+</c> new, <c>−</c> deleted.</summary>
    public string Marker => _state switch
    {
        DataRowStates.Modified => "*",
        DataRowStates.Added => "+",
        DataRowStates.Deleted => "−",
        _ => string.Empty,
    };

    public bool IsChanged(int column) => _state == DataRowStates.Added || !string.Equals(Values[column], Original[column], StringComparison.Ordinal);

    public void Set(int column, string? value)
    {
        Values[column] = value;
        if (_state == DataRowStates.Unchanged && IsChanged(column))
        {
            State = DataRowStates.Modified;
        }
        else if (_state == DataRowStates.Modified && Enumerable.Range(0, Values.Length).All(i => !IsChanged(i)))
        {
            State = DataRowStates.Unchanged;
        }
    }
}

public static class DataRowStates
{
    public const string Unchanged = "unchanged";
    public const string Modified = "modified";
    public const string Added = "added";
    public const string Deleted = "deleted";
}

/// <summary>A grid's columns and rows (a table page or a query result).</summary>
public sealed class DataGridModel(ResultColumn[] columns, bool editable)
{
    public ResultColumn[] Columns { get; } = columns;

    public bool Editable { get; } = editable;

    public ObservableCollection<DataRowViewModel> Rows { get; } = [];

    /// <summary>Column widths in pixels, from the header and the first rows' values.</summary>
    public double[] Widths { get; private set; } = [];

    public void Measure()
    {
        Widths = [.. Columns.Select((c, i) => Math.Clamp(Math.Max(c.Name.Length + 2, Rows.Take(50).Select(r => r.Values[i]?.Length ?? 4).DefaultIfEmpty(4).Max()) * 7.5 + 16, 60, 360))];
    }
}

// ------------------------------------------------------------------ table data

/// <summary>A table's data (ADR 0037): a page at a time, edits kept until Commit, export and import.</summary>
public sealed class TableDataViewModel : ObservableObject
{
    public const int DefaultPageSize = 200;

    private readonly DataService _data;
    private readonly Func<TableDataViewModel, string, Task> _act;
    private DataGridModel? _grid;
    private string? _message;
    private string? _status;
    private string _filter = string.Empty;
    private int _page;
    private long? _total;
    private bool _readOnly = true;
    private string[] _keys = [];
    private bool _isBusy;

    /// <param name="act">Actions that ask the user something: <c>export</c>, <c>import</c>.</param>
    public TableDataViewModel(DataService data, string connectionId, string? database, DbTableRef table, string connectionLabel, Func<TableDataViewModel, string, Task> act)
    {
        _data = data;
        _act = act;
        ConnectionId = connectionId;
        Database = database;
        Table = table;
        ConnectionLabel = connectionLabel;
        RefreshCommand = new RelayCommand(() => _ = LoadAsync());
        FirstCommand = new RelayCommand(() => _ = GoAsync(0));
        PreviousCommand = new RelayCommand(() => _ = GoAsync(_page - 1));
        NextCommand = new RelayCommand(() => _ = GoAsync(_page + 1));
        LastCommand = new RelayCommand(() => _ = GoAsync(LastPage));
        CommitCommand = new RelayCommand(() => _ = CommitAsync());
        RevertCommand = new RelayCommand(Revert);
        AddRowCommand = new RelayCommand(AddRow);
        DeleteRowCommand = new RelayCommand(DeleteSelected);
        ExportCommand = new RelayCommand(() => _ = act(this, "export"));
        ImportCommand = new RelayCommand(() => _ = act(this, "import"));
    }

    public string ConnectionId { get; }

    public string? Database { get; }

    public DbTableRef Table { get; }

    public string ConnectionLabel { get; }

    public string Title => (Table.Schema is { Length: > 0 } s ? s + "." : string.Empty) + Table.Name;

    public int PageSize { get; set; } = DefaultPageSize;

    public DataGridModel? Grid
    {
        get => _grid;
        private set => SetField(ref _grid, value);
    }

    public DataRowViewModel? SelectedRow { get; set; }

    public string Filter
    {
        get => _filter;
        set => SetField(ref _filter, value ?? string.Empty);
    }

    public bool IsReadOnly
    {
        get => _readOnly;
        private set
        {
            if (SetField(ref _readOnly, value))
            {
                OnPropertyChanged(nameof(IsEditable));
            }
        }
    }

    public bool IsEditable => !_readOnly;

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetField(ref _isBusy, value);
    }

    public string? Message
    {
        get => _message;
        set
        {
            if (SetField(ref _message, value))
            {
                OnPropertyChanged(nameof(HasMessage));
            }
        }
    }

    public bool HasMessage => _message is not null;

    /// <summary>"Rows 1–200 of 1,000,000 · 12 ms".</summary>
    public string? Status
    {
        get => _status;
        private set => SetField(ref _status, value);
    }

    public int PendingChanges => _grid?.Rows.Count(r => r.State != DataRowStates.Unchanged) ?? 0;

    public bool HasPendingChanges => PendingChanges > 0;

    private int LastPage => _total is { } total ? (int)Math.Max(0, (total - 1) / PageSize) : _page;

    public ICommand RefreshCommand { get; }

    public ICommand FirstCommand { get; }

    public ICommand PreviousCommand { get; }

    public ICommand NextCommand { get; }

    public ICommand LastCommand { get; }

    public ICommand CommitCommand { get; }

    public ICommand RevertCommand { get; }

    public ICommand AddRowCommand { get; }

    public ICommand DeleteRowCommand { get; }

    public ICommand ExportCommand { get; }

    public ICommand ImportCommand { get; }

    /// <summary>The grid changed (a page loaded): the view rebuilds its columns.</summary>
    public event EventHandler? GridChanged;

    /// <summary>The time the last page took (benchmarks and tests).</summary>
    public double LastReadMilliseconds { get; private set; }

    public Task LoadAsync() => GoAsync(_page);

    public async Task GoAsync(int page)
    {
        if (HasPendingChanges)
        {
            Message = "Commit or revert the changes before moving to another page.";
            return;
        }

        page = Math.Max(0, Math.Min(page, LastPage + (_total is null ? 1 : 0)));
        IsBusy = true;
        try
        {
            var filter = _filter.Trim() is { Length: > 0 } f ? f : null;
            var read = await _data.ReadAsync(new ReadParams(ConnectionId, Database, Table, page * PageSize, PageSize, Filter: filter), CancellationToken.None).ConfigureAwait(true);
            LastReadMilliseconds = read.ElapsedMilliseconds;
            _page = page;
            _keys = read.KeyColumns;
            IsReadOnly = read.ReadOnly;
            var grid = new DataGridModel(read.Columns, !read.ReadOnly);
            foreach (var row in read.Rows)
            {
                grid.Rows.Add(Track(new DataRowViewModel(row)));
            }

            grid.Measure();
            Grid = grid;
            OnPropertyChanged(nameof(PendingChanges));
            OnPropertyChanged(nameof(HasPendingChanges));
            Message = read.ReadOnlyReason;
            UpdateStatus(read.Rows.Length, read.ElapsedMilliseconds);
            GridChanged?.Invoke(this, EventArgs.Empty);
            _total = (await _data.CountAsync(new CountParams(ConnectionId, Database, Table, filter), CancellationToken.None).ConfigureAwait(true)).Count;
            UpdateStatus(read.Rows.Length, read.ElapsedMilliseconds);
        }
        catch (Exception ex) when (ex is Rpc.JsonRpcRemoteException or InvalidOperationException or Rpc.JsonRpcConnectionLostException or DataPasswordRequiredException)
        {
            Message = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private DataRowViewModel Track(DataRowViewModel row)
    {
        row.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DataRowViewModel.State))
            {
                OnPropertyChanged(nameof(PendingChanges));
                OnPropertyChanged(nameof(HasPendingChanges));
            }
        };
        return row;
    }

    private void UpdateStatus(int rows, double milliseconds)
    {
        var first = (_page * PageSize) + 1;
        var last = (_page * PageSize) + rows;
        Status = rows == 0
            ? $"No rows{(_total is { } t ? $" of {t.ToString("N0", CultureInfo.CurrentCulture)}" : string.Empty)} · {milliseconds:0} ms"
            : $"Rows {first.ToString("N0", CultureInfo.CurrentCulture)}–{last.ToString("N0", CultureInfo.CurrentCulture)}{(_total is { } total ? " of " + total.ToString("N0", CultureInfo.CurrentCulture) : string.Empty)} · {milliseconds:0} ms";
    }

    public void AddRow()
    {
        if (_grid is null || IsReadOnly)
        {
            return;
        }

        _grid.Rows.Add(Track(new DataRowViewModel(new string?[_grid.Columns.Length], DataRowStates.Added)));
        OnPropertyChanged(nameof(PendingChanges));
        OnPropertyChanged(nameof(HasPendingChanges));
    }

    public void DeleteSelected()
    {
        if (SelectedRow is not { } row || IsReadOnly || _grid is null)
        {
            return;
        }

        if (row.State == DataRowStates.Added)
        {
            _grid.Rows.Remove(row);
        }
        else
        {
            row.State = DataRowStates.Deleted;
        }

        OnPropertyChanged(nameof(PendingChanges));
        OnPropertyChanged(nameof(HasPendingChanges));
    }

    public void Revert()
    {
        if (_grid is null)
        {
            return;
        }

        foreach (var row in _grid.Rows.ToList())
        {
            if (row.State == DataRowStates.Added)
            {
                _grid.Rows.Remove(row);
            }
            else if (row.State != DataRowStates.Unchanged)
            {
                for (var i = 0; i < row.Values.Length; i++)
                {
                    row.Values[i] = row.Original[i];
                }

                row.State = DataRowStates.Unchanged;
            }
        }

        GridChanged?.Invoke(this, EventArgs.Empty);
        OnPropertyChanged(nameof(PendingChanges));
        OnPropertyChanged(nameof(HasPendingChanges));
    }

    /// <summary>The pending edits as row changes: by key for updates and deletes, the set columns for inserts.</summary>
    public RowChange[] Changes()
    {
        if (_grid is null)
        {
            return [];
        }

        var columns = _grid.Columns;
        Dictionary<string, string?> Key(DataRowViewModel row) => _keys.ToDictionary(k => k, k => row.Original[Array.FindIndex(columns, c => c.Name == k)], StringComparer.Ordinal);
        return [.. _grid.Rows.Where(r => r.State != DataRowStates.Unchanged).Select(row => row.State switch
        {
            DataRowStates.Added => new RowChange(RowChangeKinds.Insert, Values: Enumerable.Range(0, columns.Length).Where(i => row.Values[i] is not null).ToDictionary(i => columns[i].Name, i => row.Values[i], StringComparer.Ordinal)),
            DataRowStates.Deleted => new RowChange(RowChangeKinds.Delete, Key(row)),
            _ => new RowChange(RowChangeKinds.Update, Key(row), Enumerable.Range(0, columns.Length).Where(row.IsChanged).ToDictionary(i => columns[i].Name, i => row.Values[i], StringComparer.Ordinal)),
        })];
    }

    public async Task<bool> CommitAsync()
    {
        var changes = Changes();
        if (changes.Length == 0)
        {
            return true;
        }

        IsBusy = true;
        try
        {
            var result = await _data.CommitAsync(new CommitParams(ConnectionId, Database, Table, changes), CancellationToken.None).ConfigureAwait(true);
            foreach (var row in _grid!.Rows)
            {
                row.State = DataRowStates.Unchanged;
            }

            IsBusy = false;
            await LoadAsync().ConfigureAwait(true);
            Message = result.Affected == 1 ? "Saved 1 row." : $"Saved {result.Affected} rows.";
            return true;
        }
        catch (Exception ex) when (ex is Rpc.JsonRpcRemoteException or InvalidOperationException or Rpc.JsonRpcConnectionLostException)
        {
            Message = ex.Message;
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }
}

public sealed class TableDataDocument : ShellDocument
{
    public TableDataDocument(TableDataViewModel viewModel)
    {
        ViewModel = viewModel;
        Id = $"data.table:{viewModel.ConnectionId}:{viewModel.Database}:{viewModel.Title}";
        Title = viewModel.Title + " (Data)";
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TableDataViewModel.HasPendingChanges))
            {
                IsModified = viewModel.HasPendingChanges;
            }
        };
    }

    public override string Kind => "tableData";

    public TableDataViewModel ViewModel { get; }
}

// ------------------------------------------------------------------ query results

/// <summary>A result set in the Query Results window.</summary>
public sealed class ResultSetViewModel(int index, ResultSetInfo set)
{
    public DataGridModel Grid { get; } = Build(set);

    public string Header { get; } = $"Result {index} ({set.Rows.Length.ToString("N0", CultureInfo.CurrentCulture)}{(set.Truncated ? "+" : string.Empty)})";

    public bool Truncated { get; } = set.Truncated;

    private static DataGridModel Build(ResultSetInfo set)
    {
        var grid = new DataGridModel(set.Columns, editable: false);
        foreach (var row in set.Rows)
        {
            grid.Rows.Add(new DataRowViewModel(row));
        }

        grid.Measure();
        return grid;
    }
}

/// <summary>A plan operator in the plan tree.</summary>
public sealed class PlanNodeViewModel(PlanNode node)
{
    public string Operation { get; } = node.Operation;

    public string? Detail { get; } = node.Detail;

    public string Numbers { get; } = string.Join(" · ", new[]
    {
        node.CostPercent is { } p ? $"{p.ToString("0.#", CultureInfo.CurrentCulture)}%" : null,
        node.EstimatedRows is { } e ? $"est. {e.ToString("N0", CultureInfo.CurrentCulture)} rows" : null,
        node.ActualRows is { } a ? $"actual {a.ToString("N0", CultureInfo.CurrentCulture)}" : null,
    }.Where(s => s is not null));

    public bool IsExpensive { get; } = node.CostPercent is >= 25;

    public IReadOnlyList<PlanNodeViewModel> Children { get; } = [.. node.Children.Select(c => new PlanNodeViewModel(c))];

    public bool IsExpanded { get; set; } = true;

    public string AccessibleName => $"{Operation} {Detail} {Numbers}";
}

public sealed class QueryHistoryRow(QueryHistoryEntry entry)
{
    public QueryHistoryEntry Entry { get; } = entry;

    public string Text { get; } = entry.Text.ReplaceLineEndings(" ").Trim() is { Length: > 140 } long_ ? long_[..140] + "…" : entry.Text.ReplaceLineEndings(" ").Trim();

    public string Detail { get; } = $"{entry.At.LocalDateTime.ToString("g", CultureInfo.CurrentCulture)} · {entry.Connection} · {(entry.Succeeded ? $"{entry.ElapsedMilliseconds:0} ms" : "failed")}";

    public string AccessibleName => Text + ", " + Detail;
}

/// <summary>
/// The Query Results window (ADR 0037): the active SQL editor's connection, its result sets, messages, plan and the
/// history; Execute, Execute Current Statement, Cancel and the plans act on the active editor.
/// </summary>
public sealed class QueryResultsViewModel : ObservableObject
{
    public const string ToolWindowId = "data.results";

    private string? _connectionLabel;
    private string? _status;
    private string _messages = string.Empty;
    private bool _isRunning;
    private int _selectedTab;
    private string? _planRaw;
    private bool _hasPlan;

    public QueryResultsViewModel(Func<string, ICommand> command, Action<QueryHistoryRow> openHistory)
    {
        ExecuteCommand = command("data.execute");
        ExecuteStatementCommand = command("data.executeStatement");
        CancelCommand = command("data.cancelQuery");
        EstimatedPlanCommand = command("data.estimatedPlan");
        ActualPlanCommand = command("data.actualPlan");
        ConnectionCommand = command("data.chooseConnection");
        OpenPlanCommand = command("data.openPlan");
        ClearHistoryCommand = command("data.clearHistory");
        OpenHistory = openHistory;
    }

    /// <summary>The editor font (messages and history).</summary>
    public EditorFont? Font { get; init; }

    public ObservableCollection<ResultSetViewModel> Results { get; } = [];

    public ObservableCollection<PlanNodeViewModel> Plan { get; } = [];

    public ObservableCollection<QueryHistoryRow> History { get; } = [];

    public Action<QueryHistoryRow> OpenHistory { get; }

    public string? ConnectionLabel
    {
        get => _connectionLabel;
        set => SetField(ref _connectionLabel, value);
    }

    public string? Status
    {
        get => _status;
        set => SetField(ref _status, value);
    }

    public string Messages
    {
        get => _messages;
        set => SetField(ref _messages, value);
    }

    public bool IsRunning
    {
        get => _isRunning;
        set
        {
            if (SetField(ref _isRunning, value))
            {
                OnPropertyChanged(nameof(IsIdle));
            }
        }
    }

    public bool IsIdle => !_isRunning;

    /// <summary>0: results, 1: messages, 2: plan, 3: history.</summary>
    public int SelectedTab
    {
        get => _selectedTab;
        set => SetField(ref _selectedTab, value);
    }

    public string? PlanRaw
    {
        get => _planRaw;
        set => SetField(ref _planRaw, value);
    }

    public bool HasPlan
    {
        get => _hasPlan;
        set => SetField(ref _hasPlan, value);
    }

    public ICommand ExecuteCommand { get; }

    public ICommand ExecuteStatementCommand { get; }

    public ICommand CancelCommand { get; }

    public ICommand EstimatedPlanCommand { get; }

    public ICommand ActualPlanCommand { get; }

    public ICommand ConnectionCommand { get; }

    public ICommand OpenPlanCommand { get; }

    public ICommand ClearHistoryCommand { get; }

    /// <summary>The results changed: the view rebuilds its result tabs.</summary>
    public event EventHandler? ResultsChanged;

    public void Show(ExecuteResult result)
    {
        Results.Clear();
        var index = 1;
        foreach (var set in result.ResultSets)
        {
            Results.Add(new ResultSetViewModel(index++, set));
        }

        Messages = string.Join(Environment.NewLine, result.Messages.Select(m => (m.Kind == "error" ? "Error: " : m.Kind == "warning" ? "Warning: " : string.Empty) + m.Text + (m.Line is { } line ? $" (line {line})" : string.Empty)));
        Plan.Clear();
        foreach (var node in result.Plan?.Roots ?? [])
        {
            Plan.Add(new PlanNodeViewModel(node));
        }

        PlanRaw = result.Plan?.Raw;
        HasPlan = result.Plan is not null;
        var rows = result.ResultSets.Sum(r => r.Rows.Length);
        var errors = result.Messages.Count(m => m.Kind == "error");
        Status = $"{(errors > 0 ? $"{errors} error(s) · " : string.Empty)}{result.ResultSets.Length} result set(s), {rows.ToString("N0", CultureInfo.CurrentCulture)} row(s) · {result.ElapsedMilliseconds:0} ms";
        SelectedTab = result.Plan is not null ? 2 : errors > 0 || result.ResultSets.Length == 0 ? 1 : 0;
        ResultsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ShowError(string message)
    {
        Results.Clear();
        Plan.Clear();
        HasPlan = false;
        Messages = message;
        Status = "Failed";
        SelectedTab = 1;
        ResultsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void LoadHistory(IEnumerable<QueryHistoryEntry> entries)
    {
        History.Clear();
        foreach (var entry in entries)
        {
            History.Add(new QueryHistoryRow(entry));
        }
    }
}

// ------------------------------------------------------------------ connections

/// <summary>Add or edit a connection (ADR 0037): fields per engine, or a raw connection string; Test; Save.</summary>
public sealed class ConnectionFormViewModel : ObservableObject
{
    private readonly Func<ConnectionFormViewModel, Task<string>> _test;
    private readonly Func<ConnectionFormViewModel, Task> _save;
    private string _engine;
    private string _name = string.Empty;
    private string _host = "localhost";
    private string _port = string.Empty;
    private string _database = string.Empty;
    private string _file = string.Empty;
    private bool _integrated;
    private string _user = string.Empty;
    private string _password = string.Empty;
    private bool _savePassword;
    private string _options = string.Empty;
    private bool _useConnectionString;
    private string _connectionString = string.Empty;
    private string? _result;
    private bool _isBusy;

    public ConnectionFormViewModel(string? id, Func<ConnectionFormViewModel, Task<string>> test, Func<ConnectionFormViewModel, Task> save)
    {
        Id = id ?? Guid.NewGuid().ToString("N")[..12];
        IsNew = id is null;
        _engine = DataEngines.SqlServer;
        _test = test;
        _save = save;
        TestCommand = new RelayCommand(() => _ = TestAsync());
        SaveCommand = new RelayCommand(() => _ = SaveAsync());
    }

    public string Id { get; }

    public bool IsNew { get; }

    public IReadOnlyList<string> Engines { get; } = [.. DatabaseEngines.All.Select(DatabaseEngines.DisplayName)];

    public string EngineName
    {
        get => DatabaseEngines.DisplayName(_engine);
        set
        {
            var engine = DatabaseEngines.All.FirstOrDefault(e => DatabaseEngines.DisplayName(e) == value) ?? DataEngines.SqlServer;
            if (SetField(ref _engine, engine, nameof(Engine)))
            {
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsSqlite));
                OnPropertyChanged(nameof(IsServer));
                OnPropertyChanged(nameof(IsSqlServer));
                OnPropertyChanged(nameof(UsesPassword));
            }
        }
    }

    public string Engine
    {
        get => _engine;
        set => EngineName = DatabaseEngines.DisplayName(value);
    }

    public bool IsSqlite => _engine == DataEngines.Sqlite;

    public bool IsServer => !IsSqlite;

    public bool IsSqlServer => _engine == DataEngines.SqlServer;

    public bool UsesPassword => IsServer && !(IsSqlServer && _integrated);

    public string Name { get => _name; set => SetField(ref _name, value ?? string.Empty); }

    public string Host { get => _host; set => SetField(ref _host, value ?? string.Empty); }

    public string Port { get => _port; set => SetField(ref _port, value ?? string.Empty); }

    public string Database { get => _database; set => SetField(ref _database, value ?? string.Empty); }

    public string File { get => _file; set => SetField(ref _file, value ?? string.Empty); }

    public bool Integrated
    {
        get => _integrated;
        set
        {
            if (SetField(ref _integrated, value))
            {
                OnPropertyChanged(nameof(UsesPassword));
            }
        }
    }

    public string User { get => _user; set => SetField(ref _user, value ?? string.Empty); }

    public string Password { get => _password; set => SetField(ref _password, value ?? string.Empty); }

    public bool SavePassword { get => _savePassword; set => SetField(ref _savePassword, value); }

    /// <summary>Extra keywords appended to the connection string (<c>TrustServerCertificate=true</c>).</summary>
    public string Options { get => _options; set => SetField(ref _options, value ?? string.Empty); }

    public bool UseConnectionString
    {
        get => _useConnectionString;
        set
        {
            if (SetField(ref _useConnectionString, value))
            {
                OnPropertyChanged(nameof(UseFields));
            }
        }
    }

    public bool UseFields => !_useConnectionString;

    public string ConnectionString { get => _connectionString; set => SetField(ref _connectionString, value ?? string.Empty); }

    public string? Result
    {
        get => _result;
        set
        {
            if (SetField(ref _result, value))
            {
                OnPropertyChanged(nameof(HasResult));
            }
        }
    }

    public bool HasResult => _result is not null;

    public bool IsBusy { get => _isBusy; set => SetField(ref _isBusy, value); }

    public ICommand TestCommand { get; }

    public ICommand SaveCommand { get; }

    /// <summary>The connection string without the password (which is <see cref="Password"/>).</summary>
    public string BuildConnectionString()
    {
        if (_useConnectionString)
        {
            return _connectionString.Trim();
        }

        var parts = new List<string>();
        string Port(string separator) => _port.Trim() is { Length: > 0 } port ? separator + port : string.Empty;
        switch (_engine)
        {
            case DataEngines.Sqlite:
                parts.Add("Data Source=" + _file.Trim());
                break;
            case DataEngines.SqlServer:
                parts.Add("Server=" + _host.Trim() + Port(","));
                if (_database.Trim() is { Length: > 0 } db)
                {
                    parts.Add("Database=" + db);
                }

                parts.Add(_integrated ? "Integrated Security=true" : "User ID=" + _user.Trim());
                break;
            case DataEngines.PostgreSql:
                parts.Add("Host=" + _host.Trim());
                if (_port.Trim() is { Length: > 0 } pgPort)
                {
                    parts.Add("Port=" + pgPort);
                }

                if (_database.Trim() is { Length: > 0 } pgDb)
                {
                    parts.Add("Database=" + pgDb);
                }

                parts.Add("Username=" + _user.Trim());
                break;
            default:
                parts.Add("Server=" + _host.Trim());
                if (_port.Trim() is { Length: > 0 } myPort)
                {
                    parts.Add("Port=" + myPort);
                }

                if (_database.Trim() is { Length: > 0 } myDb)
                {
                    parts.Add("Database=" + myDb);
                }

                parts.Add("User ID=" + _user.Trim());
                break;
        }

        if (_options.Trim().Trim(';') is { Length: > 0 } extra)
        {
            parts.Add(extra);
        }

        return string.Join(';', parts);
    }

    public string DefaultName() => _name.Trim() is { Length: > 0 } name ? name
        : IsSqlite ? Path.GetFileName(_file.Trim())
        : $"{_database.Trim() switch { { Length: > 0 } d => d + " on ", _ => string.Empty }}{_host.Trim()}";

    public string? Validate() =>
        _useConnectionString ? (_connectionString.Trim().Length == 0 ? "Enter a connection string." : null)
        : IsSqlite ? (_file.Trim().Length == 0 ? "Choose the database file." : null)
        : _host.Trim().Length == 0 ? "Enter the server."
        : UsesPassword && _user.Trim().Length == 0 ? "Enter the user name."
        : null;

    public async Task TestAsync()
    {
        if (Validate() is { } error)
        {
            Result = error;
            return;
        }

        IsBusy = true;
        try
        {
            Result = await _test(this).ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task SaveAsync()
    {
        if (Validate() is { } error)
        {
            Result = error;
            return;
        }

        IsBusy = true;
        try
        {
            await _save(this).ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }
}

public sealed class ConnectionFormDocument : ShellDocument
{
    public ConnectionFormDocument(ConnectionFormViewModel viewModel)
    {
        ViewModel = viewModel;
        Id = "data.connection:" + viewModel.Id;
        Title = viewModel.IsNew ? "Add Connection" : "Edit Connection";
    }

    public override string Kind => "dataConnection";

    public ConnectionFormViewModel ViewModel { get; }
}
