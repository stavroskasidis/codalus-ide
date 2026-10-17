using System.Collections.ObjectModel;
using Codalus.Debugging;
using Codalus.Shell.Infrastructure;
using Codalus.Workbench.Debugging;
using Codalus.Workbench.FileSystem;
using Codalus.Workbench.Output;

namespace Codalus.Shell.ViewModels;

/// <summary>A variable (or watch, or evaluated expression) in a debugger tree; children load when it is expanded.</summary>
public sealed class VariableNode : ObservableObject
{
    private readonly Func<int, Task<IReadOnlyList<Variable>>>? _load;
    private bool _isExpanded;
    private bool _loaded;
    private string _value;
    private string? _type;
    private int _reference;

    public VariableNode(string name, string value, string? type, int reference, Func<int, Task<IReadOnlyList<Variable>>>? load, int? parentReference = null, string? evaluateName = null)
    {
        Name = name;
        EvaluateName = evaluateName;
        _value = value;
        _type = type;
        _reference = reference;
        _load = load;
        ParentReference = parentReference;
        if (reference > 0 && load is not null)
        {
            Children.Add(Placeholder);
        }
    }

    private static VariableNode Placeholder => new(string.Empty, "Loading…", null, 0, null);

    public string Name { get; }

    /// <summary>The expression that evaluates to this value (the adapter's <c>evaluateName</c>; a watch's own expression).</summary>
    public string? EvaluateName { get; }

    /// <summary>Whether the value is a string (visualizers apply).</summary>
    public bool IsString => _type is "string" or "System.String" && _value.StartsWith('"');

    /// <summary>The variables reference of the container (for setting this variable's value).</summary>
    public int? ParentReference { get; }

    public string Value
    {
        get => _value;
        private set => SetField(ref _value, value);
    }

    public string? Type
    {
        get => _type;
        private set
        {
            if (SetField(ref _type, value))
            {
                OnPropertyChanged(nameof(HasType));
            }
        }
    }

    public bool HasType => !string.IsNullOrEmpty(_type);

    public bool IsError { get; init; }

    public ObservableCollection<VariableNode> Children { get; } = [];

    public string AccessibleName => HasType ? $"{Name} = {Value} ({Type})" : $"{Name} = {Value}";

    /// <summary>Shows a value set from outside (an edit through another node).</summary>
    public void SetValue(string value, string? type)
    {
        Value = value;
        Type = type ?? Type;
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (SetField(ref _isExpanded, value) && value && !_loaded)
            {
                _ = LoadChildrenAsync();
            }
        }
    }

    /// <summary>A new value for this node (a re-evaluated watch, a changed variable): reloads its children if expanded.</summary>
    public void Update(string value, string? type, int reference)
    {
        Value = value;
        Type = type;
        if (reference == _reference && _loaded)
        {
            return;
        }

        _reference = reference;
        _loaded = false;
        Children.Clear();
        if (reference > 0 && _load is not null)
        {
            Children.Add(Placeholder);
            if (_isExpanded)
            {
                _ = LoadChildrenAsync();
            }
        }
    }

    private async Task LoadChildrenAsync()
    {
        if (_load is null || _reference <= 0)
        {
            return;
        }

        _loaded = true;
        try
        {
            var children = await _load(_reference).ConfigureAwait(true);
            Children.Clear();
            foreach (var child in children)
            {
                Children.Add(new VariableNode(child.Name, child.Value, child.Type, child.VariablesReference, _load, _reference, child.EvaluateName));
            }
        }
        catch (Exception ex) when (ex is DapException or DapConnectionLostException or InvalidOperationException or OperationCanceledException)
        {
            Children.Clear();
            Children.Add(new VariableNode(string.Empty, ex.Message, null, 0, null) { IsError = true });
        }
    }
}

/// <summary>Locals or Autos: the selected frame's variables, refreshed on every stop and frame change.</summary>
public sealed class VariablesViewModel : ObservableObject
{
    public const string LocalsWindowId = "debug.locals";
    public const string AutosWindowId = "debug.autos";

