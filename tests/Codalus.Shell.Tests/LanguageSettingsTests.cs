using System.Text.Json;
using Avalonia.Headless.XUnit;
using Codalus.Shell.Editor;
using Codalus.Workbench.Settings;

namespace Codalus.Shell.Tests;

/// <summary>
/// Per-language editor options (deferred from M12b): <c>"[languageId]"</c> blocks in settings.json, as in VS Code,
/// win over the general settings for that language's editors, and open editors follow changes.
/// </summary>
public sealed class LanguageSettingsTests
{
    private static async Task<CodeEditorController> OpenAsync(ShellFixture f, string path)
    {
        var document = await f.Shell.OpenFileAsync(path);
        Assert.NotNull(document);
        await ShellFixture.PumpUntil(() => document.Controller is not null);
        return document.Controller!;
    }

    [AvaloniaFact]
    public async Task An_editor_takes_its_languages_settings_over_the_general_ones()
    {
        await using var f = ShellFixture.Create();
        var root = Path.Combine(f.UserData, "files");
        Directory.CreateDirectory(root);
        var markdown = Path.Combine(root, "README.md");
        var text = Path.Combine(root, "notes.txt");
        await File.WriteAllTextAsync(markdown, "# Title\n", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(text, "notes\n", TestContext.Current.CancellationToken);
        var settings = f.Shell.Workbench.Settings;
        var ct = TestContext.Current.CancellationToken;

        await settings.SetAsync(EditorOptions.TabSizeKey, JsonSerializer.SerializeToElement(2), SettingScope.User, "markdown", ct);
        await settings.SetAsync(EditorOptions.WordWrapKey, JsonSerializer.SerializeToElement(true), SettingScope.User, "markdown", ct);
        var md = await OpenAsync(f, markdown);
        Assert.Equal("markdown", md.Model.LanguageId);
        await ShellFixture.PumpUntil(() => md.Editor.Options.IndentationSize == 2);
        Assert.True(md.Editor.WordWrap);
        var txt = await OpenAsync(f, text);
        Assert.Equal(4, txt.Editor.Options.IndentationSize);
        Assert.False(txt.Editor.WordWrap);
        Assert.Same(f.Shell.EditorOptions.For("markdown"), f.Shell.EditorOptions.For("markdown"));
        Assert.Same(f.Shell.EditorOptions, f.Shell.EditorOptions.For(null));

        // A change to the block, or to the general value, reaches the open editors.
        await settings.SetAsync(EditorOptions.TabSizeKey, JsonSerializer.SerializeToElement(3), SettingScope.User, "markdown", ct);
        await ShellFixture.PumpUntil(() => md.Editor.Options.IndentationSize == 3);
        await settings.SetAsync(EditorOptions.TabSizeKey, JsonSerializer.SerializeToElement(8), SettingScope.User, ct);
        await ShellFixture.PumpUntil(() => txt.Editor.Options.IndentationSize == 8);
        Assert.Equal(3, md.Editor.Options.IndentationSize);

        // Configure Language-Specific Settings adds the block when there's none and opens the settings there.
        await f.Shell.ConfigureLanguageSettingsAsync("plaintext");
        await ShellFixture.PumpUntil(() => f.Shell.ActiveEditor is { Controller: not null } active && active.Model.Path == f.Shell.Paths.SettingsFile
                                           && active.Controller.Editor.CaretOffset > 0);
        var editor = f.Shell.ActiveEditor!.Controller!.Editor;
        Assert.Contains("\"[plaintext]\": {}", editor.Document.Text, StringComparison.Ordinal);
        Assert.Equal('{', editor.Document.GetCharAt(editor.CaretOffset - 1));
    }
}
