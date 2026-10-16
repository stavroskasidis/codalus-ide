using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Codalus.Shell.Editor;
using Codalus.Tests;
using Codalus.Workbench.Ai;
using Codalus.Workbench.Settings;

namespace Codalus.Shell.Tests;

/// <summary>
/// M28b in the editor, with the OpenAI-compatible plugin against <see cref="MockModelServers"/>: inline suggestions
/// (gray text at the caret, Tab, Esc, typing through it) and next-edit suggestions (the predicted lines, Tab applies).
/// </summary>
public sealed class AiSuggestionsTests
{
    private static string Reply(MockHttpRequest request) =>
        request.Body.Contains("code completion engine", StringComparison.Ordinal) ? "return a + b;"
        : request.Body.Contains("predict a programmer", StringComparison.Ordinal) ? """{"startLine": 5, "endLine": 5, "newText": "        Print(total);"}"""
        : "";

    private static async Task<(ShellFixture F, CodeEditorController Editor)> OpenAsync(MockHttpServer server, string text, string name = "Calc.cs")
    {
        var f = ShellFixture.Create(builtInPlugins: ShellFixture.PluginBin("Codalus.Plugins.OpenAI"));
        var settings = f.Shell.Workbench.Settings;
        await settings.SetAsync("ai.openai.endpoint", server.BaseUrl + "/v1", SettingScope.User, CancellationToken.None);
        await settings.SetAsync(AiService.EnabledSetting, true, SettingScope.User, CancellationToken.None);
        await settings.SetAsync(AiService.ProviderSetting, "openai", SettingScope.User, CancellationToken.None);
        await settings.SetAsync(AiService.ModelSetting, "gpt-test", SettingScope.User, CancellationToken.None);
        await settings.SetAsync(AiCompletions.DelaySetting, 100, SettingScope.User, CancellationToken.None);
        await f.Shell.Secrets.StoreAsync("codalus.openai", "apiKey:openai", MockModelServers.Key, CancellationToken.None);
        var path = Path.Combine(f.UserData, name);
        File.WriteAllText(path, text);
        var document = await f.Shell.OpenFileAsync(path);
        await ShellFixture.PumpUntil(() => document!.Controller is not null);
        return (f, document!.Controller!);
    }

    [AvaloniaFact]
    public async Task An_inline_suggestion_shows_after_typing_and_Tab_accepts_it()
    {
        await using var server = MockModelServers.OpenAI(Reply);
        // A text file: in C#, "ret" can open the completion list first (when the C# host answers in time), and inline
        // suggestions wait while it's open.
        var (f, editor) = await OpenAsync(server, "class Calc\n{\n    int Add(int a, int b)\n    {\n        \n    }\n}\n", "Calc.txt");
        await using var _ = f;
        var inline = editor.InlineSuggestions;
        editor.Editor.Focus();
        editor.Editor.CaretOffset = editor.Model.Text.Text.IndexOf("        \n", StringComparison.Ordinal) + 8;

        // Off by default: typing sends nothing.
        f.Type("r");
        await Task.Delay(400);
        Assert.False(inline.IsVisible);
        Assert.DoesNotContain(server.Requests, r => r.Body.Contains("code completion engine", StringComparison.Ordinal));
        f.Press(Key.Back);

        await f.Shell.Workbench.Settings.SetAsync(AiCompletions.EnabledSetting, true, SettingScope.User, CancellationToken.None);
        f.Type("r");
        await ShellFixture.PumpUntil(() => inline.IsVisible, 30);
        Assert.Equal("eturn a + b;", inline.Text);

        // Typing what it shows keeps the rest; Esc dismisses; Tab accepts as one undo step.
        f.Type("e");
        Assert.Equal("turn a + b;", inline.Text);
        f.Press(Key.Escape);
        Assert.False(inline.IsVisible);
        f.Type("t");
        await ShellFixture.PumpUntil(() => inline.IsVisible, 30);
        f.Press(Key.Tab);
        Assert.False(inline.IsVisible);
        Assert.Contains("        return a + b;\n", editor.Model.Text.Text, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task A_next_edit_suggestion_follows_an_edit_and_Tab_applies_it()
    {
        await using var server = MockModelServers.OpenAI(Reply);
        var (f, editor) = await OpenAsync(server, "class Calc\n{\n    void Run(int count)\n    {\n        Print(count);\n    }\n}\n");
        await using var _ = f;
        await f.Shell.Workbench.Settings.SetAsync(AiNextEdit.EnabledSetting, true, SettingScope.User, CancellationToken.None);
        var next = editor.NextEditSuggestions;

        // Rename the parameter: the suggestion is the matching change on line 5.
        var text = editor.Model.Text;
        text.Replace(text.Text.IndexOf("int count", StringComparison.Ordinal) + 4, "count".Length, "total");
        var edit = Assert.Single(next.RecentEdits);
        Assert.Equal((3, "    void Run(int count)", "    void Run(int total)"), (edit.Line, edit.OldText, edit.NewText));
        // The edit's own pause asks (a request made here as well would be cancelled by it: only the newest one answers).
        try
        {
            await ShellFixture.PumpUntil(() => next.Suggestion is not null, 30);
        }
        catch (Xunit.Sdk.XunitException)
        {
            Assert.Fail("No suggestion:\n" + f.Shell.Workbench.Output.GetOrCreate(AiService.OutputChannelId, "AI").GetText());
        }

        Assert.Equal(new AiEditSuggestion(5, 5, "        Print(count);", "        Print(total);"), next.Suggestion);

        editor.Editor.Focus();
        f.Press(Key.Tab);
        Assert.Null(next.Suggestion);
        Assert.Contains("        Print(total);", text.Text, StringComparison.Ordinal);
    }
}
