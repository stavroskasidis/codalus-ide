using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Codalus.Shell.ViewModels;
using Codalus.Shell.Views;
using Codalus.Shell.Views.PropertyEditors;

namespace Codalus.Shell.Tests;

/// <summary>
/// #68 (D41) on every OS, in the real main window: the Properties window's grid fed by a designer that isn't one (a
/// fake <see cref="IDesignerPropertySource"/>), with keys pressed on the window so its key bindings run first. F4 and
/// Alt+Down open the selected row's drop-down in the grid (F4 elsewhere still shows the window); the arrows move,
/// expand and collapse; Enter commits and keeps the keyboard in the grid; Esc reverts; each row has one editor and no
/// drop-down is made before one opens; the toolbar is in the classic IDE's order with Property Pages grayed for a
/// component, and the component selector lists the form first.
/// </summary>
public sealed class PropertyGridViewTests
{
    /// <summary>A designer's selection without a designer: rows made again on each read, values changed by commits.</summary>
    internal sealed class FakeSource : IDesignerPropertySource
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal)
        {
            ["Text"] = "OK",
            ["Enabled"] = "True",
            ["Style"] = "Bold",
            ["Font"] = "Segoe UI, 9pt",
            ["Font.Name"] = "Segoe UI",
            ["Font.Bold"] = "False",
            ["Dock"] = "None",
            ["TabIndex"] = "3",
            ["ForeColor"] = "SteelBlue",
            ["TextAlign"] = "MiddleCenter",
            ["Notes"] = "first",
            ["ShortcutKeys"] = "Ctrl+N",
            ["Value"] = "10/21/2026 15:38:00",
            ["Cursor"] = "Default",
        };

        public List<string> Commits { get; } = [];

        /// <summary>A read-only row's value (a file's Full Path), shown in Misc when set.</summary>
        public string? ReadOnlyPath { get; set; }

        public string PropertiesTitle => "button1 System.Windows.Forms.Button";

        public bool HasEvents => true;

        public string? Selected { get; set; } = "button1";

        public bool HasOpenEditor { get; set; }

        public IReadOnlyList<DesignerComponentChoice> ComponentChoices { get; } =
            [new("Form1", "System.Windows.Forms.Form"), new("button1", "System.Windows.Forms.Button"), new("textBox1", "System.Windows.Forms.TextBox")];

        public string? SelectedComponent => Selected;

        public void SelectComponent(string name)
        {
            Selected = name;
            Changed();
        }

        public event EventHandler? PropertiesChanged;

        public void Changed() => PropertiesChanged?.Invoke(this, EventArgs.Empty);

        private Func<string?, Task> Commit(string path) => text =>
        {
            Commits.Add($"{path}={text}");
            _values[path] = text ?? string.Empty;
            Changed();
            return Task.CompletedTask;
        };

        private PropertyRowViewModel Row(string path, PropertyRowKind kind, string[]? options = null, IReadOnlyList<PropertyRowViewModel>? children = null) =>
            new(path[(path.LastIndexOf('.') + 1)..], _values[path], kind, $"The {path}.", Commit(path), options?.Select(o => new PropertyOption(o, o)).ToList(), isSet: path == "Text")
            {
                Path = path,
                Children = children ?? [],
            };

        public Task<IReadOnlyList<DesignerPropertyRow>> PropertyRowsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<DesignerPropertyRow>>(
        [
            new("Appearance", Row("Text", PropertyRowKind.Text)),
            new("Appearance", Row("Style", PropertyRowKind.Flags, ["Bold", "Italic", "Underline"])),
            new("Appearance", Row("Font", PropertyRowKind.Font, children:
            [
                Row("Font.Name", PropertyRowKind.Choice, ["Arial", "Segoe UI"]),
                Row("Font.Bold", PropertyRowKind.Choice, ["True", "False"]),
            ])),
            new("Behavior", Row("Enabled", PropertyRowKind.Choice, ["True", "False"])),
            new("Behavior", Row("TabIndex", PropertyRowKind.Text)),
            new("Layout", Row("Dock", PropertyRowKind.Dock, ["None", "Top", "Bottom", "Left", "Right", "Fill"])),
            new("Appearance", Row("ForeColor", PropertyRowKind.Color, ["Control", "GrayText", "Red", "SteelBlue", "Transparent", "White"])),
            new("Appearance", Row("TextAlign", PropertyRowKind.ContentAlignment, ["TopLeft", "TopCenter", "TopRight", "MiddleLeft", "MiddleCenter", "MiddleRight", "BottomLeft", "BottomCenter", "BottomRight"])),
            new("Appearance", Row("Notes", PropertyRowKind.Multiline)),
            new("Misc", Row("ShortcutKeys", PropertyRowKind.ShortcutKeys, ["None", "N", "S", "F4"])),
            new("Behavior", Row("Value", PropertyRowKind.Date)),
            new("Appearance", new PropertyRowViewModel("Cursor", _values["Cursor"], PropertyRowKind.Choice, "The Cursor.", Commit("Cursor"), [new("Default", "Default"), new("Hand", "Hand"), new("IBeam", "IBeam")]) { ChoicesEditable = true, IsCursor = true }),
            .. ReadOnlyPath is { } path ? [new DesignerPropertyRow("Misc", new PropertyRowViewModel("Full Path", path, PropertyRowKind.ReadOnly, "The file's path."))] : Array.Empty<DesignerPropertyRow>(),
        ]);

        public Task<IReadOnlyList<DesignerPropertyRow>> EventRowsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<DesignerPropertyRow>>([]);
    }

    internal static async Task<(ShellFixture Fixture, FakeSource Source, PropertiesWindowView View)> ShowAsync()
    {
        var f = ShellFixture.Create();
        var source = new FakeSource();
        f.Shell.Layout.ShowToolWindow(PropertiesWindowViewModel.ToolWindowId, focus: false);
        f.Shell.PropertySource = source;
        await ShellFixture.PumpUntil(() => f.Shell.PropertiesWindow.Grid.Find("Text") is not null && f.Window.GetVisualDescendants().OfType<PropertiesWindowView>().Any(v => v.IsEffectivelyVisible));
        var view = f.Window.GetVisualDescendants().OfType<PropertiesWindowView>().First(v => v.IsEffectivelyVisible);
        await ShellFixture.PumpUntil(() => Container(view, f.Shell.PropertiesWindow.Grid.Find("Text")!) is not null);
        return (f, source, view);
    }

    internal static ItemsControl Rows(PropertiesWindowView view) => view.Grid.GetVisualDescendants().OfType<ItemsControl>().First(i => i.Name == "Rows");

    internal static Control? Container(PropertiesWindowView view, object item) => Rows(view).ContainerFromItem(item);

    internal static TextBox? Editor(PropertiesWindowView view, PropertyRowViewModel row) =>
        Container(view, row)?.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(t => t.Classes.Contains("editor"));

    [AvaloniaFact]
    public async Task F4_and_Alt_Down_open_the_drop_down_in_the_grid_and_F4_elsewhere_shows_the_window()
    {
        var (f, _, view) = await ShowAsync();
        await using var _f = f;
        var grid = f.Shell.PropertiesWindow.Grid;
        var style = grid.Find("Style")!;
        Assert.Empty(view.Grid.MadeDropDowns);
        Editor(view, style)!.Focus();
        ShellFixture.Pump();
        Assert.Equal("properties", f.Shell.Workbench.Context.GetValue("focusedView"));
        Assert.Same(style, grid.SelectedRow);

        f.Press(Key.F4);
        await ShellFixture.PumpUntil(() => view.Grid.OpenDropDownControl is FlagsDropDown);
        Assert.Single(view.Grid.MadeDropDowns);
        f.Press(Key.Escape);
        await ShellFixture.PumpUntil(() => view.Grid.OpenDropDownControl is null);

        f.Press(Key.Down, RawInputModifiers.Alt);
        await ShellFixture.PumpUntil(() => view.Grid.OpenDropDownControl is FlagsDropDown);
        f.Press(Key.Escape);
        await ShellFixture.PumpUntil(() => view.Grid.OpenDropDownControl is null);

        // Elsewhere F4 still shows the Properties window.
        f.Shell.Layout.CloseToolWindow(PropertiesWindowViewModel.ToolWindowId);
        f.Shell.Layout.ShowToolWindow("solutionExplorer", focus: true);
        await ShellFixture.PumpUntil(() => f.Shell.Workbench.Context.GetValue("focusedView") is "solutionExplorer");
        f.Press(Key.F4);
        await ShellFixture.PumpUntil(() => f.Shell.Layout.IsVisible(PropertiesWindowViewModel.ToolWindowId));
    }

    [AvaloniaFact]
    public async Task The_arrows_move_expand_and_collapse_and_Enter_commits_keeping_the_keyboard()
    {
        var (f, source, view) = await ShowAsync();
        await using var _f = f;
        var grid = f.Shell.PropertiesWindow.Grid;

        // The grid has the keyboard on Font: Right expands it, Left collapses it.
        grid.SelectedRow = grid.Find("Font");
        view.Grid.FocusPrimary();
        ShellFixture.Pump();
        view.Grid.GetVisualDescendants().OfType<ItemsControl>().First(i => i.Name == "Rows").Focus();
        ShellFixture.Pump();
        f.Press(Key.Right);
        Assert.True(grid.Find("Font")!.IsExpanded);
        Assert.Contains(grid.Find("Font.Bold")!, grid.Items);
        f.Press(Key.Left);
        Assert.False(grid.Find("Font")!.IsExpanded);

        // Down from the last Appearance row crosses Behavior's header.
        grid.SelectedRow = grid.Find("TextAlign");
        f.Press(Key.Down);
        Assert.Same(grid.Categories.Single(c => c.Title == "Behavior"), grid.SelectedItem);
        f.Press(Key.Down);
        Assert.Equal("Enabled", grid.SelectedRow?.Name);

        // Typing in Text and Enter commits; the box keeps the keyboard; Up goes to the row above's editor.
        var text = grid.Find("Text")!;
        var box = Editor(view, text)!;
        box.Focus();
        box.SelectAll();
        f.Type("Hello");
        f.Press(Key.Enter);
        await ShellFixture.PumpUntil(() => source.Commits.Contains("Text=Hello") && grid.Find("Text")!.Value == "Hello");
        Assert.Same(text, grid.Find("Text"));
        Assert.True(Editor(view, text)!.IsFocused);
        f.Press(Key.Up);
        await ShellFixture.PumpUntil(() => grid.SelectedRow?.Name == "Style" && Editor(view, grid.SelectedRow!) is { IsFocused: true });

        // Esc puts the value back in the box, nothing committed.
        var style = Editor(view, grid.Find("Style")!)!;
        style.SelectAll();
        f.Type("Nope");
        Assert.Equal("Nope", style.Text);
        f.Press(Key.Escape);
        Assert.Equal("Bold", style.Text);
        Assert.DoesNotContain(source.Commits, c => c.StartsWith("Style=", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public async Task Each_row_has_one_editor_its_expander_and_no_drop_down_until_one_opens()
    {
        var (f, source, view) = await ShowAsync();
        await using var _f = f;
        var grid = f.Shell.PropertiesWindow.Grid;
        foreach (var row in grid.Items.OfType<PropertyRowViewModel>())
        {
            var cell = Container(view, row)!.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("valueCell"));
            var editors = cell.GetVisualDescendants().OfType<Control>().Where(c => c.Classes.Contains("editor")).ToList();
            Assert.True(editors.Count == 1, $"{row.Name}: {editors.Count} editors");
            Assert.Equal(row.ShowsTextBox, editors[0] is TextBox);
        }

        var expander = Container(view, grid.Find("Font")!)!.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("expander"));
        Assert.Equal("Expand Font", Avalonia.Automation.AutomationProperties.GetName(expander));
        Assert.False(Container(view, grid.Find("Text")!)!.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("expander")).IsVisible);
        Assert.Empty(view.Grid.MadeDropDowns);
        Assert.Empty(f.Window.GetVisualDescendants().OfType<IPropertyDropDown>());

        // Only the selected row shows its arrow; a choice's list commits the pick.
        var enabled = grid.Find("Enabled")!;
        bool ArrowShows(PropertyRowViewModel row) => Container(view, row)!.GetVisualDescendants().OfType<Button>().Any(b => b.Classes.Contains("dropDown") && b.IsEffectivelyVisible);
        Assert.False(ArrowShows(enabled));
        grid.SelectedRow = enabled;
        ShellFixture.Pump();
        Assert.True(ArrowShows(enabled));
        Assert.False(ArrowShows(grid.Find("Dock")!));
        grid.Toggle(grid.Find("Font")!);
        NavigationDepthTests.Screenshot(f, "m68-properties-grid");
        view.Grid.OpenDropDown(enabled);
        var list = Assert.IsType<ChoiceDropDown>(view.Grid.OpenDropDownControl);
        list.List.SelectedItem = enabled.Options.Single(o => o.Value == "False");
        list.Pick();
        await ShellFixture.PumpUntil(() => source.Commits.Contains("Enabled=False") && view.Grid.OpenDropDownControl is null);

        // A double-click on a bool's value cycles it.
        grid.Find("Enabled")!.DoubleClick();
        await ShellFixture.PumpUntil(() => source.Commits.Contains("Enabled=True"));
    }

    [AvaloniaFact]
    public async Task The_toolbar_is_the_classic_ides_and_the_selector_lists_the_form_first()
    {
        var (f, source, view) = await ShowAsync();
        await using var _f = f;
        var properties = f.Shell.PropertiesWindow;
        var toolbar = view.GetVisualDescendants().OfType<Codalus.Shell.Controls.ToolbarPanel>().First();
        var names = toolbar.Children.Where(c => c.IsVisible).Select(c => c is Border ? "|" : Avalonia.Automation.AutomationProperties.GetName(c)).ToList();
        Assert.Equal(["Categorized", "Alphabetical", "|", "Properties", "Events", "|", "Property Pages"], names);
        Assert.False(properties.PropertyPagesCommand.CanExecute(null));

        Assert.Equal(["Form1", "button1", "textBox1"], properties.ComponentChoices.Select(c => c.Name));
        Assert.Equal("button1", properties.SelectedComponent?.Name);
        properties.SelectedComponent = properties.ComponentChoices[2];
        Assert.Equal("textBox1", source.Selected);

        // A multi-selection: blank. An editor panel open over the designer: no input.
        source.Selected = null;
        source.HasOpenEditor = true;
        source.Changed();
        await ShellFixture.PumpUntil(() => properties.SelectedComponent is null && !properties.IsInputEnabled && !properties.Grid.IsEnabled);
        source.HasOpenEditor = false;
        source.Changed();
        await ShellFixture.PumpUntil(() => properties.IsInputEnabled && properties.Grid.IsEnabled);
    }

    [AvaloniaFact]
    public async Task The_drop_downs_edit_colors_alignment_text_keys_dates_and_cursors()
    {
        var (f, source, view) = await ShowAsync();
        await using var _f = f;
        var grid = f.Shell.PropertiesWindow.Grid;
        Assert.Empty(f.Window.GetVisualDescendants().OfType<ColorDropDown>());

        // F4 on a color: its drop-down, on the tab of its value; a Web color commits its name.
        var fore = grid.Find("ForeColor")!;
        Editor(view, fore)!.Focus();
        f.Press(Key.F4);
        var color = Assert.IsType<ColorDropDown>(view.Grid.OpenDropDownControl);
        Assert.Equal(PropertyColors.WebTab, color.Tab);
        Assert.DoesNotContain(color.WebColors, c => c.Name == "Control");
        color.SelectTab(PropertyColors.SystemTab);
        Assert.Equal(["Control", "GrayText"], color.SystemColors.Select(c => c.Name));
        color.SelectTab(PropertyColors.WebTab);
        color.PickNamed("Red");
        await ShellFixture.PumpUntil(() => source.Commits.Contains("ForeColor=Red") && view.Grid.OpenDropDownControl is null);

        // A custom slot: Define Color in the grid's place, OK commits "R, G, B" and keeps it in the slot for the session.
        view.Grid.OpenDropDown(grid.Find("ForeColor")!);
        color = Assert.IsType<ColorDropDown>(view.Grid.OpenDropDownControl);
        color.SelectTab(PropertyColors.CustomTab);
        color.Define(0);
        Assert.True(color.IsDefining);
        Assert.Equal("Custom color 1", color.SlotText); // panel (d): the slot's name beside "Define Color"
        color.SetChannels(Avalonia.Media.Color.FromRgb(255, 128, 0));
        Assert.True(color.TrackColor is { R: 255, G: >= 126 and <= 129, B: 0 }, color.TrackColor.ToString()); // the brightness bar: black, the color, white
        NavigationDepthTests.Screenshot(f, "m68-define-color");
        color.AcceptDefine();
        await ShellFixture.PumpUntil(() => source.Commits.Contains("ForeColor=255, 128, 0") && view.Grid.OpenDropDownControl is null);
        Assert.Equal(Avalonia.Media.Color.FromRgb(255, 128, 0), grid.CustomColors[0]);
        Assert.Single(view.Grid.MadeDropDowns, d => d.Value is ColorDropDown);

        // ContentAlignment: the 3×3 picker.
        view.Grid.OpenDropDown(grid.Find("TextAlign")!);
        Assert.IsType<ContentAlignmentDropDown>(view.Grid.OpenDropDownControl).Pick("MiddleRight");
        await ShellFixture.PumpUntil(() => source.Commits.Contains("TextAlign=MiddleRight"));

        // Multi-line text: committed with CRLF when it closes; Esc cancels.
        view.Grid.OpenDropDown(grid.Find("Notes")!);
        var multiline = Assert.IsType<MultilineTextDropDown>(view.Grid.OpenDropDownControl);
        Assert.Equal(["Enter", "Ctrl+Enter", "Esc"], multiline.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("kbd")).Select(b => ((TextBlock)b.Child!).Text));
        multiline.Box.Text = "a\nb";
        view.Grid.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.Popup>().Single(p => p.Name == "DropDown").IsOpen = false;
        await ShellFixture.PumpUntil(() => source.Commits.Contains("Notes=a\r\nb"));
        view.Grid.OpenDropDown(grid.Find("Notes")!);
        multiline = Assert.IsType<MultilineTextDropDown>(view.Grid.OpenDropDownControl);
        ShellFixture.Pump();
        multiline.Box.Focus();
        multiline.Box.Text = "discarded";
        f.Press(Key.Escape);
        await ShellFixture.PumpUntil(() => view.Grid.OpenDropDownControl is null);
        Assert.DoesNotContain(source.Commits, c => c.Contains("discarded", StringComparison.Ordinal));

        // Shortcut keys: modifiers and a key, committed when it closes.
        view.Grid.OpenDropDown(grid.Find("ShortcutKeys")!);
        var keys = Assert.IsType<ShortcutKeysDropDown>(view.Grid.OpenDropDownControl);
        Assert.Equal("Ctrl+N", keys.Composed);
        keys.Set(ctrl: true, shift: true, alt: false, "S");
        Assert.Equal("Ctrl+Shift+S", keys.Composed);
        view.Grid.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.Popup>().Single(p => p.Name == "DropDown").IsOpen = false;
        await ShellFixture.PumpUntil(() => source.Commits.Contains("ShortcutKeys=Ctrl+Shift+S"));

        // A date: a picked day keeps the time.
        view.Grid.OpenDropDown(grid.Find("Value")!);
        Assert.IsType<DateDropDown>(view.Grid.OpenDropDownControl).Pick(new DateTime(2026, 10, 23));
        await ShellFixture.PumpUntil(() => source.Commits.Contains("Value=10/23/2026 15:38:00"));

        // Cursor: a choice that takes typing, each with its picture.
        var cursor = grid.Find("Cursor")!;
        Assert.True(cursor.ShowsTextBox);
        view.Grid.OpenDropDown(cursor);
        var list = Assert.IsType<ChoiceDropDown>(view.Grid.OpenDropDownControl);
        ShellFixture.Pump();
        Assert.Contains(list.GetVisualDescendants().OfType<CursorPicture>(), p => p.CursorName == "Hand");
        Assert.True(CursorPicture.HasDrawing("IBeam"));
        list.List.SelectedItem = cursor.Options.Single(o => o.Value == "Hand");
        list.Pick();
        await ShellFixture.PumpUntil(() => source.Commits.Contains("Cursor=Hand"));
        NavigationDepthTests.Screenshot(f, "m68-properties-drop-downs");
    }

    [AvaloniaFact]
    public async Task The_flags_drop_down_adds_each_box_to_the_value_a_refresh_brought()
    {
        var (f, source, view) = await ShowAsync();
        await using var _f = f;
        var grid = f.Shell.PropertiesWindow.Grid;
        var style = grid.Find("Style")!;
        view.Grid.OpenDropDown(style);
        var flags = Assert.IsType<FlagsDropDown>(view.Grid.OpenDropDownControl);
        Assert.Equal(["Bold"], flags.Boxes.Where(b => b.IsChecked == true).Select(b => (string)b.Content!));

        // Italic commits, and the refresh gives the row new flags while the drop-down stays open; Underline adds to them.
        flags.Boxes.Single(b => (string)b.Content! == "Italic").IsChecked = true;
        await ShellFixture.PumpUntil(() => source.Commits.Contains("Style=Bold, Italic") && grid.Find("Style")!.Value == "Bold, Italic");
        Assert.Same(flags, view.Grid.OpenDropDownControl);
        flags.Boxes.Single(b => (string)b.Content! == "Underline").IsChecked = true;
        await ShellFixture.PumpUntil(() => source.Commits.Contains("Style=Bold, Italic, Underline"));
        flags.Boxes.Single(b => (string)b.Content! == "Bold").IsChecked = false;
        await ShellFixture.PumpUntil(() => source.Commits.Contains("Style=Italic, Underline"));
    }

    [AvaloniaFact]
    public async Task Keys_in_the_footer_stay_the_footers()
    {
        var (f, source, view) = await ShowAsync();
        await using var _f = f;
        var grid = f.Shell.PropertiesWindow.Grid;
        var clicks = 0;
        var link = new Button { Content = "Open Project Properties (Alt+Enter)", Classes = { "link" } };
        link.Click += (_, _) => clicks++;
        Avalonia.Automation.AutomationProperties.SetName(link, "Open Project Properties");
        var footer = view.Grid.Footer;
        view.Grid.Footer = link;
        try
        {
            await ShellFixture.PumpUntil(() => link.IsEffectivelyVisible);
            grid.SelectedRow = grid.Find("Enabled");
            link.Focus();
            ShellFixture.Pump();

            // Enter activates the link, not the selected row; Down leaves the selection and the focus where they are.
            f.Press(Key.Enter);
            Assert.Equal(1, clicks);
            Assert.DoesNotContain(source.Commits, c => c.StartsWith("Enabled=", StringComparison.Ordinal));
            f.Press(Key.Down);
            Assert.Equal("Enabled", grid.SelectedRow?.Name);
            Assert.True(link.IsFocused);
        }
        finally
        {
            view.Grid.Footer = footer;
        }
    }

    [AvaloniaFact]
    public async Task The_row_menu_resets_and_toggles_the_description_and_the_splitters_are_clamped()
    {
        var (f, _, view) = await ShowAsync();
        await using var _f = f;
        var grid = f.Shell.PropertiesWindow.Grid;
        var menu = Assert.IsType<ContextMenu>(Rows(view).ContextMenu);
        var items = menu.Items.OfType<MenuItem>().ToList();
        Assert.Equal(["Reset", "Description"], items.Select(i => i.Header as string));

        // On a row: its Reset (grayed when the row can't be reset), and Description checked while the pane shows.
        var text = grid.Find("Text")!;
        grid.SelectedRow = text;
        Container(view, text)!.RaiseEvent(new ContextRequestedEventArgs()); // a right-click on the row (Shift+F10, the Menu key)
        await ShellFixture.PumpUntil(() => menu.IsOpen);
        Assert.Same(text, menu.DataContext);
        Assert.Equal(text.ResetCommand.CanExecute(null), items[0].Command?.CanExecute(null) ?? false);
        Assert.True(items[1].IsChecked);
        var pane = view.Grid.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "DescriptionPane");
        Assert.True(pane.IsVisible);
        items[1].RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
        menu.Close();
        Assert.False(grid.ShowDescription);
        ShellFixture.Pump();
        Assert.False(pane.IsVisible);
        Container(view, text)!.RaiseEvent(new ContextRequestedEventArgs()); // a right-click on the row (Shift+F10, the Menu key)
        await ShellFixture.PumpUntil(() => menu.IsOpen);
        Assert.False(items[1].IsChecked);
        items[1].RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
        menu.Close();
        Assert.True(grid.ShowDescription);

        // The description's height and the names' share stay within their bounds.
        grid.DescriptionHeight = 10;
        Assert.Equal(40, grid.DescriptionHeight);
        grid.DescriptionHeight = 1000;
        Assert.Equal(400, grid.DescriptionHeight);
        grid.NameColumnRatio = 0;
        Assert.Equal(0.15, grid.NameColumnRatio);
        grid.NameColumnRatio = 1;
        Assert.Equal(0.85, grid.NameColumnRatio);
        grid.NameColumnRatio = 0.45;
        grid.DescriptionHeight = 76;
    }

    [AvaloniaFact]
    public async Task A_name_or_a_value_shows_its_tooltip_only_while_it_is_clipped()
    {
        var (f, _, view) = await ShowAsync();
        await using var _f = f;
        var grid = f.Shell.PropertiesWindow.Grid;
        TextBlock Name(PropertyRowViewModel row) => Container(view, row)!.GetVisualDescendants().OfType<TextBlock>().First(t => t.Classes.Contains("clipTip") && t.Text == row.Name);
        var keys = grid.Find("ShortcutKeys")!;
        Assert.Equal("ShortcutKeys", keys.NameToolTip); // the name alone, as panel (a) draws it

        grid.NameColumnRatio = 0.15;
        await ShellFixture.PumpUntil(() => Name(keys).Bounds.Width < 50);
        Assert.True(PropertyGridView.IsClipped(Name(keys)));
        grid.NameColumnRatio = 0.85;
        await ShellFixture.PumpUntil(() => Name(keys).Bounds.Width > 100);
        Assert.False(PropertyGridView.IsClipped(Name(keys)));
        Assert.False(PropertyGridView.IsClipped(Editor(view, grid.Find("Text")!)!));
        grid.NameColumnRatio = 0.45;
    }

    [AvaloniaFact]
    public async Task A_read_only_value_is_selectable_and_ctrl_c_copies_a_rows_value()
    {
        var (f, source, view) = await ShowAsync();
        await using var _f = f;
        var grid = f.Shell.PropertiesWindow.Grid;
        source.ReadOnlyPath = @"C:\src\WinFormsApp\OrderForm.cs";
        source.Changed();
        await ShellFixture.PumpUntil(() => grid.Find("Full Path") is not null && Container(view, grid.Find("Full Path")!) is not null);
        var path = grid.Find("Full Path")!;
        var value = Assert.IsType<SelectableTextBlock>(Container(view, path)!.GetVisualDescendants().OfType<Control>().Single(c => c.Classes.Contains("editor")));
        Assert.Equal(source.ReadOnlyPath, value.Text);

        // Ctrl+C on the row copies its whole value.
        grid.SelectedRow = path;
        Rows(view).Focus();
        ShellFixture.Pump();
        f.Press(Key.C, ShellFixture.Primary);
        await ShellFixture.PumpUntil(() => Avalonia.Input.Platform.ClipboardExtensions.TryGetTextAsync(f.Window.Clipboard!).Result == source.ReadOnlyPath);
    }

    [AvaloniaFact]
    public void A_grid_view_listens_to_its_grid_only_while_it_is_shown()
    {
        var grid = new PropertyGridViewModel();
        var view = new PropertyGridView { DataContext = grid };
        Assert.False(view.IsListening);
        var window = new Window { Width = 300, Height = 300, Content = view };
        window.Show();
        ShellFixture.Pump();
        Assert.True(view.IsListening);
        window.Content = null;
        ShellFixture.Pump();
        Assert.False(view.IsListening);
        window.Content = view;
        ShellFixture.Pump();
        Assert.True(view.IsListening);
        window.Close();
    }

    [AvaloniaFact]
    public async Task In_high_contrast_the_selected_name_and_the_row_under_the_pointer_are_outlined()
    {
        var (f, _, view) = await ShowAsync();
        await using var _f = f;
        var grid = f.Shell.PropertiesWindow.Grid;
        var text = grid.Find("Text")!;
        grid.SelectedRow = text;
        var old = f.Window.RequestedThemeVariant;
        try
        {
            foreach (var (_, variant) in Codalus.Shell.Theming.ThemeService.Themes.Where(t => Codalus.Shell.Theming.ThemeService.IsHighContrast(t.Variant)))
            {
                f.Window.RequestedThemeVariant = variant;
                ShellFixture.Pump();
                ShellFixture.Pump();

                // The grid without the keyboard (the component selector, or the font editor, has it): the inactive selection is
                // near black, so the name is outlined.
                view.GetVisualDescendants().OfType<ComboBox>().First(c => c.Name == "ComponentSelector").Focus();
                await ShellFixture.PumpUntil(() => !view.Grid.IsKeyboardFocusWithin);
                var name = Container(view, text)!.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("nameCell"));
                Assert.True(name.BoxShadow.Count == 1, $"{variant}: {name.BoxShadow}");
                var outline = name.BoxShadow[0];
                Assert.True(outline is { IsInset: true, Spread: >= 1 } && outline.Color.A == 255, $"{variant}: {outline}");

                // The row under the pointer takes the same outline (Border.pgRow:pointerover).
                var row = Container(view, text)!.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("pgRow"));
                Assert.True(row.TryFindResource("Shell.Row.Hover.Outline", variant, out var hover) && hover is Avalonia.Media.BoxShadows { Count: 1 } shadows && shadows[0].Color.A == 255, variant.ToString());
            }
        }
        finally
        {
            f.Window.RequestedThemeVariant = old;
            ShellFixture.Pump();
        }
    }
}
