using Avalonia.Headless.XUnit;
using Codalus.Platform;
using Codalus.Workbench.Settings;

namespace Codalus.Shell.Tests;

/// <summary>Import Settings from VS Code (M23a): what comes over, what doesn't and why, and where VS Code keeps them.</summary>
public sealed class VsCodeImportTests
{
    private const string SettingsJson = """
        {
          // VS Code's JSON with comments
          "editor.fontSize": 16,
          "editor.tabSize": "four",
          "workbench.colorTheme": "Default Light Modern",
          "workbench.colorCustomizations": { "editor.selectionBackground": "#26496980" },
          "editor.tokenColorCustomizations": { "comments": "#6A9955" },
          "python.defaultInterpreterPath": "/usr/bin/python3",
          "[csharp]": { "editor.tabSize": 4 },
        }
        """;

    private const string KeybindingsJson = """
        [
          { "key": "ctrl+shift+i", "command": "editor.action.formatDocument" },
          { "key": "ctrl+k ctrl+c", "command": "-editor.action.commentLine" },
          { "key": "ctrl+alt+p", "command": "python.execInTerminal" },
        ]
        """;

    [AvaloniaFact]
    public async Task Vs_codes_settings_and_shortcuts_that_apply_come_over_and_the_rest_is_logged()
    {
        await using var f = ShellFixture.Create();
        var folder = Path.Combine(f.UserData, "vscode-user");
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "settings.json"), SettingsJson, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(folder, "keybindings.json"), KeybindingsJson, TestContext.Current.CancellationToken);

        await f.Shell.ImportVsCodeSettingsAsync(folder, confirm: false);

        var settings = f.Shell.Workbench.Settings;
        await ShellFixture.PumpUntil(() => settings.GetInt32("editor.fontSize") == 16);
        Assert.Equal("light", settings.GetString(Theming.ThemeService.SettingKey));
        Assert.Equal("#26496980", settings.GetValue(Theming.ColorCustomizations.WorkbenchKey).GetProperty("editor.selectionBackground").GetString());
        Assert.Equal("#6A9955", settings.GetValue(Theming.ColorCustomizations.TokensKey).GetProperty("comments").GetString());
        Assert.NotEqual(0, settings.GetInt32("editor.tabSize")); // "four" is no tab size: left as it was

        // The shortcut for a command that exists here, and the removal of one; not Python's.
        var keybindings = await File.ReadAllTextAsync(f.Shell.Paths.KeybindingsFile, TestContext.Current.CancellationToken);
        Assert.Contains("editor.action.formatDocument", keybindings, StringComparison.Ordinal);
        Assert.Contains("-editor.action.commentLine", keybindings, StringComparison.Ordinal);
        Assert.DoesNotContain("python.execInTerminal", keybindings, StringComparison.Ordinal);

        var log = f.Shell.Workbench.Log.GetText();
        Assert.Contains("Skipped python.defaultInterpreterPath: not a Codalus IDE setting", log, StringComparison.Ordinal);
        Assert.Contains("Skipped [csharp]: language-specific settings", log, StringComparison.Ordinal);
        Assert.Contains("Skipped editor.tabSize", log, StringComparison.Ordinal);
        Assert.Contains("Skipped python.execInTerminal: no such command here", log, StringComparison.Ordinal);
    }

    [Fact]
    public void Vs_code_themes_that_arent_built_in_are_skipped()
    {
        var result = VsCodeImport.Settings("""{ "workbench.colorTheme": "One Dark Pro" }""", _ => null);
        Assert.Empty(result.Imported);
        Assert.Contains(result.Skipped, s => s.Name == "workbench.colorTheme" && s.Reason.Contains("One Dark Pro", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(PlatformKind.Windows, "APPDATA", "Code")]
    [InlineData(PlatformKind.Linux, "XDG_CONFIG_HOME", "VSCodium")]
    public void Vs_codes_user_folder_is_found_where_each_os_keeps_it(PlatformKind platform, string variable, string product)
    {
        var root = Path.Combine(Path.GetTempPath(), "codalus-vscode-" + Guid.NewGuid().ToString("N")[..8]);
        var user = Path.Combine(root, product, "User");
        Directory.CreateDirectory(user);
        try
        {
            Assert.Null(VsCodeImport.UserFolder(platform, name => name == variable ? root : null));
            File.WriteAllText(Path.Combine(user, "settings.json"), "{}");
            Assert.Equal(user, VsCodeImport.UserFolder(platform, name => name == variable ? root : null));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
