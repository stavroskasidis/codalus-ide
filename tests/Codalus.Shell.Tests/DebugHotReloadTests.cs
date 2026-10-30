using Avalonia.Headless.XUnit;
using Codalus.Debugging;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Debugging;
using Codalus.Workbench.Settings;

namespace Codalus.Shell.Tests;

/// <summary>
/// M55 in the shell (ADR 0086's phase 2), on samples/Debugging/HotReload.slnx under the Codalus debugger: Continue
/// applies an edit made while stopped, Apply Code Changes one made while the program runs, and so does a save; a rude
/// edit asks (Restart, or go on with the old code) and its diagnostic is in the Error List. Under netcoredbg nothing
/// takes edits.
/// </summary>
public sealed class DebugHotReloadTests
{
    private static int Line(string file, string marker) =>
        Array.FindIndex(File.ReadAllLines(file), l => l.Contains($"// [{marker}]", StringComparison.Ordinal)) + 1;

    private static async Task<string> OpenAsync(ShellFixture f, string adapter)
    {
        await f.Shell.Workbench.Settings.SetAsync(ShellHost.DebugAdapterSetting, adapter, SettingScope.User, CancellationToken.None);
        var folder = f.CopySample("Debugging");
        f.Shell.Trust.Trust(Path.GetDirectoryName(folder)!);
        await f.Shell.OpenSolutionAsync(Path.Combine(folder, "HotReload.slnx"));
        await ShellFixture.PumpUntil(() => f.Shell.Solutions.Projects.Count == 3 && f.Shell.Solutions.Progress is { Completed: true }, seconds: 180);
        f.Shell.Runs.SetStartupProject(Path.Combine(folder, "HotReload", "HotReload.csproj"));
        return Path.Combine(folder, "HotReload", "Program.cs");
    }

    private static async Task<Editor.CodeEditorDocument> EditAsync(ShellFixture f, string path, string oldText, string newText)
    {
        var editor = await f.Shell.OpenFileAsync(path, preview: false, null);
        await ShellFixture.PumpUntil(() => editor?.Controller is not null);
        var offset = editor!.Model.Text.Text.IndexOf(oldText, StringComparison.Ordinal);
        Assert.True(offset >= 0, $"'{oldText}' not found");
        editor.Model.Text.Replace(offset, oldText.Length, newText);
        return editor;
    }

    private static string Diagnostics(ShellFixture f) =>
        $"\nState: {f.Shell.Debugger.State}, edits: {f.Shell.DebugEdits.IsActive}; {f.Shell.Debugger.Describe()}\nDebug log:\n" + f.Shell.DebugLog.GetText() + "\nNotifications:\n" + string.Join('\n', f.Shell.Workbench.Notifications.Items.Select(n => n.Message));

    [AvaloniaFact]
    public async Task Continue_applies_an_edit_made_while_stopped_and_Apply_Code_Changes_one_made_while_running()
    {
        await using var f = ShellFixture.Create();
        var program = await OpenAsync(f, DebugService.CodalusAdapter);
        f.Shell.Debugger.Breakpoints.Toggle(program, Line(program, "call"));
        await f.RunAsync("debug.start");
        await ShellFixture.PumpUntil(() => f.Shell.Debugger.State == DebugState.Stopped, seconds: 180, describe: () => Diagnostics(f));
        await ShellFixture.PumpUntil(() => f.Shell.DebugEdits.IsActive && Equals(f.Shell.Workbench.Context.GetValue("hotReloadRunning"), true), seconds: 60, describe: () => Diagnostics(f));

        // Stopped before Message(0): Continue applies the edit first, so the first call runs it.
        await EditAsync(f, program, "        return text;", "        return text + \" from the shell\";");
        f.Shell.Debugger.Breakpoints.Clear();
        await f.RunAsync("debug.continue");
        await ShellFixture.PumpUntil(() => f.Shell.DebugLog.GetText().Contains("hello 0 from the shell", StringComparison.Ordinal), seconds: 60, describe: () => Diagnostics(f));

        // Running: Apply Code Changes, from the editor's text as it is (unsaved).
        await EditAsync(f, program, "\" from the shell\"", "\" applied\"");
        await f.RunAsync("run.applyCodeChanges");
        await ShellFixture.PumpUntil(() => f.Shell.DebugLog.GetText().Contains(" applied\n", StringComparison.Ordinal) || f.Shell.DebugLog.GetText().Contains(" applied\r\n", StringComparison.Ordinal), seconds: 60, describe: () => Diagnostics(f));
        var status = f.Shell.Workbench.StatusBar.GetItems(Workbench.StatusBar.StatusBarAlignment.Left).Single(i => i.Id == "codalus.hotReload").Text;
        Assert.Contains("Hot Reload: applied at", status, StringComparison.Ordinal);

        await f.RunAsync("debug.stop");
        await ShellFixture.PumpUntil(() => f.Shell.Debugger.State == DebugState.Idle && !f.Shell.DebugEdits.IsActive, seconds: 60);
        await ShellFixture.PumpUntil(() => !Equals(f.Shell.Workbench.Context.GetValue("hotReloadRunning"), true));
    }

