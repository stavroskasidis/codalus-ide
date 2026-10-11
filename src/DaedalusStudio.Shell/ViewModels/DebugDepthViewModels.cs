using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using DaedalusStudio.Debugging;
using DaedalusStudio.Shell.Infrastructure;
using DaedalusStudio.Workbench.Debugging;

namespace DaedalusStudio.Shell.ViewModels;

/// <summary>QuickWatch (Shift+F9, ADR 0021): one expression evaluated as a tree, editable, and added to Watch on request.</summary>
public sealed class QuickWatchViewModel : ObservableObject
{
    public const string ToolWindowId = "debug.quickWatch";

    private readonly DebugService _debugger;
    private readonly Func<string, Task> _addWatch;
    private string _expression = string.Empty;
    private string? _error;

    public QuickWatchViewModel(DebugService debugger, Func<string, Task> addWatch)
    {
        _debugger = debugger;
        _addWatch = addWatch;
        debugger.StateChanged += (_, _) => Ui.Post(() =>
        {
            if (debugger.State != DebugState.Stopped)
            {
                Roots.Clear();
            }
        });
        debugger.Stopped += (_, _) => Ui.Post(() => _ = ReevaluateAsync());
    }

    public string Expression
    {
        get => _expression;
        set => SetField(ref _expression, value ?? string.Empty);
    }

    public ObservableCollection<VariableNode> Roots { get; } = [];

