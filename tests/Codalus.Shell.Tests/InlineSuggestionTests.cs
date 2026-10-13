using System.Text.Json;
using Avalonia.Headless.XUnit;
using Codalus.Shell.Editor;
using Codalus.Tests;
using Codalus.Workbench.Settings;

namespace Codalus.Shell.Tests;

/// <summary>AI inline suggestions (M28b): ghost text after a pause, Tab to accept, off unless turned on.</summary>
public sealed class InlineSuggestionTests
{
    private static string AiPluginDir => Path.Combine(ShellFixture.RepoRoot, "src", "Plugins", "Codalus.Plugins.Ai", "bin", ShellFixture.Configuration, "net10.0");

    [Theory]
    [InlineData("x * 2;", "    int Twice(int x) => ", "\n}", "x * 2;")]
    [InlineData("```csharp\nx * 2;\n```", "=> ", "", "x * 2;")]
    [InlineData("    int Twice(int x) => x * 2;", "    int Twice(int x) => ", "", "x * 2;")] // repeats the line's start
    [InlineData("x * 2);", "Call(", ");\n", "x * 2")] // runs into what follows
    [InlineData("   ", "a", "b", null)]
    public void A_reply_becomes_only_the_text_to_insert(string reply, string before, string after, string? expected)
    {
        Assert.Equal(expected, ShellHost.CleanSuggestion(reply, before, after));
    }

    [AvaloniaFact]
    public async Task A_pause_in_typing_shows_the_models_suggestion_and_tab_accepts_it()
    {
        var bodies = new List<string>();
        // The request's JSON escapes '<': compare the parsed user message.
        IEnumerable<string> Users()
        {
            lock (bodies)
            {
                return [.. bodies.Where(b => b.Contains("\"messages\"", StringComparison.Ordinal)).Select(b =>
                {
                    using var json = JsonDocument.Parse(b);
                    var messages = json.RootElement.GetProperty("messages");
                    return messages[messages.GetArrayLength() - 1].GetProperty("content").GetString() ?? string.Empty;
                })];
            }
        }

        await using var server = new MockHttpServer(request =>
        {
            lock (bodies)
            {
                bodies.Add(request.Body);
            }

            return request.Path switch
            {
                "/v1/models" => MockHttpResponse.Json("""{ "data": [ { "id": "local-coder" } ] }"""),
                "/v1/chat/completions" => MockHttpResponse.Text("data: {\"choices\":[{\"delta\":{\"content\":\"x * 2;\"}}]}\n\ndata: [DONE]\n\n", contentType: "text/event-stream"),
                _ => MockHttpResponse.NotFound(),
            };
        });
        var delay = InlineSuggestions.Delay;
        InlineSuggestions.Delay = TimeSpan.FromMilliseconds(50);
        try
        {
            await using var f = ShellFixture.Create(builtInPlugins: AiPluginDir);
            var settings = f.Shell.Workbench.Settings;
            await settings.SetAsync("ai.openai.baseUrl", server.BaseUrl.TrimEnd('/') + "/v1", SettingScope.User, CancellationToken.None);
            await settings.SetAsync(ShellHost.AiEnabledSetting, true, SettingScope.User, CancellationToken.None);
            await settings.SetAsync(ShellHost.AiProviderSetting, "openai", SettingScope.User, CancellationToken.None);
            await settings.SetAsync(ShellHost.AiModelSetting, "local-coder", SettingScope.User, CancellationToken.None);

            var file = Path.Combine(f.UserData, "Math.cs");
            File.WriteAllText(file, "class M\n{\n    int Twice(int x) => \n}\n");
            var document = (await f.Shell.OpenFileAsync(file))!;
            await ShellFixture.PumpUntil(() => document.Controller is not null);
            var controller = document.Controller!;
            var editor = controller.Editor;
            var caret = editor.Text.IndexOf("=> ", StringComparison.Ordinal) + 3;
            editor.CaretOffset = caret;

            // Off until turned on: typing asks nothing.
            editor.Document.Insert(caret, " ");
            editor.Document.Remove(caret, 1);
            await Task.Delay(400);
            ShellFixture.Pump();
            Assert.Null(controller.Suggestions.Shown);
            Assert.DoesNotContain(Users(), u => u.Contains("<CURSOR>", StringComparison.Ordinal));

            await settings.SetAsync(ShellHost.AiInlineSetting, true, SettingScope.User, CancellationToken.None);
            editor.Document.Insert(caret, " ");
            editor.Document.Remove(caret, 1);
            editor.CaretOffset = caret;
            await ShellFixture.PumpUntil(() => controller.Suggestions.Shown is not null, seconds: 60);
            Assert.Equal(("x * 2;", caret), controller.Suggestions.Shown!.Value);
            Assert.Contains(Users(), u => u.Contains("int Twice(int x) => <CURSOR>", StringComparison.Ordinal));

            // Tab: inserted, and gone.
            controller.Suggestions.Accept();
            Assert.Contains("int Twice(int x) => x * 2;", editor.Text, StringComparison.Ordinal);
            Assert.Null(controller.Suggestions.Shown);
            Assert.Equal(caret + "x * 2;".Length, editor.CaretOffset);

            // Moving away dismisses a suggestion.
            controller.Suggestions.Show("y", 0);
            editor.CaretOffset = 3;
            Assert.Null(controller.Suggestions.Shown);
        }
        finally
        {
            InlineSuggestions.Delay = delay;
        }
    }
}
