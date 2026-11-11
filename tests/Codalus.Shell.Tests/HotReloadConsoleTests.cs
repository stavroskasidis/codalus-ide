using Avalonia.Headless.XUnit;
using Codalus.Workbench.Build;
using Codalus.Workbench.Settings;
using Codalus.Workbench.Terminal;

namespace Codalus.Shell.Tests;

/// <summary>
/// Start Without Debugging with Hot Reload honors <c>run.console</c>, with the real <c>dotnet watch</c> on samples/BuildAndRun:
/// <c>dotnet watch</c> runs in the integrated terminal or the console window, so the app gets that console (its output,
/// standard error included, goes there, not to the Output window), while the startup hook relays <c>dotnet watch</c>'s
/// own messages to the IDE, which follows the session (the status bar) as it does from the Output window.
/// </summary>
public sealed class HotReloadConsoleTests
{
    private static async Task<ShellFixture> OpenAsync(string console)
    {
        var f = ShellFixture.Create();
        var folder = f.CopySample("BuildAndRun");
        f.Shell.Trust.Trust(Path.GetDirectoryName(folder)!);
        await f.Shell.OpenSolutionAsync(Path.Combine(folder, "BuildAndRun.slnx"));
        await ShellFixture.PumpUntil(() => f.Shell.Solutions.Projects.Count == 2 && f.Shell.Solutions.Progress is { Completed: true }, seconds: 120);
        await f.Shell.Workbench.Settings.SetAsync(ShellHost.RunConsoleSetting, console, SettingScope.User, CancellationToken.None);
        await f.Shell.Workbench.Settings.SetAsync(ShellHost.HotReloadSetting, true, SettingScope.User, CancellationToken.None);
        return f;
    }

    private static string Text(TerminalSession session) =>
        string.Join("\n", Enumerable.Range(0, session.Terminal.TotalRows).Select(i => session.Terminal.GetRow(i).GetText()));

    /// <summary>The Hot Reload run, once <c>dotnet watch</c>'s relayed messages say the app ran and exited (Hello ends at once).</summary>
    private static async Task<RunningProgram> RanAsync(ShellFixture f)
    {
        await ShellFixture.PumpUntil(() => f.Shell.HotReloadProgram is not null, seconds: 120);
        var program = f.Shell.HotReloadProgram!;
        var output = f.Shell.Workbench.Output.GetOrCreate("codalus.run.Hello", "Run: Hello");
        await ShellFixture.PumpUntil(() => program.HotReload!.State == HotReloadState.Exited, seconds: 240,
            describe: () => $"The session is {program.HotReload!.State}. Run: Hello says:\n{output.GetText()}");
        Assert.Null(program.Channel); // the Output window isn't the program's
        Assert.Contains(output.GetText().Split('\n'), l => HotReloadOutput.IsWatchLine(l)); // dotnet watch's messages, relayed
        Assert.DoesNotContain("to stderr", output.GetText(), StringComparison.Ordinal); // the app's standard error stays in its console
        Assert.Contains(f.Shell.StatusBar.Left, i => i.Item.Id == "codalus.hotReload");
        return program;
    }

    [AvaloniaFact]
    public async Task Hot_reload_in_the_integrated_terminal_gives_the_app_the_terminal_and_follows_dotnet_watch()
    {
        await using var f = await OpenAsync("integratedTerminal");
        var terminals = f.Shell.Terminals.Sessions.Count;
        await f.Shell.StartWithoutDebuggingAsync();
        try
        {
            var program = await RanAsync(f);
            Assert.Equal(terminals + 1, f.Shell.Terminals.Sessions.Count);
            var session = f.Shell.Terminals.Sessions[^1];
            await ShellFixture.PumpUntil(() => Text(session).Contains("to stderr", StringComparison.Ordinal), describe: () => Text(session));
            Assert.Contains("Hello", Text(session), StringComparison.Ordinal);
            Assert.Contains("dotnet watch", Text(session), StringComparison.Ordinal); // its messages show there too

            f.Shell.StopRunning();
            await ShellFixture.PumpUntil(() => program.HasExited && session.ExitCode is not null, seconds: 60);
            Assert.Null(f.Shell.HotReloadProgram);
        }
        finally
        {
            await f.Shell.StopRunningAsync();
        }
    }

    [AvaloniaFact]
    public async Task Hot_reload_in_a_console_window_gives_the_app_the_window_and_follows_dotnet_watch()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Console windows are for Windows.");
        Assert.SkipUnless(Environment.GetEnvironmentVariable("CI") == "true" || Environment.GetEnvironmentVariable("CODALUS_TEST_CONSOLE_WINDOWS") == "1",
            "It opens a console window on the desktop: set CODALUS_TEST_CONSOLE_WINDOWS=1 (CI runs it).");
        await using var f = await OpenAsync("externalConsole");
        await f.Shell.StartWithoutDebuggingAsync();
        try
        {
            var program = await RanAsync(f);
            f.Shell.StopRunning(); // ends the window: cmd, dotnet watch and the app
            await ShellFixture.PumpUntil(() => program.HasExited, seconds: 60);
            Assert.Null(f.Shell.HotReloadProgram);
        }
        finally
        {
            await f.Shell.StopRunningAsync();
        }
    }
}
