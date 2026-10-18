using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Codalus.Shell.Controls;

namespace Codalus.Shell.Infrastructure;

/// <summary>
/// The control set on one page (M34h, docs/design/controls.md's <c>Controls.dc.html</c>): every button, field, choice,
/// tab, row, chip and badge of the set in its states, drawn by the real styles. The smoke test's gallery photographs it in
/// every theme (<c>controls-*</c>) and <c>ControlSetTests</c> renders it, so the set can be compared with the design.
/// States the pointer or the keyboard would bring (hover, pressed, focus) are set as pseudo-classes. The texts are the
/// design's samples, not UI the user sees, so they aren't localized.
/// </summary>
internal static class ControlGallery
{
    private static readonly string[] s_configurations = ["Debug", "Release"];
    private static readonly string[] s_lengths = ["Short", "Medium"];
    private static readonly string[] s_protocols = ["gRPC"];

    public static Control Build()
    {
        var page = new StackPanel { Spacing = 14, Margin = new Thickness(20, 16) };
        page.Children.Add(Heading("Buttons"));
        page.Children.Add(ButtonMatrix());
        page.Children.Add(Heading("Toolbar"));
        page.Children.Add(Toolbar());
        page.Children.Add(Heading("Text, search and combo boxes"));
        page.Children.Add(Fields());
        page.Children.Add(Heading("Check boxes, radio buttons, switches"));
        page.Children.Add(Choices());
        page.Children.Add(Heading("Segmented controls and tabs"));
        page.Children.Add(SegmentsAndTabs());
        page.Children.Add(Heading("Lists, chips and badges"));
        page.Children.Add(ListsAndChips());
        page.Children.Add(Heading("Empty state and a dialog's footer"));
        page.Children.Add(EmptyAndFooter());
        return new Border
        {
            Child = page,
            [!Border.BackgroundProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("Shell.Panel"),
        };
    }

    private static TextBlock Heading(string text)
    {
        var heading = new TextBlock { Text = text };
        heading.Classes.Add("subheading");
        return heading;
    }

    private static T State<T>(T control, params string[] pseudoClasses)
        where T : StyledElement
    {
        foreach (var pseudo in pseudoClasses)
        {
            ((IPseudoClasses)control.Classes).Set(pseudo, true);
        }

        return control;
    }

    private static Button Button(string? text, params string[] classes)
    {
        var button = new Button { Content = text };
        button.Classes.AddRange(classes);
        return button;
    }

    private static Grid ButtonMatrix()
    {
        var states = new[] { "Normal", "Hover", "Pressed", "Disabled" };
        var rows = new (string Name, Func<Control> Make)[]
        {
            ("Primary", () => Button("Add Reference", "accent", "dialog")),
            ("Default", () => Button("Browse…", "dialog")),
            ("Icon + text", () => WithIcon(Button("Discover", "iconText"), "search")),
            ("Subtle", () => WithIcon(Button("Reset", "subtle", "iconText"), "undo")),
            ("Icon only", () => WithIcon(Button(null, "icon"), "refresh")),
            ("Drop-down", () => new DropDownButton { Content = "Ollama · qwen2.5" }),
            ("Split", () => new SplitButton { Content = "Export CSV", Flyout = new MenuFlyout() }),
            ("Link", () => Button("Edit in settings.json", "link")),
            ("Compact (24 px)", () => WithIcon(Button("Run All", "iconText", "compact"), "play")),
        };
        var grid = new Grid { ColumnSpacing = 14, RowSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition(130, GridUnitType.Pixel));
        foreach (var _ in states)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        }

        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        for (var c = 0; c < states.Length; c++)
        {
            var header = new TextBlock { Text = states[c] };
            header.Classes.Add("muted");
            grid.Children.Add(Place(header, 0, c + 1));
        }

        for (var r = 0; r < rows.Length; r++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            grid.Children.Add(Place(new TextBlock { Text = rows[r].Name, VerticalAlignment = VerticalAlignment.Center }, r + 1, 0));
            for (var c = 0; c < states.Length; c++)
            {
                var control = rows[r].Make();
                switch (states[c])
                {
                    case "Hover":
                        State(control, ":pointerover");
                        break;
                    case "Pressed":
                        State(control, ":pointerover", ":pressed");
                        break;
                    case "Disabled":
                        control.IsEnabled = false;
                        break;
                }

                grid.Children.Add(Place(control, r + 1, c + 1));
            }
        }

        return grid;
    }

