using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace Codalus.Shell.Controls;

/// <summary>A text block that emphasizes the characters at <see cref="Highlights"/> (fuzzy-match positions).</summary>
public sealed class HighlightedText : TextBlock
{
    public static readonly StyledProperty<string?> SourceProperty = AvaloniaProperty.Register<HighlightedText, string?>(nameof(Source));

    public static readonly StyledProperty<IReadOnlyList<int>?> HighlightsProperty =
        AvaloniaProperty.Register<HighlightedText, IReadOnlyList<int>?>(nameof(Highlights));

    /// <summary>
    /// The matched characters' color: <c>Shell.Match</c>, and the selection's text on a selected row (ShellStyles'
    /// <c>TextBlock.highlighted</c>, M34g: in Shell.Match on Code Search's selected result they were 1.00:1 in Daylight
    /// High contrast). They stay bold either way.
    /// </summary>
    public static readonly StyledProperty<IBrush?> MatchForegroundProperty = AvaloniaProperty.Register<HighlightedText, IBrush?>(nameof(MatchForeground));

    public HighlightedText()
    {
        Classes.Add("highlighted");
    }

    protected override Type StyleKeyOverride => typeof(TextBlock);

    public IBrush? MatchForeground
    {
        get => GetValue(MatchForegroundProperty);
        set => SetValue(MatchForegroundProperty, value);
    }

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

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SourceProperty || change.Property == HighlightsProperty)
        {
            Rebuild();
        }
    }

    private void Rebuild()
    {
        var text = Source ?? string.Empty;
        var inlines = new InlineCollection();
        var marks = Highlights is { Count: > 0 } h ? new HashSet<int>(h) : null;
        if (marks is null)
        {
            inlines.Add(new Run(text));
        }
        else
        {
            var start = 0;
            while (start < text.Length)
            {
                var highlighted = marks.Contains(start);
                var end = start + 1;
                while (end < text.Length && marks.Contains(end) == highlighted)
                {
                    end++;
                }

                var run = new Run(text[start..end]);
                if (highlighted)
                {
                    run.FontWeight = FontWeight.Bold;
                    run.Bind(TextElement.ForegroundProperty, this.GetObservable(MatchForegroundProperty));
                }

                inlines.Add(run);
                start = end;
            }
        }

        Inlines = inlines;
    }
}
