using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Avalonia.Headless.XUnit;
using DaedalusStudio.Debugging;
using DaedalusStudio.LanguageServices.Protocol;
using DaedalusStudio.Shell.ViewModels;
using DaedalusStudio.Workbench.Debugging;
using DaedalusStudio.Workbench.Debugging.Remote;
using DaedalusStudio.Workbench.FileSystem;
using DaedalusStudio.Workbench.Remote;
using DaedalusStudio.Workbench.StatusBar;

namespace DaedalusStudio.Shell.Tests;

/// <summary>
/// A remote window (M21, ADR 0050) on the local stand-in: <c>daedalus-server</c> started here, so nothing reaches
/// another machine. On Linux and macOS its paths go through the virtual root (<c>/daedalus-remote/local</c>) as a real
/// remote machine's do; on Windows the stand-in is a Windows server, so paths stay as they are and the WSL tests of the
/// workbench cover the mapping. Everything that reads the workspace or runs its code goes through the server: the build
/// and language hosts, the editor's file system, the terminal, the program and the debugger.
/// </summary>
public sealed class RemoteShellTests
{
    private static string ServerDll => Path.Combine(ShellFixture.RepoRoot, "src", "DaedalusStudio.Remote.Server", "bin",
        typeof(RemoteShellTests).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
            .Cast<System.Reflection.AssemblyMetadataAttribute>().Single(a => a.Key == "DaedalusConfiguration").Value!, "net10.0", "daedalus-server.dll");

    /// <summary>
    /// A folder as the server's programs see it: macOS's temp folder is under the <c>/var</c> link, and the debugger
    /// and the program's working directory name <c>/private/var</c>.
    /// </summary>
    private static string Resolved(string folder) =>
        OperatingSystem.IsMacOS() && (folder.StartsWith("/var/", StringComparison.Ordinal) || folder.StartsWith("/tmp/", StringComparison.Ordinal)) ? "/private" + folder : folder;

    private static async Task<RemoteSession> ConnectAsync(ShellFixture f)
    {
        var options = new RemoteSessionOptions(RemoteTargetSpec.Parse("local")!, ShellInfo.Version, f.Shell.Paths.Cache)
        {
            LocalServer = ServerDll,
            Latency = TimeSpan.Zero, // adjustable for the budget
            MapLocalPaths = true,
        };
        Assert.True(await f.Shell.ConnectRemoteAsync(options.Spec, null, options));
        return f.Shell.Remote!;
    }

    private static string Screen(Workbench.Terminal.TerminalSession session) =>
        string.Join('\n', Enumerable.Range(0, session.Terminal.TotalRows).Select(i => session.Terminal.GetRow(i).GetText()));

