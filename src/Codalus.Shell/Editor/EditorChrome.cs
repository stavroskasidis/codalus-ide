using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Folding;
using AvaloniaEdit.Rendering;
using Codalus.LanguageServices.Protocol;
using Codalus.Shell.Controls;
using Codalus.Shell.ViewModels;

namespace Codalus.Shell.Editor;

/// <summary>
/// The navigation bar above the editor (M12a): the types of the document and the members of the selected type, from
/// the document symbols. It follows the caret; choosing an entry moves the caret to it.
/// </summary>
public sealed class NavigationBar : Border
{
    private readonly ComboBox _types = new() { Classes = { "navigationBar" } };
    private readonly ComboBox _members = new() { Classes = { "navigationBar" } };
    private CodeEditorController? _controller;
    private bool _updating;

    public NavigationBar()
    {
        // A flat bar of two pickers (M34a): Border.navigationBar and ComboBox.navigationBar in ShellStyles.axaml.
        Classes.Add("navigationBar");
        var template = new FuncDataTemplate<NavigationEntry>((entry, _) => entry is null ? new TextBlock() : new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 5,
            Children =
            {
                new Icon { Kind = entry.Icon, Width = 14, Height = 14, VerticalAlignment = VerticalAlignment.Center },
                new TextBlock { Text = entry.Display, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis },
            },
        });
        _types.PlaceholderText = "Types";
        _members.PlaceholderText = "Members";
        _types.ItemTemplate = template;
        _members.ItemTemplate = template;
        AutomationProperties.SetName(_types, "Types");
        AutomationProperties.SetName(_members, "Members");
        _types.SelectionChanged += (_, _) => OnPicked(_types, members: false);
        _members.SelectionChanged += (_, _) => OnPicked(_members, members: true);
        Grid.SetColumn(_members, 1);
        Child = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), Children = { _types, _members } };
        ToolTip.SetTip(_types, "Types");
        ToolTip.SetTip(_members, "Members");
        IsVisible = false;
    }

    /// <summary>An entry: a symbol and how it is shown (nested types as <c>Outer.Inner</c>).</summary>
    public sealed record NavigationEntry(DocumentSymbol Symbol, string Display, string Icon)
    {
        public override string ToString() => Display;
    }

    /// <summary>The entries (tests).</summary>
    internal IReadOnlyList<NavigationEntry> Types => _types.ItemsSource as IReadOnlyList<NavigationEntry> ?? [];

    internal IReadOnlyList<NavigationEntry> Members => _members.ItemsSource as IReadOnlyList<NavigationEntry> ?? [];

    internal NavigationEntry? SelectedType => _types.SelectedItem as NavigationEntry;

    internal NavigationEntry? SelectedMember => _members.SelectedItem as NavigationEntry;

    public void Attach(CodeEditorController controller)
    {
        Detach();
        _controller = controller;
        controller.Insights.SymbolsChanged += OnSymbolsChanged;
        controller.CaretChanged += OnCaretChanged;
        Rebuild();
    }

    public void Detach()
    {
        if (_controller is not null)
        {
            _controller.Insights.SymbolsChanged -= OnSymbolsChanged;
            _controller.CaretChanged -= OnCaretChanged;
            _controller = null;
        }
    }

    public bool Enabled { get; set; } = true;

    private void OnSymbolsChanged(object? sender, EventArgs e) => Rebuild();

    private bool _caretQueued;

    private void OnCaretChanged(object? sender, EventArgs e)
    {
        if (_caretQueued)
        {
            return;
        }

        // At most every 100 ms, after the keystroke's frame (typing budget).
        _caretQueued = true;
        DispatcherTimer.RunOnce(() =>
        {
            _caretQueued = false;
            SyncToCaret();
        }, TimeSpan.FromMilliseconds(100), DispatcherPriority.Background);
    }

    private static bool IsType(DocumentSymbol symbol) =>
        symbol.Kind is SymbolKind.Class or SymbolKind.Interface or SymbolKind.Struct or SymbolKind.Enum;

    private void Rebuild()
    {
        var symbols = _controller?.Insights.Symbols ?? [];
        var types = new List<NavigationEntry>();
        void Collect(IEnumerable<DocumentSymbol> level, string? outer)
        {
            foreach (var symbol in level)
            {
                if (IsType(symbol))
                {
                    var display = outer is null ? symbol.Name : outer + "." + symbol.Name;
                    types.Add(new NavigationEntry(symbol, display, SymbolNodeViewModel.IconFor(symbol.Kind)));
                    Collect(symbol.Children ?? [], display);
                }
                else if (symbol.Kind is SymbolKind.Namespace or SymbolKind.Module)
                {
                    Collect(symbol.Children ?? [], outer);
                }
            }
        }

        Collect(symbols, null);
        IsVisible = Enabled && types.Count > 0;
        _updating = true;
        _types.ItemsSource = types;
        _updating = false;
        SyncToCaret();
    }

    private void SyncToCaret()
    {
        if (_controller is not { } controller || !IsVisible)
        {
            return;
        }

        var path = controller.Insights.SymbolPath(controller.Model.PositionAt(controller.Editor.CaretOffset));
        var type = path.LastOrDefault(IsType);
        var member = path.LastOrDefault(s => !IsType(s) && s.Kind is not (SymbolKind.Namespace or SymbolKind.Module));
        _updating = true;
        var types = Types;
        var typeEntry = types.FirstOrDefault(t => t.Symbol == type) ?? (type is null && types.Count > 0 ? types[0] : null);
        if (!ReferenceEquals(_types.SelectedItem, typeEntry))
        {
            _types.SelectedItem = typeEntry;
        }

        var members = (typeEntry?.Symbol.Children ?? []).Where(s => !IsType(s))
            .Select(s => new NavigationEntry(s, s.Name + (s.Detail is { } detail && detail.StartsWith('(') ? detail.Split(" : ")[0] : string.Empty), SymbolNodeViewModel.IconFor(s.Kind)))
            .ToList();
        if (!Members.Select(m => m.Symbol).SequenceEqual(members.Select(m => m.Symbol)))
        {
            _members.ItemsSource = members;
        }

        var memberEntry = Members.FirstOrDefault(m => m.Symbol == member);
        if (!ReferenceEquals(_members.SelectedItem, memberEntry))
        {
            _members.SelectedItem = memberEntry;
        }

        _updating = false;
    }

    private void OnPicked(ComboBox box, bool members)
    {
        if (_updating || _controller is not { } controller || box.SelectedItem is not NavigationEntry entry || !box.IsDropDownOpen && !box.IsKeyboardFocusWithin)
        {
            return;
        }

        controller.Navigate(entry.Symbol.SelectionRange.Start);
        if (!members)
        {
            SyncToCaret();
        }
    }

    /// <summary>Selects an entry as the user would (tests).</summary>
    internal void Pick(NavigationEntry entry, bool member)
    {
        _controller?.Navigate(entry.Symbol.SelectionRange.Start);
        SyncToCaret();
    }
}

