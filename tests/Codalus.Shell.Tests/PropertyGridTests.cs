using Avalonia.Headless.XUnit;
using Codalus.Shell.ViewModels;

namespace Codalus.Shell.Tests;

/// <summary>
/// #68 (D41) on every OS, without a designer: the property grid's model. Categories collapse and rows expand into their
/// parts in the flattened list the view shows; Alphabetical is one list without headers, sorted as the classic IDE
/// sorts; a refresh keeps the rows (and their expansion, the collapsed categories and the selection) by path; Reset is
/// the host's CanReset; a double-click cycles a choice; colors are parsed by name, system names included.
/// </summary>
public sealed class PropertyGridTests
{
    private static readonly List<string> s_commits = [];

    private static PropertyRowViewModel Row(string name, string value = "", PropertyRowKind kind = PropertyRowKind.Text, string? path = null, IReadOnlyList<PropertyRowViewModel>? children = null, string[]? options = null, bool isSet = false)
    {
        var p = path ?? name;
        return new PropertyRowViewModel(name, value, kind, $"The {name}.", text =>
        {
            s_commits.Add($"{p}={text}");
            return Task.CompletedTask;
        }, options?.Select(o => new PropertyOption(o, o)).ToList(), isSet)
        {
            Path = p,
            Children = children ?? [],
        };
    }

    private static PropertyRowViewModel Font(string bold = "False", string size = "9") => Row("Font", $"Segoe UI, {size}pt", PropertyRowKind.Font, children:
    [
        Row("Name", "Segoe UI", PropertyRowKind.Choice, "Font.Name", options: ["Arial", "Segoe UI"]),
        Row("Size", size, path: "Font.Size"),
        Row("Bold", bold, PropertyRowKind.Choice, "Font.Bold", options: ["True", "False"]),
    ]);

    private static PropertyRowViewModel Location(string x = "96") => Row("Location", $"{x}, 70", path: "Location", isSet: true, children: [Row("X", x, path: "Location.X"), Row("Y", "70", path: "Location.Y")]);

    private static List<PropertyCategoryViewModel> Categories(string bold = "False", string x = "96", bool withPadding = false) =>
    [
        new("Accessibility", [Row("AccessibleName"), Row("AccessibleRole", "Default", PropertyRowKind.Choice, options: ["Default", "Alert"])]),
        new("Appearance", [Row("BackColor", "Control", PropertyRowKind.Color), Font(bold), Row("Text", "OK", isSet: true)]),
        new("Design", [Row("(Name)", "giftCheckBox")]),
        new("Layout", withPadding ? [Location(x), Row("Padding", "0, 0, 0, 0")] : [Location(x)]),
    ];

    private static List<string> Names(PropertyGridViewModel grid) =>
        [.. grid.Items.Select(i => i switch { PropertyCategoryViewModel c => "#" + c.Title, PropertyRowViewModel r => new string(' ', r.Depth * 2) + r.Name, _ => "?" })];

    [AvaloniaFact]
    public void Categories_collapse_and_rows_expand_into_their_parts()
    {
        var grid = new PropertyGridViewModel();
        grid.Show(Categories(), alphabetical: false);
        Assert.Equal(["#Accessibility", "AccessibleName", "AccessibleRole", "#Appearance", "BackColor", "Font", "Text", "#Design", "(Name)", "#Layout", "Location"], Names(grid));
        Assert.True(grid.Find("Font")!.IsExpandable);
        Assert.False(grid.Find("Text")!.IsExpandable);

        // A category collapses and expands again.
        var accessibility = grid.Categories[0];
        grid.Toggle(accessibility);
        Assert.Equal("chevron-right", accessibility.Chevron);
        Assert.Equal(["#Accessibility", "#Appearance", "BackColor", "Font", "Text", "#Design", "(Name)", "#Layout", "Location"], Names(grid));
        grid.Toggle(accessibility);
        Assert.Equal("chevron-down", accessibility.Chevron);
        Assert.Equal(11, grid.Items.Count);

        // A row's parts come right after it, a level down; collapsing it removes them all.
        var font = grid.Find("Font")!;
        Assert.Equal("Expand Font", font.ExpanderName);
        grid.Toggle(font);
        Assert.Equal(["#Accessibility", "AccessibleName", "AccessibleRole", "#Appearance", "BackColor", "Font", "  Name", "  Size", "  Bold", "Text", "#Design", "(Name)", "#Layout", "Location"], Names(grid));
        Assert.Equal("Collapse Font", font.ExpanderName);
        Assert.Equal(1, grid.Find("Font.Bold")!.Depth);
        Assert.Equal(16 + 4, grid.Find("Font.Bold")!.NameIndent.Left);
        grid.Toggle(font);
        Assert.DoesNotContain(grid.Items, i => i is PropertyRowViewModel { Depth: > 0 });

        // A grandchild goes with its parent's parent.
        var nested = new PropertyGridViewModel();
        nested.Show([new("Appearance", [Row("FlatAppearance", "", children: [Row("Border", "", path: "FlatAppearance.Border", children: [Row("Size", "1", path: "FlatAppearance.Border.Size")])])])], alphabetical: false);
        nested.Toggle(nested.Find("FlatAppearance")!);
        nested.Toggle(nested.Find("FlatAppearance.Border")!);
        Assert.Equal(["#Appearance", "FlatAppearance", "  Border", "    Size"], Names(nested));
        nested.Toggle(nested.Find("FlatAppearance")!);
        Assert.Equal(["#Appearance", "FlatAppearance"], Names(nested));
    }

