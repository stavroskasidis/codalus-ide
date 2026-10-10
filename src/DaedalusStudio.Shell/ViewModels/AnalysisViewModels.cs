using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using Avalonia.Threading;
using DaedalusStudio.LanguageServices.Protocol;
using DaedalusStudio.Shell.Editor;
using DaedalusStudio.Shell.Infrastructure;

namespace DaedalusStudio.Shell.ViewModels;

/// <summary>A node, token or trivia in the Syntax Visualizer.</summary>
public sealed class SyntaxItemViewModel : ObservableObject
{
    private bool _isExpanded;

    public SyntaxItemViewModel(SyntaxNodeItem item)
    {
        Item = item;
        _isExpanded = item.OnPath;
        Children = [.. item.Children.Select(c => new SyntaxItemViewModel(c))];
    }

    public SyntaxNodeItem Item { get; }

    public IReadOnlyList<SyntaxItemViewModel> Children { get; }

    public string Label => Item.Text is { } text ? $"{Item.Kind} \"{text}\"" : Item.Kind;

    public string Span => $"[{Item.Range.Start.Line + 1}:{Item.Range.Start.Character + 1}–{Item.Range.End.Line + 1}:{Item.Range.End.Character + 1}]" + (Item.Truncated ? " …" : string.Empty);

    /// <summary>Visual Studio's colors: nodes blue, tokens green, trivia maroon (theme tokens).</summary>
    public string Category => Item.Category;

    public bool IsNode => Item.Category == "node";

    public bool IsToken => Item.Category == "token";

    public bool IsTrivia => Item.Category == "trivia";

    public string AccessibleName => $"{Item.Category} {Label}";

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetField(ref _isExpanded, value);
    }

    /// <summary>The innermost item on the path to the caret.</summary>
    public SyntaxItemViewModel Deepest()
    {
        var current = this;
        while (current.Children.FirstOrDefault(c => c.Item.OnPath) is { } next)
        {
            current = next;
        }

        return current;
    }
}

/// <summary>An operation of the <c>IOperation</c> tree.</summary>
public sealed class OperationItemViewModel(OperationItem item)
{
    public string Label { get; } = item.Type is { } type ? $"{item.Kind} ({type})" : item.Kind;

    public string Syntax { get; } = item.Syntax;

    public IReadOnlyList<OperationItemViewModel> Children { get; } = [.. item.Children.Select(c => new OperationItemViewModel(c))];

    public bool IsExpanded => true;
}

/// <summary>A row of a property grid: name and value.</summary>
public sealed record PropertyRow(string Name, string Value);

/// <summary>
/// The Syntax Visualizer tool window (M12b): the C# syntax tree of the active document, kept in step with the caret,
/// with the properties and semantics of the node there and its <c>IOperation</c> tree.
/// </summary>
public sealed class SyntaxVisualizerViewModel : ObservableObject, IDisposable
{
    public const string ToolWindowId = "syntaxVisualizer";

    private readonly Func<CodeEditorController, CancellationToken, Task<SyntaxTreeResult?>> _query;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private CodeEditorController? _editor;
    private CancellationTokenSource? _cts;
    private SyntaxItemViewModel? _selected;
    private SyntaxSemantics? _semantics;
    private string _message = "Open a C# file to see its syntax tree.";
    private bool _updating;

