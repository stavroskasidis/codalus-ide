using System.Text.Json;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Codalus.Shell.Theming;
using Codalus.Workbench.Settings;

namespace Codalus.Shell.Tests;

/// <summary>Color themes from plugins (plugin API 1.0, M22a): VS Code's theme files over a base theme.</summary>
public sealed class ThemeContributionTests
{
    private static string MidnightDir => Path.Combine(ShellFixture.RepoRoot, "samples", "plugins", "MidnightTheme");

    [Fact]
    public void A_vs_code_theme_file_maps_onto_ui_tokens_editor_elements_and_token_types()
    {
        using var document = JsonDocument.Parse("""
            {
              "colors": {
                "editor.background": "#101010",
                "sideBar.background": "#202020",
                "panel.background": "#303030",
                "editor.selectionBackground": "#40404080",
                "unknown.key": "#505050",
                "statusBar.foreground": "not a color"
              },
              "tokenColors": [
                { "scope": "keyword", "settings": { "foreground": "#000001" } },
                { "scope": "keyword.control.flow", "settings": { "foreground": "#000002" } },
                { "scope": "source.cs keyword", "settings": { "foreground": "#000003" } },
                { "scope": ["string", "comment"], "settings": { "foreground": "#000004" } },
                { "scope": "entity.name.function", "settings": { "fontStyle": "bold" } },
                { "settings": { "foreground": "#000005" } }
              ],
              "semanticTokenColors": { "property": "#000006", "method.static": { "foreground": "#000007" }, "nonsense": "#000008" }
            }
            """);
        var colors = ThemeFiles.Read(document.RootElement);

        Assert.Equal(0xFF101010u, colors.Ui["Shell.Editor.Background"]);
        Assert.Equal(0xFF202020u, colors.Ui["Shell.Panel"]); // the first key a theme has wins
        Assert.False(colors.Ui.ContainsKey("Shell.StatusBar.Foreground"));
        Assert.Equal(0x80404040u, colors.Elements["editor.selectionBackground"]);

        Assert.Equal(0xFF000003u, colors.Tokens["keyword"]); // a later rule over an earlier one; a selector's last part
        Assert.Equal(0xFF000002u, colors.Tokens["controlKeyword"]); // the more specific scope
        Assert.Equal(0xFF000004u, colors.Tokens["string"]);
        Assert.Equal(0xFF000004u, colors.Tokens["comment"]);
        Assert.False(colors.Tokens.ContainsKey("method") && colors.Tokens["method"] != 0xFF000007u); // no foreground in the rule
        Assert.Equal(0xFF000006u, colors.Tokens["property"]);
        Assert.Equal(0xFF000007u, colors.Tokens["method"]);
        Assert.False(colors.Tokens.ContainsKey("nonsense"));

        var user = new ColorOverrides(new Dictionary<string, uint> { ["keyword"] = 0xFFABCDEF }, new Dictionary<string, uint>(), new Dictionary<string, uint>());
        var merged = ThemeFiles.Merge(colors, user);
        Assert.Equal(0xFFABCDEFu, merged.Tokens["keyword"]);
        Assert.Equal(0xFF000004u, merged.Tokens["string"]);
    }

    [Fact]
    public void A_theme_colors_razor_markup_through_its_html_and_razor_scopes()
    {
        using var document = JsonDocument.Parse("""
            {
              "tokenColors": [
                { "scope": "entity.name.tag", "settings": { "foreground": "#000001" } },
                { "scope": "entity.other.attribute-name", "settings": { "foreground": "#000002" } },
                { "scope": "punctuation.definition.tag", "settings": { "foreground": "#000003" } },
                { "scope": "keyword.control.razor.directive", "settings": { "foreground": "#000004" } },
                { "scope": "entity.name.type", "settings": { "foreground": "#000005" } },
                { "scope": "comment", "settings": { "foreground": "#000006" } },
                { "scope": "punctuation", "settings": { "foreground": "#000007" } }
              ],
              "semanticTokenColors": { "razorComponentAttribute": "#000008" }
            }
            """);
        var colors = ThemeFiles.Read(document.RootElement);

        Assert.Equal(0xFF000001u, colors.Tokens["markupElement"]);
        Assert.Equal(0xFF000002u, colors.Tokens["markupAttribute"]);
        Assert.Equal(0xFF000003u, colors.Tokens["markupTagDelimiter"]); // not the theme's punctuation
        Assert.Equal(0xFF000007u, colors.Tokens["punctuation"]);
        Assert.Equal(0xFF000004u, colors.Tokens["razorDirective"]);
        Assert.False(colors.Tokens.ContainsKey("controlKeyword"));
        Assert.Equal(0xFF000005u, colors.Tokens["razorComponentElement"]); // like a type
        Assert.Equal(0xFF000006u, colors.Tokens["razorComment"]);
        Assert.Equal(0xFF000008u, colors.Tokens["razorComponentAttribute"]);
    }

    [AvaloniaFact]
    public async Task A_plugin_theme_is_offered_applied_over_its_base_and_customizable()
    {
        await using var f = ShellFixture.Create(builtInPlugins: MidnightDir);
        var choice = Assert.Single(f.Shell.Theme.AllChoices, c => c.Id == "midnight");
        Assert.Equal("Midnight", choice.Title);
        Assert.Contains("midnight", f.Shell.Workbench.Settings.Descriptors.Single(d => d.Key == ThemeService.SettingKey).Enum!);

        var settings = f.Shell.Workbench.Settings;
        await settings.SetAsync(ThemeService.SettingKey, "midnight", SettingScope.User, CancellationToken.None);
        await ShellFixture.PumpUntil(() => f.Shell.Theme.ActiveContributed?.Id == "midnight");
        Assert.Equal("dark", f.Shell.Theme.ActiveTheme); // its base
        Assert.Equal(0xFF9EE6A0u, f.Shell.EditorTheme.TokenColor("string"));
        Assert.True(Avalonia.Application.Current!.TryGetResource("Shell.Editor.Background", null, out var brush));
        Assert.Equal(Color.FromUInt32(0xFF0B1020), Assert.IsAssignableFrom<ISolidColorBrush>(brush).Color);

        // The user's customizations go over the theme, by its label.
        await settings.SetAsync(ColorCustomizations.TokensKey, JsonSerializer.SerializeToElement(new Dictionary<string, object> { ["[Midnight]"] = new Dictionary<string, string> { ["strings"] = "#123456" } }), SettingScope.User, CancellationToken.None);
        await ShellFixture.PumpUntil(() => f.Shell.EditorTheme.TokenColor("string") == 0xFF123456u);
        Assert.Equal(0xFF5C6A8Fu, f.Shell.EditorTheme.TokenColor("comment")); // the theme's own

        // Back to a built-in theme: the plugin's colors go.
        await settings.SetAsync(ThemeService.SettingKey, "dark", SettingScope.User, CancellationToken.None);
        await ShellFixture.PumpUntil(() => f.Shell.Theme.ActiveContributed is null);
        Assert.Equal(Editor.EditorTheme.Dark.TokenColor("string"), f.Shell.EditorTheme.TokenColor("string"));
    }
}