    private readonly DebugService _debugger;
    private readonly Func<string, int, string?> _lineText;
    private bool _isEmpty = true;
    private int _generation;

    public VariablesViewModel(DebugService debugger, bool autos, Func<string, int, string?> lineText)
    {
        _debugger = debugger;
        IsAutos = autos;
        _lineText = lineText;
        debugger.Stopped += (_, _) => Ui.Post(() => _ = RefreshAsync());
        debugger.FrameChanged += (_, _) => Ui.Post(() => _ = RefreshAsync());
        debugger.StateChanged += (_, _) => Ui.Post(() =>
        {
            if (debugger.State != DebugState.Stopped)
            {
                Clear();
            }
        });
    }

    public bool IsAutos { get; }

    public ObservableCollection<VariableNode> Roots { get; } = [];

    public bool IsEmpty
    {
        get => _isEmpty;
        private set => SetField(ref _isEmpty, value);
    }

    private void Clear()
    {
        _generation++;
        Roots.Clear();
        IsEmpty = true;
    }

    public async Task RefreshAsync()
    {
        var generation = ++_generation;
        if (_debugger.State != DebugState.Stopped || _debugger.SelectedFrame is not { } frame)
        {
            Clear();
            return;
        }

        try
        {
            var scopes = await _debugger.ScopesAsync(frame).ConfigureAwait(true);
            var variables = new List<Variable>();
            foreach (var scope in scopes.Where(s => !s.Expensive))
            {
                variables.AddRange(await _debugger.VariablesAsync(scope.VariablesReference).ConfigureAwait(true));
            }

            if (IsAutos)
            {
                var names = AutosNames(frame);
                variables = [.. variables.Where(v => names.Contains(v.Name) || v.Name == "this")];
            }

            if (generation != _generation)
            {
                return;
            }

            Roots.Clear();
            var reference = scopes.Count > 0 ? scopes[0].VariablesReference : 0;
            foreach (var variable in variables)
            {
                Roots.Add(new VariableNode(variable.Name, variable.Value, variable.Type, variable.VariablesReference, r => _debugger.VariablesAsync(r), reference, variable.EvaluateName));
            }

            IsEmpty = Roots.Count == 0;
        }
        catch (Exception ex) when (ex is DapException or DapConnectionLostException or InvalidOperationException or OperationCanceledException)
        {
            if (generation == _generation)
            {
                Roots.Clear();
                Roots.Add(new VariableNode(string.Empty, ex.Message, null, 0, null) { IsError = true });
                IsEmpty = false;
            }
        }
    }

    /// <summary>Identifiers on the frame's line and the line before it (Autos).</summary>
    private HashSet<string> AutosNames(StackFrame frame)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        if (frame.Source?.Path is not { } path)
        {
            return names;
        }

        foreach (var line in new[] { frame.Line - 1, frame.Line })
        {
            if (_lineText(path, line) is not { } text)
            {
                continue;
            }

            var start = -1;
            for (var i = 0; i <= text.Length; i++)
            {
                var identifier = i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_');
                if (identifier && start < 0)
                {
                    start = i;
                }
                else if (!identifier && start >= 0)
                {
                    if (!char.IsDigit(text[start]))
                    {
                        names.Add(text[start..i]);
                    }

                    start = -1;
                }
            }
        }

        return names;
    }

    /// <summary>Sets a variable's value: <c>setVariable</c> in its container (Locals, Autos and their members).</summary>
    public Task<string?> SetValueAsync(VariableNode node, string value) => VariableEditing.SetAsync(_debugger, node, value, isWatchRoot: false);
}

/// <summary>The Watch window: user expressions, evaluated in the selected frame on every stop.</summary>
public sealed class WatchViewModel : ObservableObject
{
    public const string ToolWindowId = "debug.watch";

    private readonly DebugService _debugger;
    private string _newExpression = string.Empty;