/// <summary>
/// Sticky scroll (M12a): the header lines of the scopes around the top of the view (namespace, type, member), pinned
/// above the text while scrolling. From the document symbols, or the foldings for languages without them. Clicking a
/// header goes to it.
/// </summary>
/// <remarks>
/// Each pinned line is drawn as the editor draws it: its runs take the colors and font styles of the line's visual line,
/// which the editor's own colorizers (the grammar, semantic tokens, bracket pairs) built, with its indentation, in the
/// text column, and its number in the line number margin. They were one plain text in the default color (the
/// maintainer's report). Only the few pinned lines are built, and their controls are rebuilt only when a line's runs
/// change (scrolling to other scopes, typing, new tokens, the theme).
/// </remarks>
public sealed class StickyScroll : Border
{
    private const int MaxLines = 5;
    private readonly TextEditor _editor;
    private readonly Func<IReadOnlyList<DocumentSymbol>> _symbols;
    private readonly Func<FoldingManager?> _foldings;
    private readonly StackPanel _lines = new();
    private IReadOnlyList<int> _shown = [];
    private List<StickyLine> _rendered = [];

    public StickyScroll(TextEditor editor, Func<IReadOnlyList<DocumentSymbol>> symbols, Func<FoldingManager?> foldings)
    {
        _editor = editor;
        _symbols = symbols;
        _foldings = foldings;
        VerticalAlignment = VerticalAlignment.Top;
        BorderThickness = new Thickness(0, 0, 0, 1);
        BoxShadow = new BoxShadows(new BoxShadow { OffsetY = 1, Blur = 3, Color = Color.FromArgb(0x30, 0, 0, 0) });
        Child = _lines;
        IsVisible = false;
        AutomationProperties.SetName(this, "Sticky scroll");
        // An adorner of the text area (the margins and the text view): the editor draws its text above its own layers
        // and above sibling controls, so only the adorner layer (over the window's content) covers it.
        var area = editor.TextArea;
        area.AttachedToVisualTree += (_, _) => AdornerLayer.SetAdorner(area, this);
        area.DetachedFromVisualTree += (_, _) => AdornerLayer.SetAdorner(area, null);
        area.TextView.ScrollOffsetChanged += (_, _) => Update();
        area.TextView.VisualLinesChanged += (_, _) => Update();
    }

