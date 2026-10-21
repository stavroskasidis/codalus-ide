using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Codalus.Shell.Controls;
using Codalus.Shell.Infrastructure;
using Codalus.Shell.Theming;
using Codalus.Shell.ViewModels;
using Codalus.Shell.Views;
using ShapePath = Avalonia.Controls.Shapes.Path;

namespace Codalus.Shell.Tests;

/// <summary>
/// M34h: the control set (docs/design/controls.md). The default button's template draws the design's tokens in every
/// state without a class; the segmented control moves its choice with the keyboard and names itself; the in-window
/// dialog shows its buttons in the OS's order with one primary, and the checklist's Select all and count; a combo box's
/// list checks the chosen row (except the navigation bar's and an editable box's); a tab strip has a hairline that the
/// selected tab's underline covers; the whole set renders in every theme (with <c>CODALUS_UI_SNAPSHOTS</c>, the
/// pictures are saved there).
/// </summary>
public class ControlSetTests
{
    internal static Color Token(string key, ThemeVariant variant)
    {
        Assert.True(Application.Current!.TryGetResource(key, variant, out var value) && value is ISolidColorBrush, $"{key} in {variant}");
        return ((ISolidColorBrush)value!).Color;
    }

    internal static Color? BrushColor(IBrush? brush) => (brush as ISolidColorBrush)?.Color;

    internal static void SetState(StyledElement element, string pseudoClass, bool on = true) => ((IPseudoClasses)element.Classes).Set(pseudoClass, on);

    private static Window Show(Control content, ThemeVariant variant, double width = 600, double height = 300)
    {
        var window = new Window { Width = width, Height = height, Content = content, RequestedThemeVariant = variant };
        window.Show();
        ShellFixture.Pump();
        ShellFixture.Pump();
        return window;
    }

