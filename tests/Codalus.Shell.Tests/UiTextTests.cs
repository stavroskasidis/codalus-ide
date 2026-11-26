using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Codalus.Shell.Infrastructure;
using Codalus.Shell.Theming;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Settings;

namespace Codalus.Shell.Tests;

/// <summary>
/// The UI's text (#556): variable fonts laid out at the weight they're drawn at, text smoothed the system's way
/// (<c>workbench.textRendering</c>), the UI's font family and size (<c>workbench.fontFamily</c>, <c>workbench.fontSize</c>)
/// and, on Linux, its scale (<c>workbench.uiScale</c>, read before the windowing system starts).
/// </summary>
public sealed class UiTextTests
{
    /// <summary>Inter's variable font cut to ASCII (OFL-1.1, <c>samples/Fonts</c>): a <c>wght</c> axis from 100 to 900, default 400.</summary>
    private static GlyphTypeface VariableFixture()
    {
        using var file = File.OpenRead(Path.Combine(ShellFixture.RepoRoot, "samples", "Fonts", "InterVariable-ascii.ttf"));
        return ShellSystemFonts.FromFile(file) ?? throw new InvalidOperationException("The platform didn't read the variable font fixture.");
    }

    private static double Advance(GlyphTypeface face, string text)
    {
        var shaped = TextShaper.Current.ShapeText(text, new TextShaperOptions(face, 14));
        return shaped.Sum(g => g.GlyphAdvance);
    }

    [AvaloniaFact]
    public void A_variable_font_is_told_from_a_static_one()
    {
        var variable = VariableFixture();
        Assert.True(ShellSystemFonts.IsVariable(variable.PlatformTypeface));
        Assert.True(VariableFonts.IsVariable(variable));

        Assert.True(FontManager.Current.TryGetGlyphTypeface(Typeface.Default, out var system));
        Assert.SkipWhen(ShellSystemFonts.IsVariable(system.PlatformTypeface), $"The default family, {system.FamilyName}, is variable.");
        Assert.False(VariableFonts.IsVariable(system));
    }

    [AvaloniaFact]
    public void A_variable_font_is_laid_out_at_the_weight_asked_for_not_its_default_one()
    {
        // Without a weight, HarfBuzz lays every instance out at the default one's (Regular's) advances, while Skia draws
        // the instance fontconfig matched: bold glyphs at Regular's widths, crowded into each other (#556).
        const string text = "AI: Provider settings";
        var regular = VariableFixture();
        var untouched = Advance(regular, text);
        Assert.True(VariableFonts.Apply(regular, FontWeight.Normal));
        Assert.Equal(untouched, Advance(regular, text), 3);

        var bold = VariableFixture();
        Assert.True(VariableFonts.Apply(bold, FontWeight.Bold));
        var semibold = VariableFixture();
        Assert.True(VariableFonts.Apply(semibold, FontWeight.SemiBold));
        var light = VariableFixture();
        Assert.True(VariableFonts.Apply(light, FontWeight.Light));

        Assert.True(Advance(bold, text) > Advance(semibold, text), "Bold is wider than SemiBold");
        Assert.True(Advance(semibold, text) > untouched, "SemiBold is wider than Regular");
        Assert.True(Advance(light, text) < untouched, "Light is narrower than Regular");
    }

    [AvaloniaFact]
    public void A_static_font_is_left_as_it_is()
    {
        Assert.True(FontManager.Current.TryGetGlyphTypeface(Typeface.Default, out var system));
        Assert.SkipWhen(ShellSystemFonts.IsVariable(system.PlatformTypeface), $"The default family, {system.FamilyName}, is variable.");

        var before = Advance(system, "AI: Provider");
        Assert.False(VariableFonts.Apply(system, FontWeight.Bold));
        Assert.Equal(before, Advance(system, "AI: Provider"), 3);
    }