    public WatchViewModel(DebugService debugger)
    {
        _debugger = debugger;
        debugger.Stopped += (_, _) => Ui.Post(() => _ = RefreshAsync());
        debugger.FrameChanged += (_, _) => Ui.Post(() => _ = RefreshAsync());
        debugger.StateChanged += (_, _) => Ui.Post(() =>
        {
            if (debugger.State == DebugState.Idle)
            {
                foreach (var node in Roots)
                {
                    node.Update(string.Empty, null, 0);
                }
            }
        });
    }

    public ObservableCollection<VariableNode> Roots { get; } = [];

    public IReadOnlyList<string> Expressions => [.. Roots.Select(r => r.Name)];

    public string NewExpression
    {
        get => _newExpression;
        set => SetField(ref _newExpression, value ?? string.Empty);
    }

    public event EventHandler? Changed;

    public void Load(IEnumerable<string>? expressions)
    {
        Roots.Clear();
        foreach (var expression in expressions ?? [])
        {
            Roots.Add(Node(expression));
        }
    }

    public async Task AddAsync(string expression)
    {
        expression = expression.Trim();
        if (expression.Length == 0)
        {
            return;
        }

        var node = Node(expression);
        Roots.Add(node);
        NewExpression = string.Empty;
        Changed?.Invoke(this, EventArgs.Empty);
        await EvaluateAsync(node).ConfigureAwait(true);
    }

    public void Remove(VariableNode node)
    {
        Roots.Remove(node);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private VariableNode Node(string expression) => new(expression, string.Empty, null, 0, r => _debugger.VariablesAsync(r), evaluateName: expression);

    /// <summary>Sets a watch's value (<c>setExpression</c>) or a member's (<c>setVariable</c>), then re-evaluates the watches.</summary>
    public async Task<string?> SetValueAsync(VariableNode node, string value)
    {
        var error = await VariableEditing.SetAsync(_debugger, node, value, Roots.Contains(node)).ConfigureAwait(true);
        if (error is null)
        {
            await RefreshAsync().ConfigureAwait(true);
        }

        return error;
    }

    public async Task RefreshAsync()
    {
        foreach (var node in Roots.ToList())
        {
            await EvaluateAsync(node).ConfigureAwait(true);
        }
    }

    private async Task EvaluateAsync(VariableNode node)
    {
        if (_debugger.State != DebugState.Stopped)
        {
            return;
        }

        try
        {
            var result = await _debugger.EvaluateAsync(node.Name, "watch").ConfigureAwait(true);
            node.Update(result.Result, result.Type, result.VariablesReference);
        }
        catch (Exception ex) when (ex is DapException or DapConnectionLostException or InvalidOperationException or OperationCanceledException)
        {
            node.Update(ex.Message, null, 0);
        }
    }
}

/// <summary>A frame in the Call Stack window.</summary>
public sealed record FrameRow(StackFrame Frame, bool IsSelected, bool IsTop)
{
    public string Name => Frame.Name;

    /// <summary>The file and line; nothing for a label (its name says what it is), "[External Code]" without source.</summary>
    public string Location => Frame.Source?.Path is { } path ? $"{System.IO.Path.GetFileName(path)}, line {Frame.Line}" : IsLabel ? string.Empty : "[External Code]";

    /// <summary>The location's tooltip: the source's full path and line (null without source: no tooltip).</summary>
    public string? LocationTip => Frame.Source?.Path is { } path ? $"{path}, line {Frame.Line}" : Location.Length > 0 ? Location : null;

    public bool HasSource => Frame.Source?.Path is not null;

    /// <summary>
    /// A frame that is only a label (DAP's <c>label</c> hint: the desktop-CLR debugger's internal frames such as
    /// "[Managed to Native Transition]", M43b), shown in italics without a location.
    /// </summary>
    public bool IsLabel => Frame.PresentationHint == "label";

    /// <summary>Shown dimmed: no source, a label, or the adapter's <c>subtle</c> hint (code outside Just My Code, M43b).</summary>
    public bool IsDimmed => !HasSource || IsLabel || Frame.PresentationHint == "subtle";

    public string Marker => IsTop ? "▶" : IsSelected ? "▷" : string.Empty;