    [AvaloniaTheory]
    [InlineData("harbor.light")]
    [InlineData("blue")]
    [InlineData("dark")]
    [InlineData("highContrast")]
    [InlineData("ember.highContrast")]
    public void A_plain_button_draws_the_control_sets_default_look_in_every_state(string theme)
    {
        var variant = ThemeService.VariantOf(theme);
        var button = new Button { Content = "Browse…" };
        var primary = new Button { Content = "Add Reference", Classes = { "accent" } };
        var panel = new StackPanel { Spacing = 8, Children = { button, primary } };
        var window = Show(panel, variant);
        try
        {
            var presenter = button.GetVisualDescendants().OfType<ContentPresenter>().First(p => p.Name == "PART_ContentPresenter");
            void Expect(string fill, string border, string state)
            {
                ShellFixture.Pump();
                Assert.True(Token(fill, variant) == BrushColor(presenter.Background), $"{theme} {state}: fill {BrushColor(presenter.Background)} is not {fill}");
                Assert.True(Token(border, variant) == BrushColor(presenter.BorderBrush), $"{theme} {state}: border {BrushColor(presenter.BorderBrush)} is not {border}");
            }

            Expect("Shell.Button", "Shell.Button.Border", "normal");
            Assert.Equal(Token("Shell.Button.Foreground", variant), BrushColor(presenter.Foreground));
            Assert.Equal(28, button.Bounds.Height);
            Assert.Equal(Application.Current!.FindResource(variant, "Shell.Control.CornerRadius"), button.CornerRadius);
            SetState(button, ":pointerover");
            Expect("Shell.Button.Hover", "Shell.Button.Hover.Border", "hover");
            SetState(button, ":pressed");
            Expect("Shell.Button.Pressed", "Shell.Button.Pressed.Border", "pressed");
            SetState(button, ":pressed", false);
            SetState(button, ":pointerover", false);
            button.IsEnabled = false;
            Expect("Shell.Disabled", "Shell.Disabled.Border", "disabled");
            Assert.Equal(Token("Shell.Disabled.Foreground", variant), BrushColor(presenter.Foreground));

            // The primary: the accent with its text, darker under the pointer and while pressed.
            presenter = primary.GetVisualDescendants().OfType<ContentPresenter>().First(p => p.Name == "PART_ContentPresenter");
            Expect("Shell.Accent", "Shell.Accent", "primary");
            Assert.Equal(Token("Shell.AccentForeground", variant), BrushColor(presenter.Foreground));
            Assert.Equal(FontWeight.SemiBold, primary.FontWeight);
            SetState(primary, ":pointerover");
            Expect("Shell.Accent.Hover", "Shell.Accent.Hover", "primary hover");
            SetState(primary, ":pressed");
            Expect("Shell.Accent.Pressed", "Shell.Accent.Pressed", "primary pressed");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void The_keyboards_focus_ring_is_2_px_in_Shell_Focus_1_px_outside_the_button()
    {
        var variant = ThemeService.HarborLight;
        var before = new TextBox();
        var button = new Button { Content = "Browse…", Margin = new Thickness(20) };
        var window = Show(new StackPanel { Children = { before, button } }, variant);
        try
        {
            before.Focus();
            window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
            for (var i = 0; i < 3; i++)
            {
                ShellFixture.Pump();
            }

            Assert.True(button.IsFocused);
            // The adorner layer holds the ring: a 2 px border in Shell.Focus, 3 px past each edge (1 px gap, 2 px ring).
            var layer = Avalonia.Controls.Primitives.AdornerLayer.GetAdornerLayer(button);
            Assert.NotNull(layer);
            var ring = layer.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => BrushColor(b.BorderBrush) == Token("Shell.Focus", variant));
            Assert.NotNull(ring);
            Assert.Equal(new Thickness(2), ring.BorderThickness);
            var origin = ring.TranslatePoint(default, button)!.Value;
            Assert.Equal(new Point(-3, -3), origin);
            Assert.Equal(button.Bounds.Width + 6, ring.Bounds.Width);
            Assert.Equal(button.Bounds.Height + 6, ring.Bounds.Height);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Icon_buttons_draw_their_icon_and_icon_and_text_buttons_keep_their_text_as_the_name()
    {
        var icon = new Button { Classes = { "icon" } };
        ButtonIcon.SetKind(icon, "refresh");
        var iconText = new Button { Classes = { "iconText" }, Content = "Discover" };
        ButtonIcon.SetKind(iconText, "search");
        var window = Show(new StackPanel { Spacing = 8, Children = { icon, iconText } }, ThemeService.HarborLight);
        try
        {
            Assert.Equal("refresh", icon.GetVisualDescendants().OfType<Icon>().Single().Kind);
            Assert.Equal(28, icon.Bounds.Width);
            Assert.Equal("search", iconText.GetVisualDescendants().OfType<Icon>().Single().Kind);
            Assert.Contains(iconText.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Discover");
            Assert.Equal("Discover", Avalonia.Automation.Peers.ControlAutomationPeer.CreatePeerForElement(iconText).GetName());
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>The vertical middle of a text block's or text presenter's first line, in <paramref name="field"/>'s coordinates.</summary>
    internal static double TextMiddle(Control text, Visual field) => text switch
    {
        TextBlock block => block.TranslatePoint(new Point(0, block.Padding.Top + (block.TextLayout.Height / 2)), field)!.Value.Y,
        TextPresenter presenter => presenter.TranslatePoint(new Point(0, presenter.TextLayout.Height / 2), field)!.Value.Y,
        _ => throw new ArgumentException(text.GetType().Name, nameof(text)),
    };

    /// <summary>
    /// The text and placeholder a combo box shows (its selection, or its text box's text or placeholder), whichever is
    /// visible.
    /// </summary>
    internal static Control ShownText(ComboBox combo) =>
        combo.GetVisualDescendants().OfType<Control>().First(c => c.IsEffectivelyVisible && c is TextPresenter or TextBlock { Text.Length: > 0 } && (c is not TextPresenter p || p.Text is { Length: > 0 }));

    [AvaloniaTheory]
    [InlineData("harbor.light")]
    [InlineData("dark")]
    [InlineData("highContrast")]
    [InlineData("ember.highContrast")]
    public void A_combo_box_draws_one_border_around_its_text_and_chevron_and_centers_its_text(string theme)
    {
        // The maintainer's report on Find and Replace: hovering the editable Find what box drew a box around the chevron
        // only (the text box's hover fill hid the combo's border along the text), and its placeholder, like Look in's
        // and File types' text, sat at the top of the field.
        var variant = ThemeService.VariantOf(theme);
        var query = new ComboBox { IsEditable = true, PlaceholderText = "Find what", HorizontalAlignment = HorizontalAlignment.Stretch };
        var lookIn = new ComboBox { IsEditable = true, Text = "Entire solution", ItemsSource = new[] { "Entire solution" }, HorizontalAlignment = HorizontalAlignment.Stretch };
        var plain = new ComboBox { ItemsSource = new[] { "Debug", "Release" }, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        var placeholder = new ComboBox { PlaceholderText = "Default", ItemsSource = new[] { "Debug" }, HorizontalAlignment = HorizontalAlignment.Stretch };
        var window = Show(new StackPanel { Spacing = 8, Margin = new Thickness(20), Children = { query, lookIn, plain, placeholder } }, variant, 420);
        try
        {
            // Text and placeholders in the middle of the 28 px field, editable or not.
            foreach (var combo in new[] { query, lookIn, plain, placeholder })
            {
                Assert.Equal(28, combo.Bounds.Height);
                var text = ShownText(combo);
                var middle = TextMiddle(text, combo);
                Assert.True(Math.Abs(middle - (combo.Bounds.Height / 2)) <= 1.5, $"{theme}: {text.GetType().Name} of {combo.PlaceholderText ?? combo.Text ?? combo.SelectionBoxItem} centered at {middle:0.#} of {combo.Bounds.Height}");
            }

            // The field's one border, around the text and the chevron, in every state; the text box draws none of its
            // own and no fill over it.
            var field = query.GetVisualDescendants().OfType<Border>().First(b => b.Name == "Background");
            var textBox = query.GetVisualDescendants().OfType<TextBox>().First(t => t.Name == "PART_EditableTextBox");
            var inner = textBox.GetVisualDescendants().OfType<Border>().First(b => b.Name == "PART_BorderElement");
            void Expect(string border, double thickness, string state)
            {
                ShellFixture.Pump();
                Assert.True(Token(border, variant) == BrushColor(field.BorderBrush), $"{theme} {state}: border {BrushColor(field.BorderBrush)} is not {border}");
                Assert.Equal(new Thickness(thickness), field.BorderThickness);
                Assert.Equal(query.Bounds.Size, field.Bounds.Size);
                Assert.True(inner.BorderThickness == default, $"{theme} {state}: the text box draws a border of {inner.BorderThickness}");
                Assert.True(inner.Background is null || BrushColor(inner.Background) is { A: 0 }, $"{theme} {state}: the text box fills {BrushColor(inner.Background)} over the field's border");
            }

            Expect("Shell.Input.Border", 1, "normal");
            SetState(query, ":pointerover");
            SetState(textBox, ":pointerover");
            Expect("Shell.Input.Border.Hover", 1, "hover over the text");
            SetState(textBox, ":pointerover", false);
            Expect("Shell.Input.Border.Hover", 1, "hover over the chevron");
            SetState(query, ":pointerover", false);
            textBox.Focus();
            Assert.True(textBox.IsFocused);
            Expect("Shell.Focus", 2, "focus");
            SetState(query, ":pointerover");
            SetState(textBox, ":pointerover");
            Expect("Shell.Focus", 2, "focus and hover");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Opens <paramref name="combo"/>'s list and returns its rows, realized in the popup.</summary>
    private static List<ComboBoxItem> OpenRows(ComboBox combo)
    {
        combo.IsDropDownOpen = true;
        for (var i = 0; i < 3; i++)
        {
            ShellFixture.Pump();
        }

        var rows = Enumerable.Range(0, combo.ItemCount).Select(combo.ContainerFromIndex).OfType<ComboBoxItem>().ToList();
        Assert.True(rows.Count == combo.ItemCount, $"{rows.Count} of {combo.ItemCount} rows realized");
        return rows;
    }

    private static ShapePath Check(ComboBoxItem row) => row.GetVisualDescendants().OfType<ShapePath>().Single(p => p.Name == "CheckGlyph");

    [AvaloniaTheory]
    [InlineData("harbor.light")]
    [InlineData("dark")]
    [InlineData("highContrast")]
    [InlineData("ember.highContrast")]
    public void A_combo_boxs_list_marks_the_chosen_item_with_a_check(string theme)
    {
        // The design's open combo box (Controls.dc.html): a check column on every row, the check beside the chosen one.
        var variant = ThemeService.VariantOf(theme);
        var combo = new ComboBox { ItemsSource = new[] { "Debug", "Release", "Staging" }, SelectedIndex = 1, Width = 200 };
        var window = Show(new StackPanel { Margin = new Thickness(20), Children = { combo } }, variant);
        try
        {
            var rows = OpenRows(combo);
            for (var i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                var check = Check(row);
                Assert.True(check.IsVisible == (i == 1), $"{theme}: row {i}'s check is {(check.IsVisible ? "shown" : "hidden")}");
                Assert.Equal(24, row.Bounds.Height);

                // The column is kept on every row, checked or not: the text starts 28 px in.
                var text = row.GetVisualDescendants().OfType<TextBlock>().Single();
                Assert.Equal(28, text.TranslatePoint(default, row)!.Value.X);
            }

            // The check: 16 px, 6 px in from the row's edge.
            Assert.Equal(new Rect(6, 4, 16, 16), new Rect(Check(rows[1]).TranslatePoint(default, rows[1])!.Value, Check(rows[1]).Bounds.Size));

            // The chosen row in the selection's colors, its check in the selection's text color.
            var presenter = rows[1].GetVisualDescendants().OfType<ContentPresenter>().First(p => p.Name == "PART_ContentPresenter");
            Assert.Equal(Token("Shell.Selection", variant), BrushColor(presenter.Background));
            Assert.Equal(Token("Shell.Selection.Foreground", variant), BrushColor(presenter.Foreground));
            Assert.Equal(Token("Shell.Selection.Foreground", variant), BrushColor(Check(rows[1]).Stroke));

            // The closed field shows the choice through its template, without a check.
            Assert.Equal("Release", combo.SelectionBoxItem);
            Assert.DoesNotContain(combo.GetVisualDescendants().OfType<ShapePath>(), p => p.Name == "CheckGlyph");

            // Another choice moves the check.
            combo.SelectedIndex = 2;
            ShellFixture.Pump();
            Assert.False(Check(rows[1]).IsVisible);
            Assert.True(Check(rows[2]).IsVisible);
        }
        finally
        {
            combo.IsDropDownOpen = false;
            ShellFixture.Pump();
            window.Close();
        }
    }

    [AvaloniaFact]
    public void A_combo_boxs_check_is_on_the_right_from_right_to_left()
    {
        var row = new ComboBoxItem { Content = "Release", IsSelected = true };
        var frame = new Border { Width = 200, HorizontalAlignment = HorizontalAlignment.Left, Child = new StackPanel { FlowDirection = FlowDirection.RightToLeft, Children = { row } } };
        var window = Show(frame, ThemeService.HarborLight);
        try
        {
            var check = Check(row);
            Assert.True(check.IsVisible);
            var middle = check.TranslatePoint(new Point(check.Bounds.Width / 2, check.Bounds.Height / 2), frame)!.Value.X;
            Assert.Equal(200 - 6 - 8, middle);
            // The text ends 28 px from the right (a text block keeps its own direction, so take both of its edges).
            var text = row.GetVisualDescendants().OfType<TextBlock>().Single();
            var edges = new[] { text.TranslatePoint(default, frame)!.Value.X, text.TranslatePoint(new Point(text.Bounds.Width, 0), frame)!.Value.X };
            Assert.Equal(200 - 28, edges.Max());
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Navigation_bar_and_editable_combo_lists_have_no_check_column()
    {
        // The editor's member lists have no check, as the classic IDE's; an editable box's list is its history, where a
        // check would only mark the entry that equals the typed text (docs/design/controls.md, "Where the build differs").
        var navigation = new ComboBox { ItemsSource = new[] { "Greeter", "Greet(Person person)" }, SelectedIndex = 1, Classes = { "navigationBar" }, Width = 200 };
        var editable = new ComboBox { ItemsSource = new[] { "Person", "Greeter.Greet" }, SelectedIndex = 0, IsEditable = true, Width = 200 };
        var window = Show(new StackPanel { Spacing = 8, Margin = new Thickness(20), Children = { navigation, editable } }, ThemeService.HarborLight);
        try
        {
            foreach (var (combo, padding) in new[] { (navigation, new Thickness(6, 3)), (editable, new Thickness(8, 3)) })
            {
                var rows = OpenRows(combo);
                Assert.True(rows[combo.SelectedIndex].IsSelected);
                Assert.All(rows, r => Assert.False(Check(r).IsVisible));
                Assert.All(rows, r => Assert.Equal(padding, r.Padding));
                combo.IsDropDownOpen = false;
                ShellFixture.Pump();
            }
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>The selected tab's underline, in <paramref name="relativeTo"/>'s coordinates.</summary>
    internal static Rect Underline(Control tab, Visual relativeTo)
    {
        var pipe = tab.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "PART_SelectedPipe");
        Assert.True(pipe.IsVisible, "the selected tab shows its underline");
        return new Rect(pipe.TranslatePoint(default, relativeTo)!.Value, pipe.Bounds.Size);
    }

    /// <summary>The color of the frame's pixel under a point of <paramref name="visual"/>.</summary>
    private static Color Pixel(WriteableBitmap frame, Visual visual, Point point, TopLevel top)
    {
        var at = visual.TranslatePoint(point, top)!.Value * top.RenderScaling;
        using var buffer = frame.Lock();
        var offset = ((int)at.Y * buffer.RowBytes) + ((int)at.X * 4);
        var (b0, b1, b2, b3) = (System.Runtime.InteropServices.Marshal.ReadByte(buffer.Address, offset), System.Runtime.InteropServices.Marshal.ReadByte(buffer.Address, offset + 1),
            System.Runtime.InteropServices.Marshal.ReadByte(buffer.Address, offset + 2), System.Runtime.InteropServices.Marshal.ReadByte(buffer.Address, offset + 3));
        return buffer.Format == PixelFormat.Rgba8888 ? Color.FromArgb(b3, b0, b1, b2) : Color.FromArgb(b3, b2, b1, b0);
    }

    [AvaloniaTheory]
    [InlineData("harbor.light", null)]
    [InlineData("dark", null)]
    [InlineData("highContrast", "#6FC3DF")]
    [InlineData("harbor.highContrast", "#8C8C8C")]
    [InlineData("ember.highContrast", "#8C8C8C")]
    public void A_tab_strip_has_a_hairline_that_the_selected_tab_covers(string theme, string? designed)
    {
        // controls.css's .tabs: a 1 px line in the mockups' soft border under the strip, and the selected tab's 2 px accent
        // underline over it. TabControls draw it in their control theme, a TabStrip used as tabs with Classes="viewTabs".
        var variant = ThemeService.VariantOf(theme);
        var rule = Token("Shell.ViewTabs.Rule", variant);
        if (designed is not null)
        {
            Assert.Equal(Color.Parse(designed), rule);
        }

        var top = new TabControl { Width = 400, Height = 90 };
        var bottom = new TabControl { Width = 400, Height = 90, TabStripPlacement = Avalonia.Controls.Dock.Bottom };
        foreach (var tabs in new[] { top, bottom })
        {
            tabs.Items.Add(new TabItem { Header = "Body", Content = new TextBlock { Text = "{ }" } });
            tabs.Items.Add(new TabItem { Header = "Headers" });
            tabs.Items.Add(new TabItem { Header = "Request" });
            tabs.SelectedIndex = 0;
        }

        var strip = new TabStrip { Width = 400, Classes = { "viewTabs" }, HorizontalAlignment = HorizontalAlignment.Left };
        strip.Items.Add(new TabStripItem { Content = "Find Results 1" });
        strip.Items.Add(new TabStripItem { Content = "Find Results 2" });
        strip.SelectedIndex = 0;
        var window = Show(new StackPanel { Spacing = 16, Margin = new Thickness(10), Children = { top, bottom, strip } }, variant, 600, 400);
        try
        {
            using var frame = window.CaptureRenderedFrame()!;
            var accent = Token("Shell.Accent", variant);
            void Expect(string what, Control line, Thickness thickness, IBrush? brush, Control selected, Control last, bool atTop)
            {
                Assert.True(thickness == (atTop ? new Thickness(0, 1, 0, 0) : new Thickness(0, 0, 0, 1)), $"{theme} {what}: the line is {thickness}");
                Assert.True(rule == BrushColor(brush), $"{theme} {what}: the line is {BrushColor(brush)}, not Shell.ViewTabs.Rule's {rule}");
                Assert.Equal(400, line.Bounds.Width);
                var underline = Underline(selected, line);
                Assert.True(atTop ? underline.Top == 0 : underline.Bottom == line.Bounds.Height, $"{theme} {what}: the underline is at {underline}, the line's {line.Bounds}");

                // As drawn: the line's row is the rule past the last tab and the accent under the selected one.
                var y = atTop ? 0.5 : line.Bounds.Height - 0.5;
                var past = last.TranslatePoint(new Point(last.Bounds.Width + 20, 0), line)!.Value.X;
                Assert.True(Pixel(frame, line, new Point(past, y), window) == rule, $"{theme} {what}: {Pixel(frame, line, new Point(past, y), window)} past the tabs");
                Assert.True(Pixel(frame, line, new Point(underline.Center.X, y), window) == accent, $"{theme} {what}: {Pixel(frame, line, new Point(underline.Center.X, y), window)} under the selected tab");
            }

            foreach (var (tabs, atTop) in new[] { (top, false), (bottom, true) })
            {
                var line = tabs.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "PART_TabStrip");
                Expect(atTop ? "TabControl at the bottom" : "TabControl", line, line.BorderThickness, line.BorderBrush, (Control)tabs.ContainerFromIndex(0)!, (Control)tabs.ContainerFromIndex(2)!, atTop);
            }

            Expect("TabStrip.viewTabs", strip, strip.BorderThickness, strip.BorderBrush, (Control)strip.ContainerFromIndex(0)!, (Control)strip.ContainerFromIndex(1)!, atTop: false);
            Assert.Equal(new Thickness(6, 0), strip.Padding);

            // The page transition's host is kept from Fluent's template.
            Assert.Contains(top.GetVisualDescendants().OfType<ContentPresenter>(), p => p.Name == "PART_SelectedContentHost2");
            Assert.Contains(top.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "{ }" && t.IsEffectivelyVisible);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void A_toolbar_toggle_is_a_tint_with_an_accent_edge_not_an_accent_block()
    {
        var toggle = new ToggleButton { IsChecked = true, Classes = { "toolbar", "icon" } };
        ButtonIcon.SetKind(toggle, "pin");
        var window = Show(toggle, ThemeService.HarborLight);
        try
        {
            var presenter = toggle.GetVisualDescendants().OfType<ContentPresenter>().First(p => p.Name == "PART_ContentPresenter");
            Assert.Equal(Token("Shell.Toolbar.Checked", ThemeService.HarborLight), BrushColor(presenter.Background));
            Assert.Equal(Token("Shell.Toolbar.Checked.Border", ThemeService.HarborLight), BrushColor(presenter.BorderBrush));
            Assert.Equal(24, toggle.Bounds.Height);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void The_segmented_control_moves_its_choice_with_the_arrow_keys_and_names_its_segments()
    {
        var segments = new SegmentedControl { SelectedIndex = 0 };
        Avalonia.Automation.AutomationProperties.SetName(segments, "View");
        foreach (var text in new[] { "Tree", "List", "Raw" })
        {
            segments.Items.Add(new SegmentedControlItem { Content = text });
        }

        var before = new Button { Content = "Before" };
        var window = Show(new StackPanel { Spacing = 8, Children = { before, segments } }, ThemeService.HarborLight);
        try
        {
            Assert.Equal(24, segments.Bounds.Height);

            // Tab reaches the chosen segment; the arrows move the choice, Home and End go to the ends, and it doesn't wrap.
            before.Focus();
            window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
            ShellFixture.Pump();
            Assert.Same(segments.ContainerFromIndex(0), window.FocusManager?.GetFocusedElement());
            window.KeyPress(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight, null);
            ShellFixture.Pump();
            Assert.Equal(1, segments.SelectedIndex);
            window.KeyPress(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight, null);
            window.KeyPress(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight, null);
            ShellFixture.Pump();
            Assert.Equal(2, segments.SelectedIndex);
            window.KeyPress(Key.Left, RawInputModifiers.None, PhysicalKey.ArrowLeft, null);
            ShellFixture.Pump();
            Assert.Equal(1, segments.SelectedIndex);
            window.KeyPress(Key.Home, RawInputModifiers.None, PhysicalKey.Home, null);
            ShellFixture.Pump();
            Assert.Equal(0, segments.SelectedIndex);
            window.KeyPress(Key.End, RawInputModifiers.None, PhysicalKey.End, null);
            ShellFixture.Pump();
            Assert.Equal(2, segments.SelectedIndex);

            // Always one choice: a click on the chosen segment keeps it, and it is drawn in Shell.Segment.Selected.
            Assert.Equal(SelectionMode.Single | SelectionMode.AlwaysSelected, segments.SelectionMode);
            var chosen = (SegmentedControlItem)segments.ContainerFromIndex(2)!;
            var fill = chosen.GetVisualDescendants().OfType<Border>().First(b => b.Name == "PART_Border");
            Assert.Equal(Token("Shell.Segment.Selected", ThemeService.HarborLight), BrushColor(fill.Background));
            Assert.Equal(Token("Shell.Segment.Selected.Foreground", ThemeService.HarborLight), BrushColor(chosen.Foreground));

            // Screen readers: a list named by the control, its items by their text, the chosen one selected.
            var peer = Avalonia.Automation.Peers.ControlAutomationPeer.CreatePeerForElement(segments);
            Assert.Equal("View", peer.GetName());
            Assert.Equal(Avalonia.Automation.Peers.AutomationControlType.List, peer.GetAutomationControlType());
            var items = Enumerable.Range(0, 3).Select(i => Avalonia.Automation.Peers.ControlAutomationPeer.CreatePeerForElement(segments.ContainerFromIndex(i)!)).ToList();
            Assert.Equal(["Tree", "List", "Raw"], items.Select(i => i.GetName()));
            Assert.All(items, i => Assert.Equal(Avalonia.Automation.Peers.AutomationControlType.ListItem, i.GetAutomationControlType()));
            Assert.True(((Avalonia.Automation.Provider.ISelectionItemProvider)items[2]).IsSelected);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_dialogs_buttons_follow_the_OSs_order_with_one_primary(bool primaryLast)
    {
        var dialog = new DialogViewModel { PrimaryLast = primaryLast };
        var shown = dialog.ShowAsync(new DialogRequest("Generate Constructor", "Members to initialize:", ["OK", "Cancel"])
        {
            Icon = "tools",
            Context = "Person · ICalculatorService.cs",
            Choices = [new DialogChoice("name", "string Name", true) { Icon = "tools" }, new DialogChoice("age", "int Age", false, "property")],
            Reorderable = true,
        });
        ShellFixture.Pump();
        var view = new DialogView { DataContext = dialog, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
        var window = Show(view, ThemeService.HarborLight, 900, 600);
        try
        {
            // Windows and Linux: OK then Cancel; macOS: Cancel then OK. The default (Enter) is the only primary.
            string[] expected = primaryLast ? ["Cancel", "OK"] : ["OK", "Cancel"];
            Assert.Equal(expected, dialog.Buttons.Select(b => b.Label));
            var footer = view.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("dialogFooter"));
            var buttons = footer.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("dialog")).ToList();
            Assert.Equal(expected, buttons.Select(b => b.Content as string));
            Assert.Single(buttons, b => b.Classes.Contains("accent"));
            Assert.Equal("OK", buttons.Single(b => b.Classes.Contains("accent")).Content);
            Assert.All(buttons, b => Assert.True(b.Bounds.Width >= 80, $"{b.Content} is {b.Bounds.Width} px wide"));
            Assert.True(buttons[0].TranslatePoint(default, footer)!.Value.X < buttons[1].TranslatePoint(default, footer)!.Value.X);
            Assert.True(buttons[^1].TranslatePoint(new Point(buttons[^1].Bounds.Width, 0), footer)!.Value.X > footer.Bounds.Width - 30, "the buttons are right-aligned");
            Assert.Equal(Token("Shell.Dialog.Footer", ThemeService.HarborLight), BrushColor(footer.Background));

            // The header: the title at 14 px, the context line, the icon; the dialog is 520 px wide.
            Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Generate Constructor" && t.FontSize == 14);
            Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Person · ICalculatorService.cs");
            Assert.True(dialog.HasIcon);
            Assert.Equal("info", dialog.IconTone);
            Assert.InRange(view.FindControl<Border>("Frame")!.Bounds.Width, 520, 720);

            // The checklist: Select all is mixed with one of two checked; checking it checks both, and the count follows.
            Assert.Null(dialog.AllChecked);
            Assert.Equal("1 of 2", dialog.CheckedSummary);
            dialog.AllChecked = false; // a click on a mixed box checks all
            Assert.True(dialog.AllChecked);
            Assert.Equal("2 of 2", dialog.CheckedSummary);
            dialog.AllChecked = false;
            Assert.Equal("0 of 2", dialog.CheckedSummary);

            // Escape cancels, from anywhere in the dialog.
            buttons[0].Focus();
            ShellFixture.Pump();
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            ShellFixture.Pump();
            Assert.False(dialog.IsOpen);
            Assert.Null((await shown).Button);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void The_control_set_renders_in_every_theme()
    {
        var snapshots = Environment.GetEnvironmentVariable("CODALUS_UI_SNAPSHOTS");
        foreach (var (id, variant) in ThemeService.Themes)
        {
            var window = Show(new ScrollViewer { Content = ControlGallery.Build() }, variant, 1280, 1400);
            try
            {
                for (var i = 0; i < 3; i++)
                {
                    ShellFixture.Pump();
                }

                using var frame = window.CaptureRenderedFrame();
                Assert.NotNull(frame);
                if (snapshots is { Length: > 0 })
                {
                    Directory.CreateDirectory(snapshots);
                    frame.Save(Path.Combine(snapshots, $"controls-{id}.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
                }
            }
            finally
            {
                window.Close();
            }
        }
    }
}