    public bool Enabled { get; set; } = true;

    /// <summary>The one-based lines shown (tests).</summary>
    internal IReadOnlyList<int> Shown => _shown;

    /// <summary>Raised with the one-based lines pinned when they change (the controller keeps their semantic tokens).</summary>
    public event EventHandler<IReadOnlyList<int>>? LinesPinned;

    public void Update()
    {
        var view = _editor.TextArea.TextView;
        if (!Enabled || !view.VisualLinesValid || view.VisualLines.Count == 0 || view.VerticalOffset <= 0)
        {
            Show([]);
            return;
        }

        var document = _editor.Document;
        var lineHeight = view.DefaultLineHeight;
        var headers = new List<int>();
        for (var pass = 0; pass <= MaxLines; pass++)
        {
            // The line under the pinned headers decides which scopes are open.
            var top = view.GetDocumentLineByVisualTop(view.VerticalOffset + (headers.Count * lineHeight))?.LineNumber ?? 1;
            var next = Headers(document, top).Take(MaxLines).ToList();
            if (next.SequenceEqual(headers))
            {
                break;
            }

            headers = next;
        }

        Show(headers);
    }

    private IEnumerable<int> Headers(AvaloniaEdit.Document.TextDocument document, int topLine)
    {
        var symbols = _symbols();
        if (symbols.Count > 0)
        {
            var level = symbols;
            while (level.FirstOrDefault(s => s.Range.Start.Line + 1 <= topLine && topLine <= s.Range.End.Line + 1) is { } symbol)
            {
                var header = symbol.SelectionRange.Start.Line + 1;
                if (header < topLine && symbol.Range.End.Line + 1 > topLine)
                {
                    yield return header;
                }

                level = symbol.Children ?? [];
            }

            yield break;
        }

        if (_foldings() is { } foldings && topLine <= document.LineCount)
        {
            var offset = document.GetLineByNumber(topLine).Offset;
            foreach (var folding in foldings.GetFoldingsContaining(offset).OrderBy(f => f.StartOffset))
            {
                var header = document.GetLineByOffset(folding.StartOffset).LineNumber;
                var end = document.GetLineByOffset(folding.EndOffset).LineNumber;
                if (header < topLine && end > topLine)
                {
                    yield return header;
                }
            }
        }
    }

    private IBrush Resource(string key) =>
        _editor.TryFindResource(key, _editor.ActualThemeVariant, out var resource) && resource is IBrush brush ? brush : Brushes.Transparent;

