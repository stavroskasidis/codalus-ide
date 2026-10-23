using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.VisualTree;
using Codalus.Projects.Protocol;
using Codalus.Shell.Controls;
using Codalus.Shell.Theming;
using Codalus.Shell.ViewModels;
using Codalus.Shell.Views;

namespace Codalus.Shell.Tests;

/// <summary>
/// NuGet's verified-owner badge in the package manager (D39, PackageManager.dc.html): after a Browse row's ID and after the
/// details' heading, with its tooltip and accessible name, in the icon palette's blue (High contrast: the text's color).
/// The rows and details are set directly; the host's rules are in the Projects tests (PackageVerifiedOwnerTests).
/// </summary>
public sealed class PackageVerifiedBadgeTests
{
    private const string Tip = "Verified owner: nuget.org reserved this ID prefix for one of the package's owners. The authors shown are not verified.";
    private const string LongId = "Contoso.Extensions.DependencyInjection.Abstractions.Configuration.Binder.Generators";

    private static async Task<(PackageManagerViewModel Manager, Window Window)> ShowAsync(ShellFixture f, double width = 1100)
    {
        var vm = new PackageManagerViewModel(f.Shell.Packages, f.Shell.Solutions, project: null, new PackageManagerContext(f.Shell.Dialogs, f.Shell.Workbench.Notifications, _ => { }, () => { }, () => { }));
        // Without a solution the sources and the first search fail; that load is awaited here, before the view gets the
        // view model (so it doesn't start another), and the rows are then the test's own.
        await vm.ActivateAsync();
        Assert.True(vm.HasError);
        var view = new PackageManagerView();
        var window = new Window { Width = width, Height = 600, Content = view };
        window.Show();
        view.DataContext = vm;
        vm.Rows.Clear();
        vm.Rows.Add(new PackageRowViewModel("Contoso.Verified") { Version = "1.2.0", Authors = "Contoso", Downloads = 1_200_000, IsVerified = true, Source = "nuget.org" });
        vm.Rows.Add(new PackageRowViewModel("Contoso.Unverified") { Version = "1.0.0", Authors = "Contoso", Downloads = 900, Source = "nuget.org" });
        vm.Rows.Add(new PackageRowViewModel(LongId) { Version = "3.0.0", Authors = "Contoso", Downloads = 5_000, IsVerified = true, Source = "nuget.org" });
        ShellFixture.Pump();
        ShellFixture.Pump();
        return (vm, window);
    }

    private static ListBoxItem Item(Window window, string id)
    {
        var items = window.GetVisualDescendants().OfType<ListBoxItem>().ToList();
        var item = items.SingleOrDefault(i => i.DataContext is PackageRowViewModel { Id: var rowId } && rowId == id);
        Assert.True(item is not null, $"no row {id} among {string.Join(", ", items.Select(i => (i.DataContext as PackageRowViewModel)?.Id ?? "?"))}");
        return item;
    }

    private static Icon Badge(Visual within, string name) => within.GetVisualDescendants().OfType<Icon>().Single(i => i.Name == name);

    private static Rect BoundsIn(Visual visual, Visual relativeTo) => new(visual.TranslatePoint(default, relativeTo)!.Value, visual.Bounds.Size);

    private static PackageDetails Details(string id, bool verified, string source = "nuget.org") =>
        new(id, "1.2.0", [new PackageVersionInfo("1.2.0", 600, false, -1), new PackageVersionInfo("1.1.0", 600, false, -1)], null, "A package of Contoso's.", "Contoso",
            "MIT", null, null, null, false, "2026-10-01", 1200, null, [], null, [], source, verified);