    [AvaloniaFact]
    public void An_installed_variable_family_is_laid_out_at_the_widths_each_weight_is_drawn_at()
    {
        // The collection's path, with the platform's own variable family when there is one (Bahnschrift or Segoe UI
        // Variable on Windows, the system font on macOS, Noto Sans or Cantarell on many Linux desktops). Skia draws the
        // instance the platform matched; HarfBuzz must lay each glyph out at that instance's advance, not the default
        // one's. Not "bold is wider": some families keep their widths across weights (Windows CI, run 45847369745,
        // Bahnschrift).
        using var fonts = ShellSystemFonts.Create();
        var family = fonts.Select(f => f.Name).FirstOrDefault(name =>
            fonts.TryGetGlyphTypeface(name, FontStyle.Normal, FontWeight.Normal, FontStretch.Normal, out var face)
            && ShellSystemFonts.IsVariable(face.PlatformTypeface)
            && "AIPorvidest".All(c => face.CharacterToGlyphMap.ContainsGlyph(c)));
        Assert.SkipWhen(family is null, "No installed font family is a variable font (CI's Linux runners have none; Windows has Bahnschrift).");

        // Bold before Regular, then SemiBold: neither the nearest instance already looked up nor a synthetic of the file.
        Assert.True(fonts.TryGetGlyphTypeface(family, FontStyle.Normal, FontWeight.Bold, FontStretch.Normal, out var bold));
        Assert.True(fonts.TryGetGlyphTypeface(family, FontStyle.Normal, FontWeight.Normal, FontStretch.Normal, out var regular));
        Assert.True(fonts.TryGetGlyphTypeface(family, FontStyle.Normal, FontWeight.SemiBold, FontStretch.Normal, out var semibold));
        Assert.All(new[] { bold, regular, semibold }, f => Assert.Equal(FontSimulations.None, f.FontSimulations));
        Assert.NotSame(bold, regular);
        Assert.NotSame(semibold, bold);

        // And weights a family may have no instance for, which the platform matches to its nearest (Source Sans has no
        // ExtraBold: 800 is drawn as its Bold).
        Assert.True(fonts.TryGetGlyphTypeface(family, FontStyle.Normal, FontWeight.Thin, FontStretch.Normal, out var thin));
        Assert.True(fonts.TryGetGlyphTypeface(family, FontStyle.Normal, FontWeight.ExtraBold, FontStretch.Normal, out var extraBold));
        foreach (var (name, face) in new[] { ("Regular", regular), ("SemiBold", semibold), ("Bold", bold), ("Thin", thin), ("ExtraBold", extraBold) })
        {
            foreach (var c in "AIPorvidest")
            {
                // One character a run: its advance alone, no kerning.
                var glyph = Assert.Single(TextShaper.Current.ShapeText(c.ToString(), new TextShaperOptions(face, 64)));
                var drawn = ShellSystemFonts.DrawnAdvances(face, [glyph.GlyphIndex], 64);
                Assert.SkipWhen(drawn is null, "The platform's typefaces aren't Skia's.");
                Assert.True(Math.Abs(glyph.GlyphAdvance - drawn![0]) < 0.1, $"{family} {name} '{c}': laid out at {glyph.GlyphAdvance:F2} px, drawn at {drawn[0]:F2} px");
            }
        }
    }

    [Theory]
    [InlineData("system", true, false, TextRenderingMode.Antialias)]
    [InlineData("system", true, true, TextRenderingMode.SubpixelAntialias)]
    [InlineData("system", false, false, TextRenderingMode.Alias)]
    [InlineData("", true, true, TextRenderingMode.SubpixelAntialias)]
    [InlineData("nonsense", true, false, TextRenderingMode.Antialias)]
    [InlineData("grayscale", true, true, TextRenderingMode.Antialias)]
    [InlineData("Subpixel", true, false, TextRenderingMode.SubpixelAntialias)]
    [InlineData("aliased", true, true, TextRenderingMode.Alias)]
    public void Each_choice_smooths_text_its_way_and_system_follows_the_system(string setting, bool antialias, bool subpixel, TextRenderingMode expected)
    {
        var options = TextRendering.Options(setting, new SystemTextRendering(antialias, subpixel, TextHintingMode.Light));

        Assert.Equal(expected, options.TextRenderingMode);
        Assert.Equal(TextHintingMode.Light, options.TextHintingMode);
    }

    [Theory]
    [InlineData(null, null, null, null, true, false, TextHintingMode.Light)] // fontconfig's defaults: grayscale, slight
    [InlineData(true, 1, true, 1, true, true, TextHintingMode.Light)] // rgb
    [InlineData(true, 2, true, 3, true, false, TextHintingMode.Strong)] // bgr: no subpixel order Skia draws
    [InlineData(true, 5, true, 0, true, false, TextHintingMode.None)] // none
    [InlineData(true, 0, false, 3, true, false, TextHintingMode.None)] // hinting off
    [InlineData(false, 1, true, 2, false, true, TextHintingMode.Strong)]
    public void Fontconfig_s_settings_read_as_the_system_s_text_rendering(bool? antialias, int? rgba, bool? hinting, int? hintStyle, bool smooth, bool sub, TextHintingMode hint) =>
        Assert.Equal(new SystemTextRendering(smooth, sub, hint), TextRendering.Fontconfig.From(antialias, rgba, hinting, hintStyle));