    public string AccessibleName => $"{Name}{(Location.Length > 0 ? ", " + Location : string.Empty)}{(IsSelected ? ", selected" : string.Empty)}";
}

/// <summary>The Call Stack window: the threads and the selected thread's frames.</summary>
public sealed class CallStackViewModel : ObservableObject
{
    public const string ToolWindowId = "debug.callStack";

    private readonly DebugService _debugger;
    private readonly Func<string, int, Task> _open;
    private DapThread? _selectedThread;
    private bool _syncing;

    public CallStackViewModel(DebugService debugger, Func<string, int, Task> open)
    {
        _debugger = debugger;
        _open = open;
        debugger.Stopped += (_, _) => Ui.Post(Refresh);
        debugger.FrameChanged += (_, _) => Ui.Post(Refresh);
        debugger.StateChanged += (_, _) => Ui.Post(Refresh);
    }

    public ObservableCollection<DapThread> Threads { get; } = [];

    public ObservableCollection<FrameRow> Frames { get; } = [];

    public DapThread? SelectedThread
    {
        get => _selectedThread;
        set
        {
            if (SetField(ref _selectedThread, value) && !_syncing && value is not null && value.Id != _debugger.SelectedThreadId)
            {
                _ = _debugger.SelectThreadAsync(value.Id);
            }
        }
    }

    public bool IsEmpty => Frames.Count == 0;

    public void Refresh()
    {
        _syncing = true;
        try
        {
            Threads.Clear();
            Frames.Clear();
            if (_debugger.State == DebugState.Stopped)
            {
                foreach (var thread in _debugger.Threads)
                {
                    Threads.Add(thread);
                }

                SelectedThread = Threads.FirstOrDefault(t => t.Id == _debugger.SelectedThreadId);
                var frames = _debugger.Frames;
                for (var i = 0; i < frames.Count; i++)
                {
                    Frames.Add(new FrameRow(frames[i], frames[i] == _debugger.SelectedFrame, i == 0));
                }
            }
            else
            {
                SelectedThread = null;
            }
        }
        finally
        {
            _syncing = false;
        }

        OnPropertyChanged(nameof(IsEmpty));
    }

    /// <summary>Selects a frame (Locals, Autos and Watch follow) and opens its source.</summary>
    public async Task ActivateAsync(FrameRow row)
    {
        _debugger.SelectFrame(row.Frame);
        if (row.Frame.Source?.Path is not { } path)
        {
            return;
        }

        try
        {
            // Through the file system that holds it: a remote window's sources are the server's (M21).
            if (!await FileSystems.FileExistsAsync(path, CancellationToken.None).ConfigureAwait(true))
            {
                return;
            }
        }
        catch (IOException)
        {
            return;
        }

        await _open(path, row.Frame.Line).ConfigureAwait(true);
    }
}

/// <summary>The Debug Console: program and debugger output, and an input line that evaluates expressions.</summary>
public sealed class DebugConsoleViewModel : ObservableObject
{
    public const string ToolWindowId = "debug.console";

    private readonly DebugService _debugger;
    private readonly List<string> _history = [];
    private string _input = string.Empty;
    private int _historyIndex;

    public DebugConsoleViewModel(DebugService debugger, OutputChannel channel, EditorFont font)
    {
        _debugger = debugger;
        Channel = channel;
        Font = font;
    }

    public OutputChannel Channel { get; }

    public EditorFont Font { get; }

    public string Input
    {
        get => _input;
        set => SetField(ref _input, value ?? string.Empty);
    }

    /// <summary>Evaluates <see cref="Input"/> in the selected frame and writes the result.</summary>
    public async Task SubmitAsync()
    {
        var expression = Input.Trim();
        if (expression.Length == 0)
        {
            return;
        }

        _history.Remove(expression);
        _history.Add(expression);
        _historyIndex = _history.Count;
        Input = string.Empty;
        Channel.AppendLine("> " + expression);
        try
        {
            var result = await _debugger.EvaluateAsync(expression, "repl").ConfigureAwait(true);
            Channel.AppendLine(string.IsNullOrEmpty(result.Type) ? result.Result : $"{result.Result}   ({result.Type})");
        }
        catch (Exception ex) when (ex is DapException or DapConnectionLostException or InvalidOperationException or OperationCanceledException)
        {
            Channel.AppendLine("error: " + ex.Message);
        }
    }