    private void Show(List<int> headers)
    {
        var changed = !headers.SequenceEqual(_shown);
        _shown = headers;
        IsVisible = headers.Count > 0;
        if (headers.Count == 0)
        {
            _lines.Children.Clear();
            _rendered = [];
            return;
        }

        if (changed)
        {
            LinesPinned?.Invoke(this, headers);
        }

        var lines = headers.Select(Line).ToList();
        if (!lines.SequenceEqual(_rendered))
        {
            // Opaque, in the editor's colors (resolved now: the theme may have changed since the last time).
            Background = Resource("Shell.Editor.Background");
            BorderBrush = Resource("Shell.Border");
            _rendered = lines;
            _lines.Children.Clear();
            foreach (var line in lines)
            {
                _lines.Children.Add(Row(line));
            }
        }

        // The columns follow the margins and the horizontal scroll, which don't change the lines' runs.
        var view = _editor.TextArea.TextView;
        var textLeft = view.TranslatePoint(default, _editor.TextArea)?.X ?? 0;
        var numbers = _editor.TextArea.LeftMargins.OfType<AvaloniaEdit.Editing.LineNumberMargin>().FirstOrDefault(m => m.IsVisible);
        var numbersLeft = numbers?.TranslatePoint(default, _editor.TextArea)?.X ?? 0;
        foreach (var row in _lines.Children.OfType<Border>())
        {
            if (row.Child is Grid { Children: [TextBlock number, Border { Child: TextBlock text } code] })
            {
                number.IsVisible = numbers is not null;
                number.Margin = new Thickness(numbersLeft, 0, 0, 0);
                number.Width = numbers?.Bounds.Width ?? 0;
                code.Margin = new Thickness(textLeft, 0, 0, 0);
                text.Margin = new Thickness(-view.HorizontalOffset, 0, 0, 0);
            }
        }
    }

    /// <summary>The runs of a pinned line (one-based <paramref name="number"/>), colored as the editor colors it.</summary>
    private StickyLine Line(int number)
    {
        var document = _editor.Document;
        var line = document.GetLineByNumber(number);
        var text = document.GetText(line).TrimEnd();
        var runs = new List<StickyRun>();
        var foreground = _editor.Foreground;
        var visual = _editor.TextArea.TextView.GetOrConstructVisualLine(line);
        var start = line.Offset - visual.FirstDocumentLine.Offset;
        var position = 0;
        foreach (var element in visual.Elements)
        {
            var from = Math.Max(position, element.RelativeTextOffset - start);
            var to = Math.Min(text.Length, element.RelativeTextOffset - start + element.DocumentLength);
            if (to <= from)
            {
                continue;
            }

            if (from > position)
            {
                runs.Add(new StickyRun(text[position..from], foreground, FontWeight.Normal, FontStyle.Normal));
            }

            var properties = element.TextRunProperties;
            runs.Add(new StickyRun(text[from..to], properties.ForegroundBrush ?? foreground, properties.Typeface.Weight, properties.Typeface.Style));
            position = to;
        }

        if (position < text.Length)
        {
            runs.Add(new StickyRun(text[position..], foreground, FontWeight.Normal, FontStyle.Normal));
        }

        return new StickyLine(number, [.. runs]);
    }

