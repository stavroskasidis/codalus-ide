using System.Diagnostics;
using System.Text.Json;
using Avalonia.VisualTree;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Codalus.LanguageServices.Protocol;
using Codalus.Shell.ViewModels;
using Codalus.Shell.Views;
using Codalus.Tests;
using Codalus.Workbench.Ai;
using Codalus.Workbench.Settings;

namespace Codalus.Shell.Tests;

/// <summary>
/// M51's AI on existing surfaces, with the OpenAI-compatible plugin against <see cref="MockModelServers"/>: the Error
/// List's Fix with AI and Explain with AI, the AI items after the code actions (Ctrl+.), and Ask AI About the Value in the
/// debugger's variable menus. Each shows only while AI assistance is on, and goes through AiService.
/// </summary>
public sealed class AiSurfacesTests
{
    private const string Text = "class Calc\n{\n    int Add(int a, int b)\n    {\n        return a + c;\n    }\n}\n";

    private static string Reply(MockHttpRequest request) =>
        request.Body.Contains("You fix a compiler", StringComparison.Ordinal)
            ? "```csharp\n    int Add(int a, int b)\n    {\n        return a + b;\n    }\n```"
            : "It's explained.";

    private static async Task<(ShellFixture F, string Path)> SetUpAsync(MockHttpServer server, bool aiOn = true)
    {
        var f = ShellFixture.Create(builtInPlugins: ShellFixture.PluginBin("Codalus.Plugins.OpenAI"));
        var settings = f.Shell.Workbench.Settings;
        await settings.SetAsync("ai.openai.endpoint", server.BaseUrl + "/v1", SettingScope.User, CancellationToken.None);
        await settings.SetAsync(AiService.EnabledSetting, aiOn, SettingScope.User, CancellationToken.None);
        await settings.SetAsync(AiService.ProviderSetting, "openai", SettingScope.User, CancellationToken.None);
        await settings.SetAsync(AiService.ModelSetting, "gpt-test", SettingScope.User, CancellationToken.None);
        await f.Shell.Secrets.StoreAsync("codalus.openai", "apiKey:openai", MockModelServers.Key, CancellationToken.None);
        var path = System.IO.Path.Combine(f.UserData, "Calc.txt");
        File.WriteAllText(path, Text);
        return (f, path);
    }

    private static Diagnostic Error => new(new LanguageServices.Protocol.Range(new Position(4, 19), new Position(4, 20)), 1, "CS0103", "csharp", "The name 'c' does not exist in the current context");

    /// <summary>The last question sent to the model.</summary>
    private static string LastQuestion(MockHttpServer server)
    {
        using var body = JsonDocument.Parse(server.Requests.Last(r => r.Path == "/v1/chat/completions").Body);
        return string.Join("\n", body.RootElement.GetProperty("messages").EnumerateArray().Select(m => m.GetProperty("content").GetString()));
    }

    private static IReadOnlyList<string?> Headers(IEnumerable<Control> items) => [.. items.OfType<MenuItem>().Select(i => i.Header as string)];

