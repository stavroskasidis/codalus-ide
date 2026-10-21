using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using Codalus.LanguageServices.Protocol;
using Codalus.Platform;
using Codalus.Shell.Controls;
using Codalus.Shell.Editor;
using Codalus.Shell.Editor.IntelliSense;
using Codalus.Workbench.Context;
using Codalus.Workbench.Keybindings;

namespace Codalus.Shell.Tests;

/// <summary>
/// The editor's IntelliSense popups as a classic IDE's: signature help's colorized signature (the active parameter bold),
/// its overloads' chevrons; the completion list's kind icons in their colors, matched characters, filter bar and
/// description tooltip; the list and signature help never overlapping; Quick Info's layout, and Quick Info staying closed
/// while either popup is open.
/// </summary>
public class IntelliSensePopupTests
{
    private static ClassifiedRun R(string text, string kind, int? parameter = null) => new(text, kind) { Parameter = parameter };

    /// <summary>The C# service's help for <c>builder.AddTab(</c>, its base class's type arguments substituted.</summary>
    private static SignatureHelp BuilderHelp()
    {
        ClassifiedRun[] parts =
        [
            R("wsTabHostLayoutBuilder", "class"), R(" ", "text"), R("wsLayoutBuilderBase", "class"), R("<", "punctuation"), R("MyPageModel", "class"),
            R(",", "punctuation"), R(" ", "text"), R("wpLayoutBuilder", "class"), R("<", "punctuation"), R("MyPageModel", "class"), R(">", "punctuation"),
            R(">", "punctuation"), R(".", "punctuation"), R("AddTab", "method"), R("(", "punctuation"),
            R("int", "keyword", 0), R(" ", "text", 0), R("id", "parameter", 0), R(",", "punctuation"), R(" ", "text"),
            R("Action", "delegate", 1), R("<", "punctuation", 1), R("wsTabPagesLayoutBuilder", "class", 1), R(">", "punctuation", 1), R(" ", "text", 1), R("tabPageBuilder", "parameter", 1),
            R(")", "punctuation"),
        ];
        var label = string.Concat(parts.Select(p => p.Text));
        var first = new SignatureInformation(label, MarkupContent.Markdown("Method adds a tab."),
            [new ParameterInformation("int id", MarkupContent.Markdown("Id \\- incremental")), new ParameterInformation("Action<wsTabPagesLayoutBuilder> tabPageBuilder", null)])
        {
            ActiveParameter = 0,
            Parts = parts,
        };
        var second = new SignatureInformation("wsTabHostLayoutBuilder wsLayoutBuilderBase<MyPageModel, wpLayoutBuilder<MyPageModel>>.AddTab(string title)", MarkupContent.Markdown("Adds a tab with a title."),
            [new ParameterInformation("string title", null)]);
        return new SignatureHelp([first, second], 0, 0);
    }

    private static (Window Window, TextEditor Editor) ShowEditor(string text, int lines = 1)
    {
        var editor = new TextEditor
        {
            Document = new TextDocument(string.Concat(Enumerable.Repeat("// filler\n", lines - 1)) + text),
            FontFamily = MarkdownRenderer.Monospace,
        };
        var window = new Window { Width = 900, Height = 600, Content = editor };
        window.Show();
        editor.CaretOffset = editor.Document.TextLength;
        editor.TextArea.Focus();
        ShellFixture.Pump();
        return (window, editor);
    }

    private static Run RunOf(TextBlock block, string text) => block.Inlines!.OfType<Run>().First(r => r.Text == text);

    private static Color? ColorOf(Run run) => (run.Foreground as ISolidColorBrush)?.Color;