    [AvaloniaFact]
    public async Task A_remote_window_opens_edits_completes_builds_runs_and_forwards_ports_through_the_server()
    {
        await using var f = ShellFixture.Create();
        var folder = Resolved(f.CopySample("BuildAndRun"));
        var remote = await ConnectAsync(f);
        var local = remote.Map.ToLocal(folder); // the window's name for it
        Assert.Equal(OperatingSystem.IsWindows(), remote.Map.IsIdentity);
        f.Shell.Trust.Trust(Path.GetDirectoryName(local)!);

        // The status bar names the connection.
        await ShellFixture.PumpUntil(() => f.Shell.Workbench.StatusBar.GetItems(StatusBarAlignment.Left).Any(i => i.Id == ShellHost.RemoteStatusItem && i.Text.Contains("Local server", StringComparison.Ordinal)));
        Assert.Equal(true, f.Shell.Workbench.Context.GetValue("remoteConnected"));

        // Open the folder: its solution loads in the build host on the server.
        await f.Shell.OpenRemoteAsync(local);
        await ShellFixture.PumpUntil(() => f.Shell.Solutions.Projects.Count == 2 && f.Shell.Solutions.Progress is { Completed: true }, seconds: 240);
        Assert.All(f.Shell.Solutions.Projects.Keys, p => Assert.True(FileSystems.IsRemote(p), p + " is a remote path"));
        Assert.NotNull(f.Shell.Solutions.HostProcessId);

        // The editor reads the file through the server; the C# host there answers completion.
        var program = Path.Combine(local, "src", "Hello", "Program.cs");
        var editor = await f.Shell.OpenFileAsync(program);
        Assert.NotNull(editor);
        Assert.Contains("Greeter.Greet", editor.Model.Text.Text, StringComparison.Ordinal);
        await ShellFixture.PumpUntil(() => f.Shell.Languages.Status(ShellHost.CSharpLanguageId) is { State: "ready", Projects: 2 }, seconds: 240);
        var client = f.Shell.Languages.RunningClient(ShellHost.CSharpLanguageId)!;
        var text = editor.Model.Text.Text;
        var position = editor.Model.PositionAt(text.IndexOf("Console.WriteLine", StringComparison.Ordinal) + "Console.".Length);
        var completion = await client.CompletionAsync(editor.Model.Uri, position, null, ShellFixture.RequestTimeout(60));
        Assert.Contains(completion.Items, i => i.Label == "WriteLine");

        // Budget (M21): completion ≤ local + RTT + 20 ms at a simulated 50 and 150 ms (asserted off CI).
        async Task<double> CompletionMilliseconds()
        {
            var times = new List<double>();
            for (var i = 0; i < 5; i++)
            {
                var watch = Stopwatch.StartNew();
                await client.CompletionAsync(editor.Model.Uri, position, null, ShellFixture.RequestTimeout(60));
                times.Add(watch.Elapsed.TotalMilliseconds);
            }

            return times.Order().ElementAt(times.Count / 2);
        }

        var baseline = await CompletionMilliseconds();
        var report = new StringBuilder(string.Create(CultureInfo.InvariantCulture, $"completion through the server: {baseline:F0} ms"));
        foreach (var rtt in (int[])[50, 150])
        {
            remote.Connection.SimulatedLatency = TimeSpan.FromMilliseconds(rtt);
            var slower = await CompletionMilliseconds();
            report.Append(CultureInfo.InvariantCulture, $"; {slower:F0} ms at {rtt} ms RTT");
            if (Environment.GetEnvironmentVariable("CI") != "true")
            {
                Assert.True(slower <= baseline + rtt + 20, $"Completion took {slower:F0} ms at {rtt} ms RTT: over the budget of {baseline + rtt + 20:F0} ms ({report}).");
            }
        }

        remote.Connection.SimulatedLatency = TimeSpan.Zero;
        TestContext.Current.SendDiagnosticMessage(report.ToString());

        // An edit saved through the server reaches the file there.
        editor.Model.Text.Insert(0, "// edited remotely\n");
        await f.Shell.SaveAllAsync();
        Assert.StartsWith("// edited remotely", await File.ReadAllTextAsync(Path.Combine(folder, "src", "Hello", "Program.cs"), TestContext.Current.CancellationToken), StringComparison.Ordinal);

        // Build in the build host there.
        var before = f.Shell.Builds.LastBuild;
        f.Shell.Run("build.buildSolution");
        await ShellFixture.PumpUntil(() => f.Shell.Builds.LastBuild is { } b && b != before, seconds: 300);
        Assert.True(f.Shell.Builds.LastBuild!.Result.Succeeded, f.Shell.Builds.Channel.GetText());

        // Run Without Debugging: the program runs in a terminal there, with its launch profile.
        var sessions = f.Shell.Terminals.Sessions.Count;
        f.Shell.SetStartupProject(Path.Combine(local, "src", "Hello", "Hello.csproj"));
        await f.Shell.StartWithoutDebuggingAsync();
        await ShellFixture.PumpUntil(() => f.Shell.Terminals.Sessions.Count > sessions && f.Shell.Terminals.Sessions[^1].ExitCode is not null, seconds: 120);
        var run = f.Shell.Terminals.Sessions[^1];
        Assert.Equal(0, run.ExitCode);
        Assert.Contains("Hello from Hello", Screen(run), StringComparison.Ordinal);
        Assert.Contains("args: one two words", Screen(run), StringComparison.Ordinal);

        // A terminal is the user's shell there.
        var shell = f.Shell.ShowTerminal(newTerminal: true);
        Assert.NotNull(shell);
        Assert.Equal("Remote shell", shell.Name);
        await shell.WhenStartedAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        shell.Write(OperatingSystem.IsWindows() ? "echo remote-%COMPUTERNAME%-ok\r" : "echo remote-$((6*7))-ok\r");
        await ShellFixture.PumpUntil(() => Screen(shell).Contains(OperatingSystem.IsWindows() ? "remote-" + Environment.MachineName + "-ok" : "remote-42-ok", StringComparison.OrdinalIgnoreCase), seconds: 60);

        // A port that starts listening there is forwarded and listed in the Ports window.
        var opened = new List<Uri>();
        f.Shell.RemoteBrowser = uri => opened.Add(uri);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        await ShellFixture.PumpUntil(() => remote.Ports.Forwarded.Any(p => p.RemotePort == port), seconds: 30);
        f.Shell.ShowPorts();
        await ShellFixture.PumpUntil(() => f.Shell.PortsWindow.Ports.Any(p => p is { IsForwarded: true } && p.RemotePort == port));
        f.Shell.PortsWindow.Selected = f.Shell.PortsWindow.Ports.First(p => p.RemotePort == port);
        f.Shell.PortsWindow.OpenInBrowser();
        Assert.Single(opened);
        Assert.Equal(remote.Ports.Forwarded.First(p => p.RemotePort == port).LocalPort, opened[0].Port);

        // Closing the connection makes the window local again.
        await f.Shell.DisconnectRemoteAsync();
        Assert.Null(f.Shell.Remote);
        Assert.False(f.Shell.Solutions.IsOpen);
        Assert.DoesNotContain(f.Shell.Workbench.StatusBar.GetItems(StatusBarAlignment.Left), i => i.Id == ShellHost.RemoteStatusItem);
        Assert.False(FileSystems.IsRemote(program));
    }

