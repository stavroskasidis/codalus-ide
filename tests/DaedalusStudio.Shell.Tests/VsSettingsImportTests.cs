using System.Text.Json;
using Avalonia.Headless.XUnit;
using DaedalusStudio.Workbench.Settings;

namespace DaedalusStudio.Shell.Tests;

/// <summary>Import a .vssettings file (M23a): the font, colors, editor options and shortcuts that have a counterpart here.</summary>
public sealed class VsSettingsImportTests
{
    // The shape a classic IDE exports (Tools → Import and Export Settings), trimmed to the parts the importer reads.
    private const string Exported = """
        <UserSettings>
          <ApplicationIdentity version="17.0"/>
          <ToolsOptions>
            <ToolsOptionsCategory name="TextEditor" RegisteredName="TextEditor">
              <ToolsOptionsSubCategory name="AllLanguages" RegisteredName="AllLanguages">
                <PropertyValue name="TabSize">2</PropertyValue>
                <PropertyValue name="InsertTabs">true</PropertyValue>
                <PropertyValue name="ShowLineNumbers">false</PropertyValue>
                <PropertyValue name="AutoListMembers">true</PropertyValue>
              </ToolsOptionsSubCategory>
            </ToolsOptionsCategory>
          </ToolsOptions>
          <Category name="Environment_Group" RegisteredName="Environment_Group">
            <Category name="Environment_FontsAndColors" Category="{1EDA5DD4-927A-43a7-810E-7FD247D0DA1D}">
              <FontsAndColors Version="2.0">
                <Categories>
                  <Category GUID="{A27B4E24-A735-4D1D-B8E7-9716E1E3D8E0}" FontName="Fira Code" FontSize="12" CharSet="1" FontIsDefault="No">
                    <Items>
                      <Item Name="Comment" Foreground="0x0057A64A" Background="0x02000000" BoldFont="No"/>
                      <Item Name="Keyword" Foreground="0x00D69C56" Background="0x02000000" BoldFont="No"/>
                      <Item Name="String" Foreground="0x01000001" Background="0x02000000" BoldFont="No"/>
                      <Item Name="Selected Text" Foreground="0x02000000" Background="0x00794926" BoldFont="No"/>
                      <Item Name="Breakpoint (Enabled)" Foreground="0x00FFFFFF" Background="0x000000FF" BoldFont="No"/>
                    </Items>
                  </Category>
                </Categories>
              </FontsAndColors>
            </Category>
            <Category name="Environment_KeyBindings" Category="{F09035F1-80D2-4312-8EC4-4D354A4BCB4C}">
              <KeyboardShortcuts Version="2.0">
                <UserShortcuts>
                  <Shortcut Command="Edit.Duplicate" Scope="Text Editor">Ctrl+Shift+D</Shortcut>
                  <Shortcut Command="Build.BuildSolution" Scope="Global">Ctrl+Shift+B, B</Shortcut>
                  <RemoveShortcut Command="Edit.ToggleBookmark" Scope="Text Editor">Ctrl+K, Ctrl+K</RemoveShortcut>
                  <Shortcut Command="Tools.NuGetPackageManager" Scope="Global">Ctrl+Alt+N</Shortcut>
                </UserShortcuts>
              </KeyboardShortcuts>
            </Category>
          </Category>
        </UserSettings>
        """;

    [AvaloniaFact]
    public async Task A_vssettings_files_font_colors_options_and_shortcuts_come_over_and_colors_add_to_the_users()
    {
        await using var f = ShellFixture.Create();
        var settings = f.Shell.Workbench.Settings;
        using var mine = JsonDocument.Parse("""{ "editor.findMatchHighlightBackground": "#FFFF0080" }""");
        await settings.SetAsync(VsSettingsImport.ColorKeys.Workbench, mine.RootElement.Clone(), SettingScope.User, CancellationToken.None);
        var path = Path.Combine(f.UserData, "exported.vssettings");
        await File.WriteAllTextAsync(path, Exported, TestContext.Current.CancellationToken);

        await f.Shell.ImportVsSettingsAsync(path, confirm: false);

        // The font (12 points as 16 pixels), the editor options (InsertTabs is the opposite of Insert Spaces).
        await ShellFixture.PumpUntil(() => settings.GetInt32("editor.tabSize") == 2);
        Assert.Equal("Fira Code, monospace", settings.GetString("editor.fontFamily"));
        Assert.Equal(16, settings.GetInt32("editor.fontSize"));
        Assert.False(settings.GetBoolean("editor.insertSpaces", true));
        Assert.False(settings.GetBoolean("editor.lineNumbers", true));

        // Colors from BGR; "automatic" ones are left to the theme; the user's own colors stay.
        var tokens = settings.GetValue(VsSettingsImport.ColorKeys.Tokens);
        Assert.Equal("#4AA657", tokens.GetProperty("comments").GetString());
        Assert.Equal("#569CD6", tokens.GetProperty("keywords").GetString());
        Assert.False(tokens.TryGetProperty("strings", out _));
        var elements = settings.GetValue(VsSettingsImport.ColorKeys.Workbench);
        Assert.Equal("#264979", elements.GetProperty("editor.selectionBackground").GetString());
        Assert.Equal("#FFFF0080", elements.GetProperty("editor.findMatchHighlightBackground").GetString());

        // Shortcuts: chords, removals and scopes; a command with no equivalent is skipped and logged.
        var keybindings = await File.ReadAllTextAsync(f.Shell.Paths.KeybindingsFile, TestContext.Current.CancellationToken);
        Assert.Contains("\"primary+shift+d\"", keybindings, StringComparison.Ordinal);
        Assert.Contains("\"primary+shift+b b\"", keybindings, StringComparison.Ordinal);
        Assert.Contains("\"-bookmarks.toggle\"", keybindings, StringComparison.Ordinal);
        Assert.Contains("\"editorTextFocus\"", keybindings, StringComparison.Ordinal);
        Assert.DoesNotContain("NuGet", keybindings, StringComparison.Ordinal);
        var log = f.Shell.Workbench.Log.GetText();
        Assert.Contains("Skipped Tools.NuGetPackageManager (Ctrl+Alt+N): no equivalent command here", log, StringComparison.Ordinal);
        Assert.Contains("Skipped the color of \"Breakpoint (Enabled)\"", log, StringComparison.Ordinal);
        Assert.Contains(f.Shell.Workbench.Keybindings.UserRules, r => r.Command == "editor.action.duplicateSelection");
    }

    [Theory]
    [InlineData("Ctrl+K, Ctrl+D", "primary+k primary+d")]
    [InlineData("Alt+Up Arrow", "alt+up")]
    [InlineData("Ctrl++", "primary++")]
    [InlineData("Shift+F12", "shift+f12")]
    [InlineData("Ctrl+Bkspce", "primary+backspace")]
    [InlineData("Ctrl+Num *", null)]
    public void Keys_are_read_as_ours(string vs, string? ours) => Assert.Equal(ours, VsSettingsImport.Keys(vs));
}