    [AvaloniaFact]
    public void Signature_help_draws_the_signature_in_the_editors_colors_with_the_active_parameter_bold()
    {
        var (window, editor) = ShowEditor("builder.AddTab(", lines: 12);
        var theme = EditorTheme.Dark;
        var popup = new SignatureHelpPopup(editor.TextArea, BuilderHelp(), () => theme);
        try
        {
            popup.Show();
            IntelliSenseLayout.Arrange(editor.TextArea, null, popup);
            ShellFixture.Pump();

            var signature = popup.SignatureBlock;
            Assert.Equal("wsTabHostLayoutBuilder wsLayoutBuilderBase<MyPageModel, wpLayoutBuilder<MyPageModel>>.AddTab(int id, Action<wsTabPagesLayoutBuilder> tabPageBuilder)", DescriptionView.TextOf(signature));
            Assert.Equal(Color.FromUInt32(theme.TokenColor("class")!.Value), ColorOf(RunOf(signature, "wsLayoutBuilderBase")));
            Assert.Equal(Color.FromUInt32(theme.TokenColor("method")!.Value), ColorOf(RunOf(signature, "AddTab")));
            Assert.Equal(Color.FromUInt32(theme.TokenColor("keyword")!.Value), ColorOf(RunOf(signature, "int")));
            Assert.Equal(Color.FromUInt32(theme.TokenColor("parameter")!.Value), ColorOf(RunOf(signature, "id")));
            Assert.Equal(Color.FromUInt32(theme.TokenColor("delegate")!.Value), ColorOf(RunOf(signature, "Action")));

            // The active parameter's runs are bold, the rest aren't.
            Assert.Equal(FontWeight.Bold, RunOf(signature, "id").FontWeight);
            Assert.Equal(FontWeight.Bold, RunOf(signature, "int").FontWeight);
            Assert.NotEqual(FontWeight.Bold, RunOf(signature, "tabPageBuilder").FontWeight);
            Assert.NotEqual(FontWeight.Bold, RunOf(signature, "AddTab").FontWeight);

            // The summary, then the active parameter's documentation with its name bold and italic.
            Assert.Equal("Method adds a tab.", popup.SummaryBlock.Text);
            var name = popup.ParameterBlock.Inlines!.OfType<Run>().First();
            Assert.Equal("id:", name.Text);
            Assert.Equal(FontWeight.Bold, name.FontWeight);
            Assert.Equal(FontStyle.Italic, name.FontStyle);
            Assert.Equal("id: Id - incremental", DescriptionView.TextOf(popup.ParameterBlock));

            // Signature help alone goes above the caret's line.
            Assert.Equal(PopupSide.Above, popup.Side);
            Assert.True(popup.IsOpen);
        }
        finally
        {
            popup.Hide();
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Plain_signature_help_from_other_servers_bolds_the_active_parameter_of_its_label()
    {
        var (window, editor) = ShowEditor("greet(");
        var help = new SignatureHelp([new SignatureInformation("greet(name: string, times: int): void", null, [new ParameterInformation("name: string", null), new ParameterInformation("times: int", MarkupContent.Markdown("How often."))]) { ActiveParameter = 1 }], 0, 1);
        var popup = new SignatureHelpPopup(editor.TextArea, help, () => EditorTheme.Light);
        try
        {
            popup.Show();
            ShellFixture.Pump();
            Assert.Equal("greet(name: string, times: int): void", DescriptionView.TextOf(popup.SignatureBlock));
            Assert.Equal(FontWeight.Bold, RunOf(popup.SignatureBlock, "times: int").FontWeight);
            Assert.NotEqual(FontWeight.Bold, RunOf(popup.SignatureBlock, "name: string").FontWeight);
            Assert.False(RunOf(popup.SignatureBlock, "times: int").IsSet(TextElement.ForegroundProperty)); // the popup's text color: no classification
            Assert.Equal("times: How often.", DescriptionView.TextOf(popup.ParameterBlock));
            Assert.False(popup.SummaryBlock.IsVisible);
        }
        finally
        {
            popup.Hide();
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Overloads_cycle_with_the_chevrons_and_wrap_around()
    {
        var (window, editor) = ShowEditor("builder.AddTab(");
        var popup = new SignatureHelpPopup(editor.TextArea, BuilderHelp(), () => EditorTheme.Dark);
        try
        {
            popup.Show();
            ShellFixture.Pump();
            Assert.Equal("1 of 2", popup.PositionText);

            var chevrons = popup.Child!.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("signatureChevron")).ToList();
            Assert.Equal(2, chevrons.Count);
            Assert.Equal("chevron-up", ((Icon)chevrons[0].Content!).Kind);
            Assert.Equal("chevron-down", ((Icon)chevrons[1].Content!).Kind);

            chevrons[1].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal("2 of 2", popup.PositionText);
            Assert.Equal("Adds a tab with a title.", popup.SummaryBlock.Text);
            Assert.Contains("AddTab(string title)", DescriptionView.TextOf(popup.SignatureBlock), StringComparison.Ordinal);

            chevrons[1].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal("1 of 2", popup.PositionText); // wraps around
            chevrons[0].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal("2 of 2", popup.PositionText);
            Assert.True(popup.ChangeIndex(+1));
            Assert.Equal("1 of 2", popup.PositionText);

            // One overload: no chevrons, and Up and Down stay with the editor.
            popup.Update(new SignatureHelp([BuilderHelp().Signatures[1]], 0, 0), keepSelection: false);
            Assert.False(chevrons[0].IsEffectivelyVisible);
            Assert.False(popup.ChangeIndex(+1));
        }
        finally
        {
            popup.Hide();
            window.Close();
        }
    }

    private static CompletionPopup CompletionOf(TextEditor editor, int start, IEnumerable<CompletionItem> items, Func<string, string?>? keyLabel = null)
    {
        var popup = new CompletionPopup(editor.TextArea, () => EditorTheme.Dark, keyLabel ?? (_ => null)) { StartOffset = start, EndOffset = editor.CaretOffset };
        foreach (var item in items)
        {
            popup.CompletionList.CompletionData.Add(new CompletionData(item, Task.FromResult, (_, _) => { }));
        }

        popup.CompletionList.BuildFilters();
        popup.CompletionList.Refresh();
        popup.Show();
        IntelliSenseLayout.Arrange(editor.TextArea, popup, null);
        ShellFixture.Pump();
        return popup;
    }

    private static CompletionItem Item(string label, string glyph, int kind = CompletionItemKind.Text) => new(label) { Glyph = glyph, Kind = kind };

    private static readonly CompletionItem[] s_items =
    [
        Item("Console", SymbolGlyphs.Class), Item("ConsoleColor", SymbolGlyphs.Enum), Item("ConsoleKey", SymbolGlyphs.Enum),
        Item("IComparable", SymbolGlyphs.Interface), Item("DateTime", SymbolGlyphs.Struct), Item("Action", SymbolGlyphs.Delegate),
        Item("Concat", SymbolGlyphs.Method), Item("Contains", SymbolGlyphs.ExtensionMethod), Item("Count", SymbolGlyphs.Property),
        Item("_count", SymbolGlyphs.Field), Item("Changed", SymbolGlyphs.Event), Item("MaxValue", SymbolGlyphs.Constant),
        Item("context", SymbolGlyphs.Local), Item("value", SymbolGlyphs.Parameter), Item("System", SymbolGlyphs.Namespace),
        Item("continue", SymbolGlyphs.Keyword), Item("cw", SymbolGlyphs.Snippet),
    ];

    [AvaloniaFact]
    public void Completion_rows_have_their_kinds_icons_in_the_palettes_colors_and_bold_matches()
    {
        var (window, editor) = ShowEditor("Con");
        var popup = CompletionOf(editor, 0, s_items);
        try
        {
            // Every kind has an icon of its own, compiled from the committed SVGs, in its palette color.
            foreach (var symbol in IconSet.SymbolNames)
            {
                Assert.NotNull(IconSet.GetFilled(symbol));
                Assert.True(CommandIcons.HasColor(symbol), symbol);
            }

            Assert.Equal(CommandIcons.Orange, CommandIcons.ColorOf("symbol-class"));
            Assert.Equal(CommandIcons.Purple, CommandIcons.ColorOf("symbol-method"));
            Assert.Equal(CommandIcons.Purple, CommandIcons.ColorOf("symbol-extension-method"));
            Assert.Equal(CommandIcons.Yellow, CommandIcons.ColorOf("symbol-enum"));
            Assert.Equal(CommandIcons.Yellow, CommandIcons.ColorOf("symbol-event"));
            Assert.Equal(CommandIcons.Blue, CommandIcons.ColorOf("symbol-field"));
            Assert.Equal("symbol-namespace", SymbolKinds.IconOf(SymbolKinds.FromKind(CompletionItemKind.Module))); // LSP kinds map too
            Assert.Equal("symbol-local", SymbolKinds.IconOf(SymbolKinds.FromKind(CompletionItemKind.Variable)));

            // Typing "Cons" keeps the matches, selects the best (Console, the shortest) and draws its matched characters bold.
            popup.CompletionList.SelectItem("Cons");
            ShellFixture.Pump();
            Assert.Equal("Console", popup.CompletionList.SelectedItem?.Label);
            Assert.Equal([0, 1, 2, 3], popup.CompletionList.SelectedItem!.Matches);
            Assert.DoesNotContain(popup.CompletionList.CurrentList, d => d.Label == "DateTime");
            var rows = popup.CompletionList.ListBox.GetVisualDescendants().OfType<DockPanel>().Where(p => p.DataContext is CompletionData).ToList();
            var console = rows.First(r => ((CompletionData)r.DataContext!).Label == "Console");
            var icon = console.Children.OfType<Icon>().Single();
            Assert.Equal("symbol-class", icon.Kind);
            Assert.True(icon.TryFindResource("Shell.Icon.Orange", icon.ActualThemeVariant, out var orange) && orange is IBrush);
            var name = console.Children.OfType<HighlightedText>().Single();
            Assert.Equal([0, 1, 2, 3], name.Highlights);
            Assert.Contains(name.Inlines!.OfType<Run>(), r => r.Text == "Cons" && r.FontWeight == FontWeight.Bold);
            Assert.Equal("symbol-enum", rows.First(r => ((CompletionData)r.DataContext!).Label == "ConsoleColor").Children.OfType<Icon>().Single().Kind);

            // The selected row is highlighted with an accent bar.
            var selected = popup.CompletionList.ListBox.ContainerFromItem(popup.CompletionList.SelectedItem!)!;
            var bar = selected.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("completionAccent"));
            Assert.True(bar.TryFindResource("Shell.Accent", bar.ActualThemeVariant, out var accent));
            Assert.Equal(((ISolidColorBrush)accent!).Color, (bar.Background as ISolidColorBrush)?.Color);

            // The list's keys move the selection.
            Assert.True(popup.CompletionList.HandleKey(Key.Down));
            Assert.NotEqual("Console", popup.CompletionList.SelectedItem?.Label);
            Assert.True(popup.CompletionList.HandleKey(Key.Home));
            Assert.Equal(popup.CompletionList.CurrentList[0], popup.CompletionList.SelectedItem);
        }
        finally
        {
            popup.Hide();
            window.Close();
        }
    }

    [AvaloniaFact]
    public void The_filter_bar_has_a_toggle_per_kind_that_narrows_the_list_to_that_kind()
    {
        var (window, editor) = ShowEditor("");
        var popup = CompletionOf(editor, 0, s_items.Where(i => i.Glyph is not SymbolGlyphs.Snippet), id => id == SymbolKinds.FilterCommandPrefix + "classes" ? "Alt+C" : null);
        try
        {
            var toggles = popup.CompletionList.FilterToggles;
            Assert.Contains("classes", toggles.Keys);
            Assert.Contains("extensionMethods", toggles.Keys);
            Assert.DoesNotContain("snippets", toggles.Keys); // only the kinds the list has
            Assert.Equal("Classes (Alt+C)", ToolTip.GetTip(toggles["classes"]));
            Assert.Equal("Enums", ToolTip.GetTip(toggles["enums"]));
            Assert.Equal("symbol-class", ((Icon)toggles["classes"].Content!).Kind);
            Assert.Equal("Classes", Avalonia.Automation.AutomationProperties.GetName(toggles["classes"]));
            Assert.True(popup.CompletionList.FilterBar.IsVisible);
            var all = popup.CompletionList.CurrentList.Count;

            // A toggle shows only its kind; a second adds its kind; off again shows everything.
            toggles["classes"].IsChecked = true;
            Assert.Equal(["Console"], popup.CompletionList.CurrentList.Select(d => d.Label));
            Assert.True(popup.CompletionList.ToggleFilter("enums")); // Alt+E
            Assert.Equal(["Console", "ConsoleColor", "ConsoleKey"], popup.CompletionList.CurrentList.Select(d => d.Label));
            Assert.True(toggles["enums"].IsChecked);
            popup.CompletionList.ToggleFilter("classes");
            popup.CompletionList.ToggleFilter("enums");
            Assert.Equal(all, popup.CompletionList.CurrentList.Count);
            Assert.False(popup.CompletionList.ToggleFilter("snippets")); // no snippets here

            // Locals and parameters are one filter, as are methods and operators.
            popup.CompletionList.ToggleFilter("locals");
            Assert.Equal(["context", "value"], popup.CompletionList.CurrentList.Select(d => d.Label));
        }
        finally
        {
            popup.Hide();
            window.Close();
        }
    }

    private static readonly HashSet<char> s_commitCharacters = [' ', '(', ')', '.', ',', ';', '<', '='];

    /// <summary>A completion list of the given items, each committed by <see cref="s_commitCharacters"/> unless it names its own.</summary>
    private static CompletionPopup CommittingCompletionOf(TextEditor editor, int start, IEnumerable<CompletionItem> items)
    {
        var popup = new CompletionPopup(editor.TextArea, () => EditorTheme.Dark, id => id == SymbolKinds.FilterCommandPrefix + SymbolKinds.TargetTypeFilter ? "Alt+J" : null)
        {
            StartOffset = start,
            EndOffset = editor.CaretOffset,
        };
        foreach (var item in items)
        {
            popup.CompletionList.CompletionData.Add(new CompletionData(item, Task.FromResult, (_, _) => { })
            {
                CommitCharacters = item.CommitCharacters is { } own ? [.. own.Select(c => c[0])] : s_commitCharacters,
            });
        }

        popup.CompletionList.BuildFilters();
        popup.CompletionList.Refresh();
        popup.Show();
        IntelliSenseLayout.Arrange(editor.TextArea, popup, null);
        ShellFixture.Pump();
        return popup;
    }

    [AvaloniaFact]
    public void Commit_characters_commit_a_hard_selection_only_and_not_while_they_continue_the_name()
    {
        var (window, editor) = ShowEditor("");
        var popup = CommittingCompletionOf(editor, 0, [Item("WriteLine", SymbolGlyphs.Method), Item("Write", SymbolGlyphs.Method), Item("System.Text", SymbolGlyphs.Namespace)]);
        try
        {
            // Nothing typed: the first item is selected softly (an outline, no accent bar), and "(" doesn't commit it.
            Assert.True(popup.CompletionList.IsSoftSelection);
            Assert.Contains("soft", popup.CompletionList.ListBox.Classes);
            Assert.False(popup.CommitsOn('('));
            var selected = popup.CompletionList.ListBox.ContainerFromItem(popup.CompletionList.SelectedItem!)!;
            Assert.True(selected.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("completionAccent")).Background is null or ISolidColorBrush { Color.A: 0 });

            // Picked with the arrows, it's hard: "(" commits it.
            popup.CompletionList.HandleKey(Key.Down);
            Assert.False(popup.CompletionList.IsSoftSelection);
            Assert.DoesNotContain("soft", popup.CompletionList.ListBox.Classes);
            Assert.True(popup.CommitsOn('('));

            // Typed text makes the best match hard; a character that isn't the item's commit character doesn't commit.
            popup.CompletionList.SelectItem("WriteL");
            Assert.Equal("WriteLine", popup.CompletionList.SelectedItem?.Label);
            Assert.False(popup.CompletionList.IsSoftSelection);
            Assert.True(popup.CommitsOn('('));
            Assert.True(popup.CommitsOn(';'));
            Assert.False(popup.CommitsOn('x'));

            // A commit character that continues the item's name filters instead: the "." of "System.Text".
            popup.CompletionList.SelectItem("System");
            Assert.Equal("System.Text", popup.CompletionList.SelectedItem?.Label);
            Assert.False(popup.CommitsOn('.'));
            Assert.True(popup.CommitsOn(';'));

            // Only punctuation typed: soft again.
            popup.CompletionList.SelectItem(".");
            Assert.True(popup.CompletionList.SelectedItem is null || popup.CompletionList.IsSoftSelection);
        }
        finally
        {
            popup.Hide();
            window.Close();
        }

        // Suggestion mode (the server sends no commit characters): soft whatever is typed, and nothing commits.
        (window, editor) = ShowEditor("pa");
        popup = CommittingCompletionOf(editor, 0, [Item("page", SymbolGlyphs.Local) with { CommitCharacters = [] }, Item("pages", SymbolGlyphs.Local) with { CommitCharacters = [] }]);
        try
        {
            popup.CompletionList.SelectItem("pa");
            Assert.Equal("page", popup.CompletionList.SelectedItem?.Label);
            Assert.True(popup.CompletionList.IsSoftSelection);
            Assert.False(popup.CommitsOn(' '));
            Assert.False(popup.CommitsOn('('));
        }
        finally
        {
            popup.Hide();
            window.Close();
        }

        // A server's preselected item is hard with nothing typed (the type after "new"), unless it asks to be soft.
        (window, editor) = ShowEditor("");
        popup = CommittingCompletionOf(editor, 0, [Item("Alpha", SymbolGlyphs.Class), Item("List<int>", SymbolGlyphs.Class) with { Preselect = true, FilterText = "List" }]);
        try
        {
            Assert.Equal("List<int>", popup.CompletionList.SelectedItem?.Label);
            Assert.False(popup.CompletionList.IsSoftSelection);
            Assert.True(popup.CommitsOn('('));
        }
        finally
        {
            popup.Hide();
            window.Close();
        }

        (window, editor) = ShowEditor("");
        popup = CommittingCompletionOf(editor, 0, [Item("Alpha", SymbolGlyphs.Class), Item("Red", SymbolGlyphs.EnumMember) with { Preselect = true, SoftSelection = true }]);
        try
        {
            Assert.Equal("Red", popup.CompletionList.SelectedItem?.Label);
            Assert.True(popup.CompletionList.IsSoftSelection);
        }
        finally
        {
            popup.Hide();
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Popups_close_when_their_editor_leaves_its_window_and_never_place_against_another_one()
    {
        // The sweep on macOS: "Target control is not in the same tree as the popup parent" once the editor's view was
        // under another root than the window the popups opened on (a document opened over it, another window).
        var (window, editor) = ShowEditor("Wri");
        var completion = CompletionOf(editor, 0, [Item("WriteLine", SymbolGlyphs.Method), Item("Write", SymbolGlyphs.Method)]);
        var signature = new SignatureHelpPopup(editor.TextArea, BuilderHelp(), () => EditorTheme.Dark);
        signature.Show();
        IntelliSenseLayout.Arrange(editor.TextArea, completion, signature);
        ShellFixture.Pump();
        var other = new Window { Width = 600, Height = 400 };
        try
        {
            Assert.True(completion.IsOpen);
            Assert.True(signature.IsOpen);

            // The editor moves to another window: both close, and placing them again neither throws nor reopens them.
            window.Content = null;
            ShellFixture.Pump();
            other.Content = editor;
            other.Show();
            ShellFixture.Pump();
            Assert.False(completion.IsOpen);
            Assert.False(signature.IsOpen);
            IntelliSenseLayout.Arrange(editor.TextArea, completion, signature);
            completion.Place(new Rect(0, 0, 100, 16), 10, PopupSide.Below, allowFlip: true);
            ShellFixture.Pump();
            Assert.False(completion.IsOpen);

            // Shown again, they open on the editor's new window, and stay open while placed there.
            var again = CompletionOf(editor, 0, [Item("WriteLine", SymbolGlyphs.Method)]);
            Assert.True(again.IsOpen);
            Assert.Same(other, TopLevel.GetTopLevel(editor.TextArea));
            Assert.True(again.StillOnItsWindow());
            again.Hide();

            // While the editor isn't showing (another document over it), a popup asked for opens nothing.
            editor.IsVisible = false;
            ShellFixture.Pump();
            var hidden = new SignatureHelpPopup(editor.TextArea, BuilderHelp(), () => EditorTheme.Dark);
            Assert.False(hidden.Show());
            Assert.False(hidden.IsOpen);
            editor.IsVisible = true;
        }
        finally
        {
            completion.Hide();
            signature.Hide();
            other.Close();
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Items_of_the_target_type_are_preselected_and_have_a_filter_of_their_own()
    {
        var (window, editor) = ShowEditor("");
        var popup = CommittingCompletionOf(editor, 0,
        [
            Item("Console", SymbolGlyphs.Class), Item("context", SymbolGlyphs.Local), Item("Count", SymbolGlyphs.Property) with { MatchesTargetType = true },
            Item("count", SymbolGlyphs.Local) with { MatchesTargetType = true }, Item("Parse", SymbolGlyphs.Method),
        ]);
        try
        {
            // The best match: a local of the expected type before a member of it (selected softly, as nothing is typed).
            Assert.Equal("count", popup.CompletionList.SelectedItem?.Label);
            Assert.True(popup.CompletionList.IsSoftSelection);

            // Typing keeps preferring it among equal matches.
            popup.CompletionList.SelectItem("co");
            Assert.Equal("count", popup.CompletionList.SelectedItem?.Label);

            // The target type filter: its toggle (the target icon, Alt+J) shows only the matching items, of any kind.
            popup.CompletionList.SelectItem("");
            var toggle = popup.CompletionList.FilterToggles[SymbolKinds.TargetTypeFilter];
            Assert.Equal("symbol-target-type", ((Icon)toggle.Content!).Kind);
            Assert.Equal("Target type matches (Alt+J)", ToolTip.GetTip(toggle));
            Assert.Same(popup.CompletionList.FilterToggles.Values.Last(), toggle); // at the end of the bar
            Assert.True(popup.CompletionList.ToggleFilter(SymbolKinds.TargetTypeFilter));
            Assert.Equal(["Count", "count"], popup.CompletionList.CurrentList.Select(d => d.Label).Order(StringComparer.Ordinal));

            // With a kind's filter too, the list shows either.
            popup.CompletionList.ToggleFilter("classes");
            Assert.Equal(["Console", "Count", "count"], popup.CompletionList.CurrentList.Select(d => d.Label).Order(StringComparer.Ordinal));
        }
        finally
        {
            popup.Hide();
            window.Close();
        }

        // Without target type matches, there's no such filter.
        (window, editor) = ShowEditor("");
        popup = CommittingCompletionOf(editor, 0, [Item("Console", SymbolGlyphs.Class), Item("context", SymbolGlyphs.Local)]);
        try
        {
            Assert.DoesNotContain(SymbolKinds.TargetTypeFilter, popup.CompletionList.FilterToggles.Keys);
        }
        finally
        {
            popup.Hide();
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Filter_shortcuts_reach_the_list_in_every_key_scheme_on_every_platform()
    {
        foreach (var platform in Enum.GetValues<PlatformKind>())
        {
            foreach (var scheme in new[] { "default", "vscode", "resharper", "emacs" })
            {
                var context = new ContextKeyService();
                context.Set("editorTextFocus", true);
                context.Set("suggestWidgetVisible", true);
                context.Set("keyboardScheme", scheme);
                context.Set("activeDocumentKind", "textFile");
                var keybindings = new KeybindingService(context, platform);
                keybindings.SetDefaults(BuiltIns.ParseDefaultKeybindings(platform, scheme));
                foreach (var filter in SymbolKinds.Filters)
                {
                    var key = SymbolKinds.FilterKeys[filter.Id];
                    var command = keybindings.Peek(KeyChord.Parse("alt+" + key, platform)).Rule?.Command;

                    // Emacs keeps its own Alt+F, Alt+V and Alt+X (word right, page up, the command palette).
                    if (scheme == "emacs" && key is 'f' or 'v' or 'x')
                    {
                        Assert.True(command is not null && command != filter.Command, $"{platform} emacs: alt+{key} runs {command ?? "nothing"}");
                    }
                    else
                    {
                        Assert.True(command == filter.Command, $"{platform} {scheme}: alt+{key} runs {command ?? "nothing"}, not {filter.Command}");
                    }
                }

                // Without the list, the keys are what they were (menus' access keys, Emacs's keys).
                context.Set("suggestWidgetVisible", false);
                Assert.DoesNotContain(SymbolKinds.Filters, f => keybindings.Peek(KeyChord.Parse("alt+" + SymbolKinds.FilterKeys[f.Id], platform)).Rule?.Command == f.Command);

                // Quick Info's buttons name their keys: Apply Top Fix and the code actions menu.
                context.Set("suggestWidgetVisible", true);
                Assert.Equal("editor.action.applyTopFix", keybindings.Peek(KeyChord.Parse("alt+.", platform)).Rule?.Command);
                Assert.Equal("editor.action.quickFix", keybindings.Peek(KeyChord.Parse("alt+enter", platform)).Rule?.Command);
            }
        }
    }

    [AvaloniaFact]
    public async Task The_description_tooltip_beside_the_selected_item_shows_the_signature_overloads_and_summary()
    {
        var (window, editor) = ShowEditor("Console.Wr");
        var writeLine = new CompletionItem("WriteLine") { Glyph = SymbolGlyphs.Method, Kind = CompletionItemKind.Method };
        var write = new CompletionItem("Write") { Glyph = SymbolGlyphs.Method, Kind = CompletionItemKind.Method };
        var resolved = writeLine with
        {
            Description = new SymbolDescription(
                [R("void", "keyword"), R(" ", "text"), R("Console", "class"), R(".", "punctuation"), R("WriteLine", "method"), R("(", "punctuation"), R(")", "punctuation")],
                [new DescriptionSection(DescriptionSectionKinds.Summary, [R("Writes the current line terminator to the standard output stream.", "text")])])
            {
                Glyph = SymbolGlyphs.Method,
                Overloads = 17,
            },
        };
        var popup = new CompletionPopup(editor.TextArea, () => EditorTheme.Dark, _ => null) { StartOffset = "Console.".Length, EndOffset = editor.CaretOffset };
        popup.CompletionList.CompletionData.Add(new CompletionData(write, Task.FromResult, (_, _) => { }));
        popup.CompletionList.CompletionData.Add(new CompletionData(writeLine, _ => Task.FromResult(resolved), (_, _) => { }));
        popup.CompletionList.BuildFilters();
        popup.CompletionList.Refresh();
        popup.Show();
        try
        {
            popup.CompletionList.SelectedItem = popup.CompletionList.CompletionData[1];
            await ShellFixture.PumpUntil(() => popup.DescriptionTip.IsOpen, seconds: 10);

            var description = Assert.IsType<DescriptionView>(popup.DescriptionContent);
            Assert.Equal("void Console.WriteLine() (+ 17 overloads)", DescriptionView.TextOf(description.SignatureBlock!));
            Assert.Equal(Color.FromUInt32(EditorTheme.Dark.TokenColor("class")!.Value), ColorOf(RunOf(description.SignatureBlock!, "Console")));
            Assert.Equal("Writes the current line terminator to the standard output stream.", DescriptionView.TextOf(description.SectionBlocks.Single(s => s.Kind == DescriptionSectionKinds.Summary).Block));

            // Beside the selected row, to its right.
            Assert.Equal(popup.CompletionList.ListBox.ContainerFromItem(popup.CompletionList.CompletionData[1]), popup.DescriptionTip.PlacementTarget);
            Assert.Equal(Avalonia.Controls.Primitives.PopupPositioning.PopupAnchor.TopRight, popup.DescriptionTip.PlacementAnchor);

            // An item without a description (another server's): its detail and documentation.
            var plain = popup.Describe(new CompletionItem("ubuntu-latest") { Detail = "string", Documentation = MarkupContent.Markdown("The latest Ubuntu runner\\.") });
            Assert.Contains(plain!.GetLogicalDescendants().OfType<TextBlock>(), t => t.Text == "The latest Ubuntu runner.");
            Assert.Null(popup.Describe(new CompletionItem("nothing")));

            // A type, as "class System.Threading.AbandonedMutexException".
            var type = popup.Describe(new CompletionItem("AbandonedMutexException")
            {
                Description = new SymbolDescription([R("class", "keyword"), R(" ", "text"), R("System", "namespace"), R(".", "punctuation"), R("Threading", "namespace"), R(".", "punctuation"), R("AbandonedMutexException", "class")],
                    [new DescriptionSection(DescriptionSectionKinds.Summary, [R("The exception that is thrown when one thread acquires a Mutex object that another thread has abandoned.", "text")])]),
            });
            Assert.Equal("class System.Threading.AbandonedMutexException", DescriptionView.TextOf(((DescriptionView)type!).SignatureBlock!));
        }
        finally
        {
            popup.Hide();
            window.Close();
        }
    }

    [Theory]
    [InlineData(400, 400, PopupSide.Below, PopupSide.Above)] // room on both sides: the list below, signature help above
    [InlineData(500, 120, PopupSide.Above, PopupSide.Below)] // at the screen's bottom: the list above, signature help below
    [InlineData(40, 600, PopupSide.Below, PopupSide.Above)] // at the top: the list below; signature help above, never on it
    public void Completion_and_signature_help_go_on_opposite_sides_of_the_line(double above, double below, PopupSide completion, PopupSide signature)
    {
        Assert.Equal((completion, signature), IntelliSensePlacement.Decide(above, below, 230, 60, completionOpen: true));
    }

    [Fact]
    public void Signature_help_alone_prefers_above_the_line()
    {
        Assert.Equal(PopupSide.Above, IntelliSensePlacement.Decide(300, 300, 0, 60, completionOpen: false).Signature);
        Assert.Equal(PopupSide.Below, IntelliSensePlacement.Decide(20, 600, 0, 60, completionOpen: false).Signature);
    }

    [AvaloniaFact]
    public void The_completion_list_and_signature_help_never_overlap_in_the_editor()
    {
        var (window, editor) = ShowEditor("builder.AddTab(Con", lines: 12);
        var signature = new SignatureHelpPopup(editor.TextArea, BuilderHelp(), () => EditorTheme.Dark) { AnchorOffset = editor.CaretOffset - 3 };
        signature.Show();
        var completion = CompletionOf(editor, editor.CaretOffset - 3, s_items);
        try
        {
            Assert.True(IntelliSenseLayout.Arrange(editor.TextArea, completion, signature));
            ShellFixture.Pump();
            Assert.Equal(PopupSide.Below, completion.Side);
            Assert.Equal(PopupSide.Above, signature.Side);

            // Both against the caret's line, on its two sides, without flipping onto each other at an edge.
            var line = IntelliSenseLayout.CaretLine(editor.TextArea)!.Value;
            Assert.Equal(line.Top, completion.VerticalOffset);
            Assert.Equal(line.Top, signature.VerticalOffset);
            Assert.Equal(Avalonia.Controls.Primitives.PopupPositioning.PopupAnchor.BottomLeft, completion.PlacementAnchor);
            Assert.Equal(Avalonia.Controls.Primitives.PopupPositioning.PopupAnchor.TopLeft, signature.PlacementAnchor);
            Assert.False(completion.PlacementConstraintAdjustment.HasFlag(Avalonia.Controls.Primitives.PopupPositioning.PopupPositionerConstraintAdjustment.FlipY));
            Assert.False(signature.PlacementConstraintAdjustment.HasFlag(Avalonia.Controls.Primitives.PopupPositioning.PopupPositionerConstraintAdjustment.FlipY));

            // On the screen: the list's top at or below the line's bottom, signature help's bottom at or above its top.
            var lineTop = editor.TextArea.TextView.PointToScreen(new Point(0, line.Top)).Y;
            var lineBottom = editor.TextArea.TextView.PointToScreen(new Point(0, line.Bottom)).Y;
            var list = completion.Child!.PointToScreen(new Point(0, 0)).Y;
            var help = signature.Child!.PointToScreen(new Point(0, signature.Child!.Bounds.Height)).Y;
            Assert.True(list >= lineBottom - 1, $"the list's top {list} is above the line's bottom {lineBottom}");
            Assert.True(help <= lineTop + 1, $"signature help's bottom {help} is below the line's top {lineTop}");

            // The list's names line up with the typed text (its icon column to their left).
            Assert.True(completion.HorizontalOffset < editor.TextArea.TextView.GetVisualPosition(new TextViewPosition(editor.Document.GetLocation(completion.StartOffset)), AvaloniaEdit.Rendering.VisualYPosition.LineTop).X);
        }
        finally
        {
            completion.Hide();
            signature.Hide();
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Quick_info_shows_diagnostics_with_links_and_stays_closed_while_completion_is_open()
    {
        await using var f = ShellFixture.Create();
        var path = Path.Combine(f.UserData, "notes.txt");
        await File.WriteAllTextAsync(path, "alphabet soup\nalp", TestContext.Current.CancellationToken);
        var document = await f.Shell.OpenFileAsync(path);
        Assert.NotNull(document);
        await ShellFixture.PumpUntil(() => document.Controller is not null);
        var editor = document.Controller!;
        f.Shell.Languages.Diagnostics.Set(path, "plaintext", null,
        [
            new Diagnostic(new LanguageServices.Protocol.Range(new Position(0, 0), new Position(0, 8)), DiagnosticSeverity.Warning, "TXT001", "notes", "Spelling: 'alphabet' looks fine.")
            {
                CodeDescription = new CodeDescription("https://example.invalid/TXT001"),
            },
        ]);
        await ShellFixture.PumpUntil(() => editor.Markers.Segments.Any());

        // A diagnostic-only hover: the code as a link to its help, then the message; no symbol part, no AI button (AI is off).
        editor.Editor.CaretOffset = 3;
        editor.Editor.TextArea.Focus();
        editor.ShowQuickInfoAtCaret();
        await ShellFixture.PumpUntil(() => editor.IsQuickInfoOpen);
        var quickInfo = editor.LastQuickInfo!;
        Assert.Null(quickInfo.Symbol);
        Assert.Equal("TXT001", Assert.Single(quickInfo.DiagnosticLinks).Content);
        Assert.Equal("https://example.invalid/TXT001", ToolTip.GetTip(quickInfo.DiagnosticLinks[0]));
        Assert.Contains(quickInfo.Children.OfType<TextBlock>(), t => DescriptionView.TextOf(t).EndsWith(": Spelling: 'alphabet' looks fine.", StringComparison.Ordinal));
        Assert.Null(quickInfo.AddToChatButton);
        Assert.Null(quickInfo.FixButton); // no language service to fix it
        f.Press(Key.Escape);
        Assert.False(editor.IsQuickInfoOpen);
        Assert.Null(editor.HoverPopup.Child); // let go of when closed: nothing in it (a link's tooltip) opens later
        Assert.False(quickInfo.IsAttachedToVisualTree());

        // With the completion list open, Quick Info doesn't open; Escape closes the list, and it opens again.
        editor.Editor.CaretOffset = editor.Model.Text.TextLength;
        editor.CompleteWords();
        await ShellFixture.PumpUntil(() => editor.CompletionWindow is { IsOpen: true });
        Assert.True(editor.IsIntelliSenseOpen);
        Assert.Equal(true, f.Shell.Workbench.Context.GetValue("suggestWidgetVisible"));
        editor.Editor.CaretOffset = editor.Model.Text.TextLength; // the caret stays in the list's text
        editor.ShowQuickInfoAtCaret();
        await Task.Delay(300, TestContext.Current.CancellationToken);
        ShellFixture.Pump();
        Assert.False(editor.IsQuickInfoOpen);
        f.Press(Key.Escape);
        await ShellFixture.PumpUntil(() => editor.CompletionWindow is null);
        Assert.Equal(false, f.Shell.Workbench.Context.GetValue("suggestWidgetVisible"));
        editor.Editor.CaretOffset = 3;
        editor.ShowQuickInfoAtCaret();
        await ShellFixture.PumpUntil(() => editor.IsQuickInfoOpen);
    }

    /// <summary>
    /// Quick Info lets the pointer through to the text, and stays open on the way to it: across the text above its top edge
    /// to its far right (where a data tip's Pin to source is), and while the pointer stays on the symbol it describes.
    /// Moving away closes it, and so do zooming and scrolling the text under it (the wheel reaches the text while it's open).
    /// </summary>
    [AvaloniaFact]
    public async Task Quick_info_stays_open_on_the_way_to_it_and_closes_when_the_pointer_leaves_or_the_text_scrolls()
    {
        await using var f = ShellFixture.Create();
        f.Window.Width = 1400;
        f.Window.Height = 900;
        var path = Path.Combine(f.UserData, "notes.txt");
        var text = string.Join("\n", Enumerable.Range(0, 200).Select(i => i == 5 ? "an alphabetical soup" : $"line {i}"));
        await File.WriteAllTextAsync(path, text, TestContext.Current.CancellationToken);
        var document = await f.Shell.OpenFileAsync(path);
        Assert.NotNull(document);
        await ShellFixture.PumpUntil(() => document.Controller is not null);
        var editor = document.Controller!;
        var word = text.IndexOf("alphabetical", StringComparison.Ordinal);
        var column = word - text.LastIndexOf('\n', word) - 1;
        f.Shell.Languages.Diagnostics.Set(path, "plaintext", null,
        [
            new Diagnostic(new LanguageServices.Protocol.Range(new Position(5, column), new Position(5, column + "alphabetical".Length)), DiagnosticSeverity.Warning, "TXT002", "notes",
                "Spelling: 'alphabetical' is spelled correctly, and this message goes on long enough to make Quick Info about as wide as it gets."),
        ]);
        await ShellFixture.PumpUntil(() => editor.Markers.Segments.Any());
        var view = editor.Editor.TextArea.TextView;

        // A point on the text (window coordinates), a document offset's character in, or lines above it.
        Point At(int offset, int linesUp = 0)
        {
            ShellFixture.RenderFrame(f.Window);
            var visual = view.GetVisualPosition(new TextViewPosition(editor.Model.Text.GetLocation(offset)), AvaloniaEdit.Rendering.VisualYPosition.LineMiddle) - view.ScrollOffset;
            return view.TranslatePoint(new Point(visual.X + 2, visual.Y - (linesUp * view.DefaultLineHeight)), f.Window)!.Value;
        }

        // The pointer rests on the word until Quick Info opens: its bounds (window coordinates).
        async Task<Rect> HoverAsync(Point at)
        {
            Assert.False(editor.IsQuickInfoOpen);
            f.Window.MouseMove(At(word, linesUp: 3)); // from elsewhere: a move to where the pointer is may not count
            ShellFixture.RenderFrame(f.Window);
            f.Window.MouseMove(at);
            await ShellFixture.PumpUntil(() => editor.IsQuickInfoOpen && editor.HoverPopup.Child is { Bounds.Width: > 0 });
            ShellFixture.Pump();
            var child = editor.HoverPopup.Child!;
            return new Rect(f.Window.PointToClient(child.PointToScreen(default)), child.Bounds.Size);
        }

        // On the way to its far right, across the text above its top edge, past the word: still open (it closed 8 pixels
        // from where it opened).
        var start = At(word + 10);
        var quickInfo = await HoverAsync(start);
        Assert.True(quickInfo.Top > start.Y && quickInfo.Width > 300, $"Quick Info at {quickInfo}, opened at {start}");
        var target = new Point(quickInfo.Right - 12, quickInfo.Top + 8);
        var furthest = 0.0;
        for (var step = 1; step <= 20; step++)
        {
            var point = start + ((target - start) * (step / 20.0));
            if (point.Y >= quickInfo.Top - 1)
            {
                break; // over Quick Info from here
            }

            ShellFixture.RenderFrame(f.Window);
            f.Window.MouseMove(point);
            ShellFixture.Pump();
            Assert.True(editor.IsQuickInfoOpen, $"closed at {point}, on the way from {start} to {target} (Quick Info at {quickInfo})");
            furthest = point.X - start.X;
        }

        Assert.True(furthest > 50, $"the way over the text went only {furthest} px right");

        // On it (its own window on this platform, or the window's overlay): open, and the pointer on it.
        var host = TopLevel.GetTopLevel(editor.HoverPopup.Child)!;
        ShellFixture.RenderFrame(host);
        host.MouseMove(host == f.Window ? target : host.PointToClient(f.Window.PointToScreen(target)));
        ShellFixture.Pump();
        Assert.True(editor.IsQuickInfoOpen);
        Assert.True(editor.HoverPopup.Child!.IsPointerOver);

        // Away from it, over the lines above: closed.
        f.Window.MouseMove(At(word + 10, linesUp: 3));
        ShellFixture.Pump();
        Assert.False(editor.IsQuickInfoOpen);

        // Along the symbol, away from Quick Info's left edge: open; off the symbol that way: closed.
        await HoverAsync(start);
        f.Window.MouseMove(At(word + 1));
        ShellFixture.Pump();
        Assert.True(editor.IsQuickInfoOpen);
        f.Window.MouseMove(At(word - 2));
        ShellFixture.Pump();
        Assert.False(editor.IsQuickInfoOpen);

        // Ctrl+wheel zooms the text under it: closed.
        await HoverAsync(start);
        f.Window.MouseWheel(start, new Vector(0, 1), Avalonia.Input.RawInputModifiers.Control);
        ShellFixture.Pump();
        Assert.True(editor.Zoom > 1);
        Assert.False(editor.IsQuickInfoOpen);
        editor.Zoom = 1;

        // The wheel scrolls the text under it: closed.
        start = At(word + 10);
        await HoverAsync(start);
        f.Window.MouseWheel(start, new Vector(0, -1));
        await ShellFixture.PumpUntil(() => view.ScrollOffset.Y > 0);
        Assert.False(editor.IsQuickInfoOpen);
    }

    [AvaloniaFact]
    public void Quick_info_lays_out_the_symbol_its_sections_once_the_diagnostics_and_their_buttons()
    {
        var description = new SymbolDescription(
            [R("wsTabHostLayoutBuilder", "class"), R(" ", "text"), R("wsLayoutBuilderBase", "class"), R("<", "punctuation"), R("MyPageModel", "class"), R(",", "punctuation"), R(" ", "text"),
             R("wpLayoutBuilder", "class"), R("<", "punctuation"), R("MyPageModel", "class"), R(">>", "punctuation"), R(".", "punctuation"), R("AddTab", "method"), R("(", "punctuation"),
             R("int", "keyword"), R(" ", "text"), R("id", "parameter"), R(",", "punctuation"), R(" ", "text"), R("Action", "delegate"), R("<", "punctuation"),
             R("wsTabPagesLayoutBuilder", "class"), R("<", "punctuation"), R("wpLayoutBuilder", "class"), R("<", "punctuation"), R("MyPageModel", "class"), R(">>>", "punctuation"),
             R(" ", "text"), R("tabPageBuilder", "parameter"), R(")", "punctuation")],
            [
                new DescriptionSection(DescriptionSectionKinds.Summary, [R("Method adds a tab.", "text")]),
                new DescriptionSection(DescriptionSectionKinds.Returns, [R("The builder, for ", "text"), R("AddTab", "method"), R("(", "punctuation"), R("string", "keyword"), R(")", "punctuation")]),
                new DescriptionSection(DescriptionSectionKinds.Exceptions, [R("ArgumentException", "class")]),
            ])
        {
            Glyph = SymbolGlyphs.Method,
            Overloads = 1,
        };
        Diagnostic[] diagnostics =
        [
            new(new LanguageServices.Protocol.Range(new Position(0, 0), new Position(0, 6)), DiagnosticSeverity.Error, "CS1501", "csharp", "No overload for method 'AddTab' takes 1 arguments")
            {
                CodeDescription = new CodeDescription("https://learn.microsoft.com/dotnet/csharp/misc/cs1501"),
            },
        ];
        var fixedDiagnostic = (Diagnostic?)null;
        var chatted = (Diagnostic?)null;
        var view = new QuickInfoView(description, null, diagnostics, EditorTheme.Light, new QuickInfoActions
        {
            Fix = d =>
            {
                fixedDiagnostic = d;
                return Task.CompletedTask;
            },
            OtherFixes = _ => Task.CompletedTask,
            AddToChat = d =>
            {
                chatted = d;
                return Task.CompletedTask;
            },
            FixKey = "Alt+.",
            OtherFixesKey = "Alt+Enter",
            OpenLink = _ => { },
        });
        var window = new Window { Width = 420, Height = 600, Content = new Border { Classes = { "intelliSense" }, MaxWidth = 400, Child = view } };
        window.Show();
        ShellFixture.Pump();
        try
        {
            // The kind's icon, then the whole colorized signature, wrapped (never scrolled or cut off) with its overload count.
            var symbol = view.Symbol!;
            Assert.Equal("symbol-method", symbol.Icon!.Kind);
            var signature = symbol.SignatureBlock!;
            Assert.EndsWith("tabPageBuilder) (+ 1 overload)", DescriptionView.TextOf(signature), StringComparison.Ordinal);
            Assert.Equal(TextWrapping.Wrap, signature.TextWrapping);
            Assert.True(signature.Bounds.Width <= 400, $"the signature is {signature.Bounds.Width} wide");
            Assert.True(signature.Bounds.Height > signature.FontSize * 1.8, "the long signature wraps onto more lines");
            Assert.Empty(view.GetVisualDescendants().OfType<ScrollViewer>());
            Assert.Equal(Color.FromUInt32(EditorTheme.Light.TokenColor("method")!.Value), ColorOf(RunOf(signature, "AddTab")));

            // Returns once: our heading, then the body, indented and colorized.
            var headings = symbol.Children.OfType<TextBlock>().Where(t => t.Classes.Contains("sectionHeading")).Select(t => t.Text).ToList();
            Assert.Equal(["Returns:", "Exceptions:"], headings);
            var returns = symbol.SectionBlocks.Single(s => s.Kind == DescriptionSectionKinds.Returns).Block;
            Assert.Equal("The builder, for AddTab(string)", DescriptionView.TextOf(returns));
            Assert.True(returns.Margin.Left > 0);
            Assert.Equal(Color.FromUInt32(EditorTheme.Light.TokenColor("method")!.Value), ColorOf(RunOf(returns, "AddTab")));
            Assert.Equal("Method adds a tab.", DescriptionView.TextOf(symbol.SectionBlocks[0].Block));

            // A separator, then the diagnostic: its code as a link to its help, then its message.
            Assert.Single(view.Children.OfType<Border>(), b => b.Classes.Contains("quickInfoSeparator"));
            var link = Assert.Single(view.DiagnosticLinks);
            Assert.Equal("CS1501", link.Content);
            Assert.Contains("link", link.Classes);
            Assert.True(link.IsEnabled);

            // The buttons, with their keys; Add to Chat while AI is on.
            Assert.Equal("Fix (Alt+.)", Avalonia.Automation.AutomationProperties.GetName(view.FixButton!));
            Assert.Equal("Other Fixes (Alt+Enter)", Avalonia.Automation.AutomationProperties.GetName(view.OtherFixesButton!));
            Assert.Equal("Add to Chat", Avalonia.Automation.AutomationProperties.GetName(view.AddToChatButton!));
            Assert.Equal("lightbulb", view.FixButton!.GetLogicalDescendants().OfType<Icon>().Single().Kind);
            view.FixButton!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Same(diagnostics[0], fixedDiagnostic);
            view.AddToChatButton!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Same(diagnostics[0], chatted);

            // AI off: no Add to Chat.
            var off = new QuickInfoView(description, null, diagnostics, EditorTheme.Light, new QuickInfoActions { Fix = _ => Task.CompletedTask });
            Assert.Null(off.AddToChatButton);
            Assert.NotNull(off.FixButton);
        }
        finally
        {
            window.Close();
        }
    }

    private const string Builders = """


        public class PageModel { }

        public class LayoutBuilderBase<TModel, TBuilder>
        {
            /// <summary>Method adds a tab.</summary>
            /// <param name="id">Id - incremental</param>
            /// <param name="tabPageBuilder">Builds the tab's page.</param>
            /// <returns>The builder, for <see cref="AddTab(string)"/> next.</returns>
            public TBuilder AddTab(int id, System.Action<TBuilder> tabPageBuilder) => default!;

            /// <summary>Method adds a tab with a title.</summary>
            public TBuilder AddTab(string title) => default!;
        }

        public class PageBuilder<TModel> : LayoutBuilderBase<TModel, PageBuilder<TModel>> { }

        public static class Usage
        {
            public static void Build(PageBuilder<PageModel> builder)
            {
                int unusedValue = 0;
                builder.AddTab(1, page => { });
            }
        }
        """;

    [AvaloniaFact]
    public async Task With_the_csharp_service_the_popups_show_the_call_sites_types_and_quick_info_fixes()
    {
        await using var f = ShellFixture.Create();
        var folder = await f.OpenLanguageFeaturesAsync();
        var document = await f.Shell.OpenFileAsync(Path.Combine(folder, "src", "App", "Program.cs"));
        Assert.NotNull(document);
        await ShellFixture.PumpUntil(() => document.Controller is not null);
        var editor = document.Controller!;
        editor.Model.Text.Insert(editor.Model.Text.TextLength, Builders);
        void CaretAt(string marker, int delta)
        {
            editor.Editor.CaretOffset = editor.Model.Text.Text.IndexOf(marker, StringComparison.Ordinal) + delta;
            editor.Editor.TextArea.Focus();
            ShellFixture.Pump();
        }

        // Signature help: the base class's type arguments as the call site has them, the active parameter bold.
        CaretAt("builder.AddTab(1", "builder.AddTab(".Length);
        var deadline = DateTime.UtcNow.AddSeconds(150);
        while (editor.SignatureWindow is not { Count: 2 })
        {
            // Asked again until the edit's analysis is in (the first request can come back before the types bind).
            editor.ShowSignatureHelp();
            try
            {
                await ShellFixture.PumpUntil(() => editor.SignatureWindow is { Count: 2 }, seconds: 15);
            }
            catch (Exception) when (DateTime.UtcNow < deadline)
            {
            }
        }

        var signature = editor.SignatureWindow!;
        await ShellFixture.PumpUntil(() => DescriptionView.TextOf(signature.SignatureBlock).Contains("AddTab(int id", StringComparison.Ordinal));
        Assert.Contains("LayoutBuilderBase<PageModel, PageBuilder<PageModel>>.AddTab(int id, ", DescriptionView.TextOf(signature.SignatureBlock), StringComparison.Ordinal);
        Assert.EndsWith("Action<PageBuilder<PageModel>> tabPageBuilder)", DescriptionView.TextOf(signature.SignatureBlock), StringComparison.Ordinal);
        Assert.Equal(FontWeight.Bold, RunOf(signature.SignatureBlock, "id").FontWeight);
        Assert.Equal(Color.FromUInt32(editor.Colorizer.Theme.TokenColor("method")!.Value), ColorOf(RunOf(signature.SignatureBlock, "AddTab")));
        Assert.Equal("2 of 2", signature.PositionText); // AddTab(string) is first: overloads by their parameter count
        Assert.Equal("id: Id - incremental", DescriptionView.TextOf(signature.ParameterBlock));

        // The completion list opens below the line while signature help moves above it; Up and Down go to the list.
        editor.ShowCompletion();
        await ShellFixture.PumpUntil(() => editor.CompletionWindow is { IsOpen: true } w && w.CompletionList.CurrentList.Count > 0, seconds: 120);
        Assert.Equal(PopupSide.Below, editor.CompletionWindow!.Side);
        Assert.Equal(PopupSide.Above, signature.Side);
        var before = editor.CompletionWindow.CompletionList.SelectedItem;
        f.Press(Key.Down);
        Assert.NotEqual(before, editor.CompletionWindow?.CompletionList.SelectedItem);
        Assert.Equal("2 of 2", signature.PositionText);

        // Quick Info doesn't open over them.
        editor.ShowQuickInfoAtCaret();
        await Task.Delay(500, TestContext.Current.CancellationToken);
        ShellFixture.Pump();
        Assert.False(editor.IsQuickInfoOpen);

        // Escape closes the list; then Up and Down go through the overloads, and Escape closes signature help.
        f.Press(Key.Escape);
        await ShellFixture.PumpUntil(() => editor.CompletionWindow is null);
        Assert.NotNull(editor.SignatureWindow);
        f.Press(Key.Down);
        Assert.Equal("1 of 2", signature.PositionText);
        f.Press(Key.Escape);
        await ShellFixture.PumpUntil(() => editor.SignatureWindow is null);

        // Quick Info on a variable with a warning: the symbol, the diagnostic's link and Fix, which applies the top fix.
        await ShellFixture.PumpUntil(() => editor.Markers.Segments.Any(s => s.Diagnostic.Code == "CS0219"), seconds: 90);
        CaretAt("unusedValue", 3);
        editor.ShowQuickInfoAtCaret();
        await ShellFixture.PumpUntil(() => editor.IsQuickInfoOpen && editor.LastQuickInfo?.DiagnosticLinks.Count > 0, seconds: 60);
        var quickInfo = editor.LastQuickInfo!;
        Assert.NotNull(quickInfo.Symbol);
        Assert.Contains("unusedValue", DescriptionView.TextOf(quickInfo.Symbol.SignatureBlock!), StringComparison.Ordinal);
        Assert.Equal("CS0219", quickInfo.DiagnosticLinks[0].Content);
        Assert.Null(quickInfo.AddToChatButton); // AI is off
        Assert.Equal($"Fix ({f.Shell.KeyLabelOf("editor.action.applyTopFix")})", Avalonia.Automation.AutomationProperties.GetName(quickInfo.FixButton!));
        Assert.Equal($"Other Fixes ({f.Shell.KeyLabelOf("editor.action.quickFix")})", Avalonia.Automation.AutomationProperties.GetName(quickInfo.OtherFixesButton!));
        quickInfo.FixButton!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await ShellFixture.PumpUntil(() => !editor.Model.Text.Text.Contains("int unusedValue = 0;", StringComparison.Ordinal), seconds: 60);
        Assert.False(editor.IsQuickInfoOpen);

        // Hovering a symbol: Returns once, without Roslyn's own heading.
        CaretAt("builder.AddTab(1", "builder.Add".Length);
        editor.ShowQuickInfoAtCaret();
        await ShellFixture.PumpUntil(() => editor.IsQuickInfoOpen && editor.LastQuickInfo?.Symbol is not null, seconds: 60);
        var hover = editor.LastQuickInfo!.Symbol!;
        Assert.EndsWith("(+ 1 overload)", DescriptionView.TextOf(hover.SignatureBlock!), StringComparison.Ordinal);
        Assert.Equal(["Returns:"], hover.Children.OfType<TextBlock>().Where(t => t.Classes.Contains("sectionHeading")).Select(t => t.Text));
        Assert.StartsWith("The builder, for ", DescriptionView.TextOf(hover.SectionBlocks.Single(s => s.Kind == DescriptionSectionKinds.Returns).Block), StringComparison.Ordinal);
    }

    private const string Commits = """


        public static class Commits
        {
            public static void Take(int amount, string text) { }

            public static void Run(int total, string title, System.Action<int> report)
            {
                var ratio = 1.5;
                Take(/*arg*/);
                /*lambda*/
                /*semi*/
                /*space*/
                /*nospace*/
                /*dot*/
            }
        }
        """;

    [AvaloniaFact]
    public async Task With_the_csharp_service_commit_characters_commit_and_the_target_type_is_preselected()
    {
        await using var f = ShellFixture.Create();
        var folder = await f.OpenLanguageFeaturesAsync();
        var document = await f.Shell.OpenFileAsync(Path.Combine(folder, "src", "App", "Program.cs"));
        Assert.NotNull(document);
        await ShellFixture.PumpUntil(() => document.Controller is not null);
        var editor = document.Controller!;
        editor.Model.Text.Insert(editor.Model.Text.TextLength, Commits);

        // Format on type would move each ";"-ended statement to a line of its own, away from its marker.
        await f.Shell.Workbench.Settings.SetAsync(EditorOptions.FormatOnTypeKey, false, Workbench.Settings.SettingScope.User, TestContext.Current.CancellationToken);
        string Line(string marker)
        {
            var text = editor.Model.Text.Text;
            var at = text.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
            var end = text.IndexOf('\n', at);
            return text[at..end].TrimEnd();
        }

        void CaretAfter(string marker, string insert = "")
        {
            var at = editor.Model.Text.Text.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
            editor.Model.Text.Insert(at, insert);
            editor.Editor.CaretOffset = at + insert.Length;
            editor.Editor.TextArea.Focus();
            ShellFixture.Pump();
        }

        // Types a name a character at a time (each asks for completion, as typing does) and waits for the list to pick it.
        async Task<CompletionPopup> TypeUntilSelected(string typed, Func<CompletionPopup, bool> ready)
        {
            foreach (var c in typed)
            {
                f.Type(c.ToString());
            }

            var deadline = DateTime.UtcNow.AddSeconds(120);
            while (true)
            {
                try
                {
                    await ShellFixture.PumpUntil(() => editor.CompletionWindow is { IsOpen: true } w && ready(w), seconds: 15);
                    return editor.CompletionWindow!;
                }
                catch (Exception) when (DateTime.UtcNow < deadline)
                {
                    editor.ShowCompletion(); // asked again until the edit's analysis is in
                }
            }
        }

        // An argument: the item of the parameter's type (int amount) is preselected, softly as nothing is typed, and the
        // target type filter shows only such items.
        CaretAfter("        Take(");
        editor.ShowCompletion();
        var argument = await TypeUntilSelected("", w => w.CompletionList.CompletionData.Any(d => d.MatchesTargetType));
        await ShellFixture.PumpUntil(() => argument.CompletionList.SelectedItem?.Label == "total");
        Assert.True(argument.CompletionList.IsSoftSelection);
        Assert.Contains(SymbolKinds.TargetTypeFilter, argument.CompletionList.FilterToggles.Keys);
        Assert.True(editor.ToggleCompletionFilter(SymbolKinds.TargetTypeFilter));
        Assert.All(argument.CompletionList.CurrentList, d => Assert.True(d.MatchesTargetType, d.Label));
        Assert.DoesNotContain(argument.CompletionList.CurrentList, d => d.Label is "title" or "ratio");
        f.Press(Key.Escape);
        await ShellFixture.PumpUntil(() => editor.CompletionWindow is null);

        // Suggestion mode (a lambda's parameter is a new name): Space commits nothing.
        CaretAfter("/*lambda*/", "report = ");
        var lambda = await TypeUntilSelected("v", w => w.CompletionList.SelectedItem is not null);
        Assert.True(lambda.CompletionList.IsSoftSelection);
        f.Type(" ");
        Assert.Equal("report = v", Line("/*lambda*/"));
        f.Type(";"); // a complete statement before the next line

        // ";" commits the selected item, then is typed.
        CaretAfter("/*semi*/", "var name = ");
        await TypeUntilSelected("tit", w => w.CompletionList.SelectedItem?.Label == "title" && !w.CompletionList.IsSoftSelection);
        f.Type(";");
        Assert.Equal("var name = title;", Line("/*semi*/"));
        Assert.Null(editor.CompletionWindow);

        // Space commits too, by default (a classic IDE's), and not once the setting is off.
        CaretAfter("/*space*/", "var other = ");
        await TypeUntilSelected("tit", w => w.CompletionList.SelectedItem?.Label == "title" && !w.CompletionList.IsSoftSelection);
        f.Type(" ");
        Assert.Equal("var other = title", Line("/*space*/"));
        f.Type(";"); // a complete statement before the next line
        await f.Shell.Workbench.Settings.SetAsync(EditorOptions.CommitOnSpaceKey, false, Workbench.Settings.SettingScope.User, TestContext.Current.CancellationToken);
        await ShellFixture.PumpUntil(() => !f.Shell.EditorOptions.For(editor.Model.LanguageId).CommitOnSpace);
        CaretAfter("/*nospace*/", "var third = ");
        await TypeUntilSelected("tit", w => w.CompletionList.SelectedItem?.Label == "title" && !w.CompletionList.IsSoftSelection);
        f.Type(" ");
        Assert.Equal("var third = tit", Line("/*nospace*/"));
        f.Type(";"); // a complete statement before the next line
        await f.Shell.Workbench.Settings.SetAsync(EditorOptions.CommitOnSpaceKey, true, Workbench.Settings.SettingScope.User, TestContext.Current.CancellationToken);

        // "." after a type gives "Type."; "(" after a method gives "Name(" (its closing parenthesis added after it).
        CaretAfter("/*dot*/");
        await TypeUntilSelected("Consol", w => w.CompletionList.SelectedItem?.Label == "Console" && !w.CompletionList.IsSoftSelection);
        f.Type(".");
        Assert.Equal("Console.", Line("/*dot*/"));
        await TypeUntilSelected("WriteLi", w => w.CompletionList.SelectedItem?.Label == "WriteLine" && !w.CompletionList.IsSoftSelection);
        f.Type("(");
        Assert.StartsWith("Console.WriteLine(", Line("/*dot*/"), StringComparison.Ordinal);
        Assert.Equal("Console.WriteLine(".Length, editor.Editor.CaretOffset - (editor.Model.Text.Text.IndexOf("/*dot*/", StringComparison.Ordinal) + "/*dot*/".Length));
    }
}