    [AvaloniaFact]
    public async Task Debugging_in_a_remote_window_runs_netcoredbg_through_the_server_and_stops_at_a_breakpoint()
    {
        Assert.SkipWhen(DebugAdapterLocator.Resolve() is null && Environment.GetEnvironmentVariable("CI") != "true", "netcoredbg is not installed (eng/get-netcoredbg.sh).");
        await using var f = ShellFixture.Create();
        var folder = Resolved(f.CopySample("BuildAndRun"));
        var remote = await ConnectAsync(f);
        var local = remote.Map.ToLocal(folder);
        f.Shell.Trust.Trust(Path.GetDirectoryName(local)!);
        await f.Shell.OpenRemoteAsync(local);
        await ShellFixture.PumpUntil(() => f.Shell.Solutions.Projects.Count == 2 && f.Shell.Solutions.Progress is { Completed: true }, seconds: 240);
        f.Shell.SetStartupProject(Path.Combine(local, "src", "Hello", "Hello.csproj"));

        var program = Path.Combine(local, "src", "Hello", "Program.cs");
        var line = Array.FindIndex(File.ReadAllLines(Path.Combine(folder, "src", "Hello", "Program.cs")), l => l.Contains("Console.WriteLine($\"args", StringComparison.Ordinal)) + 1;
        f.Shell.Debugger.Breakpoints.Toggle(program, line);
        f.Shell.Run("debug.start");
        await ShellFixture.PumpUntil(() => f.Shell.Debugger.State == DebugState.Stopped, seconds: 300);
        try
        {
            await ShellFixture.PumpUntil(() => f.Shell.ActiveEditor is { Controller: { } c } c2 && c2.Model.Path == program && c.CurrentDebugLine() == line, seconds: 30);
        }
        catch (Xunit.Sdk.TrueException)
        {
            Assert.Fail($"Expected {program}:{line}, the active editor is {f.Shell.ActiveEditor?.Model.Path}:{f.Shell.ActiveEditor?.Controller?.CurrentDebugLine()}.\n{f.Shell.DebugLog.GetText()}");
        }
        Assert.Contains(remote.Display, f.Shell.DebugLog.GetText(), StringComparison.Ordinal);
        await ShellFixture.PumpUntil(() => f.Shell.Locals.Roots.Any(r => r.Name == "args"), seconds: 30);

        f.Shell.Debugger.Breakpoints.Clear();
        f.Shell.Run("debug.continue");
        await ShellFixture.PumpUntil(() => f.Shell.Debugger.State == DebugState.Idle, seconds: 120);
        Assert.Contains("exited with code", f.Shell.DebugLog.GetText(), StringComparison.Ordinal);
    }

