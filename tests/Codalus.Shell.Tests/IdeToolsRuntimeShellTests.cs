using System.Text.Json;
using Avalonia.Headless.XUnit;
using Codalus.Debugging;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Ai.Ide;
using Codalus.Workbench.Debugging;
using Codalus.Workbench.Settings;

namespace Codalus.Shell.Tests;

/// <summary>
/// M60's runtime tools for agents: apply_code_changes on the Codalus debugger's Hot Reload (samples/Debugging/HotReload,
/// an edit an agent wrote to disk, and a rude edit reported rather than asked about), and the profiler's CPU and heap
/// summaries (samples/Diagnostics: CpuBound launched under the profiler, Leaky run and snapshotted, then attached to).
/// </summary>
public sealed class IdeToolsRuntimeShellTests
{
    private static async Task<IdeToolResult> CallAsync(ShellFixture f, string tool, string json = "{}") =>
        await f.Shell.IdeToolAsync(tool, JsonElement.Parse(json), "Claude", CancellationToken.None);

    private static string Diagnostics(ShellFixture f) =>
        $"\nState: {f.Shell.Debugger.State}, edits: {f.Shell.DebugEdits.IsActive}; {f.Shell.Debugger.Describe()}\n"
        + "Debug log:\n" + f.Shell.DebugLog.GetText() + "\nNotifications:\n" + string.Join('\n', f.Shell.Workbench.Notifications.Items.Select(n => n.Message));

