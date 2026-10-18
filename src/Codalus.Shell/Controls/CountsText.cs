using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace Codalus.Shell.Controls;

/// <summary>
/// A status line whose numbers stand out (M34h, Find Results' summary: "Matching lines: <b>13</b>  Matching files: <b>4</b>"):
/// the numbers semibold in the text's color, the words around them muted (<c>Shell.MutedForeground</c>).
/// </summary>
public sealed class CountsText : TextBlock
{
    public static readonly StyledProperty<string?> SourceProperty = AvaloniaProperty.Register<CountsText, string?>(nameof(Source));

    protected override Type StyleKeyOverride => typeof(TextBlock);

    public string? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SourceProperty)
        {
            Rebuild();
        }
    }

    private void Rebuild()
    {
        var text = Source ?? string.Empty;
        var inlines = new InlineCollection();
        var index = 0;
        while (index < text.Length)
        {
            var number = IsNumber(text, index);
            var end = index + 1;
            while (end < text.Length && IsNumber(text, end) == number)
            {
                end++;
            }

            var run = new Run(text[index..end]);
            if (number)
            {
                run.FontWeight = FontWeight.SemiBold;
            }
            else
            {
                run.Bind(TextElement.ForegroundProperty, this.GetResourceObservable("Shell.MutedForeground"));
            }

            inlines.Add(run);
            index = end;
        }

        Inlines = inlines;
    }

    /// <summary>A digit, or the + after a capped count (10000+).</summary>
    private static bool IsNumber(string text, int index) =>
        char.IsAsciiDigit(text[index]) || (text[index] == '+' && index > 0 && char.IsAsciiDigit(text[index - 1]));
}
