using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using Codalus.LanguageServices.Protocol;
using Codalus.Shell.Controls;
using Codalus.Workbench.Localization;

namespace Codalus.Shell.Editor.IntelliSense;

/// <summary>
/// A symbol's description as Quick Info and the completion list's tooltip show it: the kind's icon and the colorized
/// signature (wrapping, never cut off) with its overload count, then the summary, then each other section under its
/// heading ("Returns:"), once, its body indented and colorized where it is code.
/// </summary>
public sealed class DescriptionView : StackPanel
{
    public DescriptionView(SymbolDescription description, EditorTheme theme, bool showIcon = true)
    {
        Description = description;
        Spacing = 4;
        Classes.Add("intelliSenseDescription");

        if (description.Signature.Length > 0)
        {
            SignatureBlock = ClassifiedInlines.Block(description.Signature, theme);
            if (OverloadsText(description.Overloads) is { } overloads)
            {
                SignatureBlock.Inlines!.Add(new Run(" " + overloads));
            }

            var header = new DockPanel();
            if (showIcon && description.Glyph is { } glyph)
            {
                Icon = new Icon { Kind = SymbolKinds.IconOf(glyph), Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 6, 0) };
                DockPanel.SetDock(Icon, Avalonia.Controls.Dock.Left);
                header.Children.Add(Icon);
            }

            header.Children.Add(SignatureBlock);
            Children.Add(header);
        }

        foreach (var section in description.Sections)
        {
            var body = ClassifiedInlines.Block(section.Text, theme);
            body.Classes.Add("section-" + section.Kind);
            if (Heading(section.Kind) is { } heading)
            {
                var title = new TextBlock { Text = heading, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 2, 0, 0) };
                title.Classes.Add("sectionHeading");
                Children.Add(title);
                body.Margin = new Thickness(12, 0, 0, 0);
            }

            Children.Add(body);
            SectionBlocks.Add((section.Kind, body));
        }
    }

    public SymbolDescription Description { get; }

    /// <summary>The colorized signature with its overload count (tests).</summary>
    public TextBlock? SignatureBlock { get; }

    /// <summary>The kind's icon (tests).</summary>
    public Icon? Icon { get; }

    /// <summary>Each section's body by its kind (tests).</summary>
    public List<(string Kind, TextBlock Block)> SectionBlocks { get; } = [];

    /// <summary>"(+ 1 overload)", "(+ 2 overloads)", or null.</summary>
    public static string? OverloadsText(int count) => count switch
    {
        <= 0 => null,
        1 => Localizer.Get("editor.quickInfo.oneOverload", "(+ 1 overload)"),
        _ => Localizer.Format("editor.quickInfo.overloads", "(+ {0} overloads)", count),
    };

    /// <summary>A section's heading, or null for the summary and the sections that read without one.</summary>
    public static string? Heading(string kind) => kind switch
    {
        DescriptionSectionKinds.Returns => Localizer.Get("editor.quickInfo.returns", "Returns:"),
        DescriptionSectionKinds.Value => Localizer.Get("editor.quickInfo.value", "Value:"),
        DescriptionSectionKinds.Exceptions => Localizer.Get("editor.quickInfo.exceptions", "Exceptions:"),
        DescriptionSectionKinds.Remarks => Localizer.Get("editor.quickInfo.remarks", "Remarks:"),
        DescriptionSectionKinds.Usage => Localizer.Get("editor.quickInfo.usage", "Usage:"),
        DescriptionSectionKinds.Captures => Localizer.Get("editor.quickInfo.captures", "Variables captured:"),
        _ => null,
    };

    /// <summary>The text of a text block's runs (tests and accessibility).</summary>
    public static string TextOf(TextBlock block) =>
        block.Inlines is { Count: > 0 } inlines
            ? string.Concat(inlines.Select(i => i switch { Run run => run.Text, LineBreak => "\n", _ => string.Empty }))
            : block.Text ?? string.Empty;
}