    [AvaloniaFact]
    public async Task A_verified_rows_ID_is_followed_by_the_badge_with_its_tooltip_and_name()
    {
        await using var f = ShellFixture.Create();
        var (vm, window) = await ShowAsync(f);

        var verified = Item(window, "Contoso.Verified");
        var badge = Badge(verified, "VerifiedBadge");
        var id = verified.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == "Contoso.Verified" && t.IsEffectivelyVisible);
        Assert.True(badge.IsEffectivelyVisible);
        Assert.Equal("verified", badge.Kind);
        Assert.Equal(new Size(14, 14), badge.Bounds.Size);
        var idBounds = BoundsIn(id, verified);
        var badgeBounds = BoundsIn(badge, verified);
        Assert.InRange(badgeBounds.Left - idBounds.Right, 3.5, 4.5); // 4 px after the ID
        Assert.InRange(badgeBounds.Center.Y - idBounds.Center.Y, -1, 1); // centered on the ID's line
        Assert.Equal(Tip, ToolTip.GetTip(badge));
        Assert.Equal("verified owner", AutomationProperties.GetName(badge));
        Assert.EndsWith(", verified owner", vm.Rows[0].AccessibleName, StringComparison.Ordinal);
        Assert.Equal(vm.Rows[0].AccessibleName, AutomationProperties.GetName(verified));

