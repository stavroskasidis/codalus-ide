using System.Text.Json;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Codalus.Shell.Theming;
using Codalus.Workbench.Settings;

namespace Codalus.Shell.Tests;

/// <summary>Fonts and Colors (M23a): the user's colors over a theme, in VS Code's settings shape.</summary>
public sealed class ColorCustomizationTests
{
    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    [Theory]
    [InlineData("#abc", 0xFFAABBCCu)]
    [InlineData("#112233", 0xFF112233u)]
    [InlineData("#11223380", 0x80112233u)]
    public void Colors_parse_as_vs_code_writes_them(string text, uint argb)
    {
        Assert.Equal(argb, ColorCustomizations.Parse(text));
        Assert.Equal(argb, ColorCustomizations.Parse(ColorCustomizations.Format(argb)));
    }

    [Theory]
    [InlineData("red")]
    [InlineData("#12")]
    [InlineData("#GGHHII")]
    public void Anything_else_is_ignored(string text) => Assert.Null(ColorCustomizations.Parse(text));

    [Fact]
    public void Token_shorthands_semantic_colors_and_theme_scopes_combine()
    {
        var tokens = Json("""
            {
              "comments": "#00FF00",
              "semanticTokenColors": { "class.declaration": "#112233", "method": { "foreground": "#445566" }, "unknownType": "#FFFFFF" },
              "[Dark]": { "comments": "#008000" },
              "[light]": { "strings": "#FF0000" }
            }
            """);
        var workbench = Json("""
            {
              "editor.selectionBackground": "#26496980",
              "Shell.Background": "#101010",
              "terminal.background": "#000000",
              "[dark]": { "editorError.foreground": "#FF0000" }
            }
            """);

        var dark = ColorCustomizations.For("dark", "Dark", workbench, tokens);
        Assert.Equal(0xFF008000u, dark.Tokens["comment"]); // the theme's scope wins
        Assert.Equal(0xFF008000u, dark.Tokens["xmlDocComment"]);
        Assert.Equal(0xFF112233u, dark.Tokens["class"]);
        Assert.Equal(0xFF445566u, dark.Tokens["method"]);
        Assert.False(dark.Tokens.ContainsKey("unknownType"));
        Assert.False(dark.Tokens.ContainsKey("string")); // light's
        Assert.Equal(0x80264969u, dark.Elements["editor.selectionBackground"]);
        Assert.Equal(0xFFFF0000u, dark.Elements["editorError.foreground"]);
        Assert.Equal(0xFF101010u, dark.Ui["Shell.Background"]);
        Assert.False(dark.Ui.ContainsKey("terminal.background"));

        var light = ColorCustomizations.For("light", "Light", workbench, tokens);
        Assert.Equal(0xFF00FF00u, light.Tokens["comment"]);
        Assert.Equal(0xFFFF0000u, light.Tokens["stringVerbatim"]);
        Assert.False(light.Elements.ContainsKey("editorError.foreground"));

        Assert.True(ColorCustomizations.For("dark", "Dark", default, default).IsEmpty);
    }

