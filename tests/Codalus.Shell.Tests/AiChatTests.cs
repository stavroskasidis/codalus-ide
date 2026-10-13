using System.Text.Json;
using Avalonia.Headless.XUnit;
using Codalus.Tests;
using Codalus.Workbench.Settings;

namespace Codalus.Shell.Tests;

/// <summary>The AI chat (M28a, ADR 0062) with the built-in OpenAI-compatible provider against a mock server.</summary>
public sealed class AiChatTests
{
    private static string AiPluginDir => Path.Combine(ShellFixture.RepoRoot, "src", "Plugins", "Codalus.Plugins.Ai", "bin", ShellFixture.Configuration, "net10.0");

    private static readonly string[] s_answer =
    [
        """data: {"choices":[{"delta":{"content":"Here it is:\n```csharp\n"}}]}""",
        """data: {"choices":[{"delta":{"content":"int Twice(int x) => x * 2;\n```\n"}}]}""",
        "data: [DONE]",
    ];

    [AvaloniaFact]
    public async Task Off_until_turned_on_then_asks_about_the_selection_streams_and_applies_the_answer()
    {
        var bodies = new List<string>();
        await using var server = new MockHttpServer(request =>
        {
            lock (bodies)
            {
                bodies.Add(request.Body);
            }

            return request.Path switch
            {
                "/v1/models" => MockHttpResponse.Json("""{ "data": [ { "id": "local-coder" } ] }"""),
                "/v1/chat/completions" => MockHttpResponse.Text(string.Join("\n\n", s_answer) + "\n\n", contentType: "text/event-stream"),
                _ => MockHttpResponse.NotFound(),
            };
        });
        await using var f = ShellFixture.Create(builtInPlugins: AiPluginDir);
        var shell = f.Shell;
        string Text()
        {
            static IEnumerable<string> Walk(Codalus.Plugins.Protocol.ViewNodeDto node) =>
                new[] { node.Text, node.Value }.Where(t => t is not null).Select(t => t!).Concat((node.Children ?? []).SelectMany(Walk));
            return string.Join(" | ", Walk(shell.AiChatRoot()));
        }

        // Off by default: nothing to send with, and no indicator.
        Assert.False(shell.AiEnabled);
        Assert.Contains("AI is off", Text(), StringComparison.Ordinal);
        Assert.DoesNotContain(shell.Workbench.StatusBar.GetItems(Codalus.Workbench.StatusBar.StatusBarAlignment.Right), i => i.Id == "ai.status");

        await shell.Workbench.Settings.SetAsync("ai.openai.baseUrl", server.BaseUrl.TrimEnd('/') + "/v1", SettingScope.User, CancellationToken.None);
        await shell.OnAiChatEventAsync("turnOn", "click", null);
        await ShellFixture.PumpUntil(() => shell.AiEnabled);
        await shell.OnAiChatEventAsync("provider", "change", "OpenAI-compatible");
        await ShellFixture.PumpUntil(() => shell.Workbench.Settings.GetString(ShellHost.AiModelSetting) == "local-coder", seconds: 60);
        await ShellFixture.PumpUntil(() => shell.Workbench.StatusBar.GetItems(Codalus.Workbench.StatusBar.StatusBarAlignment.Right).Any(i => i.Id == "ai.status" && i.Text.Contains("local-coder", StringComparison.Ordinal)));

        // A question about the selection.
        var file = Path.Combine(f.UserData, "Math.cs");
        File.WriteAllText(file, "class M\n{\n    int Double(int x) => x + x;\n}\n");
        var document = (await shell.OpenFileAsync(file))!;
        await ShellFixture.PumpUntil(() => document.Controller is not null);
        var editor = document.Controller!.Editor;
        var start = editor.Text.IndexOf("int Double", StringComparison.Ordinal);
        editor.Select(start, editor.Text.IndexOf(';', start) + 1 - start);
        await shell.OnAiChatEventAsync("question", "change", "Rename it to Twice");
        try
        {
            await ShellFixture.PumpUntil(() => Text().Contains("int Twice(int x)", StringComparison.Ordinal), seconds: 60);
        }
        catch (Xunit.Sdk.XunitException)
        {
            Assert.Fail("No answer. The chat: " + Text() + "\nPlugins: " + shell.Workbench.Output.GetOrCreate("codalus.plugins", "Plugins").GetText());
        }
        Assert.Contains("Sent with: The selection in Math.cs (lines 3–3)", Text(), StringComparison.Ordinal);

        string request;
        lock (bodies)
        {
            request = bodies.Last(b => b.Contains("\"messages\"", StringComparison.Ordinal));
        }

        using (var json = JsonDocument.Parse(request))
        {
            var messages = json.RootElement.GetProperty("messages");
            Assert.Equal("system", messages[0].GetProperty("role").GetString());
            Assert.Contains("int Double(int x) => x + x;", messages[messages.GetArrayLength() - 1].GetProperty("content").GetString(), StringComparison.Ordinal);
        }

        // Apply: through the preview, onto the selection the question was about.
        var apply = shell.OnAiChatEventAsync("apply:1:0", "click", null);
        await ShellFixture.PumpUntil(() => shell.Dialogs.IsOpen);
        shell.Dialogs.Accept();
        await apply;
        await ShellFixture.PumpUntil(() => document.Model.Text.Text.Contains("int Twice(int x) => x * 2;", StringComparison.Ordinal));
        Assert.DoesNotContain("x + x", document.Model.Text.Text, StringComparison.Ordinal);

        // A secret is never sent, even as the active file.
        var secrets = Path.Combine(f.UserData, ".env");
        File.WriteAllText(secrets, "API_KEY=do-not-send\n");
        var secretDocument = (await shell.OpenFileAsync(secrets))!;
        await ShellFixture.PumpUntil(() => secretDocument.Controller is not null && shell.ActiveEditor == secretDocument);
        await shell.OnAiChatEventAsync("question", "change", "What is in it?");
        await ShellFixture.PumpUntil(() => Text().Contains("Not sent:", StringComparison.Ordinal), seconds: 60);
        lock (bodies)
        {
            Assert.DoesNotContain(bodies, b => b.Contains("do-not-send", StringComparison.Ordinal));
        }
    }
}
