using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Styling;
using Avalonia.VisualTree;
using DaedalusStudio.Shell.Controls;
using DaedalusStudio.Shell.Docking;
using DaedalusStudio.Shell.Theming;
using DaedalusStudio.Workbench.Settings;

namespace DaedalusStudio.Shell.Tests;

/// <summary>
/// M34d (UI polish, round 2): WCAG AA contrast for every token pair in every theme, no text cut off without an
/// ellipsis at the tool windows' minimum width, toolbars that overflow instead of wrapping, and the document tabs'
/// states (selected without the focus, preview on the right, pinned, colored by project).
/// </summary>
public partial class PolishTests
{
    /// <summary>The narrowest a docked tool window is expected to be (Shell.Size.ToolWindowMinWidth).</summary>
    private const double ToolWindowMinWidth = 280;

    private static readonly (string Name, ThemeVariant Variant)[] s_themes =
        [("Blue", ThemeService.Blue), ("Light", ThemeVariant.Light), ("Dark", ThemeVariant.Dark), ("High Contrast", ThemeService.HighContrast)];

    // ------------------------------------------------------------------ contrast

    [AvaloniaFact]
    public void Every_token_pair_meets_WCAG_AA_in_every_theme()
    {
        var pairs = File.ReadAllLines(Path.Combine(ShellFixture.RepoRoot, "tests", "DaedalusStudio.Shell.Tests", "TestData", "contrast-pairs.txt"))
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Select(p => (Foreground: p[0], Background: p[1], Minimum: double.Parse(p[2], CultureInfo.InvariantCulture)))
            .ToList();
        var failures = new List<string>();
        foreach (var (name, variant) in s_themes)
        {
            foreach (var (foreground, background, minimum) in pairs)
            {
                var fg = Color(foreground, variant);
                var bg = Color(background, variant);
                var ratio = Contrast(fg, bg);
                if (ratio < minimum)
                {
                    failures.Add($"{name}: {foreground} {fg} on {background} {bg} is {ratio:0.00}:1, below {minimum}:1");
                }
            }
        }

        Assert.Empty(failures);

        // Every foreground token is checked against what it is drawn on: a new one must be added to the list.
        var tokens = File.ReadAllText(Path.Combine(ShellFixture.RepoRoot, "src", "DaedalusStudio.Shell", "Theming", "Tokens.axaml"));
        var foregrounds = KeyPattern().Matches(tokens).Select(m => m.Groups[1].Value).Where(k => k.StartsWith("Shell.", StringComparison.Ordinal) && k.EndsWith("Foreground", StringComparison.Ordinal)).Distinct();
        var listed = pairs.Select(p => p.Foreground).ToHashSet(StringComparer.Ordinal);
        Assert.All(foregrounds, key => Assert.Contains(key, listed));
    }

    private static Color Color(string key, ThemeVariant variant)
    {
        Assert.True(Application.Current!.TryGetResource(key, variant, out var value) && value is ISolidColorBrush, $"{key} is not a color in {variant}");
        return ((ISolidColorBrush)value!).Color;
    }

