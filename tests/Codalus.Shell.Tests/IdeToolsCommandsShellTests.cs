using System.Text.Json;
using Avalonia.Headless.XUnit;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Ai.Ide;
using Codalus.Workbench.Commands;
using Codalus.Workbench.Context;
using Codalus.Workbench.Settings;

namespace Codalus.Shell.Tests;

/// <summary>
/// The IDE's tools for agents, round 2 (M60): the IDE itself. Commands run under their own enablement, never the ones
/// agents don't get, with the dialogs and pickers they open cancelled; settings are read with secrets masked; a picture
/// of the main window or a tool window, never while a file outside the workspace shows; and the integrated terminal.
/// </summary>
public sealed class IdeToolsCommandsShellTests
{
    private static async Task<IdeToolResult> CallAsync(ShellFixture f, string tool, string json = "{}") =>
        await f.Shell.IdeToolAsync(tool, JsonElement.Parse(json), "Claude", CancellationToken.None);

    [AvaloniaFact]
    public async Task Agents_run_the_ides_commands_under_their_enablement_and_never_the_denied_ones()
    {
        await using var f = ShellFixture.Create();

        // The list leaves out what agents never get, and says which are disabled now: every id by area, whole (well under
        // the answer's size), and with a filter, the details.
        var listed = await CallAsync(f, "list_commands");
        Assert.False(listed.IsError, listed.Text);
        Assert.True(listed.Text.Length < 25_000, $"{listed.Text.Length} characters");
        Assert.Contains("workbench.view.debug.locals", listed.Text, StringComparison.Ordinal);
        Assert.Contains("workbench.closeSolution (off)", listed.Text, StringComparison.Ordinal);
        Assert.Contains("debug.restart (off)", listed.Text, StringComparison.Ordinal);
        Assert.Contains("run.configureStartupProjects", listed.Text, StringComparison.Ordinal); // no "restart" in it
        Assert.DoesNotContain("workbench.quit", listed.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("plugins.browse", listed.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("settingsSync.", listed.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("containers.composeUp", listed.Text, StringComparison.Ordinal);
        Assert.NotNull(IdeShellTools.DeniedReason("web.manageUserSecrets"));
        Assert.NotNull(IdeShellTools.DeniedReason("git.forcePush"));
        var closing = (await CallAsync(f, "list_commands", """{"filter":"closeSolution"}""")).Text;
        Assert.StartsWith("1 command(s) matching 'closeSolution'", closing, StringComparison.Ordinal);
        Assert.EndsWith("\nworkbench.closeSolution — File: Close Solution [disabled now: solutionOpen]", closing, StringComparison.Ordinal);

        // An allowed command runs as its menu item does.
        var ran = await CallAsync(f, "run_command", """{"id":"workbench.view.debug.locals"}""");
        Assert.False(ran.IsError, ran.Text);
        Assert.StartsWith("Ran '", ran.Text, StringComparison.Ordinal);
        Assert.True(f.Shell.Layout.IsVisible(VariablesViewModel.LocalsWindowId));

        // A disabled one is refused, saying when it runs.
        var disabled = await CallAsync(f, "run_command", """{"id":"workbench.closeSolution"}""");
        Assert.Equal((true, "'File: Close Solution' (workbench.closeSolution) is disabled right now: it runs only when 'solutionOpen'."), (disabled.IsError, disabled.Text));

        // Denied ones never run: exit, plugins, sign-ins, AI's settings, and what the command sweep skips for leaving the machine.
        var quits = 0;
        using var _ = f.Shell.Workbench.Commands.RegisterHandler("workbench.quit", (_, _) =>
        {
            quits++;
            return Task.FromResult<JsonElement?>(null);
        });
        foreach (var id in new[] { "workbench.quit", "plugins.browse", "packages.signIn", "ai.enable", "solution.trust", "workbench.setSetting", "containers.composeUp", "update.check" })
        {
            var refused = await CallAsync(f, "run_command", $$"""{"id":"{{id}}"}""");
            Assert.True(refused.IsError, id);
            Assert.StartsWith($"'{id}' isn't run for agents: ", refused.Text, StringComparison.Ordinal);
        }

        Assert.Equal(0, quits);
        Assert.Contains("builds images and pulls", (await CallAsync(f, "run_command", """{"id":"containers.composeUp"}""")).Text, StringComparison.Ordinal);
        Assert.Equal("There is no command 'no.such.command'. list_commands lists them.", (await CallAsync(f, "run_command", """{"id":"no.such.command"}""")).Text);

        // A dialog or a file picker a command opens is cancelled (nobody would answer it), and the command carries on.
        var answers = new List<string?>();
        using var ask = f.Shell.Workbench.Commands.Register(new CommandDescriptor("test.askThenPick", "Ask Then Pick", ContributionSource.Shell), async (args, _) =>
        {
            answers.Add((await f.Shell.Dialogs.ShowAsync(new DialogRequest("Replace the file?", "It exists.", ["Replace", "Cancel"]))).Button);
            answers.Add(await f.Shell.Window!.PickFileAsync("Choose a Dump", "Dumps", ["*.dmp"]));
            f.Shell.Workbench.Notifications.Info("Nothing was chosen.");
            return JsonSerializer.SerializeToElement(args.Count);
        });
        var asked = await CallAsync(f, "run_command", """{"id":"test.askThenPick","args":["a",2]}""");
        Assert.False(asked.IsError, asked.Text);
        Assert.Equal([null, null], answers);
        Assert.Equal(
            "Ran 'Ask Then Pick' (test.askThenPick). It returned: 2"
            + "\nIt opened a dialog ('Replace the file?': It exists.), which an agent can't answer: it was cancelled. Pass what it asks for as the command's args, or ask the user."
            + "\nIt opened a file picker (Choose a Dump), which an agent can't answer: it was cancelled. Pass what it asks for as the command's args, or ask the user."
            + "\nNotification (info): Nothing was chosen.",
            asked.Text);
        Assert.IsType<Views.MainWindow>(f.Shell.Window);

        // A command that fails says why.
        using var fails = f.Shell.Workbench.Commands.Register(new CommandDescriptor("test.fails", "Fails", ContributionSource.Shell) { Enablement = WhenClause.Always }, (_, _) => throw new InvalidOperationException("the disk is full"));
        var failed = await CallAsync(f, "run_command", """{"id":"test.fails"}""");
        Assert.Equal((true, "'Fails' (test.fails) failed: the disk is full"), (failed.IsError, failed.Text));
    }

    [AvaloniaFact]
    public async Task Agents_read_settings_without_secrets()
    {
        await using var f = ShellFixture.Create();
        var settings = f.Shell.Workbench.Settings;
        await settings.SetAsync("editor.tabSize", JsonSerializer.SerializeToElement(2), SettingScope.User, CancellationToken.None);

        var changed = await CallAsync(f, "get_settings");
        Assert.False(changed.IsError, changed.Text);
        Assert.StartsWith("The settings set to something other than their default:\n", changed.Text, StringComparison.Ordinal);
        Assert.Contains("editor.tabSize = 2 (from the user's settings; default 4)", changed.Text, StringComparison.Ordinal);

        var named = await CallAsync(f, "get_settings", """{"keys":["editor.tabSize","terminal.","no.such.setting"]}""");
        Assert.Contains("editor.tabSize = 2", named.Text, StringComparison.Ordinal);
        Assert.Contains("terminal.scrollback = ", named.Text, StringComparison.Ordinal);
        Assert.Contains("no.such.setting: no such setting", named.Text, StringComparison.Ordinal);

        // A value whose name says it is a secret is masked, wherever it is in the value.
        var value = JsonElement.Parse("""{"name":"agent","env":{"OPENAI_API_KEY":"abc123","MODE":"fast"},"token":"xyz","headers":[{"Authorization":"Bearer q"}]}""");
        Assert.Equal(
            """{"name":"agent","env":{"OPENAI_API_KEY":"(hidden)","MODE":"fast"},"token":"(hidden)","headers":[{"Authorization":"(hidden)"}]}""",
            ShellHost.MaskedSettingJson("ai.agents", value));
        Assert.Equal("\"(hidden)\"", ShellHost.MaskedSettingJson("nuget.feed.apiKey", JsonElement.Parse("\"abc\"")));
    }

    [AvaloniaFact]
    public async Task Agents_get_a_picture_of_the_ide_but_not_of_a_file_outside_the_workspace()
    {
        await using var f = ShellFixture.Create();
        f.Window.Width = 1000;
        f.Window.Height = 700;
        ShellFixture.Pump();

        var shot = await CallAsync(f, "ide_screenshot");
        Assert.False(shot.IsError, shot.Text);
        var image = Assert.Single(shot.Images);
        Assert.Equal("image/png", image.MimeType);
        using (var bitmap = new Avalonia.Media.Imaging.Bitmap(new MemoryStream(Convert.FromBase64String(image.Data))))
        {
            Assert.True(bitmap.PixelSize.Width >= 1000 && bitmap.PixelSize.Height >= 700, $"{bitmap.PixelSize}");
        }

        // One tool window, by its id or title: smaller than the window.
        f.Shell.Layout.ShowToolWindow(VariablesViewModel.LocalsWindowId, focus: false);
        await ShellFixture.PumpUntil(() => f.Shell.Layout.IsVisible(VariablesViewModel.LocalsWindowId));
        ShellFixture.Pump();
        var locals = await CallAsync(f, "ide_screenshot", """{"window":"Locals"}""");
        Assert.False(locals.IsError, locals.Text);
        using (var bitmap = new Avalonia.Media.Imaging.Bitmap(new MemoryStream(Convert.FromBase64String(Assert.Single(locals.Images).Data))))
        {
            Assert.True(bitmap.PixelSize.Width < 1000 || bitmap.PixelSize.Height < 700, $"{bitmap.PixelSize}");
        }

        Assert.True((await CallAsync(f, "ide_screenshot", """{"window":"no such window"}""")).IsError);

        // A file outside the workspace showing in the editor: no picture.
        var outside = Path.Combine(f.UserData, "outside.txt");
        File.WriteAllText(outside, "a private note");
        await f.Shell.OpenFileAsync(outside);
        await ShellFixture.PumpUntil(() => f.Shell.ActiveEditor?.Model.Path == outside);
        var refused = await CallAsync(f, "ide_screenshot");
        Assert.True(refused.IsError);
        Assert.Empty(refused.Images);
        Assert.Contains("outside.txt", refused.Text, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task Agents_type_into_the_integrated_terminal_and_read_its_screen()
    {
        await using var f = ShellFixture.Create();
        f.Window.Width = 1200;
        f.Window.Height = 800;
        var folder = Path.Combine(f.UserData, "work");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "notes.txt"), "notes");
        await f.Shell.Workbench.Settings.SetAsync(ShellHost.TerminalDefaultProfileSetting, OperatingSystem.IsWindows() ? "cmd" : "sh", SettingScope.User, CancellationToken.None);

        // Only in a workspace, a trusted one.
        var none = await CallAsync(f, "terminal_send", """{"text":"echo hi"}""");
        Assert.Equal((true, "No solution or folder is open."), (none.IsError, none.Text));
        Assert.Empty(f.Shell.TerminalWindow.Tabs);
        f.Shell.Trust.Trust(folder);
        await f.Shell.OpenFolderAsync(folder);
        await ShellFixture.PumpUntil(() => f.Shell.Solutions.Solution is { Trusted: true }, seconds: 60);

        var command = OperatingSystem.IsWindows() ? "set /a 6*7" : "echo $((6*7))";
        var sent = await CallAsync(f, "terminal_send", $$"""{"text":"{{command}}","waitSeconds":20}""");
        Assert.False(sent.IsError, sent.Text);
        Assert.Contains("(a new one)", sent.Text, StringComparison.Ordinal);
        Assert.Contains("\n42", sent.Text.ReplaceLineEndings("\n"), StringComparison.Ordinal);
        Assert.Single(f.Shell.TerminalWindow.Tabs);
        Assert.True(f.Shell.Layout.IsVisible(TerminalViewModel.ToolWindowId));

        // The same terminal next time, unless a new one is asked for.
        var again = await CallAsync(f, "terminal_send", $$"""{"text":"{{(OperatingSystem.IsWindows() ? "echo again" : "echo again")}}","waitSeconds":20}""");
        Assert.DoesNotContain("(a new one)", again.Text, StringComparison.Ordinal);
        Assert.Contains("\nagain", again.Text.ReplaceLineEndings("\n"), StringComparison.Ordinal);
        Assert.Single(f.Shell.TerminalWindow.Tabs);
        await CallAsync(f, "terminal_send", """{"text":"echo third","newTerminal":true,"waitSeconds":20}""");
        Assert.Equal(2, f.Shell.TerminalWindow.Tabs.Count);
    }
}
