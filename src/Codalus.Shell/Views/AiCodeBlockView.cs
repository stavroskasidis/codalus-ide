using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Codalus.Shell.Controls;
using Codalus.Shell.Editor;
using Codalus.Shell.Infrastructure;
using Codalus.Workbench.Localization;

namespace Codalus.Shell.Views;

/// <summary>
/// A code block of an answer in AI Chat (M34h; redesigned for ADR 0092): the code on the editor's background, in its
/// colors, with no header bar and no actions (the agent changes files itself, through the preview). Its language, and the
/// file it names, sit small in its top-right corner, beside a Wrap toggle that shows on hover and focus. Lines wrap to the
/// pane unless the toggle turns it off; a block of more than
/// <see cref="FoldedLines"/> lines shows that many, with "Show all N lines". The text stays selectable.
/// </summary>
internal sealed class AiCodeBlockView : Border
{
    /// <summary>How many lines a long block shows until "Show all" opens it.</summary>
    public const int FoldedLines = 20;

    private const double CornerRow = 32;
    private readonly CodeViewer _viewer;
    private readonly TextBlock _language = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 220 };
    private readonly ToggleButton _wrap = new() { IsChecked = true };
    private readonly Button _fold = new() { HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(16, 0, 0, 8) };
    private string? _text;
    private (string Code, string? Language)? _colored;
    private int _version;
    private int _lines;
    private bool _expanded;
    private bool _released;

    public AiCodeBlockView(IEditorServices services, EditorFont font)
    {
        Classes.Add("codeBlock");
        _language.Classes.Add("muted");
        _language.Classes.Add("small");
        _viewer = new CodeViewer(services, font) { EditorColors = true, FitsContent = true };
        _viewer.Editor.FontSize = Math.Max(9, font.Size - 1);
        _viewer.Editor.Padding = new Thickness(4, CornerRow, 12, 8);

        // The block's own surface, not the editor's (a step off the panel in every theme), and a mark beside each
        // wrapped line's continuation, so a source line reads as one (the text engine has no hanging indent).
        _viewer.Editor.Bind(AvaloniaEdit.TextEditor.BackgroundProperty, _viewer.GetResourceObservable("Shell.CodeBlock.Background"));
        _viewer.Editor.TextArea.LeftMargins.Insert(0, new WrapMarks(_viewer));
        AutomationProperties.SetName(_viewer.Editor, Localizer.Get("msg.ai.codeReadOnly", "Code (read-only)"));
        // TODO(issue): a hanging indent for wrapped lines; Avalonia's text layout ignores AvaloniaEdit's WordWrapIndentation.
        Wrap(true);

        // Tab leaves the code for the next control, as in the rest of the chat (the editor keeps Tab for itself).
        _viewer.Editor.TextArea.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Tab && e.KeyModifiers is KeyModifiers.None or KeyModifiers.Shift && TopLevel.GetTopLevel(this)?.FocusManager is { } focus)
            {
                e.Handled = focus.TryMoveFocus(e.KeyModifiers == KeyModifiers.Shift ? NavigationDirection.Previous : NavigationDirection.Next);
            }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        _wrap.Classes.Add("toolbar");
        _wrap.Classes.Add("icon");
        _wrap.Classes.Add("wrapToggle");
        ButtonIcon.SetKind(_wrap, "word-wrap");
        var wrapName = Localizer.Get("msg.ai.wrapLines", "Wrap long lines");
        ToolTip.SetTip(_wrap, wrapName);
        AutomationProperties.SetName(_wrap, wrapName);
        _wrap.IsCheckedChanged += (_, _) => Wrap(_wrap.IsChecked == true);

        _fold.Classes.Add("link");
        _fold.Classes.Add("small");
        _fold.IsVisible = false;
        _fold.Click += (_, _) =>
        {
            _expanded = !_expanded;
            UpdateFold();
        };

        // The corner: the language (and file), then the Wrap toggle, over the code's top padding.
        var corner = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 5, 6, 0),
            Children = { _language, _wrap },
        };
        var code = new Panel { Children = { _viewer, corner } };
        DockPanel.SetDock(_fold, Avalonia.Controls.Dock.Bottom);
        Child = new DockPanel { Children = { _fold, code } };
    }

    /// <summary>Whether long lines wrap (the toggle; on by default).</summary>
    public bool WrapsLines => _viewer.Editor.WordWrap;

    /// <summary>Whether a long block shows only its first <see cref="FoldedLines"/> lines (tests).</summary>
    public bool IsFolded => _fold.IsVisible && !_expanded;

    /// <summary>The fold link's text, or null without one (tests).</summary>
    public string? FoldText => _fold.IsVisible ? _fold.Content as string : null;

    /// <summary>The Wrap toggle (tests).</summary>
    internal ToggleButton WrapToggle => _wrap;

    /// <summary>The fold link (tests).</summary>
    internal Button FoldButton => _fold;

    private void Wrap(bool wrap)
    {
        _viewer.Editor.WordWrap = wrap;
        _viewer.Editor.HorizontalScrollBarVisibility = wrap ? Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled : Avalonia.Controls.Primitives.ScrollBarVisibility.Auto;
        UpdateFold();
    }

    /// <summary>A long block shows its first lines (about <see cref="FoldedLines"/> of them, wrapped lines counting) until it's opened.</summary>
    private void UpdateFold()
    {
        var foldable = _lines > FoldedLines;
        _fold.IsVisible = foldable;
        _fold.Content = _expanded
            ? Localizer.Get("msg.ai.showFewerLines", "Show fewer lines")
            : Localizer.Format("msg.ai.showAllLines", "Show all {0} lines", _lines);
        AutomationProperties.SetName(_fold, (string)_fold.Content);
        var lineHeight = _viewer.Editor.TextArea.TextView.DefaultLineHeight is > 0 and var measured ? measured : _viewer.Editor.FontSize * 1.35;
        var folded = foldable && !_expanded;
        _viewer.MaxHeight = folded ? CornerRow + (lineHeight * FoldedLines) + 8 : double.PositiveInfinity;
        _viewer.ClipToBounds = true;

        // A folded block fades out over its last lines, so the cut reads as "more below", whatever the theme's colors.
        _viewer.OpacityMask = folded
            ? new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Colors.Black, 0), new GradientStop(Colors.Black, 0.88), new GradientStop(Colors.Transparent, 1) },
            }
            : null;
    }

    private static int LineCount(string code)
    {
        var lines = 1;
        foreach (var c in code.AsSpan().TrimEnd('\n'))
        {
            if (c == '\n')
            {
                lines++;
            }
        }

        return lines;
    }

    /// <summary>The viewer (tests).</summary>
    public CodeViewer Viewer => _viewer;

    /// <summary>Styled as a border (<c>Border.codeBlock</c>).</summary>
    protected override Type StyleKeyOverride => typeof(Border);

    /// <summary>The language as the header shows it (tests).</summary>
    public string? HeaderText => _language.Text;

    /// <summary>The code is in its language's colors (tests: none while it is still written).</summary>
    public bool IsColored => _colored is not null;

    /// <summary>
    /// Shows <paramref name="code"/> in <paramref name="language"/> (the fence's name: <c>csharp</c>, <c>cs</c>, <c>json</c>…)
    /// with the file it names, if any. While the block itself
    /// is still written (<paramref name="streaming"/> and not <paramref name="closed"/>), it is plain text that grows at
    /// its end; it is colored once it is complete, after the layout (tokenizing a long block on every render of a
    /// streaming answer would hold up the UI thread).
    /// </summary>
    public void Show(string language, string? path, string code, bool streaming, bool closed)
    {
        var id = LanguageId(language);
        var label = LanguageLabel(language);
        _language.Text = path is { Length: > 0 } ? $"{label} · {Path.GetFileName(path)}" : label;
        ToolTip.SetTip(_language, path is { Length: > 0 } ? $"{label} · {path}" : label);
        if (LineCount(code) is var lines && lines != _lines)
        {
            _lines = lines;
            UpdateFold();
        }

        var complete = !streaming || closed;
        if (complete && _colored == (code, id))
        {
            return;
        }

        if (_colored is not null && _colored != (code, id))
        {
            _colored = null;
            _viewer.ShowPlain(code);
            _text = code;
        }
        else if (_text is { } shown && code.Length > shown.Length && code.StartsWith(shown, StringComparison.Ordinal))
        {
            _viewer.Append(code[shown.Length..]);
            _text = code;
        }
        else if (_text != code)
        {
            _viewer.ShowPlain(code);
            _text = code;
        }

        var version = ++_version;
        if (complete)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (version == _version && !_released)
                {
                    _viewer.Show(code, id, null);
                    _colored = (code, id);
                }
            }, Avalonia.Threading.DispatcherPriority.Background);
        }
    }

    /// <summary>Stops following the theme (the turn left the screen or the chat was cleared).</summary>
    public void Release()
    {
        _released = true;
        _version++;
        _viewer.Release();
    }

    /// <summary>The editor's language for a fence's name, or null for plain text (names it doesn't know stay plain).</summary>
    public static string? LanguageId(string fence) => fence.ToLowerInvariant() switch
    {
        "csharp" or "cs" or "c#" => "csharp",
        "vb" or "vbnet" or "vb.net" or "visualbasic" => "vb",
        "fsharp" or "fs" or "f#" => "fsharp",
        "json" or "jsonc" => "json",
        "xml" or "xaml" or "axaml" or "csproj" or "props" or "targets" or "msbuild" or "resx" => "xml",
        "html" or "htm" => "html",
        "razor" or "cshtml" => "razor",
        "css" => "css",
        "scss" => "scss",
        "js" or "javascript" or "jsx" => "javascript",
        "ts" or "typescript" or "tsx" => "typescript",
        "sh" or "bash" or "shell" or "zsh" or "console" => "shellscript",
        "ps" or "ps1" or "powershell" or "pwsh" => "powershell",
        "yaml" or "yml" => "yaml",
        "sql" => "sql",
        "md" or "markdown" => "markdown",
        "py" or "python" => "python",
        "diff" or "patch" => "diff",
        "ini" or "editorconfig" => "ini",
        "dockerfile" or "docker" => "dockerfile",
        _ => null,
    };

    /// <summary>A fence's language as people name it (<c>C#</c>, <c>JSON</c>), or the fence's own name.</summary>
    public static string LanguageLabel(string fence) => fence.ToLowerInvariant() switch
    {
        "" => Localizer.Get("msg.ai.code", "Code"),
        "csharp" or "cs" or "c#" => "C#",
        "vb" or "vbnet" or "vb.net" or "visualbasic" => "Visual Basic",
        "fsharp" or "fs" or "f#" => "F#",
        "json" or "jsonc" => "JSON",
        "xml" or "xaml" or "axaml" or "csproj" or "props" or "targets" or "msbuild" or "resx" => fence.Length <= 5 ? fence.ToUpperInvariant() : "XML",
        "html" or "htm" => "HTML",
        "razor" or "cshtml" => "Razor",
        "css" or "scss" or "sql" or "yaml" or "ini" => fence.ToUpperInvariant(),
        "yml" => "YAML",
        "js" or "javascript" or "jsx" => "JavaScript",
        "ts" or "typescript" or "tsx" => "TypeScript",
        "sh" or "bash" or "shell" or "zsh" or "console" => "Shell",
        "ps" or "ps1" or "powershell" or "pwsh" => "PowerShell",
        "md" or "markdown" => "Markdown",
        "py" or "python" => "Python",
        "diff" or "patch" => "Diff",
        "dockerfile" or "docker" => "Dockerfile",
        _ => fence,
    };

    /// <summary>
    /// A thin mark in a narrow margin beside each wrapped line's continuation (ADR 0092): a source line's screen lines read
    /// as one, and the next source line as a new one, without a hanging indent (the text engine has none).
    /// </summary>
    private sealed class WrapMarks(CodeViewer viewer) : AvaloniaEdit.Editing.AbstractMargin
    {
        protected override void OnTextViewChanged(AvaloniaEdit.Rendering.TextView? oldTextView, AvaloniaEdit.Rendering.TextView? newTextView)
        {
            if (oldTextView is not null)
            {
                oldTextView.VisualLinesChanged -= OnVisualLinesChanged;
            }

            base.OnTextViewChanged(oldTextView, newTextView);
            if (newTextView is not null)
            {
                newTextView.VisualLinesChanged += OnVisualLinesChanged;
            }
        }

        private void OnVisualLinesChanged(object? sender, EventArgs e) => InvalidateVisual();

        protected override Size MeasureOverride(Size availableSize) => new(12, 0);

        public override void Render(DrawingContext context)
        {
            if (!viewer.Editor.WordWrap || TextView is not { VisualLinesValid: true } view)
            {
                return;
            }

            var brush = this.TryFindResource("Shell.Editor.LineNumbers", ActualThemeVariant, out var value) && value is IBrush found ? found : Brushes.Gray;
            foreach (var line in view.VisualLines)
            {
                if (line.TextLines.Count < 2)
                {
                    continue;
                }

                // Only within the margin: a folded block's hidden lines draw nothing below its fold.
                var first = line.TextLines[0].Height;
                var top = line.VisualTop - view.VerticalOffset + first + 2;
                var bottom = Math.Min(line.VisualTop - view.VerticalOffset + line.Height - 2, Bounds.Height);
                if (bottom > top)
                {
                    context.FillRectangle(brush, new Rect(4, top, 2, bottom - top), 1);
                }
            }
        }
    }
}