    public string? Error
    {
        get => _error;
        private set
        {
            if (SetField(ref _error, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => _error is not null;

    /// <summary>Evaluates <paramref name="expression"/> (or the current one) in the selected frame.</summary>
    public async Task EvaluateAsync(string? expression = null)
    {
        if (expression is not null)
        {
            Expression = expression;
        }

        await ReevaluateAsync().ConfigureAwait(true);
    }

    private async Task ReevaluateAsync()
    {
        var expression = Expression.Trim();
        if (expression.Length == 0)
        {
            Roots.Clear();
            Error = null;
            return;
        }

        if (_debugger.State != DebugState.Stopped)
        {
            Error = "Expressions can be evaluated only while the program is paused.";
            Roots.Clear();
            return;
        }

        try
        {
            var result = await _debugger.EvaluateAsync(expression, "watch").ConfigureAwait(true);
            Roots.Clear();
            Roots.Add(new VariableNode(expression, result.Result, result.Type, result.VariablesReference, r => _debugger.VariablesAsync(r), evaluateName: expression) { IsExpanded = result.VariablesReference > 0 });
            Error = null;
        }
        catch (Exception ex) when (ex is DapException or DapConnectionLostException or InvalidOperationException or OperationCanceledException)
        {
            Roots.Clear();
            Error = ex.Message;
        }
    }

    public Task AddWatchAsync() => Expression.Trim() is { Length: > 0 } expression ? _addWatch(expression) : Task.CompletedTask;

    public async Task<string?> SetValueAsync(VariableNode node, string value)
    {
        var error = await VariableEditing.SetAsync(_debugger, node, value, Roots.Contains(node)).ConfigureAwait(true);
        if (error is null)
        {
            await ReevaluateAsync().ConfigureAwait(true);
        }

        return error;
    }
}

/// <summary>An editor data tip: one evaluated expression as an editable tree (ADR 0021).</summary>
public sealed class DataTipViewModel(DebugService debugger, string expression, EvaluateResponse result) : ObservableObject
{
    public ObservableCollection<VariableNode> Roots { get; } =
        [new VariableNode(expression, result.Result, result.Type, result.VariablesReference, r => debugger.VariablesAsync(r), evaluateName: expression)];

    public Task<string?> SetValueAsync(VariableNode node, string value) => VariableEditing.SetAsync(debugger, node, value, Roots.Contains(node));

    /// <summary>Pins the tip to its line (M14b); null where it can't be pinned (read-only documents).</summary>
    public Action? PinToSource { get; init; }

    public bool CanPin => PinToSource is not null;

    public void Pin() => PinToSource?.Invoke();
}

/// <summary>A row of the Threads window.</summary>
public sealed class ThreadRow(DapThread thread, string location, bool isCurrent, bool isFlagged, Action<ThreadRow, bool> setFlagged) : ObservableObject
{
    private bool _isFlagged = isFlagged;

    public DapThread Thread { get; } = thread;

    public int Id => Thread.Id;

    public string IdText => Thread.Id.ToString(CultureInfo.InvariantCulture);

    public string Name => string.IsNullOrEmpty(Thread.Name) ? "<No Name>" : Thread.Name;

    public string Location { get; } = location;

    public bool IsCurrent { get; } = isCurrent;

    public string Marker => IsCurrent ? "▶" : string.Empty;

    public bool IsFlagged
    {
        get => _isFlagged;
        set
        {
            if (SetField(ref _isFlagged, value))
            {
                setFlagged(this, value);
            }
        }
    }

    public string AccessibleName => $"Thread {IdText} {Name}, {Location}{(IsCurrent ? ", current" : string.Empty)}{(IsFlagged ? ", flagged" : string.Empty)}";
}

/// <summary>The Threads window: every thread with its location; flags; switching the active thread.</summary>
public sealed class ThreadsViewModel : ObservableObject
{
    public const string ToolWindowId = "debug.threads";

    private readonly DebugService _debugger;
    private readonly HashSet<int> _flagged = [];
    private bool _flaggedOnly;
    private int _generation;

    public ThreadsViewModel(DebugService debugger)
    {
        _debugger = debugger;
        debugger.Stopped += (_, _) => Ui.Post(() => _ = RefreshAsync());
        debugger.FrameChanged += (_, _) => Ui.Post(() => _ = RefreshAsync());
        debugger.StateChanged += (_, _) => Ui.Post(() =>
        {
            if (debugger.State != DebugState.Stopped)
            {
                _generation++;
                Rows.Clear();
                OnPropertyChanged(nameof(IsEmpty));
            }

            if (debugger.State == DebugState.Idle)
            {
                _flagged.Clear();
            }
        });
    }

    public ObservableCollection<ThreadRow> Rows { get; } = [];

    public bool IsEmpty => Rows.Count == 0;

    /// <summary>Shows only the flagged threads.</summary>
    public bool FlaggedOnly
    {
        get => _flaggedOnly;
        set
        {
            if (SetField(ref _flaggedOnly, value))
            {
                _ = RefreshAsync();
            }
        }
    }

    public async Task RefreshAsync()
    {
        var generation = ++_generation;
        if (_debugger.State != DebugState.Stopped)
        {
            Rows.Clear();
            OnPropertyChanged(nameof(IsEmpty));
            return;
        }

        IReadOnlyDictionary<int, StackFrame> tops;
        try
        {
            tops = await _debugger.TopFramesAsync(CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is DapException or DapConnectionLostException or OperationCanceledException)
        {
            tops = new Dictionary<int, StackFrame>();
        }

        if (generation != _generation)
        {
            return;
        }

        Rows.Clear();
        foreach (var thread in _debugger.Threads.Where(t => !_flaggedOnly || _flagged.Contains(t.Id)))
        {
            var location = tops.TryGetValue(thread.Id, out var frame) ? frame.Name : "[External Code]";
            Rows.Add(new ThreadRow(thread, location, thread.Id == _debugger.SelectedThreadId, _flagged.Contains(thread.Id), (row, flagged) =>
            {
                if (flagged)
                {
                    _flagged.Add(row.Id);
                }
                else
                {
                    _flagged.Remove(row.Id);
                }
            }));
        }

        OnPropertyChanged(nameof(IsEmpty));
    }

    /// <summary>Makes the thread the active one (Call Stack, Locals and Watch follow).</summary>
    public Task ActivateAsync(ThreadRow row) => _debugger.SelectThreadAsync(row.Id);
}

/// <summary>A row of the Modules window.</summary>
public sealed record ModuleRow(DebugModule Module)
{
    public string Name => Module.Name;

    public string Path => Module.Path ?? string.Empty;

    public string SymbolStatus => Module.SymbolStatus ?? string.Empty;

    public string Optimized => Module.IsOptimized switch { true => "Yes", false => "No", _ => string.Empty };

    public string UserCode => Module.IsUserCode switch { true => "Yes", false => "No", _ => string.Empty };

    public string Order => (Module.Order + 1).ToString(CultureInfo.InvariantCulture);

    public string AccessibleName => $"{Name}, {SymbolStatus}";
}

/// <summary>The Modules window: the modules the debuggee loaded, with their symbol status (from <c>module</c> events).</summary>
public sealed class ModulesViewModel : ObservableObject
{
    public const string ToolWindowId = "debug.modules";

    private readonly DebugService _debugger;
    private string _filter = string.Empty;
    private bool _queued;

    public ModulesViewModel(DebugService debugger)
    {
        _debugger = debugger;
        debugger.ModulesChanged += (_, _) => Ui.Post(Queue);
        Refresh();
    }

    public ObservableCollection<ModuleRow> Rows { get; } = [];

    public bool IsEmpty => Rows.Count == 0;

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

    private void Queue()
    {
        if (_queued)
        {
            return;
        }

        _queued = true;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _queued = false;
            Refresh();
        }, Avalonia.Threading.DispatcherPriority.Background);
    }

    public void Refresh()
    {
        Rows.Clear();
        foreach (var module in _debugger.Modules.Where(m => _filter.Length == 0
                     || m.Name.Contains(_filter, StringComparison.OrdinalIgnoreCase)
                     || (m.Path?.Contains(_filter, StringComparison.OrdinalIgnoreCase) ?? false)))
        {
            Rows.Add(new ModuleRow(module));
        }

        OnPropertyChanged(nameof(IsEmpty));
    }
}

/// <summary>A type in the Exception Settings window.</summary>
public sealed class ExceptionTypeRow(string type, bool breaks, ExceptionSettings settings) : ObservableObject
{
    private bool _breaks = breaks;

    public string Type { get; } = type;

    public bool BreakWhenThrown
    {
        get => _breaks;
        set
        {
            if (SetField(ref _breaks, value))
            {
                settings.Set(Type, value);
            }
        }
    }

    /// <summary>Module names where it breaks (M14b): comma-separated, wildcards, <c>!</c> to exclude.</summary>
    public string Condition
    {
        get => settings.ConditionOf(Type) ?? string.Empty;
        set => settings.SetCondition(Type, value);
    }

    public string AccessibleName => $"{Type}, {(BreakWhenThrown ? "breaks when thrown" : "doesn't break when thrown")}{(settings.ConditionOf(Type) is { } c ? ", in modules " + c : string.Empty)}";
}

/// <summary>The Exception Settings window (ADR 0021): which exception types break when thrown.</summary>
public sealed class ExceptionSettingsViewModel : ObservableObject
{
    public const string ToolWindowId = "debug.exceptionSettings";

    private readonly ExceptionSettings _settings;
    private string _filter = string.Empty;
    private string _newType = string.Empty;
    private bool _syncing;

    public ExceptionSettingsViewModel(ExceptionSettings settings)
    {
        _settings = settings;
        settings.Changed += (_, _) => Ui.Post(Refresh);
        Refresh();
    }

    public ObservableCollection<ExceptionTypeRow> Rows { get; } = [];

    /// <summary>Whether types that aren't listed break when thrown.</summary>
    public bool BreakOnOthers
    {
        get => _settings.BreakOnOthers;
        set
        {
            if (!_syncing && value != _settings.BreakOnOthers)
            {
                _settings.SetBreakOnOthers(value);
            }
        }
    }

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

    public string NewType
    {
        get => _newType;
        set => SetField(ref _newType, value ?? string.Empty);
    }

    public void Refresh()
    {
        _syncing = true;
        try
        {
            Rows.Clear();
            foreach (var (type, breaks) in _settings.Types.Where(t => _filter.Length == 0 || t.Key.Contains(_filter, StringComparison.OrdinalIgnoreCase)))
            {
                Rows.Add(new ExceptionTypeRow(type, breaks, _settings));
            }

            OnPropertyChanged(nameof(BreakOnOthers));
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary>Adds <see cref="NewType"/> to the list, breaking when thrown.</summary>
    public void Add()
    {
        if (NewType.Trim() is { Length: > 0 } type)
        {
            _settings.Set(type, true);
            NewType = string.Empty;
        }
    }

    public void Remove(ExceptionTypeRow row) => _settings.Remove(row.Type);

    public void SetAll(bool value) => _settings.SetAll(value);

    public void Reset() => _settings.Reset();
}

/// <summary>The built-in string visualizers (ADR 0021).</summary>
public enum VisualizerKind
{
    Text,
    Json,
    Xml,
    Html,

    /// <summary>A collection's elements as a table (M14b).</summary>
    Grid,

    /// <summary>A byte array shown as the image it holds (M14b).</summary>
    Image,
}

/// <summary>Turns a debugger string value into a visualizer's text.</summary>
public static class Visualizers
{
    public static string Title(VisualizerKind kind) => kind switch
    {
        VisualizerKind.Json => "JSON Visualizer",
        VisualizerKind.Xml => "XML Visualizer",
        VisualizerKind.Html => "HTML Visualizer",
        VisualizerKind.Grid => "Grid Visualizer",
        VisualizerKind.Image => "Image Visualizer",
        _ => "Text Visualizer",
    };

    /// <summary>The file extension that picks the visualizer's grammar.</summary>
    public static string Extension(VisualizerKind kind) => kind switch
    {
        VisualizerKind.Json => ".json",
        VisualizerKind.Xml => ".xml",
        VisualizerKind.Html => ".html",
        _ => ".txt",
    };

    /// <summary>The string a C# literal (as the debugger shows it) stands for: quotes removed, escapes resolved.</summary>
    public static string Unquote(string value)
    {
        if (value.Length < 2 || value[0] != '"' || value[^1] != '"')
        {
            return value;
        }

        var text = new StringBuilder(value.Length);
        for (var i = 1; i < value.Length - 1; i++)
        {
            var c = value[i];
            if (c != '\\' || i + 1 >= value.Length - 1)
            {
                text.Append(c);
                continue;
            }

            var next = value[++i];
            switch (next)
            {
                case 'n': text.Append('\n'); break;
                case 'r': text.Append('\r'); break;
                case 't': text.Append('\t'); break;
                case '0': text.Append('\0'); break;
                case 'a': text.Append('\a'); break;
                case 'b': text.Append('\b'); break;
                case 'f': text.Append('\f'); break;
                case 'v': text.Append('\v'); break;
                case 'u' when i + 4 < value.Length - 1 && int.TryParse(value.AsSpan(i + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code):
                    text.Append((char)code);
                    i += 4;
                    break;
                default: text.Append(next); break;
            }
        }

        return text.ToString();
    }

    /// <summary>The text to show: JSON and XML are indented when they parse; otherwise the text is shown as it is.</summary>
    public static string Format(VisualizerKind kind, string text)
    {
        try
        {
            switch (kind)
            {
                case VisualizerKind.Json:
                    using (var document = JsonDocument.Parse(text, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }))
                    {
                        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
                        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
                        {
                            document.WriteTo(writer);
                        }

                        return Encoding.UTF8.GetString(buffer.WrittenSpan);
                    }

                case VisualizerKind.Xml:
                    return System.Xml.Linq.XDocument.Parse(text).ToString();
                default:
                    return text;
            }
        }
        catch (Exception ex) when (ex is JsonException or System.Xml.XmlException)
        {
            return text;
        }
    }

    private static readonly VisualizerKind[] StringKinds = [VisualizerKind.Text, VisualizerKind.Json, VisualizerKind.Xml, VisualizerKind.Html];

    /// <summary>The visualizers for a value that isn't a string: Grid for collections, and Image too for byte arrays.</summary>
    public static IReadOnlyList<VisualizerKind> ForType(string? type) =>
        CollectionVisualizers.IsByteArray(type) ? [VisualizerKind.Image, VisualizerKind.Grid]
        : CollectionVisualizers.IsCollection(type) ? [VisualizerKind.Grid]
        : [];

    /// <summary>The kinds a string value probably is, most likely first (Text is always offered).</summary>
    public static IReadOnlyList<VisualizerKind> Suggested(string text)
    {
        var trimmed = text.TrimStart();
        var kinds = new List<VisualizerKind>();
        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
        {
            kinds.Add(VisualizerKind.Json);
        }
        else if (trimmed.StartsWith("<!doctype html", StringComparison.OrdinalIgnoreCase) || trimmed.StartsWith("<html", StringComparison.OrdinalIgnoreCase))
        {
            kinds.Add(VisualizerKind.Html);
        }
        else if (trimmed.StartsWith('<'))
        {
            kinds.Add(VisualizerKind.Xml);
        }

        kinds.Add(VisualizerKind.Text);
        foreach (var kind in StringKinds.Where(k => !kinds.Contains(k)))
        {
            kinds.Add(kind);
        }

        return kinds;
    }
}