    public SyntaxVisualizerViewModel(Func<CodeEditorController, CancellationToken, Task<SyntaxTreeResult?>> query)
    {
        _query = query;
        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            _ = RefreshAsync();
        };
    }

    public ObservableCollection<SyntaxItemViewModel> Roots { get; } = [];

    public ObservableCollection<OperationItemViewModel> Operations { get; } = [];

    public ObservableCollection<PropertyRow> Properties { get; } = [];

    public string Message
    {
        get => _message;
        private set => SetField(ref _message, value);
    }

    public bool HasMessage => Roots.Count == 0;

    public bool HasOperations => Operations.Count > 0;

    /// <summary>The selected item; choosing one selects its text in the editor.</summary>
    public SyntaxItemViewModel? Selected
    {
        get => _selected;
        set
        {
            if (SetField(ref _selected, value))
            {
                ShowProperties();
                if (!_updating && value is not null && _editor is { } editor)
                {
                    var start = editor.Model.OffsetAt(value.Item.Range.Start);
                    var end = editor.Model.OffsetAt(value.Item.Range.End);
                    editor.Editor.Select(start, end - start);
                    var location = editor.Model.Text.GetLocation(start);
                    editor.Editor.ScrollTo(location.Line, location.Column);
                }
            }
        }
    }

    /// <summary>Whether the tool window is open; while it is closed nothing is requested.</summary>
    public Func<bool> IsShown { get; set; } = () => true;

    /// <summary>Completes when the tree for the current caret has been shown (tests).</summary>
    public Task? Pending { get; private set; }

    public void Dispose()
    {
        _timer.Stop();
        SetEditor(null);
        _cts?.Cancel();
        _cts?.Dispose();
    }

    /// <summary>Follows another editor (null: none).</summary>
    public void SetEditor(CodeEditorController? editor)
    {
        if (_editor == editor)
        {
            return;
        }

        if (_editor is not null)
        {
            _editor.CaretChanged -= OnChanged;
            _editor.Editor.Document.Changed -= OnChanged;
        }

        _editor = editor is { Model.LanguageId: "csharp" } ? editor : null;
        if (_editor is not null)
        {
            _editor.CaretChanged += OnChanged;
            _editor.Editor.Document.Changed += OnChanged;
        }

        Pending = RefreshAsync();
    }

    private void OnChanged(object? sender, EventArgs e)
    {
        // The editor selects text when an item is chosen; that moves the caret, which must not rebuild the tree.
        if (!_updating && (_editor?.Editor.TextArea.IsKeyboardFocusWithin == true || sender is AvaloniaEdit.Document.TextDocument))
        {
            _timer.Stop();
            _timer.Start();
        }
    }

    /// <summary>
    /// Asks for the tree at the caret again (also after edits, 400 ms later); <paramref name="force"/>: even while the
    /// tool window is not open yet (it is being shown).
    /// </summary>
    public Task RefreshAsync(bool force = false)
    {
        _cts?.Cancel();
        if (_editor is not { } editor)
        {
            Clear("Open a C# file to see its syntax tree.");
            return Task.CompletedTask;
        }

        if (!force && !IsShown())
        {
            return Task.CompletedTask;
        }

        var cts = _cts = new CancellationTokenSource();
        return Pending = LoadAsync(editor, cts.Token);
    }

    private async Task LoadAsync(CodeEditorController editor, CancellationToken cancellationToken)
    {
        // A document opened a moment ago may not have reached the language service yet: ask again a few times.
        SyntaxTreeResult? result = null;
        for (var attempt = 1; attempt <= 5 && result is null; attempt++)
        {
            try
            {
                result = await _query(editor, cancellationToken).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or DaedalusStudio.Rpc.JsonRpcRemoteException or DaedalusStudio.Rpc.JsonRpcConnectionLostException)
            {
                result = null;
            }

            if (cancellationToken.IsCancellationRequested || editor != _editor)
            {
                return;
            }

            if (result is null && attempt < 5)
            {
                try
                {
                    await Task.Delay(400 * attempt, cancellationToken).ConfigureAwait(true);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }

        if (result is null)
        {
            Clear("The C# language service is not ready.");
            return;
        }

        _updating = true;
        try
        {
            Roots.Clear();
            var root = new SyntaxItemViewModel(result.Root);
            Roots.Add(root);
            _semantics = result.Semantics;
            Operations.Clear();
            if (result.Semantics?.Operation is { } operation)
            {
                Operations.Add(new OperationItemViewModel(operation));
            }

            Selected = root.Deepest();
            OnPropertyChanged(nameof(HasMessage));
            OnPropertyChanged(nameof(HasOperations));
            ShowProperties();
        }
        finally
        {
            _updating = false;
        }
    }

    private void Clear(string message)
    {
        Roots.Clear();
        Operations.Clear();
        Properties.Clear();
        _semantics = null;
        Message = message;
        OnPropertyChanged(nameof(HasMessage));
        OnPropertyChanged(nameof(HasOperations));
    }

    private void ShowProperties()
    {
        Properties.Clear();
        if (_selected is { } item)
        {
            Properties.Add(new PropertyRow("Kind", item.Item.Kind));
            Properties.Add(new PropertyRow("Category", item.Item.Category));
            Properties.Add(new PropertyRow("Span", item.Span));
            if (item.Item.Text is { } text)
            {
                Properties.Add(new PropertyRow("Text", text));
            }
        }

        if (_semantics is { } semantics)
        {
            Properties.Add(new PropertyRow("Semantics of", semantics.NodeKind));
            void Add(string name, string? value)
            {
                if (value is not null)
                {
                    Properties.Add(new PropertyRow(name, value));
                }
            }

            Add("Symbol", semantics.Symbol);
            Add("Symbol kind", semantics.SymbolKind);
            Add("Type", semantics.Type);
            Add("Converted type", semantics.ConvertedType);
            Add("Conversion", semantics.Conversion);
            Add("Constant value", semantics.ConstantValue);
        }
    }
}

/// <summary>A row of the Code Metrics results: a project, namespace, type or member.</summary>
public sealed class MetricsNodeViewModel : ObservableObject
{
    private bool _isExpanded;

    public MetricsNodeViewModel(CodeMetricsItem item)
    {
        Item = item;
        _isExpanded = item.Kind is "project";
        Children = [.. item.Children.Select(c => new MetricsNodeViewModel(c))];
    }

    public CodeMetricsItem Item { get; }

    public IReadOnlyList<MetricsNodeViewModel> Children { get; }

    public string Name => Item.Name;

    public string Icon => Item.Kind switch
    {
        "project" => "project",
        "namespace" => "namespace",
        "type" => "class",
        _ => "method",
    };

    public string Maintainability => Item.MaintainabilityIndex.ToString(CultureInfo.InvariantCulture);

    /// <summary>Visual Studio's ratings: 20–100 good, 10–19 moderate, 0–9 low.</summary>
    public string Rating => Item.MaintainabilityIndex >= 20 ? "good" : Item.MaintainabilityIndex >= 10 ? "moderate" : "low";

    public bool IsModerate => Rating == "moderate";

    public bool IsLow => Rating == "low";

    public string Complexity => Item.CyclomaticComplexity.ToString(CultureInfo.InvariantCulture);

    public string Depth => Item.Kind == "member" ? string.Empty : Item.DepthOfInheritance.ToString(CultureInfo.InvariantCulture);

    public string Coupling => Item.ClassCoupling.ToString(CultureInfo.InvariantCulture);

    public string SourceLines => Item.SourceLines.ToString(CultureInfo.InvariantCulture);

    public string ExecutableLines => Item.ExecutableLines.ToString(CultureInfo.InvariantCulture);

    public string AccessibleName =>
        $"{Item.Kind} {Name}: maintainability {Maintainability} ({Rating}), complexity {Complexity}, coupling {Coupling}, {SourceLines} lines";

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetField(ref _isExpanded, value);
    }

    public IEnumerable<MetricsNodeViewModel> DescendantsAndSelf() => Children.SelectMany(c => c.DescendantsAndSelf()).Prepend(this);
}