        // Before the byline, which follows the badge.
        var byline = verified.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == "by Contoso, 1.2M downloads");
        Assert.True(BoundsIn(byline, verified).Left >= badgeBounds.Right, "the byline follows the badge");

        // An unverified row: no badge, and nothing about it in its name.
        var unverified = Item(window, "Contoso.Unverified");
        Assert.False(Badge(unverified, "VerifiedBadge").IsVisible);
        Assert.DoesNotContain("verified owner", vm.Rows[1].AccessibleName, StringComparison.Ordinal);

        // A long ID trims and keeps its badge, inside the name column.
        var longItem = Item(window, LongId);
        var longBadge = Badge(longItem, "VerifiedBadge");
        var longId = longItem.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == LongId && t.IsEffectivelyVisible);
        var column = longId.GetVisualAncestors().OfType<StackPanel>().First();
        Assert.True(longBadge.IsEffectivelyVisible);
        Assert.True(longId.TextLayout.TextLines[0].HasCollapsed, "the long ID trims");
        Assert.True(BoundsIn(longBadge, column).Right <= column.Bounds.Width + 0.5, "the badge stays in the name column");
        window.Close();
    }

    [AvaloniaFact]
    public async Task The_details_heading_shows_the_badge_when_the_details_say_verified()
    {
        await using var f = ShellFixture.Create();
        var (vm, window) = await ShowAsync(f);
        vm.Selected = vm.Rows[0];
        await ShellFixture.PumpUntil(() => vm.Details is { IsLoading: false });

        var heading = window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "DetailsHeading");
        var badge = Badge(window, "HeadingVerifiedBadge");
        Assert.Equal(24, heading.LineHeight);
        Assert.False(badge.IsVisible);

        vm.Details!.Apply(Details("Contoso.Verified", verified: true));
        ShellFixture.Pump();
        ShellFixture.Pump();
        Assert.True(badge.IsEffectivelyVisible);
        Assert.Equal(new Size(14, 14), badge.Bounds.Size);
        var headingBounds = BoundsIn(heading, (Visual)heading.Parent!);
        var badgeBounds = BoundsIn(badge, (Visual)heading.Parent!);
        Assert.InRange(badgeBounds.Left - headingBounds.Right, 3.5, 4.5); // right after a short heading
        Assert.InRange(badgeBounds.Top - headingBounds.Top, 4.5, 5.5); // centered on the heading's 24 px first line
        Assert.Equal(Tip, ToolTip.GetTip(badge));
        Assert.Equal("verified owner", AutomationProperties.GetName(badge));

        // Another source's details (or nuget.org's without the flag): none, and the heading doesn't move.
        var before = headingBounds;
        vm.Details.Apply(Details("Contoso.Verified", verified: false, source: "local"));
        ShellFixture.Pump();
        Assert.False(badge.IsVisible);
        Assert.Equal(before, BoundsIn(heading, (Visual)heading.Parent!));
        window.Close();
    }

    [AvaloniaFact]
    public async Task The_badge_is_the_palettes_blue_and_the_texts_color_in_High_contrast_in_every_theme()
    {
        await using var f = ShellFixture.Create();
        var (vm, window) = await ShowAsync(f);
        vm.Selected = vm.Rows[2]; // the long one: the verified row tested unselected stays unselected
        await ShellFixture.PumpUntil(() => vm.Details is { IsLoading: false });
        var problems = new List<string>();
        foreach (var (theme, variant) in ThemeService.Themes)
        {
            window.RequestedThemeVariant = variant;
            ShellFixture.Pump();
            ShellFixture.Pump();
            using var frame = window.CaptureRenderedFrame()!;
            var highContrast = ThemeService.IsHighContrast(variant);
            Assert.True(window.TryFindResource("Shell.Icon.Blue", variant, out var blue) && blue is ISolidColorBrush);
            foreach (var (row, state) in new[] { ("Contoso.Verified", "unselected"), (LongId, "selected") })
            {
                var item = Item(window, row);
                var badge = Badge(item, "VerifiedBadge");
                var expected = highContrast ? ((ISolidColorBrush)badge.Foreground!).Color : ((ISolidColorBrush)blue!).Color;
                var (matching, background) = Sample(frame, badge, item, window, expected);
                if (matching < 20)
                {
                    problems.Add($"{theme}, {state}: {matching} of the badge's pixels are {expected}");
                }

                if (Contrast(expected, background) < 3)
                {
                    problems.Add($"{theme}, {state}: the badge's {expected} on {background} is {Contrast(expected, background):0.00}:1");
                }
            }
        }

        Assert.True(problems.Count == 0, string.Join('\n', problems));
        window.Close();
    }

    /// <summary>How many of the badge's pixels are <paramref name="expected"/>, and the row's background beside the ID.</summary>
    private static (int Matching, Color Background) Sample(WriteableBitmap frame, Icon badge, ListBoxItem item, TopLevel top, Color expected)
    {
        var scale = top.RenderScaling;
        using var buffer = frame.Lock();
        Color At(Point point)
        {
            var at = point * scale;
            var offset = ((int)at.Y * buffer.RowBytes) + ((int)at.X * 4);
            var bytes = new byte[4];
            System.Runtime.InteropServices.Marshal.Copy(buffer.Address + offset, bytes, 0, 4);
            return buffer.Format == PixelFormat.Rgba8888 ? Color.FromArgb(bytes[3], bytes[0], bytes[1], bytes[2]) : Color.FromArgb(bytes[3], bytes[2], bytes[1], bytes[0]);
        }

        var origin = badge.TranslatePoint(default, top)!.Value;
        var matching = 0;
        for (var y = 0.5; y < badge.Bounds.Height; y++)
        {
            for (var x = 0.5; x < badge.Bounds.Width; x++)
            {
                var color = At(origin + new Point(x, y));
                if (Math.Abs(color.R - expected.R) <= 6 && Math.Abs(color.G - expected.G) <= 6 && Math.Abs(color.B - expected.B) <= 6)
                {
                    matching++;
                }
            }
        }

        // The row's own background: just right of the badge, above the ID's text.
        var background = At(item.TranslatePoint(new Point(item.Bounds.Width - 3, 2), top)!.Value);
        return (matching, background);
    }

    private static double Contrast(Color a, Color b)
    {
        static double Luminance(Color c)
        {
            static double Channel(byte v) => v / 255.0 <= 0.03928 ? v / 255.0 / 12.92 : Math.Pow(((v / 255.0) + 0.055) / 1.055, 2.4);
            return (0.2126 * Channel(c.R)) + (0.7152 * Channel(c.G)) + (0.0722 * Channel(c.B));
        }

        var (l1, l2) = (Luminance(a), Luminance(b));
        return (Math.Max(l1, l2) + 0.05) / (Math.Min(l1, l2) + 0.05);
    }
}