    [Theory]
    [InlineData(true, 2, 1, true, true)] // ClearType, RGB
    [InlineData(true, 2, 0, true, false)] // ClearType, BGR
    [InlineData(true, 1, 1, true, false)] // standard: grayscale
    [InlineData(false, 2, 1, false, true)]
    [InlineData(null, null, null, true, true)]
    public void Windows_font_smoothing_reads_as_the_system_s_text_rendering(bool? smoothing, int? type, int? orientation, bool smooth, bool sub) =>
        Assert.Equal(new SystemTextRendering(smooth, sub, TextHintingMode.Unspecified), TextRendering.Windows.From(smoothing, type, orientation));

    [AvaloniaFact]
    public async Task Every_window_gets_the_setting_s_text_rendering_and_a_change_reaches_the_open_ones()
    {
        await using var f = ShellFixture.Create();
        var settings = f.Shell.Workbench.Settings;

        await settings.SetAsync(TextRendering.Setting, "aliased", SettingScope.User, CancellationToken.None);
        await ShellFixture.PumpUntil(() => TextOptions.GetTextRenderingMode(f.Window) == TextRenderingMode.Alias);

        var other = new Window { Content = new TextBlock { Text = "AI: Provider" } };
        other.Show();
        try
        {
            ShellFixture.Pump();
            Assert.Equal(TextRenderingMode.Alias, TextOptions.GetTextRenderingMode(other));

            await settings.SetAsync(TextRendering.Setting, "grayscale", SettingScope.User, CancellationToken.None);
            await ShellFixture.PumpUntil(() => TextOptions.GetTextRenderingMode(other) == TextRenderingMode.Antialias);
            Assert.Equal(TextRenderingMode.Antialias, TextOptions.GetTextRenderingMode(f.Window));
            Assert.Equal(f.Shell.TextRendering.Current, TextOptions.GetTextOptions(other));
        }
        finally
        {
            other.Close();
        }
    }

    [Theory]
    [InlineData(12, 11, 12, 14, 18, 22, 24)]
    [InlineData(14, 13, 14, 16.5, 21, 26, 28)]
    [InlineData(10, 9, 10, 11.5, 15, 18, 20)]
    public void The_UI_font_size_scales_every_size_token_and_row_from_its_default(double body, double small, double bodyToken, double heading, double title, double row, double treeRow)
    {
        var sizes = UiFont.SizeTokens.ToDictionary(t => t.Key, t => UiFont.TokenSize(t.Size, body));
        Assert.Equal(small, sizes["Shell.FontSize.Small"]);
        Assert.Equal(bodyToken, sizes["Shell.FontSize.Body"]);
        Assert.Equal(heading, sizes["Shell.FontSize.Heading"]);
        Assert.Equal(title, sizes["Shell.FontSize.Title"]);
        var rows = UiFont.RowTokens.ToDictionary(t => t.Key, t => UiFont.RowSize(t.Size, body));
        Assert.Equal(row, rows["Shell.Size.Row"]);
        Assert.Equal(treeRow, rows["Shell.Size.TreeRow"]);
    }

    [Theory]
    [InlineData(12, 12)]
    [InlineData(3, UiFont.MinSize)]
    [InlineData(100, UiFont.MaxSize)]
    [InlineData(0, UiFont.DefaultSize)]
    [InlineData(double.NaN, UiFont.DefaultSize)]
    public void The_UI_font_size_is_kept_to_what_the_rows_hold(double setting, double expected) =>
        Assert.Equal(expected, UiFont.SizeOf(setting));

