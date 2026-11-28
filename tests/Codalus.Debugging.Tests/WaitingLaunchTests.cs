using System.Diagnostics;
using Codalus.Workbench.Debugging;

namespace Codalus.Debugging.Tests;

/// <summary>
/// ADR 0115, on every OS: the Codalus debugger's launch of a program that something else starts (a CoreCLR app that the
/// iOS SDK's <c>Run</c> target starts in a simulator), against a console program this test starts with the startup hook
/// and the folder it reports in. The debugger waits for the hook's word, attaches before <c>Main</c> and lets the hook
/// go; stopping the session ends the program; a start that is given up, or never comes, ends the wait with what
/// happened.
/// </summary>
public sealed class WaitingLaunchTests(DebuggeeFixture fixture) : IClassFixture<DebuggeeFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DapJsonContext s_json = DapJsonContext.Default;

    private static NetFxDebugArguments Waiting(string folder, int seconds = 90) =>
        new("Debuggee", Type: "coreclr") { WaitFolder = folder, WaitSeconds = seconds };

    [Fact]
    public async Task A_program_started_elsewhere_with_the_hook_stops_on_Mains_first_line_and_ends_with_the_session()
    {
        var hook = HeldLaunch.ResolveHook() ?? throw new InvalidOperationException("The startup hook isn't next to the tests.");
        var wait = Directory.CreateTempSubdirectory("codalus-waiting-").FullName;
        Process? program = null;
        try
        {
            await using var adapter = await DebugAdapter.StartAsync(DebuggeeFixture.CodalusAdapter(), Ct);
            var events = new EventLog(adapter);
            var launch = adapter.LaunchAsync(Waiting(wait), s_json.NetFxDebugArguments, Ct);

            // The debugger waits before there is a program: the run that starts it builds and deploys first.
            await Task.Delay(500, Ct);
            Assert.False(launch.IsCompleted);
            var start = new ProcessStartInfo(DebuggeeFixture.DotnetHost, ["exec", fixture.Dll]) { WorkingDirectory = fixture.Folder, RedirectStandardOutput = true, RedirectStandardError = true };
            start.Environment["DOTNET_STARTUP_HOOKS"] = hook;
            start.Environment[HeldLaunch.WaitVariable] = wait;
            program = Process.Start(start)!;
            _ = program.StandardOutput.ReadToEndAsync(Ct);
            _ = program.StandardError.ReadToEndAsync(Ct);

            await launch.WaitAsync(TimeSpan.FromSeconds(90), Ct);
            await adapter.Initialized.WaitAsync(TimeSpan.FromSeconds(60), Ct);
            var first = DebuggeeFixture.Line(fixture.Program, "first");
            await adapter.SetBreakpointsAsync(fixture.Program, [new SourceBreakpoint(first)], Ct);
            await adapter.ConfigurationDoneAsync(Ct);
            var stop = await events.StoppedAsync();
            var top = (await adapter.StackTraceAsync(stop.ThreadId!.Value, 1, Ct)).StackFrames[0];
            Assert.Equal(first, top.Line);

            // The hook took the debugger's word and cleaned up after itself.
            Assert.False(Directory.Exists(wait));

            await adapter.DisconnectAsync(terminateDebuggee: true, Ct);
            await program.WaitForExitAsync(Ct).WaitAsync(TimeSpan.FromSeconds(30), Ct);
        }
        finally
        {
            if (program is { HasExited: false })
            {
                Codalus.Platform.ProcessTree.Kill(program);
            }

            program?.Dispose();
            if (Directory.Exists(wait))
            {
                Directory.Delete(wait, recursive: true);
            }
        }
    }

    [Fact]
    public async Task The_hook_makes_its_folder_when_told_to_and_reports_there()
    {
        // ADR 0116: an Android app's folder is in its own storage, which the IDE can't reach before the app is installed.
        var hook = HeldLaunch.ResolveHook() ?? throw new InvalidOperationException("The startup hook isn't next to the tests.");
        var parent = Directory.CreateTempSubdirectory("codalus-waiting-").FullName;
        var wait = Path.Combine(parent, "made-by-the-hook");
        Process? program = null;
        try
        {
            var start = new ProcessStartInfo(DebuggeeFixture.DotnetHost, ["exec", fixture.Dll]) { WorkingDirectory = fixture.Folder, RedirectStandardOutput = true, RedirectStandardError = true };
            start.Environment["DOTNET_STARTUP_HOOKS"] = hook;
            start.Environment[HeldLaunch.WaitVariable] = wait;
            start.Environment[HeldLaunch.CreateVariable] = "1";
            program = Process.Start(start)!;
            _ = program.StandardOutput.ReadToEndAsync(Ct);
            _ = program.StandardError.ReadToEndAsync(Ct);

            var pid = Path.Combine(wait, "pid");
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
            while (!File.Exists(pid))
            {
                Assert.True(DateTime.UtcNow < deadline, "The program didn't report in the folder the hook was to make.");
                Assert.False(program.HasExited, "The program ended without waiting.");
                await Task.Delay(50, Ct);
            }

            Assert.Equal(program.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), (await File.ReadAllTextAsync(pid, Ct)).Trim());

            // Let go, the hook removes the folder it made.
            await File.WriteAllTextAsync(Path.Combine(wait, "go"), string.Empty, Ct);
            deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            while (Directory.Exists(wait))
            {
                Assert.True(DateTime.UtcNow < deadline, "The hook didn't take the word.");
                await Task.Delay(50, Ct);
            }
        }
        finally
        {
            if (program is { HasExited: false })
            {
                Codalus.Platform.ProcessTree.Kill(program);
            }

            program?.Dispose();
            Directory.Delete(parent, recursive: true);
        }
    }

    [Fact]
    public async Task A_start_that_is_given_up_ends_the_wait()
    {
        var wait = Directory.CreateTempSubdirectory("codalus-waiting-").FullName;
        await using var adapter = await DebugAdapter.StartAsync(DebuggeeFixture.CodalusAdapter(), Ct);
        var launch = adapter.LaunchAsync(Waiting(wait), s_json.NetFxDebugArguments, Ct);
        await Task.Delay(300, Ct);
        Directory.Delete(wait, recursive: true);

        var error = await Assert.ThrowsAsync<DapException>(() => launch.WaitAsync(TimeSpan.FromSeconds(30), Ct));
        Assert.Contains("wasn't started", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_program_that_never_reports_ends_the_wait_with_why()
    {
        var wait = Directory.CreateTempSubdirectory("codalus-waiting-").FullName;
        try
        {
            await using var adapter = await DebugAdapter.StartAsync(DebuggeeFixture.CodalusAdapter(), Ct);
            var error = await Assert.ThrowsAsync<DapException>(() => adapter.LaunchAsync(Waiting(wait, seconds: 1), s_json.NetFxDebugArguments, Ct).WaitAsync(TimeSpan.FromSeconds(30), Ct));
            Assert.Contains("startup hook in 1 seconds", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(wait, recursive: true);
        }
    }
}
