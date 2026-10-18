using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Codalus.LanguageServices.Protocol;
using Codalus.Shell.Editor;

namespace Codalus.Shell.Controls;

/// <summary>
/// A node of an XML tree as code (M34h, the WCF Test Client's response): the element's name in the editor's type color,
/// then <c> = </c> and its value in the string color, from the editor theme of the theme it is shown in. On a selected
/// row (<see cref="IsPlain"/>, from ShellStyles) it takes the row's text color.
/// </summary>
public sealed class XmlNodeText : TextBlock
{
    public static readonly StyledProperty<string?> NodeNameProperty = AvaloniaProperty.Register<XmlNodeText, string?>(nameof(NodeName));

    public static readonly StyledProperty<string?> NodeValueProperty = AvaloniaProperty.Register<XmlNodeText, string?>(nameof(NodeValue));

    public static readonly StyledProperty<bool> IsPlainProperty = AvaloniaProperty.Register<XmlNodeText, bool>(nameof(IsPlain));

    private static readonly int s_type = TokenTypes.IndexOf("class");
    private static readonly int s_string = TokenTypes.IndexOf("string");

    private bool _attached;

    public XmlNodeText()
    {
        Classes.Add("xmlNode"); // styled as a TextBlock: ShellStyles and the views select it by this class
        ActualThemeVariantChanged += (_, _) => Rebuild();
    }

    protected override Type StyleKeyOverride => typeof(TextBlock);

    public string? NodeName
    {
        get => GetValue(NodeNameProperty);
        set => SetValue(NodeNameProperty, value);
    }

    public string? NodeValue
    {
        get => GetValue(NodeValueProperty);
        set => SetValue(NodeValueProperty, value);
    }

    public bool IsPlain
    {
        get => GetValue(IsPlainProperty);
        set => SetValue(IsPlainProperty, value);
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
        if (change.Property == NodeNameProperty || change.Property == NodeValueProperty || change.Property == IsPlainProperty)
        {
            Rebuild();
        }
    }

    private void OnColorsChanged(object? sender, EventArgs e) => Rebuild();

    private void Rebuild()
    {
        var theme = IsPlain ? null : CodeColors.ThemeFor(ActualThemeVariant);
        var inlines = new InlineCollection { new Run(NodeName ?? string.Empty) { Foreground = theme?.Token(s_type) } };
        if (NodeValue is { } value)
        {
            var equals = new Run(" = ");
            if (!IsPlain)
            {
                equals.Bind(TextElement.ForegroundProperty, this.GetResourceObservable("Shell.MutedForeground"));
            }

            inlines.Add(equals);
            inlines.Add(new Run(value) { Foreground = theme?.Token(s_string) });
        }

        Inlines = inlines;
    }
}
