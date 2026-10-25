using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Codalus.Shell.Controls;
using Codalus.Shell.Theming;

namespace Codalus.Shell.Tests;

/// <summary>
/// The maintainer's rule (2026-10-13): every icon is colored, in the classic IDE's palette. Commands, tool windows and
/// the icons views declare have a palette color; file icons' fills map to the palette by hue; the palette exists in
/// every theme and High Contrast keeps it monochrome.
/// </summary>
public partial class IconColorTests
{
    private static readonly string[] s_palette = ["Blue", "Green", "Red", "Orange", "Purple", "Yellow", "Folder"];

    [Fact]
    public void Every_command_icon_has_a_palette_color()
    {
        foreach (var (command, icon) in CommandIcons.All)
        {
            var resolved = CommandIcons.For(command)!.Value;
            Assert.True(resolved.Accent is not null, $"{command} ({icon.Kind}) has no color");
            Assert.True(icon.Accent is not null || CommandIcons.HasColor(icon.Kind), $"the icon kind '{icon.Kind}' of {command} has no color of its own: add it to CommandIcons' kind colors");
        }
    }

    [AvaloniaFact]
    public async Task Every_tool_window_icon_has_a_palette_color()
    {
        await using var f = ShellFixture.Create();
        foreach (var descriptor in f.Shell.Workbench.ToolWindows.All.Where(d => d.Icon is not null))
        {
            Assert.True(CommandIcons.HasColor(descriptor.Icon!), $"the tool window {descriptor.Id}'s icon '{descriptor.Icon}' has no color: add it to CommandIcons' kind colors");
        }
    }

    [Fact]
    public void Every_icon_a_view_declares_has_a_palette_color()
    {
        var views = Directory.EnumerateFiles(Path.Combine(ShellFixture.RepoRoot, "src", "Codalus.Shell"), "*.axaml", SearchOption.AllDirectories);
        var missing = views
            .SelectMany(v => ViewIcon().Matches(File.ReadAllText(v)).Select(m => (View: Path.GetFileName(v), Kind: m.Groups[1].Value)))
            .Where(i => !i.Kind.Contains('@', StringComparison.Ordinal) && !i.Kind.StartsWith(IconSet.ClassicPrefix, StringComparison.Ordinal) && !i.Kind.StartsWith(IconSet.MaterialPrefix, StringComparison.Ordinal)
                        && CommandIcons.DefaultColorOf(i.Kind) is not null && !CommandIcons.HasColor(i.Kind)) // file icons take the palette by their fills
            .Select(i => $"{i.View}: {i.Kind}")
            .Distinct()
            .ToList();
        Assert.True(missing.Count == 0, "icons without a color of their own:\n" + string.Join('\n', missing));
    }

    [Theory]
    [InlineData(0xFFF44336u, "Red")]
    [InlineData(0xFF4CAF50u, "Green")]
    [InlineData(0xFF2196F3u, "Blue")]
    [InlineData(0xFFFFCA28u, "Yellow")]
    [InlineData(0xFFFF9800u, "Orange")]
    [InlineData(0xFF9C27B0u, "Purple")]
    [InlineData(0xFF795548u, "Folder")]
    [InlineData(0xFF90A4AEu, null)] // a gray-blue page stays gray, as the classic IDE's pages
    public void File_icon_fills_take_the_nearest_palette_color(uint argb, string? family) => Assert.Equal(family, Icon.PaletteColor(argb));

    [AvaloniaFact]
    public async Task The_palette_is_in_every_theme_and_monochrome_in_High_Contrast()
    {
        await using var f = ShellFixture.Create();
        foreach (var theme in ThemeService.Themes.Select(t => t.Variant))
        {
            foreach (var color in s_palette)
            {
                Assert.True(f.Window.TryFindResource("Shell.Icon." + color, theme, out var value) && value is ISolidColorBrush, $"Shell.Icon.{color} in {theme}");
                var brush = (ISolidColorBrush)value!;
                if (ThemeService.IsHighContrast(theme))
                {
                    Assert.Equal(Colors.White, brush.Color);
                }
                else
                {
                    Assert.NotEqual(Colors.White, brush.Color);
                }
            }
        }
    }

    [GeneratedRegex(@"(?:controls:Icon[^>]*?Kind|controls:ButtonIcon\.Kind|controls:SegmentedControlItem[^>]*?Icon)=""([a-z0-9:@-]+)""")]
    private static partial Regex ViewIcon();
}