    /// <summary>Up and Down walk the input history.</summary>
    public void History(int direction)
    {
        if (_history.Count == 0)
        {
            return;
        }

        _historyIndex = Math.Clamp(_historyIndex + direction, 0, _history.Count);
        Input = _historyIndex < _history.Count ? _history[_historyIndex] : string.Empty;
    }

    public void Clear() => Channel.Clear();
}

/// <summary>A row of the Breakpoints window: a source breakpoint (<see cref="Spec"/>) or a function breakpoint (<see cref="Function"/>).</summary>
public sealed class BreakpointRow : ObservableObject
{
    private readonly Action<BreakpointRow, bool> _setEnabled;

    public BreakpointRow(BreakpointSpec spec, BreakpointState? state, Action<BreakpointRow, bool> setEnabled)
    {
        Spec = spec;
        State = state;
        _setEnabled = setEnabled;
    }

    public BreakpointRow(FunctionBreakpointSpec function, BreakpointState? state, Action<BreakpointRow, bool> setEnabled)
    {
        Function = function;
        State = state;
        _setEnabled = setEnabled;
    }

    public BreakpointSpec? Spec { get; }

    public FunctionBreakpointSpec? Function { get; }

    public BreakpointState? State { get; }

    public string Location => Spec is { } spec ? $"{System.IO.Path.GetFileName(spec.Path)}, line {spec.Line}" : $"Function {Function!.Name}";

    public string Labels => Spec?.Labels ?? Function?.Labels ?? string.Empty;

    public bool HasLabels => Labels.Length > 0;

    public string Details => string.Join("; ", new[]
    {
        (Spec?.Condition ?? Function?.Condition) is { } c ? "when " + c : null,
        Spec?.HitCondition is { } h ? "hit count " + h : null,
        Spec?.Filter is { } f ? "filter " + f : null,
        Spec?.LogMessage is { } m ? "log \"" + m + "\"" : null,
        Spec?.Temporary == true ? "temporary" : null,
        Spec?.DependsOn is { } d ? $"after {System.IO.Path.GetFileName(d.Path)}, line {d.Line}" : null,
        State is { Verified: false, Message: { } message } ? message : null,
    }.OfType<string>());

    public bool IsVerified => State?.Verified ?? true;

    public bool Enabled
    {
        get => Spec?.Enabled ?? Function!.Enabled;
        set
        {
            if (value != Enabled)
            {
                _setEnabled(this, value);
            }
        }
    }

    /// <summary>Whether the row matches the window's search text (location, condition, labels).</summary>
    public bool Matches(string filter) =>
        filter.Length == 0
        || Location.Contains(filter, StringComparison.OrdinalIgnoreCase)
        || Labels.Contains(filter, StringComparison.OrdinalIgnoreCase)
        || Details.Contains(filter, StringComparison.OrdinalIgnoreCase)
        || (Spec?.Path.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false);

    public string AccessibleName => $"Breakpoint {Location}{(Enabled ? string.Empty : ", disabled")}{(HasLabels ? ", labels " + Labels : string.Empty)}{(Details.Length > 0 ? ", " + Details : string.Empty)}";
}

/// <summary>The Breakpoints window: source and function breakpoints, searchable by location, condition or label.</summary>
public sealed class BreakpointsViewModel : ObservableObject
{
    public const string ToolWindowId = "debug.breakpoints";

    private readonly DebugService _debugger;
    private readonly Func<string, int, Task> _open;
    private bool _refreshQueued;
    private string _filter = string.Empty;

    public BreakpointsViewModel(DebugService debugger, Func<string, int, Task> open)
    {
        _debugger = debugger;
        _open = open;
        debugger.Breakpoints.Changed += (_, _) => Ui.Post(QueueRefresh);
        debugger.Breakpoints.FunctionsChanged += (_, _) => Ui.Post(QueueRefresh);
        debugger.Breakpoints.StatesChanged += (_, _) => Ui.Post(QueueRefresh);
        Refresh();
    }