    [AvaloniaFact]
    public void Alphabetical_is_one_list_sorted_as_the_classic_ide_sorts()
    {
        var grid = new PropertyGridViewModel();
        grid.Show(Categories(), alphabetical: true);
        Assert.Single(grid.Categories);
        Assert.False(grid.Categories[0].ShowHeader);

        // Ordinal, ignoring case: "(" before every letter, so (Name) leads; parts keep the converter's order.
        Assert.Equal(["(Name)", "AccessibleName", "AccessibleRole", "BackColor", "Font", "Location", "Text"], Names(grid));
        grid.Toggle(grid.Find("Font")!);
        Assert.Equal(["(Name)", "AccessibleName", "AccessibleRole", "BackColor", "Font", "  Name", "  Size", "  Bold", "Location", "Text"], Names(grid));

        // Back to categories: the expansion stays.
        grid.Show(Categories(), alphabetical: false);
        Assert.Contains("  Bold", Names(grid));
        Assert.Equal(4, grid.Categories.Count);
    }

    [AvaloniaFact]
    public void A_refresh_keeps_the_rows_their_expansion_and_the_selection()
    {
        var grid = new PropertyGridViewModel();
        grid.Show(Categories(), alphabetical: false);
        var font = grid.Find("Font")!;
        var bold = grid.Find("Font.Bold")!;
        grid.Toggle(font);
        grid.Toggle(grid.Find("Location")!);
        grid.Toggle(grid.Categories.Single(c => c.Title == "Accessibility"));
        grid.SelectedRow = bold;
        Assert.True(bold.IsSelected);
        Assert.Equal("Bold", grid.DescriptionTitle);
        Assert.Equal("The Bold.", grid.Description);

        // The same paths after a commit: the same instances, with the new values; a new row arrives, a part goes.
        var fresh = Categories(bold: "True", x: "100", withPadding: true);
        fresh[3] = new PropertyCategoryViewModel("Layout", [Row("Location", "100, 70", path: "Location", isSet: true, children: [Row("X", "100", path: "Location.X")]), fresh[3].Rows[1]]);
        grid.Show(fresh, alphabetical: false);
        Assert.Same(font, grid.Find("Font"));
        Assert.Same(bold, grid.Find("Font.Bold"));
        Assert.Equal("True", bold.Value);
        Assert.Equal("True", bold.Text);
        Assert.Equal("100, 70", grid.Find("Location")!.Value);
        Assert.Null(grid.Find("Location.Y"));
        Assert.NotNull(grid.Find("Padding"));
        Assert.Same(bold, grid.SelectedRow);
        Assert.True(bold.IsSelected);
        Assert.Equal(["#Accessibility", "#Appearance", "BackColor", "Font", "  Name", "  Size", "  Bold", "Text", "#Design", "(Name)", "#Layout", "Location", "  X", "Padding"], Names(grid));

        // Another selection with the same paths (another control): expanded rows and collapsed categories stay so.
        grid.Show(Categories(), alphabetical: false);
        Assert.True(grid.Find("Font")!.IsExpanded);
        Assert.False(grid.Categories.Single(c => c.Title == "Accessibility").IsExpanded);

        // The selected part's parent collapses: the selection goes to the parent, and stays there after a refresh.
        grid.Toggle(grid.Find("Font")!);
        Assert.Same(grid.Find("Font"), grid.SelectedRow);
        grid.SelectedRow = grid.Find("Location.X");
        grid.Toggle(grid.Find("Location")!);
        grid.Show(Categories(), alphabetical: false);
        Assert.Equal("Location", grid.SelectedRow?.Path);

        // A row whose kind changes is replaced, not updated.
        var text = grid.Find("Text")!;
        grid.Show([new("Appearance", [Row("Text", "a\r\nb", PropertyRowKind.Multiline)])], alphabetical: false);
        Assert.NotSame(text, grid.Find("Text"));
        Assert.True(grid.Find("Text")!.HasLineBreaks);
        Assert.Equal("a ⏎ b", grid.Find("Text")!.DisplayValue);
    }

