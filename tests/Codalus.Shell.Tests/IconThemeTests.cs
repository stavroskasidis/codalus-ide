using Avalonia.Headless.XUnit;
using Codalus.Shell.Controls;
using Codalus.Shell.Theming;
using Codalus.Workbench.Settings;

namespace Codalus.Shell.Tests;

/// <summary>File icon themes from plugins (plugin API 1.0, M22a).</summary>
public sealed class IconThemeTests
{
    private static string BlockIconsDir => Path.Combine(ShellFixture.RepoRoot, "samples", "plugins", "BlockIcons");

    [Fact]
    public void A_vs_code_icon_theme_maps_names_extensions_and_folders_to_raster_icons()
    {
        var theme = IconTheme.Read(Path.Combine(BlockIconsDir, "blocks-icon-theme.json"));
        string Icon(string file) => Path.GetFileName(file["img:".Length..]);

        Assert.Equal("csharp.png", Icon(theme.ForFile("Program.cs")!));
        Assert.Equal("json.png", Icon(theme.ForFile("global.json")!)); // by name
        Assert.Equal("json.png", Icon(theme.ForFile("appsettings.Development.json")!)); // the longest extension that has one
        Assert.Equal("file.png", Icon(theme.ForFile("README.txt")!)); // the default
        Assert.Equal("file.png", Icon(theme.ForFile("NOTES.md")!)); // its SVG icon isn't converted yet
        Assert.Contains(theme.Skipped, s => s.StartsWith("_markdown", StringComparison.Ordinal));
        Assert.Equal("markdown.svg", Path.GetFileName(Assert.Single(theme.SvgFiles)));

        // Once converted (outside the shell), the SVG icon is a vector drawing.
        var converted = IconTheme.Read(Path.Combine(BlockIconsDir, "blocks-icon-theme.json"), path => "vec:" + path);
        Assert.Equal("vec:" + theme.SvgFiles[0], converted.ForFile("NOTES.md"));
        Assert.DoesNotContain(converted.Skipped, s => s.StartsWith("_markdown", StringComparison.Ordinal));

        Assert.Equal("folder-src.png", Icon(theme.ForFolder("src", open: false)!));
        Assert.Equal("folder-src.png", Icon(theme.ForFolder("src", open: true)!));
        Assert.Equal("folder.png", Icon(theme.ForFolder("docs", open: false)!));
        Assert.Equal("folder-open.png", Icon(theme.ForFolder("docs", open: true)!));
    }

    [AvaloniaFact]
    public async Task The_setting_offers_a_plugins_icon_theme_and_switches_the_file_icons()
    {
        await using var f = ShellFixture.Create(builtInPlugins: BlockIconsDir);
        try
        {
            Assert.Contains("blocks", f.Shell.Workbench.Settings.Descriptors.Single(d => d.Key == ShellHost.IconThemeSetting).Enum!);
            Assert.Contains("material", f.Shell.Workbench.Settings.Descriptors.Single(d => d.Key == ShellHost.IconThemeSetting).Enum!);
            Assert.Equal("cl:csharp", FileIcons.ForFile("Program.cs"));

            // Material Icon Theme is the other built-in theme.
            await f.Shell.Workbench.Settings.SetAsync(ShellHost.IconThemeSetting, "material", SettingScope.User, CancellationToken.None);
            await ShellFixture.PumpUntil(() => FileIcons.ForFile("Program.cs") == "mi:csharp");
            Assert.Equal("mi:folder-src", FileIcons.ForFolder("src", open: false));

            await f.Shell.Workbench.Settings.SetAsync(ShellHost.IconThemeSetting, "blocks", SettingScope.User, CancellationToken.None);
            await ShellFixture.PumpUntil(() => FileIcons.ForFile("Program.cs").StartsWith("img:", StringComparison.Ordinal));
            Assert.EndsWith("csharp.png", FileIcons.ForFile("Program.cs"), StringComparison.Ordinal);
            Assert.EndsWith("folder-src.png", FileIcons.ForFolder("src", open: false), StringComparison.Ordinal);

            // The theme's SVG icon is converted by the plugin host and drawn from its operations.
            await ShellFixture.PumpUntil(() => FileIcons.ForFile("NOTES.md").StartsWith(IconSet.VectorPrefix, StringComparison.Ordinal), seconds: 60);
            var markdown = IconSet.GetFilled(FileIcons.ForFile("NOTES.md"));
            Assert.NotNull(markdown);

            await f.Shell.Workbench.Settings.SetAsync(ShellHost.IconThemeSetting, "default", SettingScope.User, CancellationToken.None);
            await ShellFixture.PumpUntil(() => FileIcons.ForFile("Program.cs") == "cl:csharp");
        }
        finally
        {
            FileIcons.Theme = null; // static: leave the built-in icons for the other tests
            FileIcons.Material = false;
        }
    }
}
