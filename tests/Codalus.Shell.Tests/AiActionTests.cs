using System.Diagnostics;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using Codalus.LanguageServices.Protocol;
using Codalus.Tests;
using Codalus.Workbench.Settings;

namespace Codalus.Shell.Tests;

/// <summary>AI actions on existing surfaces (M28c) against a mock OpenAI-compatible server.</summary>
public sealed class AiActionTests
{
    private static string AiPluginDir => Path.Combine(ShellFixture.RepoRoot, "src", "Plugins", "Codalus.Plugins.Ai", "bin", ShellFixture.Configuration, "net10.0");

    private sealed class Server : IAsyncDisposable
    {
        private readonly List<string> _users = [];

        public Server(string answer)
        {
            Http = new MockHttpServer(request =>
            {
                if (request.Path == "/v1/chat/completions")
                {
                    using var json = JsonDocument.Parse(request.Body);
                    var messages = json.RootElement.GetProperty("messages");
                    lock (_users)
                    {
                        _users.Add(messages[messages.GetArrayLength() - 1].GetProperty("content").GetString() ?? string.Empty);
                    }

                    return MockHttpResponse.Text("data: " + JsonSerializer.Serialize(new { choices = new[] { new { delta = new { content = answer } } } }) + "\n\ndata: [DONE]\n\n", contentType: "text/event-stream");
                }

                return request.Path == "/v1/models" ? MockHttpResponse.Json("""{ "data": [ { "id": "local-coder" } ] }""") : MockHttpResponse.NotFound();
            });
        }

        public MockHttpServer Http { get; }

        public IReadOnlyList<string> Users
        {
            get
            {
                lock (_users)
                {
                    return [.. _users];
                }
            }
        }

        public ValueTask DisposeAsync() => Http.DisposeAsync();
    }

    private static async Task TurnOnAsync(ShellFixture f, Server server)
    {
        var settings = f.Shell.Workbench.Settings;
        await settings.SetAsync("ai.openai.baseUrl", server.Http.BaseUrl.TrimEnd('/') + "/v1", SettingScope.User, CancellationToken.None);
        await settings.SetAsync(ShellHost.AiEnabledSetting, true, SettingScope.User, CancellationToken.None);
        await settings.SetAsync(ShellHost.AiProviderSetting, "openai", SettingScope.User, CancellationToken.None);
        await settings.SetAsync(ShellHost.AiModelSetting, "local-coder", SettingScope.User, CancellationToken.None);
        await ShellFixture.PumpUntil(() => f.Shell.Workbench.Context.GetValue("aiReady") is true);
    }

    [AvaloniaFact]
    public async Task Ask_ai_about_a_problem_sends_the_problem_and_the_code_around_it()
    {
        await using var server = new Server("Declare y first.");
        await using var f = ShellFixture.Create(builtInPlugins: AiPluginDir);
        await TurnOnAsync(f, server);
        var file = Path.Combine(f.UserData, "Broken.cs");
        File.WriteAllText(file, "class B\n{\n    int X() => y;\n}\n");
        var document = (await f.Shell.OpenFileAsync(file))!;
        await ShellFixture.PumpUntil(() => document.Controller is not null);
        f.Shell.Languages.Diagnostics.Set(file, "csharp", null,
            [new Diagnostic(new LanguageServices.Protocol.Range(new Position(2, 16), new Position(2, 17)), 1, "CS0103", "csharp", "The name 'y' does not exist in the current context")]);
        document.Controller!.Editor.CaretOffset = File.ReadAllText(file).IndexOf('y', StringComparison.Ordinal);

        await f.Shell.Workbench.Commands.ExecuteAsync("ai.explainProblem", [], CancellationToken.None);
        await ShellFixture.PumpUntil(() => server.Users.Count > 0, seconds: 60);
        var user = server.Users[0];
        Assert.Contains("CS0103: The name 'y' does not exist", user, StringComparison.Ordinal);
        Assert.Contains("3:     int X() => y;", user, StringComparison.Ordinal); // the code, numbered
        await ShellFixture.PumpUntil(() => JsonSerializer.Serialize(f.Shell.AiChatRoot()).Contains("Declare y first.", StringComparison.Ordinal), seconds: 30);
    }

    [AvaloniaFact]
    public async Task A_commit_message_is_drafted_from_the_staged_changes_into_git_changes()
    {
        await using var server = new Server("Add the greeter");
        await using var f = ShellFixture.Create(builtInPlugins: AiPluginDir);
        await TurnOnAsync(f, server);
        var repo = Path.Combine(f.UserData, "repo");
        Directory.CreateDirectory(repo);
        void Git(params string[] args)
        {
            var start = new ProcessStartInfo("git") { WorkingDirectory = repo, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in args)
            {
                start.ArgumentList.Add(arg);
            }

            using var process = Process.Start(start)!;
            process.WaitForExit();
        }

        Git("init", "-q");
        Git("config", "user.email", "test@example.invalid");
        Git("config", "user.name", "Test");
        File.WriteAllText(Path.Combine(repo, "Greeter.cs"), "class Greeter { string Hi() => \"hi\"; }\n");
        File.WriteAllText(Path.Combine(repo, ".env"), "TOKEN=do-not-send\n");
        Git("add", "Greeter.cs", ".env");
        Assert.True(await f.Shell.Git.OpenAsync(repo, CancellationToken.None));
        await f.Shell.Git.RefreshAsync();
        await ShellFixture.PumpUntil(() => f.Shell.GitChanges.Staged.Count == 2 && f.Shell.Workbench.Context.GetValue("gitRepository") is true, seconds: 30);

        await f.Shell.Workbench.Commands.ExecuteAsync("ai.draftCommitMessage", [], CancellationToken.None);
        await ShellFixture.PumpUntil(() => f.Shell.GitChanges.Message == "Add the greeter", seconds: 60);
        var user = Assert.Single(server.Users);
        Assert.Contains("+class Greeter", user, StringComparison.Ordinal);
        Assert.DoesNotContain("do-not-send", user, StringComparison.Ordinal); // a secret's diff isn't sent
        Assert.Contains("Also changed (not shown): .env", user, StringComparison.Ordinal);
    }
}