    [AvaloniaFact]
    public async Task The_UI_font_settings_change_the_window_s_font_and_sizes_at_once()
    {
        await using var f = ShellFixture.Create();
        var settings = f.Shell.Workbench.Settings;
        var family = FontManager.Current.DefaultFontFamily.Name;
        Assert.Equal(12, f.Window.FontSize);

        await settings.SetAsync(UiFont.SizeSetting, 15, SettingScope.User, CancellationToken.None);
        await settings.SetAsync(UiFont.FamilySetting, family, SettingScope.User, CancellationToken.None);
        await ShellFixture.PumpUntil(() => f.Window.FontSize == 15 && f.Window.FontFamily.Name == family, describe: () => $"{f.Window.FontSize} {f.Window.FontFamily}");

        Assert.True(f.Window.TryFindResource("Shell.FontSize.Heading", out var heading));
        Assert.Equal(17.5, heading);
        Assert.True(f.Window.TryFindResource("Shell.Size.TreeRow", out var treeRow));
        Assert.Equal(30d, treeRow);

        await settings.SetAsync(UiFont.FamilySetting, (System.Text.Json.JsonElement?)null, SettingScope.User, CancellationToken.None);
        await settings.SetAsync(UiFont.SizeSetting, (System.Text.Json.JsonElement?)null, SettingScope.User, CancellationToken.None);
        await ShellFixture.PumpUntil(() => f.Window.FontSize == 12 && f.Window.FontFamily == FontFamily.Default);
    }

    [AvaloniaFact]
    public async Task Fonts_and_Colors_opens_the_UI_font_s_settings_beside_the_editor_s()
    {
        await using var f = ShellFixture.Create();
        f.Shell.OpenFontsAndColors();
        var model = Assert.Single(f.Shell.Layout.Documents.OfType<FontsAndColorsDocument>()).ViewModel;

        model.OpenUiFontSettingsCommand.Execute(null);
        ShellFixture.Pump();

        var search = Assert.Single(f.Shell.Layout.Documents.OfType<SettingsDocument>()).ViewModel.Search;
        Assert.Equal(FontsAndColorsViewModel.UiFontQuery, search);
        var keys = f.Shell.Workbench.Settings.Descriptors.Select(d => d.Key).Where(k => k.Contains(search, StringComparison.Ordinal)).ToList();
        Assert.Contains(UiFont.FamilySetting, keys);
        Assert.Contains(UiFont.SizeSetting, keys);
    }

    [Theory]
    [InlineData("{ \"workbench.uiScale\": 1.5 }", 1.5)]
    [InlineData("// comments\n{ \"workbench.uiScale\": 1.25, }", 1.25)]
    [InlineData("{ \"workbench.uiScale\": 1 }", null)]
    [InlineData("{ \"workbench.uiScale\": 9 }", null)]
    [InlineData("{ \"workbench.uiScale\": \"2\" }", null)]
    [InlineData("{ }", null)]
    [InlineData("not json", null)]
    [InlineData("[]", null)]
    public void The_UI_scale_is_read_from_the_settings_file(string json, double? expected) =>
        Assert.Equal(expected, UiScale.Read(json));

    [Fact]
    public void The_UI_scale_is_Linux_s_and_a_variable_the_user_set_wins()
    {
        var file = Path.Combine(Path.GetTempPath(), $"codalus-uiscale-{Guid.NewGuid():N}.json");
        File.WriteAllText(file, "{ \"workbench.uiScale\": 1.5 }");
        try
        {
            Assert.Equal(1.5, UiScale.Factor(file, _ => null, linux: true));
            Assert.Null(UiScale.Factor(file, _ => null, linux: false));
            Assert.Null(UiScale.Factor(file, name => name == UiScale.GlobalVariable ? "2" : null, linux: true));
            Assert.Null(UiScale.Factor(file, name => name == UiScale.ScreenVariable ? "eDP-1=2" : null, linux: true));
            Assert.Null(UiScale.Factor(file + ".missing", _ => null, linux: true));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [AvaloniaFact]
    public async Task The_UI_scale_setting_says_it_is_Linux_s_alone_and_a_change_offers_a_restart_there()
    {
        await using var f = ShellFixture.Create();
        var descriptor = Assert.Single(f.Shell.Workbench.Settings.Descriptors, d => d.Key == UiScale.Setting);
        Assert.StartsWith("Linux only:", descriptor.Description, StringComparison.Ordinal);

        await f.Shell.Workbench.Settings.SetAsync(UiScale.Setting, 1.5, SettingScope.User, CancellationToken.None);
        if (OperatingSystem.IsLinux())
        {
            await ShellFixture.PumpUntil(() => f.Shell.Workbench.Notifications.Items.Any(n => n.Message.Contains("UI scale", StringComparison.Ordinal)));
        }
        else
        {
            ShellFixture.Pump();
            Assert.DoesNotContain(f.Shell.Workbench.Notifications.Items, n => n.Message.Contains("UI scale", StringComparison.Ordinal));
        }
    }
}
