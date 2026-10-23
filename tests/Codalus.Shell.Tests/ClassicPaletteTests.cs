using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Codalus.Shell.Editor;
using Codalus.Shell.Theming;
using Codalus.Workbench.Settings;

namespace Codalus.Shell.Tests;

/// <summary>
/// #83 (D47): with the document view's 0.85 opacity gone (<see cref="DocumentOpacityTests"/>), Classic's editor takes the
/// classic IDE's measured palette: control keywords, comments, strings, gray line numbers with a brighter current line
/// number, CodeLens and the current line, and no bracket pair colors by default. Classic Light and Blue take its Light
/// scheme. The families keep theirs.
/// </summary>
public sealed class ClassicPaletteTests
{
    private static Color Over(uint argb, Color background)
    {
        var color = Color.FromUInt32(argb);
        var a = color.A / 255.0;
        byte Mix(byte fg, byte bg) => (byte)Math.Round((fg * a) + (bg * (1 - a)));
        return Color.FromRgb(Mix(color.R, background.R), Mix(color.G, background.G), Mix(color.B, background.B));
    }

    [Fact]
    public void Classic_dark_has_the_classic_ides_measured_syntax_colors()
    {
        var dark = EditorTheme.Dark;
        Assert.Equal(0xFFD8A0DFu, dark.TokenColor("controlKeyword"));
        Assert.Equal(0xFF57A64Au, dark.TokenColor("comment"));
        Assert.Equal(0xFF57A64Au, dark.TokenColor("xmlDocText"));
        Assert.Equal(0xFF57A64Au, dark.TokenColor("razorComment"));
        Assert.Equal(0xFF57A64Au, dark.TokenColor("markupComment"));
        Assert.Equal(0xFFD69D85u, dark.TokenColor("string"));
        Assert.Equal(0xFFD69D85u, dark.TokenColor("stringVerbatim"));

        // The current line: a #272727 fill on the #1E1E1E editor, with no border.
        Assert.Equal(Color.Parse("#272727"), Over(dark.ElementColor("editor.lineHighlightBackground"), Color.Parse("#1E1E1E")));

        // The colors that already matched stay.
        Assert.Equal(0xFF569CD6u, dark.TokenColor("keyword"));
        Assert.Equal(0xFFDCDCAAu, dark.TokenColor("method"));
        Assert.Equal(0xFF4EC9B0u, dark.TokenColor("class"));
        Assert.Equal(0xFF9CDCFEu, dark.TokenColor("local"));
    }

    [Fact]
    public void Classic_light_and_blue_take_the_classic_ides_light_scheme_and_the_families_keep_theirs()
    {
        Assert.Same(EditorTheme.Light, EditorTheme.For("blue"));
        Assert.Equal(0xFF8F08C4u, EditorTheme.Light.TokenColor("controlKeyword"));
        Assert.Equal(0xFF008000u, EditorTheme.Light.TokenColor("comment"));
        Assert.Equal(0xFFA31515u, EditorTheme.Light.TokenColor("string"));

        foreach (var family in new[] { "harbor", "graphite", "daylight", "ember" })
        {
            Assert.Equal(EditorTheme.FamilyLightControlKeyword, EditorTheme.For($"{family}.light").TokenColor("controlKeyword"));
            Assert.Equal(EditorTheme.FamilyDarkControlKeyword, EditorTheme.For($"{family}.dark").TokenColor("controlKeyword"));
            Assert.Equal(0xFFC586C0u, EditorTheme.For($"{family}.highContrast").TokenColor("controlKeyword"));
        }

        Assert.Equal(0xFFC586C0u, EditorTheme.HighContrast.TokenColor("controlKeyword"));
    }

