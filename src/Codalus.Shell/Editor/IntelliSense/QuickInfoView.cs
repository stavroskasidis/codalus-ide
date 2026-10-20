using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using Codalus.LanguageServices.Protocol;
using Codalus.Shell.Controls;
using Codalus.Workbench.Localization;

namespace Codalus.Shell.Editor.IntelliSense;

/// <summary>What Quick Info's buttons do for a diagnostic, and their keys' labels.</summary>
public sealed record QuickInfoActions
{
    /// <summary>Applies the diagnostic's top code fix; null hides Fix.</summary>
    public Func<Diagnostic, Task>? Fix { get; init; }

    /// <summary>Opens the code actions menu at the diagnostic; null hides Other Fixes.</summary>
    public Func<Diagnostic, Task>? OtherFixes { get; init; }

    /// <summary>Sends the diagnostic and its place to AI Chat; null (AI off) hides Add to Chat.</summary>
    public Func<Diagnostic, Task>? AddToChat { get; init; }

    /// <summary>The key of Fix's command (Apply Top Fix), or null.</summary>
    public string? FixKey { get; init; }

    /// <summary>The key of the code actions menu, or null.</summary>
    public string? OtherFixesKey { get; init; }

    /// <summary>Opens a diagnostic's help link.</summary>
    public Action<string>? OpenLink { get; init; }
}

/// <summary>
/// Quick Info as one tooltip, as a classic IDE shows it: the symbol (its kind's icon, the colorized signature wrapped
/// across lines and its overload count, the summary, Returns and the other sections), a separator, each diagnostic (its
/// code as a link to its help, then its message), and the buttons: Fix, Other Fixes, and Add to Chat while AI is on. A
/// hover with diagnostics only has the same layout without the symbol.
/// </summary>
public sealed class QuickInfoView : StackPanel
{
    public QuickInfoView(SymbolDescription? description, Control? markdown, IReadOnlyList<Diagnostic> diagnostics, EditorTheme theme, QuickInfoActions actions)
    {
        Spacing = 6;
        Classes.Add("quickInfo");
        if (description is not null && (description.Signature.Length > 0 || description.Sections.Length > 0))
        {
            Symbol = new DescriptionView(description, theme);
            Children.Add(Symbol);
        }
        else if (markdown is not null)
        {
            Children.Add(markdown);
        }

        if (diagnostics.Count == 0)
        {
            return;
        }

        if (Children.Count > 0)
        {
            Children.Add(new Border { Classes = { "quickInfoSeparator" } });
        }

        foreach (var diagnostic in diagnostics)
        {
            var line = new TextBlock { TextWrapping = TextWrapping.Wrap };
            line.Classes.Add("quickInfoDiagnostic");
            if (diagnostic.Code is { Length: > 0 } code)
            {
                var link = new Button { Classes = { "link", "diagnosticCode" }, Content = code, Focusable = false };
                AutomationProperties.SetName(link, Localizer.Format("editor.quickInfo.diagnosticHelp", "Help for {0}", code));
                if (diagnostic.CodeDescription?.Href is { Length: > 0 } href && actions.OpenLink is { } open)
                {
                    ToolTip.SetTip(link, href);
                    link.Click += (_, _) => open(href);
                }
                else
                {
                    link.IsEnabled = false;
                }

                DiagnosticLinks.Add(link);
                line.Inlines!.Add(new InlineUIContainer(link) { BaselineAlignment = BaselineAlignment.TextBottom });
                line.Inlines.Add(new Run(": "));
            }

            line.Inlines!.Add(new Run(diagnostic.Message));
            Children.Add(line);
        }

        // The buttons act on the first diagnostic (the most severe one at that spot).
        var first = diagnostics[0];
        var buttons = new WrapPanel { Orientation = Orientation.Horizontal };
        buttons.Classes.Add("quickInfoActions");
        if (actions.Fix is { } fix)
        {
            FixButton = Action("lightbulb", Label(Localizer.Get("editor.quickInfo.fix", "Fix"), actions.FixKey), () => fix(first));
            buttons.Children.Add(FixButton);
        }

        if (actions.OtherFixes is { } otherFixes)
        {
            OtherFixesButton = Action(null, Label(Localizer.Get("editor.quickInfo.otherFixes", "Other Fixes"), actions.OtherFixesKey), () => otherFixes(first));
            buttons.Children.Add(OtherFixesButton);
        }

        if (actions.AddToChat is { } addToChat)
        {
            AddToChatButton = Action("sparkle", Localizer.Get("editor.quickInfo.addToChat", "Add to Chat"), () => addToChat(first));
            buttons.Children.Add(AddToChatButton);
        }

        if (buttons.Children.Count > 0)
        {
            Children.Add(buttons);
        }
    }

    /// <summary>The symbol's part, when there is a symbol (tests).</summary>
    public DescriptionView? Symbol { get; }

    /// <summary>The diagnostics' codes as links (tests).</summary>
    public List<Button> DiagnosticLinks { get; } = [];

    public Button? FixButton { get; }

    public Button? OtherFixesButton { get; }

    public Button? AddToChatButton { get; }

    /// <summary>Raised after a button ran (the hover closes).</summary>
    public event EventHandler? ActionInvoked;

    private static string Label(string text, string? key) => key is null ? text : Localizer.Format("editor.quickInfo.withKey", "{0} ({1})", text, key);

    private Button Action(string? icon, string label, Func<Task> run)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        if (icon is not null)
        {
            content.Children.Add(new Icon { Kind = icon, Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center });
        }

        content.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        var button = new Button { Classes = { "quickInfoAction" }, Content = content, Focusable = false, Margin = new Thickness(0, 0, 8, 0) };
        AutomationProperties.SetName(button, label);
        button.Click += (_, _) =>
        {
            ActionInvoked?.Invoke(this, EventArgs.Empty);
            _ = run();
        };
        return button;
    }
}