    private Border Row(StickyLine line)
    {
        var view = _editor.TextArea.TextView;
        var tabSize = Math.Max(1, _editor.Options.IndentationSize);
        // A line wider than the text column (a narrow docked editor) ends in an ellipsis at the column's edge, and its
        // tooltip shows it whole (M34g): the text view scrolls sideways, a pinned header doesn't.
        var code = new TextBlock
        {
            FontFamily = _editor.FontFamily,
            FontSize = _editor.FontSize,
            Height = view.DefaultLineHeight,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
        };
        var column = 0;
        foreach (var run in line.Runs)
        {
            // Tabs to the next tab stop, as the text view lays them out in its fixed-width font.
            var expanded = new System.Text.StringBuilder();
            foreach (var c in run.Text)
            {
                if (c == '\t')
                {
                    var width = tabSize - (column % tabSize);
                    expanded.Append(' ', width);
                    column += width;
                }
                else
                {
                    expanded.Append(c);
                    column++;
                }
            }

            code.Inlines!.Add(new Avalonia.Controls.Documents.Run(expanded.ToString()) { Foreground = run.Foreground, FontWeight = run.Weight, FontStyle = run.Style });
        }

        var number = new TextBlock
        {
            Text = line.Number.ToString(System.Globalization.CultureInfo.InvariantCulture),
            FontFamily = _editor.FontFamily,
            FontSize = _editor.FontSize,
            Height = view.DefaultLineHeight,
            TextAlignment = TextAlignment.Right,
            HorizontalAlignment = HorizontalAlignment.Left,
            Foreground = _editor.LineNumbersForeground,
        };
        var row = new Border
        {
            Child = new Grid { Children = { number, new Border { Child = code, ClipToBounds = true } } },
            Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        row.PointerEntered += (_, _) => row.Background = Resource("Shell.Hover");
        row.PointerExited += (_, _) => row.Background = Brushes.Transparent;
        var target = line.Number;
        row.PointerPressed += (_, e) =>
        {
            _editor.TextArea.Caret.Line = target;
            _editor.TextArea.Caret.Column = 1;
            _editor.ScrollTo(target, 1);
            _editor.TextArea.Focus();
            e.Handled = true;
        };
        var whole = string.Concat(line.Runs.Select(r => r.Text)).Trim();
        AutomationProperties.SetName(row, $"Line {line.Number}: {whole}");
        ToolTip.SetTip(code, whole);
        return row;
    }

    /// <summary>A pinned line: its number and its runs.</summary>
    internal sealed record StickyLine(int Number, StickyRun[] Runs)
    {
        public bool Equals(StickyLine? other) => other is not null && Number == other.Number && Runs.AsSpan().SequenceEqual(other.Runs);

        public override int GetHashCode() => Number;
    }

    /// <summary>A run of a pinned line, in the editor's color and font style for it.</summary>
    internal sealed record StickyRun(string Text, IBrush? Foreground, FontWeight Weight, FontStyle Style);
}

/// <summary>
/// The overview ruler beside the scroll bar (Visual Studio's map mode, M12a): errors, warnings, Git changes,
/// breakpoints, find results and the caret, positioned by line over the whole document, with the document's health at
/// the top (click: next problem). Clicking elsewhere scrolls there.
/// </summary>
public sealed class OverviewRuler : Control
{
    private const double RulerWidth = 12;
    private const double HealthHeight = 14;
    private readonly TextEditor _editor;
    private readonly IEditorServices _services;
    private readonly Func<string> _path;
    private readonly Func<IReadOnlyList<Workbench.Git.LineChange>> _gitChanges;
    private readonly Func<AvaloniaEdit.Search.SearchPanel?> _search;
    private (string Pattern, bool Case, bool Words, bool Regex, int Version) _searchKey;
    private IReadOnlyList<int> _searchLines = [];
    private bool _queued;

    public OverviewRuler(TextEditor editor, IEditorServices services, Func<string> path, Func<IReadOnlyList<Workbench.Git.LineChange>> gitChanges, Func<AvaloniaEdit.Search.SearchPanel?> search)
    {
        _editor = editor;
        _services = services;
        _path = path;
        _gitChanges = gitChanges;
        _search = search;
        Width = RulerWidth;
        Cursor = new Cursor(StandardCursorType.Arrow);
        AutomationProperties.SetName(this, "Overview ruler");
        editor.TextArea.TextView.VisualLinesChanged += (_, _) => Invalidate();
        editor.TextArea.Caret.PositionChanged += (_, _) => Invalidate();
    }

    /// <summary>Errors and warnings in the document (the health indicator; tests).</summary>
    internal (int Errors, int Warnings) Health
    {
        get
        {
            var diagnostics = _services.Diagnostics.For(_path());
            return (diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error), diagnostics.Count(d => d.Severity == DiagnosticSeverity.Warning));
        }
    }

