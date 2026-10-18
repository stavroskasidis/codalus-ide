using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Codalus.Shell.Controls;
using Codalus.Shell.Infrastructure;
using Codalus.Shell.Theming;
using Codalus.Shell.ViewModels;
using Codalus.Shell.Views;

namespace Codalus.Shell.Tests;

/// <summary>
/// M34h: the control set (docs/design/controls.md). The default button's template draws the design's tokens in every
/// state without a class; the segmented control moves its choice with the keyboard and names itself; the in-window
/// dialog shows its buttons in the OS's order with one primary, and the checklist's Select all and count; the whole set
/// renders in every theme (with <c>CODALUS_UI_SNAPSHOTS</c>, the pictures are saved there).
/// </summary>
public class ControlSetTests
{
    private static Color Token(string key, ThemeVariant variant)
    {
        Assert.True(Application.Current!.TryGetResource(key, variant, out var value) && value is ISolidColorBrush, $"{key} in {variant}");
        return ((ISolidColorBrush)value!).Color;
    }

    private static Color? BrushColor(IBrush? brush) => (brush as ISolidColorBrush)?.Color;

    private static void SetState(StyledElement element, string pseudoClass, bool on = true) => ((IPseudoClasses)element.Classes).Set(pseudoClass, on);

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
