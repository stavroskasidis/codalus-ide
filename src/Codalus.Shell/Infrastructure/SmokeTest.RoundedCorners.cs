using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.VisualTree;
using Codalus.Shell.Controls;
using Codalus.Shell.Theming;
using Codalus.Workbench.Settings;
using MenuItem = Avalonia.Controls.MenuItem;

namespace Codalus.Shell.Infrastructure;

/// <summary>
/// Rounded corners' smoke step (D46, <c>design/controls/RoundedCorners.dc.html</c>): Classic's cards, tabs and toolbar in
/// the shipped app, and its popups' rounded corners where the window system can't make a window's corners transparent.
/// On X11 without a compositor (CI's Xvfb) a popup is an opaque window: what its rounded border leaves uncovered shows
/// the popup window's fallback fill, which must be the theme's surface, not black squares at the corners. The
/// pictures (<c>smoke-test-rounded-corners-*.png</c>) are the gallery's.
/// </summary>
internal sealed partial class SmokeTest
{
    private async Task RoundedCornersStepAsync()
    {
        var before = shell.Workbench.Settings.GetLayerValue(ThemeService.SettingKey, SettingScope.User);
        try
        {
            foreach (var theme in new[] { "light", "dark" })
            {
                await shell.Workbench.Settings.SetAsync(ThemeService.SettingKey, theme, SettingScope.User, CancellationToken.None).ConfigureAwait(true);
                await WaitAsync(() => shell.Theme.ActiveTheme == theme, $"Classic {theme} applies").ConfigureAwait(true);
                await RenderedAsync().ConfigureAwait(true);

                // The shapes: the document well's card, the frame's edge under the tabs, the toolbar's card, the 6 px gutter.
                var visuals = window.GetVisualDescendants().ToList();
                var card = visuals.OfType<Border>().FirstOrDefault(b => b.Name == "PART_Card" && b.IsEffectivelyVisible) ?? throw new SmokeFailure($"{theme}: no document well card");
                Check(card.CornerRadius == new CornerRadius(4) && card.BorderThickness == new Thickness(1), $"{theme}: the document well is a 4 px card");
                Check(visuals.OfType<TabStripEdge>().Any(e => e.IsEffectivelyVisible && e.Bounds.Height == 1), $"{theme}: the frame's edge is drawn under the tabs");
                Check(visuals.OfType<Border>().Any(b => b.Classes.Contains("standardToolbar") && b.CornerRadius == new CornerRadius(4)), $"{theme}: the toolbar is a 4 px card");
                Check(visuals.OfType<Dock.Controls.ProportionalStackPanel.ProportionalStackPanelSplitter>().Any(s => s.IsEffectivelyVisible && s.Thickness == 6), $"{theme}: 6 px between the cards");
                Screenshot($"smoke-test-rounded-corners-{theme}.png");

                // A menu's popup: its 6 px corners, and what's under them where the popup is a window of its own.
                if (OperatingSystem.IsMacOS())
                {
                    continue; // the menus are the system's menu bar
                }

                var bar = visuals.OfType<Menu>().FirstOrDefault(m => m.Name == "MenuBar") ?? throw new SmokeFailure("no menu bar");
                var file = bar.Items.OfType<MenuItem>().First();
                file.Open();
                try
                {
                    await WaitAsync(() => PopupOf(file) is { IsOpen: true, Child: { } child } && child.Bounds.Height > 0, $"{theme}: the File menu opens").ConfigureAwait(true);
                    await RenderedAsync().ConfigureAwait(true);
                    var popup = PopupOf(file)!;
                    var presenter = popup.Child!.GetSelfAndVisualDescendants().OfType<Border>().FirstOrDefault(b => b.CornerRadius != default);
                    Check(presenter?.CornerRadius == new CornerRadius(6), $"{theme}: the menu's corners are 6 px (they are {presenter?.CornerRadius})");
                    if (TopLevel.GetTopLevel(popup.Child) is { } host && host != window)
                    {
                        var level = host.ActualTransparencyLevel;
                        var fallback = (host.TransparencyBackgroundFallback as ISolidColorBrush)?.Color;
                        var surface = Token("Shell.Background", ThemeService.VariantOf(theme));
                        shell.Workbench.Log.AppendLine($"[smoke] {theme}: the menu's popup window has transparency {level}, fallback {fallback?.ToString() ?? "none"}");
                        if (level == WindowTransparencyLevel.None)
                        {
                            Check(fallback is { } color && color == surface, $"{theme}: without transparency (no compositor) the menu's corners show the window's color {surface}, not {fallback?.ToString() ?? "nothing"}");
                        }
                    }

                    Screenshot($"smoke-test-rounded-corners-menu-{theme}.png", file);
                }
                finally
                {
                    file.Close();
                }
            }
        }
        finally
        {
            await shell.Workbench.Settings.SetAsync(ThemeService.SettingKey, before, SettingScope.User, CancellationToken.None).ConfigureAwait(true);
        }
    }
}