    /// <summary>Redraws once per frame at most.</summary>
    public void Invalidate()
    {
        if (_queued || !IsVisible)
        {
            return;
        }

        _queued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _queued = false;
            InvalidateVisual();
        }, DispatcherPriority.Render);
    }

    private IBrush Brush(string key) =>
        this.TryFindResource(key, ActualThemeVariant, out var resource) && resource is IBrush brush ? brush : Brushes.Gray;

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        context.FillRectangle(Brush("Shell.Editor.Background"), bounds);
        context.FillRectangle(Brush("Shell.Border"), new Rect(0, 0, 1, bounds.Height));
        var document = _editor.Document;
        var lines = Math.Max(1, document.LineCount);
        var top = HealthHeight + 2;
        var height = Math.Max(1, bounds.Height - top - 2);
        double Y(int line) => top + ((line - 1) * height / lines);

        // Health: a red, yellow or green square.
        var (errors, warnings) = Health;
        var health = errors > 0 ? Brush("Shell.Error") : warnings > 0 ? Brush("Shell.Warning") : Brush("Shell.Diff.Added");
        context.FillRectangle(health, new Rect(3, 3, RulerWidth - 5, HealthHeight - 5), 2);
        ToolTip.SetTip(this, errors + warnings == 0 ? "No problems" : $"{errors} errors, {warnings} warnings (F8: next problem)");

        // The visible area.
        var view = _editor.TextArea.TextView;
        if (view.VisualLinesValid && view.VisualLines.Count > 0)
        {
            var first = view.VisualLines[0].FirstDocumentLine.LineNumber;
            var last = view.VisualLines[^1].LastDocumentLine.LineNumber;
            context.FillRectangle(new SolidColorBrush(Colors.Gray, 0.15), new Rect(1, Y(first), RulerWidth - 1, Math.Max(2, Y(last + 1) - Y(first))));
        }

        // Git changes on the left third.
        foreach (var change in _gitChanges())
        {
            var brush = change.Kind switch
            {
                Workbench.Git.LineChangeKind.Added => Brush("Shell.Diff.Added"),
                Workbench.Git.LineChangeKind.Deleted => Brush("Shell.Diff.Removed"),
                _ => Brush("Shell.Modified"),
            };
            context.FillRectangle(brush, new Rect(1, Y(change.Line), 3, Math.Max(2, change.Count * height / lines)));
        }

        // Find results in the middle.
        foreach (var line in SearchLines())
        {
            context.FillRectangle(Brush("Shell.Match"), new Rect(4, Y(line), 4, 2));
        }

        // Breakpoints.
        foreach (var breakpoint in _services.Debugger.Breakpoints.For(_path()))
        {
            context.DrawEllipse(Brush("Shell.Error"), null, new Point(6, Y(breakpoint.Line) + 1), 2, 2);
        }

        // Diagnostics on the right, errors over warnings.
        foreach (var diagnostic in _services.Diagnostics.For(_path()).Where(d => d.Severity <= DiagnosticSeverity.Warning).OrderByDescending(d => d.Severity))
        {
            var brush = diagnostic.Severity == DiagnosticSeverity.Error ? Brush("Shell.Error") : Brush("Shell.Warning");
            context.FillRectangle(brush, new Rect(7, Y(diagnostic.Range.Start.Line + 1), RulerWidth - 7, 3));
        }

        // The caret.
        context.FillRectangle(Brush("Shell.Foreground"), new Rect(1, Y(_editor.TextArea.Caret.Line), RulerWidth - 1, 1.5));
    }

    private IReadOnlyList<int> SearchLines()
    {
        if (_search() is not { IsClosed: false } panel || string.IsNullOrEmpty(panel.SearchPattern) || _editor.Document.TextLength > 4_000_000)
        {
            return [];
        }

        var key = (panel.SearchPattern, panel.MatchCase, panel.WholeWords, panel.UseRegex, _editor.Document.Version?.GetHashCode() ?? 0);
        if (key == _searchKey)
        {
            return _searchLines;
        }

        _searchKey = key;
        try
        {
            var pattern = panel.UseRegex ? panel.SearchPattern : Regex.Escape(panel.SearchPattern);
            if (panel.WholeWords)
            {
                pattern = @"\b" + pattern + @"\b";
            }

            var regex = new Regex(pattern, (panel.MatchCase ? RegexOptions.None : RegexOptions.IgnoreCase) | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
            var document = _editor.Document;
            _searchLines = [.. regex.Matches(document.Text).Take(5000).Select(m => document.GetLineByOffset(m.Index).LineNumber).Distinct()];
        }
        catch (Exception ex) when (ex is ArgumentException or RegexMatchTimeoutException)
        {
            _searchLines = [];
        }

        return _searchLines;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetPosition(this);
        if (point.Y < HealthHeight + 2)
        {
            _services.RunCommand("editor.action.marker.next");
            e.Handled = true;
            return;
        }

        var lines = Math.Max(1, _editor.Document.LineCount);
        var height = Math.Max(1, Bounds.Height - HealthHeight - 4);
        var line = Math.Clamp((int)((point.Y - HealthHeight - 2) * lines / height) + 1, 1, lines);
        _editor.ScrollTo(line, 1);
        _editor.TextArea.Caret.Line = line;
        e.Handled = true;
    }
}
