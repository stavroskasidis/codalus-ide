using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Codalus.Shell.Editor;

namespace Codalus.Shell.Controls;

/// <summary>
/// One line of code in the editor's syntax colors with its matches highlighted (M34h, Find Results): the line's
/// language comes from <see cref="Path"/>, its colors from the editor theme of the theme it is shown in
/// (<see cref="CodeColors"/>), and the characters at <see cref="Highlights"/> are bold on <c>Shell.Match.Background</c>.
/// <see cref="IsPlain"/> (a selected row, from ShellStyles) draws the line in its foreground with the matches underlined.
/// </summary>
public sealed class CodeLine : TextBlock
{
    public static readonly StyledProperty<string?> SourceProperty = AvaloniaProperty.Register<CodeLine, string?>(nameof(Source));

    public static readonly StyledProperty<IReadOnlyList<int>?> HighlightsProperty =
        AvaloniaProperty.Register<CodeLine, IReadOnlyList<int>?>(nameof(Highlights));

    /// <summary>The file the line is from: its language colors it.</summary>
    public static readonly StyledProperty<string?> PathProperty = AvaloniaProperty.Register<CodeLine, string?>(nameof(Path));

    /// <summary>No syntax colors and no match background: the row is selected (its text takes the selection's color).</summary>
    public static readonly StyledProperty<bool> IsPlainProperty = AvaloniaProperty.Register<CodeLine, bool>(nameof(IsPlain));

    private bool _attached;

    public CodeLine()
    {
        Classes.Add("codeLine");
        ActualThemeVariantChanged += (_, _) => Rebuild();
    }

    protected override Type StyleKeyOverride => typeof(TextBlock);

    public string? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public IReadOnlyList<int>? Highlights
    {
        get => GetValue(HighlightsProperty);
        set => SetValue(HighlightsProperty, value);
    }

    public string? Path
    {
        get => GetValue(PathProperty);
        set => SetValue(PathProperty, value);
    }

    public bool IsPlain
    {
        get => GetValue(IsPlainProperty);
        set => SetValue(IsPlainProperty, value);
    }

    /// <summary>The token type of the character at <paramref name="index"/> as colored, or -1 (tests).</summary>
    public int TokenAt(int index)
    {
        foreach (var (start, length, type) in CodeColors.Tokens(CodeColors.LanguageOf(Path), Source ?? string.Empty))
        {
            if (index >= start && index < start + length)
            {
                return type;
            }
        }

        return -1;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (!_attached)
        {
            _attached = true;
            CodeColors.Changed += OnColorsChanged;
        }

        Rebuild();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_attached)
        {
            _attached = false;
            CodeColors.Changed -= OnColorsChanged;
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SourceProperty || change.Property == HighlightsProperty || change.Property == PathProperty
            || change.Property == IsPlainProperty)
        {
            Rebuild();
        }
    }

    private void OnColorsChanged(object? sender, EventArgs e) => Rebuild();

    private void Rebuild()
    {
        var text = Source ?? string.Empty;
        var plain = IsPlain;
        var marks = Highlights is { Count: > 0 } h ? new HashSet<int>(h) : null;
        var colors = new IBrush?[text.Length];
        if (!plain && text.Length > 0)
        {
            var theme = CodeColors.ThemeFor(ActualThemeVariant);
            foreach (var (start, length, type) in CodeColors.Tokens(CodeColors.LanguageOf(Path), text))
            {
                if (theme.Token(type) is { } brush)
                {
                    for (var i = start; i < start + length && i < colors.Length; i++)
                    {
                        colors[i] = brush;
                    }
                }
            }
        }

        if (marks is null && Array.TrueForAll(colors, c => c is null))
        {
            Inlines = null;
            Text = text;
            return;
        }

        var matchBackground = plain ? null : Resource("Shell.Match.Background");
        var matchForeground = plain ? null : Resource("Shell.Match.Background.Foreground");
        var inlines = new InlineCollection();
        var index = 0;
        while (index < text.Length)
        {
            var highlighted = marks?.Contains(index) == true;
            var color = colors[index];
            var end = index + 1;
            while (end < text.Length && (marks?.Contains(end) == true) == highlighted && ReferenceEquals(colors[end], color))
            {
                end++;
            }

            var run = new Run(text[index..end]);
            if (highlighted)
            {
                run.FontWeight = FontWeight.Bold;
                if (plain)
                {
                    run.TextDecorations = Avalonia.Media.TextDecorations.Underline;
                }
                else
                {
                    run.Background = matchBackground;
                    run.Foreground = matchForeground;
                }
            }
            else if (color is not null)
            {
                run.Foreground = color;
            }

            inlines.Add(run);
            index = end;
        }

        Text = null;
        Inlines = inlines;
    }

    private IBrush? Resource(string key) => this.TryFindResource(key, ActualThemeVariant, out var value) && value is IBrush brush ? brush : null;
}