    private static T WithIcon<T>(T control, string kind)
        where T : ContentControl
    {
        ButtonIcon.SetKind(control, kind);
        AutomationName(control, (control.Content as string) ?? kind);
        return control;
    }

    private static void AutomationName(Control control, string name) => Avalonia.Automation.AutomationProperties.SetName(control, name);

    private static Control Place(Control control, int row, int column)
    {
        Grid.SetRow(control, row);
        Grid.SetColumn(control, column);
        return control;
    }

    private static Border Toolbar()
    {
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        foreach (var (kind, state) in new[] { ("arrow-up", ""), ("arrow-down", ""), ("refresh", ":pointerover"), ("stop", ":pressed") })
        {
            var button = WithIcon(Button(null, "toolbar", "icon"), kind);
            if (state.Length > 0)
            {
                State(button, state);
            }

            bar.Children.Add(button);
        }

        bar.Children.Add(Separator());
        var keep = WithIcon(new ToggleButton { IsChecked = true }, "pin");
        keep.Classes.AddRange(["toolbar", "icon"]);
        bar.Children.Add(keep);
        var filter = WithIcon(new ToggleButton(), "filter");
        filter.Classes.AddRange(["toolbar", "icon"]);
        bar.Children.Add(filter);
        var inProcess = WithIcon(new ToggleButton { IsChecked = true, Content = "In process" }, "play");
        inProcess.Classes.AddRange(["toolbar", "iconText"]);
        bar.Children.Add(inProcess);
        bar.Children.Add(Separator());
        bar.Children.Add(WithIcon(Button("Modify Find", "toolbar", "iconText"), "edit"));
        var split = new SplitButton { Content = new Icon { Kind = "save", Width = 16, Height = 16 }, Flyout = new MenuFlyout() };
        split.Classes.Add("toolbar");
        AutomationName(split, "Export");
        bar.Children.Add(split);
        var services = WithIcon(new DropDownButton { Content = "Running Services" }, "cloud");
        services.Classes.AddRange(["toolbar", "iconText"]);
        bar.Children.Add(services);
        var disabled = WithIcon(Button(null, "toolbar", "icon"), "trash");
        disabled.IsEnabled = false;
        bar.Children.Add(disabled);
        var search = new TextBox { PlaceholderText = "Search Find Results", Width = 220, Margin = new Thickness(6, 0, 0, 0) };
        search.Classes.Add("search");
        bar.Children.Add(search);
        return new Border
        {
            Child = bar,
            Padding = new Thickness(4, 2),
            BorderThickness = new Thickness(0, 0, 0, 1),
            [!Border.BackgroundProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("Shell.Toolbar"),
            [!Border.BorderBrushProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("Shell.Toolbar.Border"),
        };
    }

    private static Border Separator()
    {
        var separator = new Border();
        separator.Classes.Add("toolbarSeparator");
        return separator;
    }

    private static Grid Fields()
    {
        var grid = new Grid { ColumnSpacing = 14, RowSpacing = 10 };
        for (var c = 0; c < 4; c++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition(230, GridUnitType.Pixel));
        }

        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        grid.Children.Add(Place(Labeled("Placeholder", new TextBox { PlaceholderText = "http://localhost:5000/Service.svc?wsdl" }), 0, 0));
        grid.Children.Add(Place(Labeled("Hover", State(new TextBox { PlaceholderText = "http://localhost:5000/Service.svc?wsdl" }, ":pointerover")), 0, 1));
        grid.Children.Add(Place(Labeled("Focused", State(new TextBox { Text = "http://localhost:5280/CalculatorService.svc" }, ":focus")), 0, 2));
        grid.Children.Add(Place(Labeled("Filled", new TextBox { Text = "CalculatorClient.Calculator" }), 0, 3));
        var error = new TextBox { Text = "Gr" };
        DataValidationErrors.SetError(error, new InvalidOperationException("The name is too short."));
        grid.Children.Add(Place(Labeled("Error", error), 1, 0));
        grid.Children.Add(Place(Labeled("Disabled", new TextBox { Text = "x64", IsEnabled = false }), 1, 1));
        grid.Children.Add(Place(Labeled("Read-only", new TextBox { Text = ".work/user-data/settings.json", IsReadOnly = true }), 1, 2));
        var attached = new DockPanel();
        var browse = WithIcon(Button(null, "icon"), "folder-open");
        DockPanel.SetDock(browse, Avalonia.Controls.Dock.Right);
        browse.Margin = new Thickness(4, 0, 0, 0);
        attached.Children.Add(browse);
        attached.Children.Add(new TextBox { Text = "contracts/CalculatorService.wsdl" });
        grid.Children.Add(Place(Labeled("With a button", attached), 1, 3));
        var typed = new TextBox { Text = "tab size" };
        typed.Classes.Add("search");
        grid.Children.Add(Place(Labeled("Search, typed", typed), 2, 0));
        var combo = new ComboBox { ItemsSource = s_configurations, SelectedIndex = 0, Width = 140 };
        grid.Children.Add(Place(Labeled("Combo box", combo), 2, 1));
        var focusedCombo = State(new ComboBox { ItemsSource = s_lengths, SelectedIndex = 0, Width = 140 }, ":focus-visible");
        grid.Children.Add(Place(Labeled("Combo box, focused", focusedCombo), 2, 2));
        grid.Children.Add(Place(Labeled("Combo box, disabled", new ComboBox { ItemsSource = s_protocols, SelectedIndex = 0, Width = 140, IsEnabled = false }), 2, 3));
        return grid;
    }

    private static StackPanel Labeled(string label, Control control)
    {
        var panel = new StackPanel { Spacing = 4 };
        var caption = new TextBlock { Text = label };
        caption.Classes.AddRange(["muted", "small"]);
        panel.Children.Add(caption);
        panel.Children.Add(control);
        AutomationName(control, label);
        return panel;
    }

    private static Grid Choices()
    {
        var grid = new Grid { ColumnSpacing = 24, RowSpacing = 8 };
        for (var c = 0; c < 4; c++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        }

        for (var r = 0; r < 4; r++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        }

        grid.Children.Add(Place(new CheckBox { Content = "Word Wrap" }, 0, 0));
        grid.Children.Add(Place(State(new CheckBox { Content = "Word Wrap" }, ":pointerover"), 0, 1));
        grid.Children.Add(Place(new CheckBox { Content = "Insert Spaces", IsChecked = true }, 0, 2));
        grid.Children.Add(Place(new CheckBox { Content = "Word Wrap", IsEnabled = false }, 0, 3));
        grid.Children.Add(Place(new CheckBox { Content = "Select all (2 of 4)", IsThreeState = true, IsChecked = null }, 1, 0));
        grid.Children.Add(Place(new CheckBox { Content = "Insert Spaces", IsChecked = true, IsEnabled = false }, 1, 1));
        grid.Children.Add(Place(new RadioButton { Content = "Clients", IsChecked = true, GroupName = "gallery" }, 1, 2));
        grid.Children.Add(Place(new RadioButton { Content = "Servers", GroupName = "gallery" }, 1, 3));
        grid.Children.Add(Place(new ToggleSwitch { Content = "Off" }, 2, 0));
        grid.Children.Add(Place(new ToggleSwitch { Content = "Agent", IsChecked = true }, 2, 1));
        grid.Children.Add(Place(new ToggleSwitch { Content = "Agent", IsChecked = true, IsEnabled = false }, 2, 2));
        grid.Children.Add(Place(new RadioButton { Content = "Disabled", IsEnabled = false, GroupName = "gallery2" }, 2, 3));
        return grid;
    }

    private static StackPanel SegmentsAndTabs()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 24 };
        var segments = new SegmentedControl { SelectedIndex = 0 };
        segments.Items.Add(new SegmentedControlItem { Content = "Form" });
        segments.Items.Add(new SegmentedControlItem { Content = "XML" });
        AutomationName(segments, "Request");
        row.Children.Add(segments);
        var icons = new SegmentedControl { SelectedIndex = 1 };
        icons.Items.Add(new SegmentedControlItem { Content = "Tree", Icon = "type-hierarchy" });
        icons.Items.Add(State(new SegmentedControlItem { Content = "List", Icon = "list" }, ":pointerover"));
        icons.Items.Add(new SegmentedControlItem { Content = "Raw" });
        AutomationName(icons, "View");
        row.Children.Add(icons);
        var disabled = new SegmentedControl { SelectedIndex = 0, IsEnabled = false };
        disabled.Items.Add(new SegmentedControlItem { Content = "User" });
        disabled.Items.Add(new SegmentedControlItem { Content = "Workspace" });
        AutomationName(disabled, "Scope");
        row.Children.Add(disabled);