    [Fact]
    public void Grammar_colored_text_takes_the_same_colors_in_classic_only()
    {
        var dark = TextMateSupport.RecolorsFor("dark");
        Assert.Equal("#D8A0DF", dark["#C586C0"]);
        Assert.Equal("#57A64A", dark["#6a9955"]);
        Assert.Equal("#D69D85", dark["#CE9178"]);
        Assert.Equal("#DCDCDC", dark["#D4D4D4"]);
        Assert.Equal("#8F08C4", TextMateSupport.RecolorsFor("light")["#AF00DB"]);
        Assert.Equal("#8F08C4", TextMateSupport.RecolorsFor("blue")["#AF00DB"]);

        // The families keep Dark+ and Light+ (plain text aside), High Contrast its own.
        Assert.Equal(["#D4D4D4"], TextMateSupport.RecolorsFor("harbor.dark").Keys);
        Assert.Empty(TextMateSupport.RecolorsFor("ember.light"));
        Assert.Empty(TextMateSupport.RecolorsFor("highContrast"));
    }

    private static Color Token(string key, string theme)
    {
        var variant = ThemeService.VariantOf(theme);
        Assert.True(Application.Current!.TryGetResource(key, variant, out var value), $"{theme}: {key} is missing");
        return ((ISolidColorBrush)value!).Color;
    }

    [AvaloniaFact]
    public void Line_numbers_codelens_and_the_current_line_are_the_classic_ides()
    {
        Assert.Equal(Color.Parse("#8A8A8A"), Token("Shell.Editor.LineNumbers", "dark"));
        Assert.Equal(Color.Parse("#CCCCCC"), Token("Shell.Editor.LineNumbers.Current", "dark"));
        Assert.Equal(Color.Parse("#A1A1A1"), Token("Shell.Editor.CodeLens.Foreground", "dark"));
        Assert.Equal(Color.Parse("#272727"), Token("Shell.Editor.CurrentLine", "dark"));

        // Light and Blue (not measured: the Dark values' contrast on the Light editor, docs/design/shell-themes.md).
        foreach (var theme in new[] { "light", "blue" })
        {
            Assert.Equal(Color.Parse("#727272"), Token("Shell.Editor.LineNumbers", theme));
            Assert.Equal(Color.Parse("#3F3F3F"), Token("Shell.Editor.LineNumbers.Current", theme));
            Assert.Equal(Color.Parse("#5E5E5E"), Token("Shell.Editor.CodeLens.Foreground", theme));
        }

        // The families and High Contrast look as they did: the current number as the others, CodeLens as inlay hints.
        foreach (var (id, _) in ThemeService.Themes.Where(t => ThemeService.FamilyOf(t.Id) != ThemeService.Classic || t.Id == "highContrast"))
        {
            Assert.Equal(Token("Shell.Editor.LineNumbers", id), Token("Shell.Editor.LineNumbers.Current", id));
            Assert.Equal(Token("Shell.Editor.InlayHint.Foreground", id), Token("Shell.Editor.CodeLens.Foreground", id));
        }
    }

    [Fact]
    public void Bracket_pair_colors_default_off_in_classic_and_on_in_the_families()
    {
        Assert.False(BuiltIns.EditorSettings().Single(d => d.Key == EditorOptions.BracketPairColorizationKey).Default.GetBoolean());
        Assert.False(BuiltIns.BracketPairColorizationSetting(ThemeService.Classic).Default.GetBoolean());
        Assert.True(BuiltIns.BracketPairColorizationSetting("harbor").Default.GetBoolean());
    }

    [AvaloniaFact]
    public async Task Bracket_pair_colors_follow_the_family_unless_the_user_sets_them()
    {
        await using var f = ShellFixture.Create();
        var settings = f.Shell.Workbench.Settings;
        async Task Use(string theme)
        {
            await settings.SetAsync(ThemeService.SettingKey, theme, SettingScope.User, CancellationToken.None);
            await ShellFixture.PumpUntil(() => f.Shell.Theme.ActiveTheme == theme);
            ShellFixture.Pump();
        }

        await Use("dark");
        await ShellFixture.PumpUntil(() => !f.Shell.EditorOptions.BracketPairColorization);
        Assert.False(settings.GetBoolean(EditorOptions.BracketPairColorizationKey, true));

        await Use("harbor.dark");
        await ShellFixture.PumpUntil(() => f.Shell.EditorOptions.BracketPairColorization);
        Assert.True(settings.Find(EditorOptions.BracketPairColorizationKey)!.Default.GetBoolean());

        await Use("light");
        await ShellFixture.PumpUntil(() => !f.Shell.EditorOptions.BracketPairColorization);

        // A value in settings.json wins in any family.
        await settings.SetAsync(EditorOptions.BracketPairColorizationKey, true, SettingScope.User, CancellationToken.None);
        await ShellFixture.PumpUntil(() => f.Shell.EditorOptions.BracketPairColorization);
        await Use("blue");
        ShellFixture.Pump();
        Assert.True(f.Shell.EditorOptions.BracketPairColorization);
    }