    [AvaloniaFact]
    public async Task Agents_apply_code_changes_to_the_program_being_debugged()
    {
        await using var f = ShellFixture.Create();
        await f.Shell.Workbench.Settings.SetAsync(ShellHost.DebugAdapterSetting, DebugService.CodalusAdapter, SettingScope.User, CancellationToken.None);
        var folder = f.CopySample("Debugging");
        f.Shell.Trust.Trust(Path.GetDirectoryName(folder)!);
        await f.Shell.OpenSolutionAsync(Path.Combine(folder, "HotReload.slnx"));
        await ShellFixture.PumpUntil(() => f.Shell.Solutions.Projects.Count == 3 && f.Shell.Solutions.Progress is { Completed: true }, seconds: 180);
        f.Shell.Runs.SetStartupProject(Path.Combine(folder, "HotReload", "HotReload.csproj"));

        // Nothing runs: the answer says how to start something that takes changes.
        var none = await CallAsync(f, "apply_code_changes");
        Assert.True(none.IsError);
        Assert.Contains("debug_start", none.Text, StringComparison.Ordinal);

        var started = await CallAsync(f, "debug_start");
        Assert.False(started.IsError, started.Text);
        await ShellFixture.PumpUntil(() => f.Shell.Debugger.State == DebugState.Running && f.Shell.DebugEdits.IsActive, seconds: 180, describe: () => Diagnostics(f));
        await ShellFixture.PumpUntil(() => f.Shell.DebugLog.GetText().Contains("hello 2", StringComparison.Ordinal), seconds: 60, describe: () => Diagnostics(f));

        // The agent edits the closed file (written to disk), then applies: the running program prints the new text.
        var edited = await CallAsync(f, "replace_in_files", """{"find":"        return text;","replace":"        return text + \" from the agent\";","glob":"Program.cs"}""");
        Assert.False(edited.IsError, edited.Text);
        Assert.Contains("(written to disk)", edited.Text, StringComparison.Ordinal);
        var applied = await CallAsync(f, "apply_code_changes");
        Assert.False(applied.IsError, applied.Text + Diagnostics(f));
        Assert.StartsWith("The code changes were applied to the program being debugged", applied.Text, StringComparison.Ordinal);
        await ShellFixture.PumpUntil(() => f.Shell.DebugLog.GetText().Contains(" from the agent", StringComparison.Ordinal), seconds: 60, describe: () => Diagnostics(f));

        // Nothing more to apply.
        var again = await CallAsync(f, "apply_code_changes");
        Assert.Equal("There were no code changes to apply: the program already runs the code as it is.", again.Text);

        // A rude edit: reported with its diagnostic, never asked about; the program goes on with the old code.
        Assert.False((await CallAsync(f, "replace_in_files", """{"find":"static class Greeter","replace":"class Greeter : Exception","glob":"Program.cs","matchCase":true}""")).IsError);
        var rude = await CallAsync(f, "apply_code_changes");
        Assert.True(rude.IsError, rude.Text);
        Assert.StartsWith("Not applied: the changes can't be applied while the program runs (a rude edit)", rude.Text, StringComparison.Ordinal);
        Assert.Contains("- ENC", rude.Text, StringComparison.Ordinal);
        Assert.Contains("(HotReload/Program.cs:", rude.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(f.Shell.Workbench.Notifications.Items, n => n.Actions.Contains("Continue with the Old Code"));
        Assert.Equal(DebugState.Running, f.Shell.Debugger.State);

        await CallAsync(f, "stop_run");
        await ShellFixture.PumpUntil(() => f.Shell.Debugger.State == DebugState.Idle && !f.Shell.DebugEdits.IsActive, seconds: 60);
    }

    [AvaloniaFact]
    public async Task Agents_profile_a_launch_snapshot_a_heap_and_profile_a_running_program()
    {
        await using var f = ShellFixture.Create();
        var folder = f.CopySample("Diagnostics");
        foreach (var (project, arguments) in new[] { ("CpuBound", "3"), ("Leaky", "0") })
        {
            Directory.CreateDirectory(Path.Combine(folder, project, "Properties"));
            File.WriteAllText(Path.Combine(folder, project, "Properties", "launchSettings.json"), $$"""{ "profiles": { "{{project}}": { "commandName": "Project", "commandLineArgs": "{{arguments}}" } } }""");
        }

        f.Shell.Trust.Trust(Path.GetDirectoryName(folder)!);
        await f.Shell.OpenSolutionAsync(Path.Combine(folder, "Diagnostics.slnx"));
        await ShellFixture.PumpUntil(() => f.Shell.Solutions.Projects.Count == 6 && f.Shell.Solutions.Progress is { Completed: true }, seconds: 300);
        f.Shell.SetStartupProject(Path.Combine(folder, "CpuBound", "CpuBound.csproj"));
        Assert.Equal("No profile is collecting: profile_start first.", (await CallAsync(f, "profile_stop")).Text);

        // Nothing runs: the startup project is launched under the profiler; its end stops the profile, and profile_stop
        // still answers with it.
        var start = await CallAsync(f, "profile_start");
        Assert.False(start.IsError, start.Text);
        Assert.StartsWith("Launched CpuBound (process ", start.Text, StringComparison.Ordinal);
        await ShellFixture.PumpUntil(() => f.Shell.ActiveProfiling is null, seconds: 240);
        var cpu = await CallAsync(f, "profile_stop");
        Assert.False(cpu.IsError, cpu.Text);
        Assert.StartsWith("CPU profile of CpuBound (process ", cpu.Text, StringComparison.Ordinal);
        Assert.Contains("Hot path (inclusive % of the samples", cpu.Text, StringComparison.Ordinal);
        Assert.Contains("Top functions by self time", cpu.Text, StringComparison.Ordinal);
        Assert.Contains("Top functions by total time", cpu.Text, StringComparison.Ordinal);
        Assert.Contains("CpuBound.Primes.IsPrime", cpu.Text, StringComparison.Ordinal);
        Assert.Contains("(CpuBound/Program.cs:", cpu.Text, StringComparison.Ordinal);
        Assert.Contains(f.Shell.Layout.Documents.OfType<SolutionManagementDocument>(), d => d.ViewModel is ProfilingReportViewModel);
        Assert.True((await CallAsync(f, "profile_stop")).IsError);

        // A program that runs: its heap's largest types and what holds them.
        Assert.True((await CallAsync(f, "memory_snapshot")).IsError); // nothing runs
        var run = await CallAsync(f, "run_project", """{"project":"Leaky"}""");
        Assert.False(run.IsError, run.Text);
        await ShellFixture.PumpUntil(() => f.Shell.Runs.Running.Any(p => !p.HasExited && (p.Channel?.GetText().Contains("orders", StringComparison.Ordinal) ?? false)), seconds: 120);
        var heap = await CallAsync(f, "memory_snapshot");
        Assert.False(heap.IsError, heap.Text);
        Assert.StartsWith("Heap of Leaky (process ", heap.Text, StringComparison.Ordinal);
        Assert.Contains("Largest types", heap.Text, StringComparison.Ordinal);
        Assert.Contains(" Order", heap.Text, StringComparison.Ordinal);
        Assert.Contains("What keeps the largest alive", heap.Text, StringComparison.Ordinal);

        // Attached to the program that runs, stopped by the agent.
        var attach = await CallAsync(f, "profile_start");
        Assert.False(attach.IsError, attach.Text);
        Assert.StartsWith("Profiling Leaky (process ", attach.Text, StringComparison.Ordinal);
        Assert.True((await CallAsync(f, "profile_start")).IsError); // one at a time
        await Task.Delay(1500, TestContext.Current.CancellationToken);
        var stopped = await CallAsync(f, "profile_stop");
        Assert.False(stopped.IsError, stopped.Text);
        Assert.StartsWith("CPU profile of ", stopped.Text, StringComparison.Ordinal);

        await CallAsync(f, "stop_run");
        await ShellFixture.PumpUntil(() => f.Shell.Runs.Running.All(p => p.HasExited), seconds: 60);
    }
}