    [AvaloniaFact]
    public async Task A_save_applies_while_running_and_a_rude_edit_asks_and_shows_in_the_Error_List()
    {
        await using var f = ShellFixture.Create();
        var program = await OpenAsync(f, DebugService.CodalusAdapter);
        await f.RunAsync("debug.start");
        await ShellFixture.PumpUntil(() => f.Shell.Debugger.State == DebugState.Running && f.Shell.DebugEdits.IsActive, seconds: 180, describe: () => Diagnostics(f));
        await ShellFixture.PumpUntil(() => f.Shell.DebugLog.GetText().Contains("hello 2", StringComparison.Ordinal), seconds: 60, describe: () => Diagnostics(f));

        // A save while it runs (debug.hotReload is on by default).
        await EditAsync(f, program, "        return text;", "        return text + \" saved\";");
        await f.RunAsync("file.save");
        await ShellFixture.PumpUntil(() => f.Shell.DebugLog.GetText().Contains(" saved", StringComparison.Ordinal), seconds: 60, describe: () => Diagnostics(f));

        // A rude edit: the question, and its diagnostic in the Error List; going on keeps the old code running.
        await EditAsync(f, program, "static class Greeter", "class Greeter : Exception");
        _ = f.RunAsync("run.applyCodeChanges");
        await ShellFixture.PumpUntil(() => f.Shell.Workbench.Notifications.Items.Any(n => n.Actions.Contains("Continue with the Old Code")), seconds: 60, describe: () => Diagnostics(f));
        var question = f.Shell.Workbench.Notifications.Items.Single(n => n.Actions.Contains("Continue with the Old Code"));
        Assert.Contains("Restart", question.Actions);
        await ShellFixture.PumpUntil(() => f.Shell.ErrorList.Rows.Any(r => r.Source == ErrorListViewModel.HotReloadSource && r.Code.StartsWith("ENC", StringComparison.Ordinal)), seconds: 30);
        f.Shell.Workbench.Notifications.Choose(question, "Continue with the Old Code");
        await ShellFixture.PumpUntil(() => f.Shell.Workbench.StatusBar.GetItems(Workbench.StatusBar.StatusBarAlignment.Left).Single(i => i.Id == "codalus.hotReload").Text.Contains("restart needed", StringComparison.Ordinal));
        Assert.Equal(DebugState.Running, f.Shell.Debugger.State);

        // Asked again, Restart: the program is built and debugged again, and the new session takes edits.
        var before = f.Shell.Debugger.ProcessId;
        _ = f.RunAsync("run.applyCodeChanges");
        await ShellFixture.PumpUntil(() => f.Shell.Workbench.Notifications.Items.Any(n => n.Actions.Contains("Continue with the Old Code")), seconds: 60, describe: () => Diagnostics(f));
        f.Shell.Workbench.Notifications.Choose(f.Shell.Workbench.Notifications.Items.Single(n => n.Actions.Contains("Continue with the Old Code")), "Restart");
        await ShellFixture.PumpUntil(() => f.Shell.Debugger.State == DebugState.Running && f.Shell.Debugger.ProcessId is { } now && now != before && f.Shell.DebugEdits.IsActive, seconds: 180, describe: () => Diagnostics(f));
        await ShellFixture.PumpUntil(() => !f.Shell.ErrorList.Rows.Any(r => r.Source == ErrorListViewModel.HotReloadSource));

        await f.RunAsync("debug.stop");
        await ShellFixture.PumpUntil(() => f.Shell.Debugger.State == DebugState.Idle && !f.Shell.DebugEdits.IsActive, seconds: 60);
    }