    /// <summary>WCAG 2.1's contrast ratio of two opaque colors.</summary>
    internal static double Contrast(Color a, Color b)
    {
        static double Channel(byte value)
        {
            var c = value / 255.0;
            return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        static double Luminance(Color c) => 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
        var (la, lb) = (Luminance(a), Luminance(b));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    [GeneratedRegex("x:Key=\"([^\"]+)\"")]
    private static partial Regex KeyPattern();

    // ------------------------------------------------------------------ clipped text

    [AvaloniaFact]
    public async Task Tool_windows_at_their_minimum_width_cut_no_text_without_an_ellipsis()
    {
        await using var f = ShellFixture.Create();
        var solution = f.CopyFixture();
        f.Shell.Trust.Trust(Path.GetDirectoryName(Path.GetDirectoryName(solution))!);
        await f.Shell.OpenSolutionAsync(solution);
        await ShellFixture.PumpUntil(() => f.Shell.Explorer.Rows.Count > 3, seconds: 180);

        var problems = new List<string>();
        foreach (var descriptor in f.Shell.Workbench.ToolWindows.All.ToList())
        {
            if (f.Shell.Layout.ShowToolWindow(descriptor.Id, focus: false) is not { } tool)
            {
                continue;
            }

            ShellFixture.Pump();
            var window = new Window { Width = ToolWindowMinWidth, Height = 420, Content = new ContentControl { Content = tool } };
            window.Show();
            ShellFixture.Pump();
            ShellFixture.Pump();
            problems.AddRange(Clipped(window).Select(p => $"{descriptor.Id}: {p}"));
            window.Close();
            ShellFixture.Pump();
        }

        Assert.True(problems.Count == 0, "Text cut off at " + ToolWindowMinWidth + " px:\n" + string.Join('\n', problems));
    }

    /// <summary>
    /// TextBlocks under <paramref name="root"/> whose text is cut off: wider than what their clipping ancestors show,
    /// without an ellipsis (or with one but no tooltip to read the rest), where the content doesn't scroll sideways.
    /// </summary>
    internal static IEnumerable<string> Clipped(Visual root)
    {
        var rootRect = new Rect(root.Bounds.Size);
        foreach (var text in root.GetVisualDescendants().OfType<TextBlock>())
        {
            var content = text.Text ?? text.Inlines?.Text;
            if (!text.IsEffectivelyVisible || string.IsNullOrWhiteSpace(content) || text.Bounds.Width < 1 || text.Bounds.Height < 1
                || text.TextWrapping != TextWrapping.NoWrap || text.FindAncestorOfType<TextBox>() is not null)
            {
                continue;
            }

            if (text.TranslatePoint(default, root) is not { } origin)
            {
                continue;
            }

            // What its clipping ancestors (and the root) show of it, sideways and up and down.
            var box = new Rect(origin, text.Bounds.Size);
            double left = Math.Max(box.Left, 0), right = Math.Min(box.Right, rootRect.Right);
            double top = Math.Max(box.Top, 0), bottom = Math.Min(box.Bottom, rootRect.Bottom);
            var scrolls = false;
            for (var v = text.GetVisualParent(); v is not null && v != root; v = v.GetVisualParent())
            {
                if (v is ScrollContentPresenter { CanHorizontallyScroll: true })
                {
                    scrolls = true;
                    break;
                }

                if (v.ClipToBounds && v.TranslatePoint(default, root) is { } at)
                {
                    left = Math.Max(left, at.X);
                    right = Math.Min(right, at.X + v.Bounds.Width);
                    top = Math.Max(top, at.Y);
                    bottom = Math.Min(bottom, at.Y + v.Bounds.Height);
                }
            }

            if (scrolls || bottom - top < 1 || InOverflow(text))
            {
                continue; // scrolls sideways, or is scrolled out of view
            }

            var visible = new Rect(left, top, Math.Max(0, right - left), bottom - top);
            var layout = new TextLayout(content, new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch), text.FontSize, null);
            var needed = layout.WidthIncludingTrailingWhitespace + text.Padding.Left + text.Padding.Right;
            if (needed <= visible.Width + 1)
            {
                continue;
            }

            var label = content.Length > 40 ? content[..40] + "…" : content;
            if (text.TextTrimming == TextTrimming.None)
            {
                yield return $"\"{label}\" needs {needed:0} px, {visible.Width:0} px shown, no ellipsis ({Describe(text, root)})";
            }
            else if (box.Width > visible.Width + 1)
            {
                yield return $"\"{label}\" is trimmed in a box wider than shown ({box.Width:0} > {visible.Width:0} px), so its ellipsis is cut off ({Describe(text, root)})";
            }
            else if (!HasTooltip(text))
            {
                yield return $"\"{label}\" is trimmed without a tooltip ({Describe(text, root)})";
            }
        }
    }

    /// <summary>Whether the text is on a toolbar item that moved into the toolbar's overflow menu.</summary>
    private static bool InOverflow(Visual text)
    {
        Visual? child = text;
        for (var v = text.GetVisualParent(); v is not null; child = v, v = v.GetVisualParent())
        {
            if (v is ToolbarPanel panel)
            {
                return child is Control item && panel.Overflowed.Contains(item);
            }
        }

        return false;
    }

    private static bool HasTooltip(Visual text)
    {
        var depth = 0;
        for (Visual? v = text; v is not null && depth < 8; v = v.GetVisualParent(), depth++)
        {
            if (v is Control control && ToolTip.GetTip(control) is not null)
            {
                return true;
            }

            if (v is ListBoxItem or TreeViewItem)
            {
                break;
            }
        }

        return false;
    }

    private static string Describe(Visual visual, Visual root)
    {
        var names = new List<string>();
        for (var v = visual; v is not null && v != root && names.Count < 6; v = v.GetVisualParent())
        {
            names.Add(v is Control { Name: { Length: > 0 } name } ? $"{v.GetType().Name}#{name}" : v.GetType().Name);
        }

        names.Reverse();
        return string.Join(" > ", names);
    }

    // ------------------------------------------------------------------ toolbars

    [AvaloniaFact]
    public void A_narrow_toolbar_moves_what_does_not_fit_into_its_overflow_menu()
    {
        var clicked = 0;
        var panel = new ToolbarPanel();
        for (var i = 0; i < 8; i++)
        {
            var button = new Button { Content = new Icon { Kind = "play" } };
            button.Classes.Add("toolbar");
            Avalonia.Automation.AutomationProperties.SetName(button, "Command " + i);
            button.Click += (_, _) => clicked++;
            panel.Children.Add(button);
        }

        var combo = new ComboBox { ItemsSource = new[] { "One", "Two" }, SelectedIndex = 0, Width = 90 };
        combo.Classes.Add("toolbar");
        Avalonia.Automation.AutomationProperties.SetName(combo, "Group by");
        panel.Children.Add(combo);
        var search = new TextBox { MinWidth = 60, PlaceholderText = "Search" };
        panel.Children.Add(search);

        var window = new Window { Width = 600, Height = 100, Content = panel };
        window.Show();
        ShellFixture.Pump();
        Assert.Empty(panel.Overflowed);
        Assert.True(search.Bounds.Width >= 60, $"the search box fills the rest ({search.Bounds.Width})");
        Assert.True(panel.Bounds.Height <= 30, $"one row ({panel.Bounds.Height} px)");

        window.Width = 200;
        ShellFixture.Pump();
        Assert.Contains(combo, panel.Overflowed);
        Assert.True(panel.Overflowed.Count >= 3, $"{panel.Overflowed.Count} items overflow");
        Assert.True(panel.Bounds.Height <= 30, "still one row");
        Assert.DoesNotContain(search, panel.Overflowed);

        // The overflow menu's entries run what the buttons run and choose in the combo box.
        var hiddenButton = panel.Overflowed.OfType<Button>().First();
        var entry = (MenuItem)ToolbarPanel.Entry(hiddenButton, panel)!;
        Assert.StartsWith("Command ", entry.Header as string);
        entry.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
        Assert.Equal(1, clicked);
        var choices = (MenuItem)ToolbarPanel.Entry(combo, panel)!;
        Assert.Equal("Group by", choices.Header);
        ((MenuItem)choices.Items[1]!).RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
        Assert.Equal(1, combo.SelectedIndex);
        window.Close();
    }

    // ------------------------------------------------------------------ document tabs

    [AvaloniaFact]
    public async Task Tabs_keep_their_titles_readable_preview_on_the_right_pinned_on_the_left_and_colored_by_project()
    {
        await using var f = ShellFixture.Create();
        var solution = f.CopyFixture();
        f.Shell.Trust.Trust(Path.GetDirectoryName(Path.GetDirectoryName(solution))!);
        await f.Shell.OpenSolutionAsync(solution);
        await ShellFixture.PumpUntil(() => f.Shell.Solutions.Projects.Count > 1 && f.Shell.Solutions.Progress is { Completed: true }, seconds: 180);
        // Three source files of the loaded projects (the fixture also has files no project of the solution owns).
        var files = f.Shell.Solutions.Projects.Values
            .SelectMany(p => p.Items.Where(i => i.Link is null).Select(i => i.Path))
            .Where(p => p.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) && File.Exists(p))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Take(3)
            .ToList();
        Assert.True(files.Count == 3, "three source files in the fixture's projects");

        await f.Shell.OpenFileAsync(files[0], preview: false);
        await f.Shell.OpenFileAsync(files[1], preview: true);
        await f.Shell.OpenFileAsync(files[2], preview: false);
        ShellFixture.Pump();
        var tabs = f.Shell.Layout.Documents.ToList();
        Assert.Equal(files[1], tabs[^1].FilePath); // the preview tab stays on the right
        Assert.True(tabs[^1].IsPreview);

        // Pinning moves a tab to the left; Close All But Pinned keeps it.
        var third = tabs.Single(d => d.FilePath == files[2]);
        f.Shell.Layout.SetPinned(third, true);
        Assert.Equal(third, f.Shell.Layout.Documents.First());
        Assert.False(third.IsPreview);

        // Colors by project, once the setting is on.
        Assert.All(f.Shell.Layout.Documents, d => Assert.Null(d.TabAccent));
        await f.Shell.Workbench.Settings.SetAsync(ShellHost.ColorTabsByProjectSetting, true, SettingScope.User, CancellationToken.None);
        await ShellFixture.PumpUntil(() => f.Shell.Layout.Documents.All(d => d.TabAccent is not null));
        Assert.Equal(new Thickness(0, 0, 0, 3), third.TabAccentThickness);

        // The selected tab of a pane without the focus is readable in every theme (it was white on light gray).
        foreach (var (name, variant) in s_themes)
        {
            Assert.True(Contrast(Color("Shell.Tab.Selected.Foreground", variant), Color("Shell.Tab.Selected", variant)) >= 4.5, name);
        }

        f.Shell.Run("workbench.closeUnpinnedDocuments");
        await ShellFixture.PumpUntil(() => f.Shell.Layout.Documents.Count() == 1);
        Assert.Equal(third, f.Shell.Layout.Documents.Single());
    }
}
