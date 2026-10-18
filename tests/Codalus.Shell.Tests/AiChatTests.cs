using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Codalus.Shell.ViewModels;
using Codalus.Shell.Views;
using Codalus.Tests;
using Codalus.Workbench.Ai;
using Codalus.Workbench.Settings;

namespace Codalus.Shell.Tests;

/// <summary>
/// AI assistance in the shell (M28a, ADR 0062), with the OpenAI-compatible plugin against
/// <see cref="MockModelServers"/>: off until the user turns it on, nothing sent before a provider is chosen, a question
/// about the selection streamed into AI Chat, and its code applied after the preview.
/// </summary>
public sealed class AiChatTests
{
    private const string Reply = "Shorter:\n\n```csharp\nstatic int Add(int a, int b) => a + b;\n```\n";

    private static async Task<AiChatView> ShowChatAsync(ShellFixture f)
    {
        f.Shell.Run("ai.chat.show");
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<AiChatView>().Any());
        return f.Window.GetVisualDescendants().OfType<AiChatView>().First();
    }

    [AvaloniaFact]
    public async Task AI_is_off_until_the_user_turns_it_on_and_then_asks_for_a_provider()
    {
        await using var f = ShellFixture.Create(builtInPlugins: ShellFixture.PluginBin("Codalus.Plugins.OpenAI"));
        var view = await ShowChatAsync(f);
        var chat = f.Shell.AiChat;

        Assert.True(chat.IsOff);
        Assert.DoesNotContain(f.Shell.StatusBar.Right, i => i.Item.Id == "codalus.ai");
        Assert.Contains(view.GetVisualDescendants().OfType<Button>(), b => AutomationProperties.GetName(b) == "Turn On AI Assistance" && b.IsEffectivelyVisible);

        await f.Shell.TurnOnAiAsync();
        await ShellFixture.PumpUntil(() => chat.NeedsProvider);
        await ShellFixture.PumpUntil(() => f.Shell.StatusBar.Right.Any(i => i.Item.Id == "codalus.ai" && i.Item.Text.Contains("no provider", StringComparison.Ordinal)));
        Assert.Equal(["ollama", "openai"], f.Shell.LanguageModels.Providers.Select(p => p.Id).Order());
        Assert.NotEqual(Codalus.Workbench.Plugins.PluginState.Active, f.Shell.Plugins.Plugins.Single(p => p.Id == "codalus.openai").State);
    }

    [AvaloniaFact]
    public async Task A_question_about_the_selection_streams_and_its_code_applies_after_the_preview()
    {
        await using var server = MockModelServers.OpenAI(Reply);
        await using var f = ShellFixture.Create(builtInPlugins: ShellFixture.PluginBin("Codalus.Plugins.OpenAI"));
        var settings = f.Shell.Workbench.Settings;
        await settings.SetAsync("ai.openai.endpoint", server.BaseUrl + "/v1", SettingScope.User, CancellationToken.None);
        await settings.SetAsync(AiService.EnabledSetting, true, SettingScope.User, CancellationToken.None);
        await settings.SetAsync(AiService.ProviderSetting, "openai", SettingScope.User, CancellationToken.None);
        await settings.SetAsync(AiService.ModelSetting, "gpt-test", SettingScope.User, CancellationToken.None);
        await f.Shell.Secrets.StoreAsync("codalus.openai", "apiKey:openai", MockModelServers.Key, CancellationToken.None);

        var path = Path.Combine(f.UserData, "Calc.cs");
        File.WriteAllText(path, "class Calc\n{\n    static int Add(int a, int b)\n    {\n        return a + b;\n    }\n}\n");
        var document = await f.Shell.OpenFileAsync(path);
        await ShellFixture.PumpUntil(() => document!.Controller is not null);
        var editor = document!.Controller!;
        var method = "    static int Add(int a, int b)\n    {\n        return a + b;\n    }\n";
        editor.Editor.Select(editor.Model.Text.Text.IndexOf(method, StringComparison.Ordinal), method.Length);

        var view = await ShowChatAsync(f);
        var chat = f.Shell.AiChat;
        chat.IncludeCurrentFile = false;
        chat.IncludeSolution = false;
        Assert.True(chat.ContextPreview.Contains("Calc.cs", StringComparison.Ordinal), $"preview: '{chat.ContextPreview}', active: {f.Shell.ActiveEditor?.Model.Path}, selection: {editor.Editor.SelectionLength}, state: {f.Shell.Ai.State}");
        chat.Input = "Make it shorter";
        await chat.SendAsync();
        var turn = Assert.Single(chat.Chat.Turns);
        await ShellFixture.PumpUntil(() => !turn.IsStreaming);

        Assert.Null(turn.Error);
        Assert.Equal(Reply, turn.Answer);
        Assert.Contains("Sent: Calc.cs", AiChatViewModel.ContextSummary(turn), StringComparison.Ordinal);
        using (var body = System.Text.Json.JsonDocument.Parse(server.Requests.Last(r => r.Path == "/v1/chat/completions").Body))
        {
            var question = body.RootElement.GetProperty("messages").EnumerateArray().Last().GetProperty("content").GetString()!;
            Assert.Contains("return a + b;", question, StringComparison.Ordinal);
            Assert.EndsWith("Make it shorter", question, StringComparison.Ordinal);
        }

        await ShellFixture.PumpUntil(() => view.GetVisualDescendants().OfType<Button>().Any(b => AutomationProperties.GetName(b) == "Apply the code after a preview"));
        await ShellFixture.PumpUntil(() => f.Shell.StatusBar.Right.Any(i => i.Item.Id == "codalus.ai" && i.Item.Text.Contains("OpenAI-compatible", StringComparison.Ordinal)));

        // M34h: at a tool window's narrowest (280 px) the code block's Copy, Insert at Cursor and Apply… wrap onto
        // another line instead of running off the pane (the gallery showed Apply… cut off).
        var narrow = new Window { Width = 280, Height = 640, Content = new AiChatView { DataContext = chat } };
        narrow.Show();
        await ShellFixture.PumpUntil(() => narrow.GetVisualDescendants().OfType<Button>().Any(b => AutomationProperties.GetName(b) == "Apply the code after a preview" && b.Bounds.Width > 0));
        ShellFixture.Pump();
        foreach (var name in new[] { "Copy the code", "Insert the code at the cursor", "Apply the code after a preview" })
        {
            var button = narrow.GetVisualDescendants().OfType<Button>().Single(b => AutomationProperties.GetName(b) == name);
            var right = button.TranslatePoint(new Point(button.Bounds.Width, 0), narrow)!.Value.X;
            Assert.True(right <= narrow.Bounds.Width - 8, $"'{name}' ends at {right:0} px, past the 280 px pane");
        }

        narrow.Close();

        // Apply replaces the selection after the preview, as one undoable change.
        var block = Assert.Single(AiCodeBlocks.Parse(turn.Answer));
        var applying = chat.ApplyAsync(turn, block);
        await ShellFixture.PumpUntil(() => f.Shell.Dialogs.IsOpen && f.Shell.Dialogs.HasDetails);
        Assert.Contains(f.Shell.Dialogs.Details, l => l.Text.Contains("static int Add(int a, int b) => a + b;", StringComparison.Ordinal));
        f.Shell.Dialogs.Accept();
        await applying;
        Assert.Equal("class Calc\n{\n    static int Add(int a, int b) => a + b;\n}\n", editor.Model.Text.Text);

        // The request log names what was sent, not what it said.
        var log = f.Shell.Workbench.Output.GetOrCreate(AiService.OutputChannelId).GetText();
        Assert.Contains("chat: openai/gpt-test", log, StringComparison.Ordinal);
        Assert.DoesNotContain("return a + b", log, StringComparison.Ordinal);
    }
}