    /// <summary>
    /// A WSL distribution as the remote machine (Windows, <c>DAEDALUS_TEST_WSL</c>, default <c>Ubuntu</c>, with .NET):
    /// the bundle is copied into it (a test folder, not the user's <c>~/.daedalus-server</c>), the hosts run there on
    /// its .NET, and its files are <c>\\wsl.localhost\…</c> paths here. With netcoredbg for Linux in the WSL mirror of
    /// this checkout (<c>eng/wsl-test.sh</c>), F5 stops at a breakpoint there too.
    /// </summary>
    [AvaloniaFact]
    public async Task A_remote_window_on_WSL_maps_paths_builds_runs_and_debugs_there()
    {
        var distribution = Environment.GetEnvironmentVariable("DAEDALUS_TEST_WSL") is { Length: > 0 } name ? name : "Ubuntu";
        Assert.SkipUnless(OperatingSystem.IsWindows(), "WSL is a Windows stand-in (Linux and macOS run the fake ssh in the workbench tests).");
        var listed = await RemoteTargets.WslDistributionsAsync(TestContext.Current.CancellationToken);
        var probe = listed.Contains(distribution, StringComparer.OrdinalIgnoreCase)
            ? await RemoteShell.RunAsync(new WslTarget(distribution), "PATH=\"$PATH:$HOME/.dotnet\" command -v dotnet; echo \"home $HOME\"", TestContext.Current.CancellationToken)
            : null;
        Assert.SkipWhen(probe is null || !probe.Output.Contains("/dotnet", StringComparison.Ordinal), $"No WSL distribution '{distribution}' with .NET (DAEDALUS_TEST_WSL).");

        await using var f = ShellFixture.Create();
        var source = f.CopySample("BuildAndRun");
        var id = Guid.NewGuid().ToString("N")[..8];
        var work = "/tmp/daedalus-remote-test-" + id;
        static string Mnt(string path) => "/mnt/" + char.ToLowerInvariant(path[0]) + path[2..].Replace('\\', '/');
        var copy = await RemoteShell.RunAsync(new WslTarget(distribution), $"mkdir -p {work} && cp -r \"{Mnt(Path.GetDirectoryName(source)!)}/.\" {work}/ && echo copied", TestContext.Current.CancellationToken);
        Assert.Contains("copied", copy.Output, StringComparison.Ordinal);
        var options = new RemoteSessionOptions(RemoteTargetSpec.Parse("wsl:" + distribution)!, ShellInfo.Version, f.Shell.Paths.Cache)
        {
            Hosts = ShellHost.RemoteHostParts(),
            ServerHome = "/tmp/daedalus-server-tests-" + id,
        };
        Environment.SetEnvironmentVariable(RemoteServerBundle.ServerVariable, ServerDll);
        try
        {
            Assert.True(await f.Shell.ConnectRemoteAsync(options.Spec, null, options), f.Shell.Workbench.Output.Channels.FirstOrDefault(c => c.Name == "Remote")?.GetText());
            var remote = f.Shell.Remote!;
            Assert.Equal(@"\\wsl.localhost\" + distribution, remote.Map.LocalRoot, ignoreCase: true);
            Assert.Equal("linux", remote.Connection.Server.Os);
            var local = remote.Map.ToLocal(work + "/BuildAndRun");
            f.Shell.Trust.Trust(remote.Map.ToLocal(work));
            await f.Shell.OpenRemoteAsync(local);
            await ShellFixture.PumpUntil(() => f.Shell.Solutions.Projects.Count == 2 && f.Shell.Solutions.Progress is { Completed: true }, seconds: 300);
            Assert.All(f.Shell.Solutions.Projects.Keys, p => Assert.StartsWith(remote.Map.LocalRoot!, p, StringComparison.OrdinalIgnoreCase));

            var program = Path.Combine(local, "src", "Hello", "Program.cs");
            var editor = await f.Shell.OpenFileAsync(program);
            Assert.NotNull(editor);
            await ShellFixture.PumpUntil(() => f.Shell.Languages.Status(ShellHost.CSharpLanguageId) is { State: "ready", Projects: 2 }, seconds: 300);
            var client = f.Shell.Languages.RunningClient(ShellHost.CSharpLanguageId)!;
            var text = editor.Model.Text.Text;
            var completion = await client.CompletionAsync(editor.Model.Uri, editor.Model.PositionAt(text.IndexOf("Console.WriteLine", StringComparison.Ordinal) + "Console.".Length), null, ShellFixture.RequestTimeout(60));
            Assert.Contains(completion.Items, i => i.Label == "WriteLine");

            var before = f.Shell.Builds.LastBuild;
            f.Shell.Run("build.buildSolution");
            await ShellFixture.PumpUntil(() => f.Shell.Builds.LastBuild is { } b && b != before, seconds: 300);
            Assert.True(f.Shell.Builds.LastBuild!.Result.Succeeded, f.Shell.Builds.Channel.GetText());

            // The program runs in the distribution: its cwd is a Linux path.
            var sessions = f.Shell.Terminals.Sessions.Count;
            f.Shell.SetStartupProject(Path.Combine(local, "src", "Hello", "Hello.csproj"));
            await f.Shell.StartWithoutDebuggingAsync();
            await ShellFixture.PumpUntil(() => f.Shell.Terminals.Sessions.Count > sessions && f.Shell.Terminals.Sessions[^1].ExitCode is not null, seconds: 120);
            Assert.Contains("cwd: " + work + "/BuildAndRun", Screen(f.Shell.Terminals.Sessions[^1]), StringComparison.Ordinal);

            // F5 with netcoredbg copied into the distribution (from the WSL mirror's .work/netcoredbg, with consent).
            var mirror = @"\\wsl.localhost\" + distribution + probe!.Output.Split('\n').First(l => l.StartsWith("home ", StringComparison.Ordinal))[5..].Trim().Replace('/', '\\') + @"\daedalus-studio\.work\netcoredbg";
            if (File.Exists(Path.Combine(mirror, "netcoredbg")))
            {
                Environment.SetEnvironmentVariable("DAEDALUS_NETCOREDBG_LINUX_X64", mirror);
                var line = Array.FindIndex(text.Split('\n'), l => l.Contains("Console.WriteLine($\"args", StringComparison.Ordinal)) + 1;
                f.Shell.Debugger.Breakpoints.Toggle(program, line);
                f.Shell.Run("debug.start");
                var answered = false;
                await ShellFixture.PumpUntil(() =>
                {
                    if (!answered && f.Shell.Dialogs.IsOpen && f.Shell.Dialogs.Title == "Copy the Debugger")
                    {
                        f.Shell.Dialogs.Accept();
                        answered = true;
                    }

                    return f.Shell.Debugger.State == DebugState.Stopped;
                }, seconds: 300);
                await ShellFixture.PumpUntil(() => f.Shell.ActiveEditor is { Controller: { } c } e && string.Equals(e.Model.Path, program, StringComparison.OrdinalIgnoreCase) && c.CurrentDebugLine() == line, seconds: 30);
                f.Shell.Debugger.Breakpoints.Clear();
                f.Shell.Run("debug.continue");
                await ShellFixture.PumpUntil(() => f.Shell.Debugger.State == DebugState.Idle, seconds: 120);
            }
            else
            {
                TestContext.Current.SendDiagnosticMessage("No netcoredbg in the WSL mirror (" + mirror + "): debugging in WSL wasn't tried.");
            }

            await f.Shell.DisconnectRemoteAsync();
        }
        finally
        {
            Environment.SetEnvironmentVariable(RemoteServerBundle.ServerVariable, null);
            Environment.SetEnvironmentVariable("DAEDALUS_NETCOREDBG_LINUX_X64", null);
            await RemoteShell.RunAsync(new WslTarget(distribution), $"rm -rf {work} /tmp/daedalus-server-tests-{id}", CancellationToken.None);
        }
    }

    [AvaloniaFact]
    public async Task Open_Remote_starts_a_remote_window_with_the_target_and_path()
    {
        await using var f = ShellFixture.Create();
        f.Shell.Trust.Trust(Path.GetTempPath());
        var file = Path.Combine(f.UserData, "note.txt");
        await File.WriteAllTextAsync(file, "x", TestContext.Current.CancellationToken);
        await f.Shell.OpenFileAsync(file); // something is open: Open Remote starts another window
        IReadOnlyList<string>? started = null;
        f.Shell.StartWindow = arguments => started = arguments;
        var open = f.Shell.OpenRemoteWindowAsync("ssh:pi@raspberry:2222");
        await ShellFixture.PumpUntil(() => f.Shell.Dialogs.IsOpen);
        f.Shell.Dialogs.InputText = "/home/pi/App";
        f.Shell.Dialogs.Accept();
        await open;
        Assert.NotNull(started);
        Assert.Equal(["--remote", "ssh:pi@raspberry:2222", "/home/pi/App", "--user-data-dir", f.UserData], started);
        Assert.Contains(f.Shell.Workbench.Menus.GetItems(BuiltIns.FileOpenMenu), i => i.Command == "remote.open"); // File ▸ Open ▸ Remote Window…
        var parsed = ShellOptions.Parse(["--remote", "wsl:Ubuntu", "/home/me"]);
        Assert.Equal("wsl:Ubuntu", parsed.Remote);
        Assert.Equal("/home/me", parsed.Path);
    }
}