    [AvaloniaFact]
    public void A_refresh_keeps_the_text_being_typed_but_not_what_was_committed()
    {
        var grid = new PropertyGridViewModel();
        grid.Show(Categories(), alphabetical: false);
        var text = grid.Find("Text")!;
        text.IsEditing = true;
        s_commits.Clear();
        text.Text = "Hello";
        Assert.Equal(["Text=Hello"], s_commits);
        grid.Show([new("Appearance", [Row("Text", "Hello")])], alphabetical: false);
        Assert.Equal("Hello", text.Text);

        // While the box has the focus, a value changed elsewhere doesn't replace what it shows; Esc shows the new value.
        var typed = new PropertyGridViewModel();
        typed.Show(Categories(), alphabetical: false);
        var row = typed.Find("Text")!;
        row.IsEditing = true;
        typed.Show([new("Appearance", [Row("Text", "Changed")])], alphabetical: false);
        Assert.Equal("Changed", row.Value);
        Assert.Equal("OK", row.Text);
        row.RevertText();
        Assert.Equal("Changed", row.Text);

        // Without the focus, a refresh shows the new value.
        row.IsEditing = false;
        typed.Show([new("Appearance", [Row("Text", "Again")])], alphabetical: false);
        Assert.Equal("Again", row.Text);
    }

    [AvaloniaFact]
    public void Keys_move_expand_and_collapse()
    {
        var grid = new PropertyGridViewModel();
        grid.Show(Categories(), alphabetical: false);
        grid.SelectedRow = grid.Find("Font");
        grid.MoveRight();
        Assert.True(grid.Find("Font")!.IsExpanded);
        grid.MoveRight();
        Assert.Equal("Font.Name", grid.SelectedRow?.Path);
        grid.MoveLeft();
        Assert.Equal("Font", grid.SelectedRow?.Path);
        grid.MoveLeft();
        Assert.False(grid.Find("Font")!.IsExpanded);

        // Down crosses a header; Left on a property goes to its category's header, which then collapses.
        grid.SelectedRow = grid.Find("Text");
        grid.MoveSelection(1);
        Assert.Same(grid.Categories.Single(c => c.Title == "Design"), grid.SelectedItem);
        Assert.Equal("Design", grid.DescriptionTitle);
        grid.MoveSelection(1);
        Assert.Equal("(Name)", grid.SelectedRow?.Name);
        grid.MoveLeft();
        Assert.Same(grid.Categories.Single(c => c.Title == "Design"), grid.SelectedItem);
        grid.MoveLeft();
        Assert.False(grid.Categories.Single(c => c.Title == "Design").IsExpanded);
        grid.MoveRight();
        Assert.True(grid.Categories.Single(c => c.Title == "Design").IsExpanded);
    }