    private static void Git(string directory, params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEndAsync();
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)}: {error.GetAwaiter().GetResult()}");
    }

    /// <summary>
    /// Write the Commit Message with AI describes what Commit will commit: with nothing staged, all the changes, new files
    /// included, as Commit All commits them (it said "Stage the changes first" in the status bar, which looked like nothing
    /// happened). While it writes, Git Changes says so with a bar under the message and its ✦ button waits (ADR 0097).
    /// </summary>
    [AvaloniaFact]
    public async Task With_nothing_staged_the_commit_message_describes_all_changes_and_Git_Changes_shows_it_being_written()
    {
        using var release = new ManualResetEventSlim();
        await using var server = MockModelServers.OpenAI(request =>
        {
            if (request.Body.Contains("You write git commit messages", StringComparison.Ordinal))
            {
                release.Wait(TimeSpan.FromSeconds(60)); // held until the test has seen it being written
                return "Add a greeting and a helper";
            }

            return "It's explained.";
        });
        var (f, _) = await SetUpAsync(server);
        await using var _ = f;
        var folder = f.CopySample("BuildAndRun");
        Git(folder, "init", "-q", "-b", "main");
        Git(folder, "config", "user.name", "Ada Tester");
        Git(folder, "config", "user.email", "ada@example.com");
        Git(folder, "config", "commit.gpgsign", "false");
        Git(folder, "config", "core.autocrlf", "false");
        File.WriteAllText(System.IO.Path.Combine(folder, ".gitignore"), "bin/\nobj/\n");
        Git(folder, "add", ".");
        Git(folder, "commit", "-q", "-m", "initial");
        f.Shell.Trust.Trust(System.IO.Path.GetDirectoryName(folder)!);
        await f.Shell.OpenSolutionAsync(System.IO.Path.Combine(folder, "BuildAndRun.slnx"));
        await ShellFixture.PumpUntil(() => f.Shell.Git.Status is { Branch: "main" });

        // An edit and a new file, nothing staged: Commit All would commit both.
        var greeter = System.IO.Path.Combine(folder, "src", "Greetings", "Greeter.cs");
        File.WriteAllText(greeter, "// a greeting\n" + File.ReadAllText(greeter));
        File.WriteAllText(System.IO.Path.Combine(folder, "src", "Greetings", "Helper.cs"), "class Helper { }\n");
        await f.Shell.Git.RefreshAsync(TestContext.Current.CancellationToken);
        await ShellFixture.PumpUntil(() => f.Shell.GitChanges.Changes.Count >= 2 && !f.Shell.GitChanges.HasStaged);
        f.Shell.Layout.ShowToolWindow(GitChangesViewModel.ToolWindowId);
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<GitChangesView>().Any(v => v.IsEffectivelyVisible));
        var view = f.Window.GetVisualDescendants().OfType<GitChangesView>().First(v => v.IsEffectivelyVisible);
        var sparkle = view.GetVisualDescendants().OfType<Button>().Single(b => b.Tag as string == "ai.generateCommitMessage");
        Assert.True(sparkle.IsEffectivelyEnabled);

        var writing = f.Shell.GenerateCommitMessageAsync();
        await ShellFixture.PumpUntil(() => f.Shell.GitChanges.IsWritingMessage && view.FindControl<Grid>("WritingMessage")!.IsEffectivelyVisible);
        Assert.False(sparkle.IsEffectivelyEnabled); // one at a time
        release.Set();
        await writing;

        Assert.Equal("Add a greeting and a helper", f.Shell.GitChanges.Message);
        Assert.False(f.Shell.GitChanges.IsWritingMessage);
        ShellFixture.Pump();
        Assert.False(view.FindControl<Grid>("WritingMessage")!.IsEffectivelyVisible);
        Assert.True(sparkle.IsEffectivelyEnabled);
        var question = LastQuestion(server);
        Assert.Contains("nothing is staged", question, StringComparison.Ordinal);
        Assert.Contains("+// a greeting", question, StringComparison.Ordinal);
        Assert.Contains("+class Helper { }", question, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task The_Error_Lists_menu_offers_Fix_and_Explain_with_AI_while_AI_is_on()
    {
        await using var server = MockModelServers.OpenAI(Reply);
        var (f, path) = await SetUpAsync(server, aiOn: false);
        await using var _ = f;
        f.Shell.Languages.Diagnostics.Set(path, "plaintext", null, [Error]);
        await ShellFixture.PumpUntil(() => f.Shell.ErrorList.Items.OfType<ErrorListRow>().Any());
        var row = f.Shell.ErrorList.Items.OfType<ErrorListRow>().Single();
        var view = new ErrorListView { DataContext = f.Shell.ErrorList };

        Assert.Equal(["Go To", "Copy"], Headers(view.MenuItems(f.Shell.ErrorList, row)));
        Assert.Equal("CS0103\tThe name 'c' does not exist in the current context\t\tCalc.txt\t5", ErrorListViewModel.CopyText(row));

        await f.Shell.Workbench.Settings.SetAsync(AiService.EnabledSetting, true, SettingScope.User, CancellationToken.None);
        Assert.Equal(["Go To", "Copy", "Fix with AI", "Explain with AI"], Headers(view.MenuItems(f.Shell.ErrorList, row)));

        // Explain with AI asks AI Chat about the error, with the lines around it.
        await f.Shell.ErrorList.ExplainWithAi!(row);
        var turn = Assert.Single(f.Shell.AiChat.Chat.Turns);
        await ShellFixture.PumpUntil(() => !turn.IsStreaming);
        Assert.Equal(AiActions.ExplainDiagnosticQuestion, turn.Question);
        var question = LastQuestion(server);
        Assert.Contains("CS0103: The name 'c' does not exist in the current context (Calc.txt:5)", question, StringComparison.Ordinal);
        Assert.Contains("return a + c;", question, StringComparison.Ordinal);

        // Fix with AI opens the file at the error and fixes it through the preview.
        var fixing = f.Shell.ErrorList.FixWithAi!(row);
        await ShellFixture.PumpUntil(() => f.Shell.Dialogs.IsOpen && f.Shell.Dialogs.HasDetails, 30);
        Assert.Equal(path, f.Shell.ActiveEditor?.Model.Path);
        Assert.Contains(server.Requests, r => r.Body.Contains("You fix a compiler", StringComparison.Ordinal));
        f.Shell.Dialogs.Accept();
        await fixing;
        Assert.Contains("return a + b;", f.Shell.ActiveEditor!.Model.Text.Text, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task The_code_actions_end_with_the_AI_items_while_AI_is_on()
    {
        await using var server = MockModelServers.OpenAI(Reply);
        var (f, path) = await SetUpAsync(server, aiOn: false);
        await using var _ = f;
        Assert.Empty(f.Shell.AiCodeActions(path, [Error]));

        await f.Shell.Workbench.Settings.SetAsync(AiService.EnabledSetting, true, SettingScope.User, CancellationToken.None);
        Assert.Equal(["Generate Tests with AI"], f.Shell.AiCodeActions(path, []).Select(i => i.Label));
        var items = f.Shell.AiCodeActions(path, [Error]);
        Assert.Equal(["Fix with AI", "Explain with AI", "Generate Tests with AI"], items.Select(i => i.Label));
        Assert.All(items, i => Assert.Equal("AI", i.Detail));

        // Explain with AI asks about the first diagnostic on the line.
        await ((Func<Task>)items[1].Tag!)();
        var turn = Assert.Single(f.Shell.AiChat.Chat.Turns);
        await ShellFixture.PumpUntil(() => !turn.IsStreaming);
        Assert.Contains("CS0103", LastQuestion(server), StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task Ask_AI_About_the_Value_is_in_the_variables_menu_and_sends_the_value()
    {
        await using var server = MockModelServers.OpenAI(Reply);
        var (f, _) = await SetUpAsync(server, aiOn: false);
        await using var __ = f;
        var node = new VariableNode("total", "42", "int", 0, null, evaluateName: "order.Total");
        Assert.DoesNotContain("Ask AI About the Value", Headers(VariableTreeSupport.MenuItems(node, null)));

        await f.Shell.Workbench.Settings.SetAsync(AiService.EnabledSetting, true, SettingScope.User, CancellationToken.None);
        var item = VariableTreeSupport.MenuItems(node, null).OfType<MenuItem>().Single(i => (i.Header as string) == "Ask AI About the Value");
        item.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
        await ShellFixture.PumpUntil(() => f.Shell.AiChat.Chat.Turns.Count == 1);
        var turn = f.Shell.AiChat.Chat.Turns[0];
        await ShellFixture.PumpUntil(() => !turn.IsStreaming);
        Assert.Equal(AiActions.ExplainValueQuestion, turn.Question);
        Assert.Contains("order.Total (int) = 42", LastQuestion(server), StringComparison.Ordinal);
    }
}