/// <summary>The Code Metrics tool window (M12b): calculated on demand for the solution or the active document's project.</summary>
public sealed class CodeMetricsViewModel : ObservableObject, IDisposable
{
    public const string ToolWindowId = "codeMetrics";

    private readonly Func<bool, CancellationToken, Task<CodeMetricsResult?>> _calculate;
    private readonly Func<Location, Task> _open;
    private CancellationTokenSource? _cts;
    private string _message = "Calculate the metrics of the solution or of the active project.";
    private bool _isBusy;

    public CodeMetricsViewModel(Func<bool, CancellationToken, Task<CodeMetricsResult?>> calculate, Func<Location, Task> open, Func<string, Task> copy)
    {
        _calculate = calculate;
        _open = open;
        SolutionCommand = new RelayCommand(() => _ = CalculateAsync(projectOnly: false));
        ProjectCommand = new RelayCommand(() => _ = CalculateAsync(projectOnly: true));
        CopyCommand = new RelayCommand(() => _ = copy(ToTable()));
    }

    public ObservableCollection<MetricsNodeViewModel> Roots { get; } = [];

    public ICommand SolutionCommand { get; }

    public ICommand ProjectCommand { get; }

    public ICommand CopyCommand { get; }

    public string Message
    {
        get => _message;
        private set => SetField(ref _message, value);
    }

    public bool HasMessage => Roots.Count == 0;

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetField(ref _isBusy, value);
    }

    public async Task CalculateAsync(bool projectOnly)
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        IsBusy = true;
        Roots.Clear();
        Message = "Calculating code metrics…";
        OnPropertyChanged(nameof(HasMessage));
        try
        {
            var result = await _calculate(projectOnly, cts.Token).ConfigureAwait(true);
            if (cts.IsCancellationRequested)
            {
                return;
            }

            foreach (var project in result?.Projects ?? [])
            {
                Roots.Add(new MetricsNodeViewModel(project));
            }

            Message = result is null ? "The C# language service is not running. Open a solution first."
                : Roots.Count == 0 ? "No C# projects to measure." : string.Empty;
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidOperationException or DaedalusStudio.Rpc.JsonRpcRemoteException or DaedalusStudio.Rpc.JsonRpcConnectionLostException)
        {
            Message = cts.IsCancellationRequested ? Message : "Could not calculate the metrics: " + ex.Message;
        }
        finally
        {
            if (_cts == cts)
            {
                IsBusy = false;
            }

            OnPropertyChanged(nameof(HasMessage));
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
    }

    public Task OpenAsync(MetricsNodeViewModel node) => node.Item.Location is { } location ? _open(location) : Task.CompletedTask;

    /// <summary>The results as tab-separated text, with a header (Copy).</summary>
    public string ToTable()
    {
        var lines = new List<string> { "Scope\tName\tMaintainability Index\tCyclomatic Complexity\tDepth of Inheritance\tClass Coupling\tLines of Source Code\tLines of Executable Code" };
        lines.AddRange(Roots.SelectMany(r => r.DescendantsAndSelf()).Select(n =>
            string.Join('\t', n.Item.Kind, n.Name, n.Maintainability, n.Complexity, n.Depth, n.Coupling, n.SourceLines, n.ExecutableLines)));
        return string.Join(Environment.NewLine, lines);
    }
}