    [AvaloniaFact]
    public async Task A_process_attached_to_without_modifiable_assemblies_says_why_edits_dont_apply()
    {
        await using var f = ShellFixture.Create();
        var program = await OpenAsync(f, DebugService.CodalusAdapter);
        await f.Shell.BuildAsync(Codalus.Projects.Protocol.BuildTargets.Build, null);
        var dll = Path.Combine(Path.GetDirectoryName(program)!, "bin", "Debug", "net11.0", "HotReload.dll");
        var go = Path.Combine(Path.GetDirectoryName(program)!, "go-" + Guid.NewGuid().ToString("N"));
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("dotnet", ["exec", dll, go]) { UseShellExecute = false, RedirectStandardOutput = true })!;
        try
        {
            Assert.Equal("waiting", await process.StandardOutput.ReadLineAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken));
            await f.Shell.Debugger.AttachAsync(process.Id, TestContext.Current.CancellationToken);
            await ShellFixture.PumpUntil(() => f.Shell.DebugEdits.IsActive && f.Shell.Debugger.Modules.Any(m => m.Name == "HotReload.dll"), seconds: 60, describe: () => Diagnostics(f));
            await EditAsync(f, program, "        return text;", "        return text + \" attached\";");
            await f.RunAsync("run.applyCodeChanges");
            await ShellFixture.PumpUntil(() => f.Shell.Workbench.Notifications.Items.Any(n => n.Message.Contains("DOTNET_MODIFIABLE_ASSEMBLIES=debug", StringComparison.Ordinal)), seconds: 60, describe: () => Diagnostics(f));
            await f.Shell.Debugger.DetachAsync();
        }
        finally
        {
            Codalus.Platform.ProcessTree.Kill(process);
        }
    }

    [AvaloniaFact]
    public async Task Under_netcoredbg_nothing_takes_edits()
    {
        Assert.SkipWhen(DebugAdapterLocator.Resolve() is null && Environment.GetEnvironmentVariable("CI") != "true", "netcoredbg is not installed (eng/get-netcoredbg.sh).");
        Assert.SkipWhen(Environment.GetEnvironmentVariable("CODALUS_DEBUG_ADAPTER") == DebugService.CodalusAdapter, "The debug-codalus shard runs every session under the Codalus debugger.");
        await using var f = ShellFixture.Create();
        var program = await OpenAsync(f, DebugService.NetcoredbgAdapter);
        f.Shell.Debugger.Breakpoints.Toggle(program, Line(program, "call"));
        await f.RunAsync("debug.start");
        await ShellFixture.PumpUntil(() => f.Shell.Debugger.State == DebugState.Stopped, seconds: 180, describe: () => Diagnostics(f));
        Assert.False(f.Shell.DebugEdits.IsActive);
        Assert.NotEqual(true, f.Shell.Workbench.Context.GetValue("hotReloadRunning"));

        // A saved edit offers the Codalus debugger, once; Use It is for the next start.
        await EditAsync(f, program, "        return text;", "        return text + \" offered\";");
        await f.RunAsync("file.save");
        await ShellFixture.PumpUntil(() => f.Shell.Workbench.Notifications.Items.Any(n => n.Actions.Contains("Use It")), seconds: 30);
        f.Shell.Workbench.Notifications.Choose(f.Shell.Workbench.Notifications.Items.Single(n => n.Actions.Contains("Use It")), "Use It");
        await ShellFixture.PumpUntil(() => f.Shell.Workbench.Settings.GetString(ShellHost.DebugAdapterSetting, "") == DebugService.CodalusAdapter);
        await EditAsync(f, program, "\" offered\"", "\" again\"");
        await f.RunAsync("file.save");
        await Task.Delay(500, TestContext.Current.CancellationToken);
        Assert.DoesNotContain(f.Shell.Workbench.Notifications.Items, n => n.Actions.Contains("Use It"));
        await f.RunAsync("debug.stop");
        await ShellFixture.PumpUntil(() => f.Shell.Debugger.State == DebugState.Idle, seconds: 60);
    }
}