        var tabs = new TabControl { Width = 420, Height = 70 };
        tabs.Items.Add(new TabItem { Header = "Body", Content = new TextBlock { Text = "{ \"id\": 7 }", Margin = new Thickness(8) } });
        var headers = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        headers.Children.Add(new TextBlock { Text = "Headers", VerticalAlignment = VerticalAlignment.Center });
        var badge = new Border { Child = new TextBlock { Text = "8" } };
        badge.Classes.Add("badge");
        headers.Children.Add(badge);
        tabs.Items.Add(new TabItem { Header = headers });
        tabs.Items.Add(new TabItem { Header = "Request" });
        tabs.Items.Add(new TabItem { Header = "Timeline", IsEnabled = false });
        tabs.SelectedIndex = 0;
        row.Children.Add(tabs);
        return row;
    }

    private static StackPanel ListsAndChips()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 24 };
        var list = new ListBox { Width = 260 };
        list.Classes.Add("rows");
        foreach (var text in new[] { "string Greet(Person person);", "Person[] FindPeople(string prefix);", "public sealed class Person", "private static readonly Person s_people" })
        {
            list.Items.Add(new ListBoxItem { Content = text });
        }

        State((ListBoxItem)list.Items[1]!, ":pointerover");
        list.SelectedIndex = 2;
        AutomationName(list, "Results");
        row.Children.Add(Bordered(list));

        var chips = new WrapPanel { Width = 360 };
        foreach (var (text, kind) in new[] { ("200 OK", "success"), ("201 Created", "success"), ("304 Not Modified", "warning"), ("404 Not Found", "error"), ("Fault s:Client", "error"), ("GET", "info"), ("POST", "info"), ("BasicHttpBinding", "") })
        {
            var chip = new Border { Child = new TextBlock { Text = text }, Margin = new Thickness(0, 0, 6, 6) };
            chip.Classes.Add("chip");
            if (kind.Length > 0)
            {
                chip.Classes.Add(kind);
            }

            chips.Children.Add(chip);
        }

        foreach (var count in new[] { "3", "128" })
        {
            var badge = new Border { Child = new TextBlock { Text = count }, Margin = new Thickness(0, 2, 6, 6) };
            badge.Classes.Add("badge");
            chips.Children.Add(badge);
        }

        row.Children.Add(chips);
        return row;
    }

    private static Border Bordered(Control child) => new()
    {
        Child = child,
        BorderThickness = new Thickness(1),
        [!Border.BorderBrushProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("Shell.Border"),
    };

    private static StackPanel EmptyAndFooter()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 24 };
        var empty = new StackPanel();
        empty.Classes.Add("emptyState");
        empty.Children.Add(new Icon { Kind = "container" });
        empty.Children.Add(new TextBlock { Text = "No containers (Podman, Linux). Start one with Ctrl+F5 on a project with a Dockerfile, or run docker compose up." });
        empty.Children.Add(WithIcon(Button("Refresh", "iconText"), "refresh"));
        row.Children.Add(Bordered(empty));

        var footer = new Border { Width = 420, VerticalAlignment = VerticalAlignment.Top };
        footer.Classes.Add("dialogFooter");
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(Button("OK", "accent", "dialog"));
        buttons.Children.Add(Button("Cancel", "dialog"));
        footer.Child = buttons;
        row.Children.Add(footer);
        return row;
    }
}
