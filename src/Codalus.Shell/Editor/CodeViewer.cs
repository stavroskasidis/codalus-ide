using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using Codalus.LanguageServices.Protocol;
using Location = Codalus.LanguageServices.Protocol.Location;
using Codalus.Shell.Controls;
using Codalus.Shell.Infrastructure;

namespace Codalus.Shell.Editor;

/// <summary>
/// A read-only code view with TextMate colors and a highlighted range: Peek Definition and the Code Definition window
/// (M12a). It has no language service of its own; C# is colored with its grammar.
/// </summary>
public sealed class CodeViewer : UserControl
{
    private readonly IEditorServices _services;
    private readonly RangeHighlight _highlight = new();
    private readonly LongLineCutter _longLines;
    private AvaloniaEdit.TextMate.TextMate.Installation? _textMate;
    private GrammarColorizer? _grammarColors;
    private string? _scope;

    public CodeViewer(IEditorServices services, EditorFont font)
    {
        _services = services;
        Editor = new TextEditor
        {
            IsReadOnly = true,
            ShowLineNumbers = true,
            Padding = new Thickness(4, 2, 0, 0),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = font.Family,
            FontSize = font.Size,
        };
        Editor.Bind(TextEditor.BackgroundProperty, this.GetResourceObservable("Shell.Editor.Background"));
        Editor.Bind(TextEditor.ForegroundProperty, this.GetResourceObservable("Shell.Editor.Foreground"));
        Editor.Bind(TextEditor.LineNumbersForegroundProperty, this.GetResourceObservable("Shell.Editor.LineNumbers"));
        Editor.TextArea.TextView.BackgroundRenderers.Add(_highlight);

        // Long lines are cut as in the editor (M56): Peek Definition into a minified file drew its whole line at each redraw.
        _longLines = new LongLineCutter(Editor.TextArea, () => this.TryFindResource("Shell.Editor.InlayHint.Foreground", ActualThemeVariant, out var value) && value is IBrush brush ? brush : Brushes.Gray);
        Editor.TextArea.TextView.LineTransformers.Add(_longLines);
        AutomationProperties.SetName(Editor, "Definition (read-only)");
        Content = Editor;
        services.ThemeChanged += OnThemeChanged;
    }

    public TextEditor Editor { get; }

    /// <summary>
    /// Colors C# and Visual Basic in the editor theme's colors, the editor's, from their grammar's scopes
    /// (<see cref="GrammarColorizer"/>), instead of the TextMate theme of a view without a language service (M34h: Add
    /// New Item's preview). Other languages keep their TextMate colors, which are the editor's for them too.
    /// </summary>
    public bool EditorColors { get; init; }

    /// <summary>The colorizer of <see cref="EditorColors"/>, or null when the language is colored by TextMate (tests).</summary>
    public GrammarColorizer? GrammarColors => _grammarColors;

    /// <summary>
    /// As high as its text, without line numbers or a vertical scroll bar: a code block among other content that scrolls
    /// (AI Chat's answers, M34h). Long lines still scroll sideways.
    /// </summary>
    public bool FitsContent
    {
        get => !Editor.ShowLineNumbers && Editor.VerticalScrollBarVisibility == ScrollBarVisibility.Disabled;
        init
        {
            if (value)
            {
                Editor.ShowLineNumbers = false;
                Editor.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
                Editor.Options.AllowScrollBelowDocument = false;
                Editor.Padding = new Thickness(12, 8, 8, 8);
            }
        }
    }

    /// <summary>
    /// Shows <paramref name="text"/> without colors (code still being written, M34h: nothing is tokenized while it
    /// grows). <see cref="Show"/> colors it later.
    /// </summary>
    public void ShowPlain(string text)
    {
        _textMate?.Dispose();
        _textMate = null;
        _scope = null;
        RemoveGrammarColors();
        if (Editor.Document.Text != text)
        {
            Editor.Document = new TextDocument(text);
        }

        _highlight.Set(0, 0);
        Editor.TextArea.TextView.Redraw();
    }

    /// <summary>Adds <paramref name="text"/> at the end of what is shown: the lines laid out already stay as they are.</summary>
    public void Append(string text) => Editor.Document.Insert(Editor.Document.TextLength, text);