    public ObservableCollection<BreakpointRow> Rows { get; } = [];

    public bool IsEmpty => Rows.Count == 0;

    /// <summary>Search text: matches locations, conditions and labels.</summary>
    public string Filter
    {
        get => _filter;
        set
        {
            if (SetField(ref _filter, value ?? string.Empty))
            {
                Refresh();
            }
        }
    }

    private void QueueRefresh()
    {
        if (_refreshQueued)
        {
            return;
        }

        _refreshQueued = true;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _refreshQueued = false;
            Refresh();
        }, Avalonia.Threading.DispatcherPriority.Background);
    }

    public void Refresh()
    {
        var store = _debugger.Breakpoints;
        Rows.Clear();
        var rows = store.All.Select(b => new BreakpointRow(b, store.StateOf(b), SetEnabled))
            .Concat(store.Functions.Select(f => new BreakpointRow(f, store.StateOf(f), SetEnabled)));
        foreach (var row in rows.Where(r => r.Matches(_filter.Trim())))
        {
            Rows.Add(row);
        }

        OnPropertyChanged(nameof(IsEmpty));
    }

    private void SetEnabled(BreakpointRow row, bool enabled)
    {
        if (row.Spec is { } spec)
        {
            _debugger.Breakpoints.Set(spec with { Enabled = enabled });
        }
        else if (row.Function is { } function)
        {
            _debugger.Breakpoints.SetFunction(function with { Enabled = enabled });
        }
    }

    /// <summary>Enables or disables every breakpoint shown (the search applies).</summary>
    public void SetAllEnabled(bool enabled)
    {
        foreach (var row in Rows.ToList())
        {
            if (row.Enabled != enabled)
            {
                SetEnabled(row, enabled);
            }
        }
    }

    public Task OpenAsync(BreakpointRow row) => row.Spec is { } spec ? _open(spec.Path, spec.Line) : Task.CompletedTask;

    public void Remove(BreakpointRow row)
    {
        if (row.Spec is { } spec)
        {
            _debugger.Breakpoints.Remove(spec);
        }
        else if (row.Function is { } function)
        {
            _debugger.Breakpoints.RemoveFunction(function);
        }
    }

    /// <summary>Sets a row's labels (a comma-separated list).</summary>
    public void SetLabels(BreakpointRow row, string? labels)
    {
        var normalized = BreakpointStore.JoinLabels(BreakpointStore.SplitLabels(labels));
        if (row.Spec is { } spec)
        {
            _debugger.Breakpoints.Set(spec with { Labels = normalized });
        }
        else if (row.Function is { } function)
        {
            _debugger.Breakpoints.SetFunction(function with { Labels = normalized });
        }
    }
}

/// <summary>Editing values in the debugger's trees (ADR 0021).</summary>
internal static class VariableEditing
{
    /// <summary>
    /// A root of Watch or QuickWatch is assigned with <c>setExpression</c>; a variable with <c>setVariable</c> in its
    /// container, or by its evaluate name when it has none. Returns an error message, or null.
    /// </summary>
    public static async Task<string?> SetAsync(DebugService debugger, VariableNode node, string value, bool isWatchRoot)
    {
        try
        {
            if (!isWatchRoot && node.ParentReference is { } parent)
            {
                var result = await debugger.SetVariableAsync(parent, node.Name, value).ConfigureAwait(true);
                node.Update(result.Value, result.Type ?? node.Type, result.VariablesReference ?? 0);
                return null;
            }

            if (node.EvaluateName is { } expression)
            {
                var result = await debugger.SetExpressionAsync(expression, value).ConfigureAwait(true);
                node.Update(result.Value, result.Type ?? node.Type, result.VariablesReference ?? 0);
                return null;
            }

            return "This value can't be changed.";
        }
        catch (Exception ex) when (ex is DapException or DapConnectionLostException or InvalidOperationException)
        {
            return ex.Message;
        }
    }
}