    [AvaloniaFact]
    public async Task The_fonts_and_colors_page_writes_a_color_for_the_theme_or_every_theme_and_resets_it()
    {
        await using var f = ShellFixture.Create();
        var settings = f.Shell.Workbench.Settings;
        await settings.SetAsync(Theming.ThemeService.SettingKey, "dark", SettingScope.User, CancellationToken.None);
        ShellFixture.Pump();
        f.Shell.Run("workbench.openFontsAndColors");
        ShellFixture.Pump();
        var page = Assert.IsType<ViewModels.FontsAndColorsDocument>(f.Shell.Layout.ActiveDocument).ViewModel;
        Assert.Equal("Dark", page.ThemeTitle);
        var comment = page.All.Single(i => i.Kind == ViewModels.ColorItemKind.Syntax && i.Key == "comment");
        Assert.Equal(ColorCustomizations.Format(Editor.EditorTheme.Dark.TokenColor("comment")!.Value), comment.Hex);
        Assert.False(comment.IsCustom);

        // For the active theme: under "[Dark]".
        comment.Hex = "#ABCDEF";
        await ShellFixture.PumpUntil(() => f.Shell.EditorTheme.TokenColor("comment") == 0xFFABCDEF && comment.IsCustom);
        Assert.Equal("#ABCDEF", settings.GetValue(ColorCustomizations.TokensKey).GetProperty("[Dark]").GetProperty("semanticTokenColors").GetProperty("comment").GetString());

        // For every theme: at the top.
        page.ForAllThemes = true;
        var selection = page.All.Single(i => i.Key == "editor.selectionBackground");
        selection.Hex = "#11223344";
        await ShellFixture.PumpUntil(() => f.Shell.EditorTheme.ElementColor("editor.selectionBackground") == 0x44112233);
        Assert.Equal("#11223344", settings.GetValue(ColorCustomizations.WorkbenchKey).GetProperty("editor.selectionBackground").GetString());

        // A bad color says so and writes nothing; Reset returns to the theme's.
        selection.Hex = "blue";
        Assert.True(selection.HasError);
        comment.ResetCommand.Execute(null);
        await ShellFixture.PumpUntil(() => f.Shell.EditorTheme.TokenColor("comment") == Editor.EditorTheme.Dark.TokenColor("comment") && !comment.IsCustom);
    }

    [AvaloniaFact]
    public async Task Customized_colors_reach_the_editor_and_the_ui_and_follow_the_theme()
    {
        await using var f = ShellFixture.Create();
        var settings = f.Shell.Workbench.Settings;
        await settings.SetAsync(Theming.ThemeService.SettingKey, "dark", SettingScope.User, CancellationToken.None);
        ShellFixture.Pump();
        var before = f.Shell.EditorTheme.TokenColor("comment");

        var changed = 0;
        f.Shell.ThemeChanged += (_, _) => changed++;
        await settings.SetAsync(ColorCustomizations.TokensKey, Json("""{ "[Dark]": { "comments": "#123456" } }"""), SettingScope.User, CancellationToken.None);
        await settings.SetAsync(ColorCustomizations.WorkbenchKey, Json("""{ "editor.lineHighlightBackground": "#20FFFFFF", "Shell.Background": "#202122" }"""), SettingScope.User, CancellationToken.None);
        await ShellFixture.PumpUntil(() => changed >= 2);

        Assert.Equal(0xFF123456u, f.Shell.EditorTheme.TokenColor("comment"));
        Assert.Equal(0xFF20FFFFu, f.Shell.EditorTheme.ElementColor("editor.lineHighlightBackground")); // #RRGGBBAA: alpha last
        Assert.True(Avalonia.Application.Current!.TryGetResource("Shell.Background", null, out var brush));
        Assert.Equal(Color.FromUInt32(0xFF202122), Assert.IsAssignableFrom<ISolidColorBrush>(brush).Color);

        // Another theme: the Dark-only token color doesn't apply; the all-themes UI color does.
        await settings.SetAsync(Theming.ThemeService.SettingKey, "light", SettingScope.User, CancellationToken.None);
        await ShellFixture.PumpUntil(() => f.Shell.Theme.ActiveTheme == "light");
        Assert.Equal(Editor.EditorTheme.Light.TokenColor("comment"), f.Shell.EditorTheme.TokenColor("comment"));

        // Cleared: the theme's own colors again.
        await settings.SetAsync(Theming.ThemeService.SettingKey, "dark", SettingScope.User, CancellationToken.None);
        await settings.SetAsync(ColorCustomizations.TokensKey, (JsonElement?)null, SettingScope.User, CancellationToken.None);
        await settings.SetAsync(ColorCustomizations.WorkbenchKey, (JsonElement?)null, SettingScope.User, CancellationToken.None);
        await ShellFixture.PumpUntil(() => f.Shell.Theme.ActiveTheme == "dark" && f.Shell.EditorTheme.TokenColor("comment") == before);
    }
}