    [AvaloniaFact]
    public async Task The_current_lines_number_is_brighter_than_the_others()
    {
        await using var f = ShellFixture.Create();
        await f.Shell.Workbench.Settings.SetAsync(ThemeService.SettingKey, "dark", SettingScope.User, CancellationToken.None);
        await ShellFixture.PumpUntil(() => f.Shell.Theme.ActiveTheme == "dark");
        var path = Path.Combine(Directory.CreateDirectory(Path.Combine(f.UserData, "numbers")).FullName, "Notes.txt");
        await File.WriteAllTextAsync(path, "one\ntwo\nthree\n", TestContext.Current.CancellationToken);
        var document = await f.Shell.OpenFileAsync(path, preview: false);
        await ShellFixture.PumpUntil(() => document?.Controller is not null);
        var editor = document!.Controller!.Editor;
        ShellFixture.Pump();

        var margins = editor.TextArea.LeftMargins.OfType<AvaloniaEdit.Editing.LineNumberMargin>().ToList();
        var margin = Assert.IsType<CurrentLineNumberMargin>(Assert.Single(margins));
        Color? Of(IBrush? brush) => (brush as ISolidColorBrush)?.Color;

        editor.TextArea.Caret.Line = 2;
        ShellFixture.Pump();
        Assert.Equal(2, margin.CaretLine);
        Assert.Equal(Color.Parse("#CCCCCC"), Of(margin.BrushFor(2)));
        Assert.Equal(Color.Parse("#8A8A8A"), Of(margin.BrushFor(1)));
        Assert.Equal(Color.Parse("#8A8A8A"), Of(margin.BrushFor(3)));

        editor.TextArea.Caret.Line = 3;
        ShellFixture.Pump();
        Assert.Equal(Color.Parse("#CCCCCC"), Of(margin.BrushFor(3)));
        Assert.Equal(Color.Parse("#8A8A8A"), Of(margin.BrushFor(2)));

        // Numbers off and on again: the editor's new margin is replaced too, and follows the theme.
        await f.Shell.Workbench.Settings.SetAsync(EditorOptions.LineNumbersKey, false, SettingScope.User, CancellationToken.None);
        await ShellFixture.PumpUntil(() => !editor.TextArea.LeftMargins.OfType<AvaloniaEdit.Editing.LineNumberMargin>().Any());
        await f.Shell.Workbench.Settings.SetAsync(EditorOptions.LineNumbersKey, true, SettingScope.User, CancellationToken.None);
        await ShellFixture.PumpUntil(() => editor.TextArea.LeftMargins.OfType<CurrentLineNumberMargin>().Any());
        Assert.Single(editor.TextArea.LeftMargins.OfType<AvaloniaEdit.Editing.LineNumberMargin>());
        await f.Shell.Workbench.Settings.SetAsync(ThemeService.SettingKey, "light", SettingScope.User, CancellationToken.None);
        await ShellFixture.PumpUntil(() => f.Shell.Theme.ActiveTheme == "light");
        ShellFixture.Pump();
        margin = editor.TextArea.LeftMargins.OfType<CurrentLineNumberMargin>().Single();
        Assert.Equal(Color.Parse("#3F3F3F"), Of(margin.BrushFor(margin.CaretLine)));
    }
}