    [AvaloniaFact]
    public void Reset_follows_the_hosts_can_reset_and_a_double_click_cycles_a_choice()
    {
        var resets = 0;
        Task Reset()
        {
            resets++;
            return Task.CompletedTask;
        }

        var text = new PropertyRowViewModel("Text", "OK", PropertyRowKind.Multiline, string.Empty, _ => Task.CompletedTask, isSet: true) { Reset = Reset, CanReset = true };
        Assert.True(text.ResetCommand.CanExecute(null));
        text.ResetCommand.Execute(null);
        Assert.Equal(1, resets);

        // (Name) is set, but the host can't reset it; a row without a Reset (the Solution Explorer's) can't either.
        var name = new PropertyRowViewModel("(Name)", "giftCheckBox", PropertyRowKind.Text, string.Empty, _ => Task.CompletedTask, isSet: true) { Reset = Reset, CanReset = false };
        Assert.False(name.ResetCommand.CanExecute(null));
        var file = new PropertyRowViewModel("Custom Tool", "Gen", PropertyRowKind.Text, string.Empty, _ => Task.CompletedTask, isSet: true) { CanReset = true };
        Assert.False(file.ResetCommand.CanExecute(null));

        // A bool cycles from True to False; an enum wraps around.
        s_commits.Clear();
        Row("Enabled", "True", PropertyRowKind.Choice, options: ["True", "False"]).DoubleClick();
        Row("FlatStyle", "System", PropertyRowKind.Choice, options: ["Flat", "Popup", "Standard", "System"]).DoubleClick();
        Assert.Equal(["Enabled=False", "FlatStyle=Flat"], s_commits);

        // Every settable editor but an exclusive choice and a picker is typed in the cell.
        Assert.All(new[] { PropertyRowKind.Multiline, PropertyRowKind.ShortcutKeys, PropertyRowKind.Date, PropertyRowKind.Font, PropertyRowKind.Color, PropertyRowKind.Text },
            kind => Assert.True(Row("X", "1", kind).IsTextEditor, kind.ToString()));
        Assert.False(Row("Enabled", "True", PropertyRowKind.Choice, options: ["True", "False"]).IsTextEditor);
        Assert.True(new PropertyRowViewModel("Cursor", "Default", PropertyRowKind.Choice, string.Empty, _ => Task.CompletedTask, [new("Default", "Default")]) { ChoicesEditable = true }.IsTextEditor);
        Assert.False(new PropertyRowViewModel("Size", "1", PropertyRowKind.Text, string.Empty).IsTextEditor);

        // An exclusive choice shows "(none)" for an empty value it doesn't list; an editable one doesn't add it.
        Assert.Contains(Row("AcceptButton", string.Empty, PropertyRowKind.Choice, options: ["cancelButton"]).Options, o => o.Label == "(none)");
        Assert.DoesNotContain(new PropertyRowViewModel("Cursor", "Hand2", PropertyRowKind.Choice, string.Empty, _ => Task.CompletedTask, [new("Default", "Default")]) { ChoicesEditable = true }.Options, o => o.Value == "Hand2");
    }

    [AvaloniaFact]
    public void A_rejected_value_reaches_the_grid()
    {
        var grid = new PropertyGridViewModel();
        var x = new PropertyRowViewModel("X", "96", PropertyRowKind.Text, string.Empty, text => Task.FromResult<string?>(text == "abc" ? "'abc' is not a valid value for Int32." : null)) { Path = "Location.X" };
        grid.Show([new("Layout", [x])], alphabetical: false);
        PropertyValueRejectedEventArgs? rejected = null;
        grid.ValueRejected += (_, e) => rejected = e;
        x.Text = "12";
        Assert.Null(rejected);
        x.Text = "abc";
        Assert.NotNull(rejected);
        Assert.Same(x, rejected!.Row);
        Assert.Equal("abc", rejected.Text);
        Assert.Contains("Int32", rejected.Error, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void Colors_are_parsed_by_name_numbers_and_system_names()
    {
        Assert.NotNull(PropertyRowViewModel.ParseColor("GrayText"));
        Assert.NotNull(PropertyRowViewModel.ParseColor("Control"));
        Assert.Equal(Avalonia.Media.Color.FromRgb(0x46, 0x82, 0xB4), PropertyRowViewModel.ParseColor("SteelBlue"));
        Assert.Equal(Avalonia.Media.Color.FromRgb(255, 128, 0), PropertyRowViewModel.ParseColor("255, 128, 0"));
        Assert.Equal(Avalonia.Media.Color.FromRgb(255, 128, 0), PropertyRowViewModel.ParseColor("#FF8000"));
        Assert.Null(PropertyRowViewModel.ParseColor("Nope"));
        Assert.Null(PropertyRowViewModel.ParseColor(string.Empty));
        Assert.NotNull(Row("ForeColor", "GrayText", PropertyRowKind.Color).Swatch);
    }
}
