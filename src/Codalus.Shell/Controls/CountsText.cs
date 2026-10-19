using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace Codalus.Shell.Controls;

/// <summary>
/// A status line whose numbers stand out (M34h, Find Results' summary: "Matching lines: <b>13</b>  Matching files: <b>4</b>"):
/// the numbers semibold in the text's color, the words around them muted (<c>Shell.MutedForeground</c>). Screen
/// readers get the plain text as its name, and a live region hears each change (the runs change, not <c>Text</c>).
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
            ControlAutomationPeer.FromElement(this)?.RaisePropertyChangedEvent(AutomationElementIdentifiers.NameProperty, change.OldValue, change.NewValue);
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

    protected override AutomationPeer OnCreateAutomationPeer() => new CountsTextAutomationPeer(this);

    /// <summary>A digit, or the + after a capped count (10000+).</summary>
    private static bool IsNumber(string text, int index) =>
        char.IsAsciiDigit(text[index]) || (text[index] == '+' && index > 0 && char.IsAsciiDigit(text[index - 1]));
}

/// <summary>A <see cref="CountsText"/> for screen readers: text whose name is its plain text.</summary>
public sealed class CountsTextAutomationPeer(CountsText owner) : ControlAutomationPeer(owner)
{
    protected override string? GetNameCore() => AutomationProperties.GetName(Owner) is { Length: > 0 } name ? name : ((CountsText)Owner).Source;

    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Text;

    protected override bool IsContentElementCore() => true;

    protected override bool IsControlElementCore() => true;
}