    /// <summary>Shows <paramref name="text"/> with <paramref name="highlight"/> marked and scrolled into view.</summary>
    public void Show(string text, string? languageId, LanguageServices.Protocol.Range? highlight)
    {
        if (Editor.Document.Text != text)
        {
            Editor.Document = new TextDocument(text);
            _textMate?.Dispose();
            _textMate = null;
            _scope = null;
        }

        var scope = _services.TextMate.GrammarScopeFor(languageId);
        if (EditorColors && languageId is "csharp" or "vb")
        {
            ShowWithEditorColors(languageId);
        }
        else if (scope != _scope || _grammarColors is not null)
        {
            RemoveGrammarColors();
            _scope = scope;
            _textMate?.Dispose();
            _textMate = null;
            if (scope is not null)
            {
                try
                {
                    _textMate = AvaloniaEdit.TextMate.TextMate.InstallTextMate(Editor, _services.TextMate, initCurrentDocument: true, ex => _services.Log("TextMate: " + ex.Message));
                    _textMate.SetTheme(_services.TextMate.ThemeFor(_services.Theme.Id));
                    _textMate.SetGrammar(scope);
                    KeepLongLinesLast();
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    _services.Log("TextMate: " + ex.Message);
                }
            }
        }

        _highlight.Brush = this.TryFindResource("Shell.Selection", ActualThemeVariant, out var resource) && resource is IBrush brush ? brush : Brushes.LightBlue;
        if (highlight is { } range && range.Start.Line < Editor.Document.LineCount)
        {
            var line = Editor.Document.GetLineByNumber(range.Start.Line + 1);
            var start = Math.Min(line.EndOffset, line.Offset + range.Start.Character);
            var endLine = Editor.Document.GetLineByNumber(Math.Min(Editor.Document.LineCount, range.End.Line + 1));
            var end = Math.Min(endLine.EndOffset, endLine.Offset + range.End.Character);
            _highlight.Set(start, Math.Max(start, end));
            Editor.TextArea.Caret.Offset = start;
            Editor.ScrollTo(line.LineNumber, 1);
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                // Center the definition: a few lines of context above it.
                var top = Math.Max(0, (line.LineNumber - 4) * Editor.TextArea.TextView.DefaultLineHeight);
                Editor.ScrollToVerticalOffset(top);
            }, Avalonia.Threading.DispatcherPriority.Background);
        }
        else
        {
            _highlight.Set(0, 0);
        }

        Editor.TextArea.TextView.Redraw();
    }

    private void ShowWithEditorColors(string languageId)
    {
        _textMate?.Dispose();
        _textMate = null;
        _scope = null;
        if (_grammarColors is null)
        {
            _grammarColors = new GrammarColorizer();
            Editor.TextArea.TextView.LineTransformers.Add(_grammarColors);
            KeepLongLinesLast();
        }

        _grammarColors.Theme = _services.Theme;
        if (_services.TextMate.GrammarFor(languageId) is { } grammar)
        {
            _grammarColors.Tokenize(Editor.Document, grammar);
        }
        else
        {
            _grammarColors.Clear();
        }
    }

    /// <summary>The cut of long lines after the colorizers (a grammar's installs itself last): nothing draws past it.</summary>
    private void KeepLongLinesLast()
    {
        var transformers = Editor.TextArea.TextView.LineTransformers;
        if (transformers[^1] != _longLines)
        {
            transformers.Remove(_longLines);
            transformers.Add(_longLines);
        }
    }

    private void RemoveGrammarColors()
    {
        if (_grammarColors is not null)
        {
            Editor.TextArea.TextView.LineTransformers.Remove(_grammarColors);
            _grammarColors = null;
        }
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        _textMate?.SetTheme(_services.TextMate.ThemeFor(_services.Theme.Id));
        if (_grammarColors is not null)
        {
            _grammarColors.Theme = _services.Theme;
            Editor.TextArea.TextView.Redraw();
        }
    }

    /// <summary>Stops following the theme and releases the grammar (the view closed).</summary>
    public void Release()
    {
        _services.ThemeChanged -= OnThemeChanged;
        _textMate?.Dispose();
        _textMate = null;
        _longLines.Detach();
    }

    private sealed class RangeHighlight : IBackgroundRenderer
    {
        private (int Start, int End) _range;

        public IBrush Brush { get; set; } = Brushes.LightBlue;

        public KnownLayer Layer => KnownLayer.Background;

        public void Set(int start, int end) => _range = (start, end);

        public void Draw(TextView textView, DrawingContext drawingContext)
        {
            if (_range.End <= _range.Start || !textView.VisualLinesValid)
            {
                return;
            }

            var builder = new BackgroundGeometryBuilder { CornerRadius = 2 };
            builder.AddSegment(textView, new TextSegment { StartOffset = _range.Start, EndOffset = Math.Min(_range.End, textView.Document.TextLength) });
            if (builder.CreateGeometry() is { } geometry)
            {
                drawingContext.DrawGeometry(Brush, null, geometry);
            }
        }
    }
}

