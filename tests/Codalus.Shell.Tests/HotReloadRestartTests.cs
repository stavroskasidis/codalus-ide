using System.Diagnostics;
using Avalonia.Headless.XUnit;
using Codalus.Shell.Infrastructure;
using Codalus.Workbench.Settings;

namespace Codalus.Shell.Tests;

/// <summary>
/// Stopping a Hot Reload session, then starting another, never blocks the UI thread: on macOS the end of a process tree
/// can wait seconds for the system's process list (the command sweep's run.startWithHotReload stalled for 10 s).
/// </summary>
public sealed class HotReloadRestartTests
{
    /// <summary>How long the stand-in takes to end the first session's process tree.</summary>
    private static readonly TimeSpan s_treeEnding = TimeSpan.FromSeconds(3);

    /// <summary>
    /// The longest the UI thread may stall: half the tree's ending, so ending it on the UI thread (the bug) always fails,
    /// and a busy runner's pause doesn't. A macOS runner stalled 767 ms once while three test jobs shared it, with no tree
    /// ended on the UI thread (develop run 39492777973).
    /// </summary>
    private static readonly TimeSpan s_mostStall = s_treeEnding / 2;

    [AvaloniaFact]
    public async Task Stopping_a_slow_ending_session_and_starting_again_keeps_the_UI_thread_free()
    {
        // The stand-in dotnet watch is a shell script: on Windows, Hot Reload starts dotnet.exe.
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The stand-in dotnet watch is a shell script (Hot Reload runs dotnet.exe on Windows).");
        await using var f = ShellFixture.Create();
        var folder = f.CopySample("BuildAndRun");
        f.Shell.Trust.Trust(Path.GetDirectoryName(folder)!);
        await f.Shell.OpenSolutionAsync(Path.Combine(folder, "BuildAndRun.slnx"));
        await ShellFixture.PumpUntil(() => f.Shell.Solutions.Projects.Count == 2 && f.Shell.Solutions.Progress is { Completed: true }, seconds: 90);
        await f.Shell.Workbench.Settings.SetAsync(ShellHost.BuildBeforeRunSetting, "never", SettingScope.User, CancellationToken.None);

        // A dotnet watch that runs until it is ended, with the app as its child.
        var standIn = Path.Combine(f.UserData, "dotnet-stand-in");
        Directory.CreateDirectory(standIn);
        var dotnet = Path.Combine(standIn, "dotnet");
        File.WriteAllText(dotnet, "#!/bin/sh\necho \"stand-in dotnet $*\"\nsleep 600 &\nwait\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(dotnet, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        f.Shell.Runs.DotnetRoot = standIn;

        // Ending the tree takes 3 s, as when macOS's process list blocks (ReportCrash holding a crashed process).
        using var treeEnding = new ManualResetEventSlim();
        f.Shell.Runs.KillTree = process =>
        {
            treeEnding.Set();
            Thread.Sleep(s_treeEnding);
            process.Kill(entireProcessTree: true);
        };

        await f.Shell.StartWithHotReloadAsync();
        await ShellFixture.PumpUntil(() => f.Shell.HotReloadProgram is not null, seconds: 60);
        var first = f.Shell.HotReloadProgram!;
        await ShellFixture.PumpUntil(() => first.Channel?.GetText().Contains("stand-in dotnet watch run", StringComparison.Ordinal) == true);

        var stall = new SmokeTest.SweepStallMonitor();
        try
        {
            ShellFixture.Pump();
            stall.Reset();

            // Stop, then start again while the first session is still ending.
            var clock = Stopwatch.StartNew();
            f.Shell.StopRunning();
            Assert.True(clock.ElapsedMilliseconds < 500, $"Stopping the session held the UI thread for {clock.ElapsedMilliseconds} ms.");
            Assert.True(treeEnding.Wait(TimeSpan.FromSeconds(10)), "The session's process tree was never ended.");
            Assert.False(first.HasExited);

            await f.Shell.StartWithHotReloadAsync();
            await ShellFixture.PumpUntil(() => f.Shell.Runs.Running.Any(p => p != first && p.HotReload is not null), seconds: 60);
            var second = f.Shell.Runs.Running.First(p => p != first && p.HotReload is not null);

            await ShellFixture.PumpUntil(() => first.HasExited, seconds: 30);
            Assert.False(second.HasExited);
            Assert.Same(second, f.Shell.HotReloadProgram);
            Assert.True(stall.MaxMilliseconds < s_mostStall.TotalMilliseconds, $"The UI thread stalled for {stall.MaxMilliseconds:0} ms (the GC paused {stall.GcPauseOfMaxMilliseconds:0} ms of it).");
        }
        finally
        {
            stall.Stop();
            f.Shell.Runs.KillTree = null;
            await f.Shell.StopRunningAsync();
            await ShellFixture.PumpUntil(() => f.Shell.Runs.Running.Count == 0, seconds: 30);
        }
    }
}