/// <summary>
/// Peek Definition (Alt+F12, M12a): the definition in a popup under the caret's line, without leaving the file.
/// Escape closes it; Enter, double-click or "Open" opens the definition as a document.
/// </summary>
public sealed class PeekPresenter
{
    private readonly Popup _popup;
    private readonly CodeViewer _viewer;
    private readonly TextBlock _title = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0), TextTrimming = TextTrimming.CharacterEllipsis };
    private Location? _location;

    public PeekPresenter(Control owner, IEditorServices services, EditorFont font, Func<Location, Task> open)
    {
        _viewer = new CodeViewer(services, font) { Height = 260 };
        var openButton = new Button { Content = "Open", Classes = { "toolbar" }, Margin = new Thickness(4, 2) };
        openButton.Click += (_, _) => OpenAndClose(open);
        var closeButton = new Button { Classes = { "toolbar" }, Content = new Icon { Kind = "close", Width = 12, Height = 12 }, Margin = new Thickness(0, 2, 4, 2) };
        closeButton.Click += (_, _) => Close();
        AutomationProperties.SetName(closeButton, "Close peek");
        var header = new DockPanel { Children = { closeButton, openButton, _title } };
        DockPanel.SetDock(closeButton, Avalonia.Controls.Dock.Right);
        DockPanel.SetDock(openButton, Avalonia.Controls.Dock.Right);
        var headerBorder = new Border { Child = header, BorderThickness = new Thickness(0, 0, 0, 1) };
        headerBorder.Bind(Border.BackgroundProperty, owner.GetResourceObservable("Shell.Panel"));
        headerBorder.Bind(Border.BorderBrushProperty, owner.GetResourceObservable("Shell.Border"));
        var body = new DockPanel { Children = { headerBorder, _viewer } };
        DockPanel.SetDock(headerBorder, Avalonia.Controls.Dock.Top);
        var frame = new Border { Child = body, BorderThickness = new Thickness(1, 2, 1, 2) };
        frame.Bind(Border.BorderBrushProperty, owner.GetResourceObservable("Shell.Accent"));
        _popup = new Popup
        {
            Child = frame,
            IsLightDismissEnabled = true,
            PlacementTarget = owner,
            Placement = PlacementMode.AnchorAndGravity,
            PlacementAnchor = Avalonia.Controls.Primitives.PopupPositioning.PopupAnchor.TopLeft,
            PlacementGravity = Avalonia.Controls.Primitives.PopupPositioning.PopupGravity.BottomRight,
        };
        _popup.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close();
                e.Handled = true;
            }
            else if (e.Key == Key.Enter)
            {
                OpenAndClose(open);
                e.Handled = true;
            }
        };
        _viewer.Editor.TextArea.DoubleTapped += (_, _) => OpenAndClose(open);
        AutomationProperties.SetName(frame, "Peek Definition");
    }

    public bool IsOpen => _popup.IsOpen;

    /// <summary>The popup; the editor view adds it to its tree.</summary>
    public Popup Popup => _popup;

    /// <summary>The viewer (tests).</summary>
    public CodeViewer Viewer => _viewer;

    /// <summary>Opens the peek at <paramref name="anchor"/> (the caret line's bottom-left, in the owner's coordinates).</summary>
    public void Show(Rect anchor, double width, Location location, string title, string text, string? languageId)
    {
        _location = location;
        _title.Text = title;
        _viewer.Width = Math.Max(300, width);
        _popup.PlacementRect = anchor;
        _popup.IsOpen = true;
        _viewer.Show(text, languageId, location.Range);
        _viewer.Editor.TextArea.Focus();
    }

    public void Close()
    {
        _popup.IsOpen = false;
    }

    private void OpenAndClose(Func<Location, Task> open)
    {
        Close();
        if (_location is { } location)
        {
            _ = open(location);
        }
    }

    /// <summary>Closes the popup and releases the viewer (the editor closed).</summary>
    public void Release()
    {
        _popup.IsOpen = false;
        _viewer.Release();
    }
}
