using System.Diagnostics;
using System.Text;
using System.Threading.Channels;
using Codalus.Workbench.Build;
using Codalus.Workbench.Debugging;
using Codalus.Workbench.Debugging.Remote;

namespace Codalus.Debugging.Tests;

/// <summary>
/// Remote debugging (M14c, ADR 0039) on the real netcoredbg, through pipe transports to local stand-ins
/// (<see cref="RemoteFixture"/>): attach in a WSL distribution and launch there (the WSL launch profile), attach over
/// SSH, and attach in a container, with netcoredbg copied to each target on first use.
/// </summary>
public sealed class RemoteTests(DebuggeeFixture debuggee) : IClassFixture<DebuggeeFixture>, IAsyncLifetime
{
    private readonly Channel<StopInfo> _stops = Channel.CreateUnbounded<StopInfo>();
    private readonly TaskCompletionSource<int?> _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly StringBuilder _output = new();
    private readonly string _cache = Path.Combine(Path.GetTempPath(), "codalus-remote-cache");
    private DebugService _debug = null!;

    /// <summary>netcoredbg on the stand-ins goes to /tmp, not the user's home.</summary>
    private const string TestHome = "/tmp/codalus-tests/netcoredbg";

    public ValueTask InitializeAsync()
    {
        Assert.SkipWhen(DebuggeeFixture.Adapter is null, "netcoredbg is not installed (eng/get-netcoredbg.sh).");
        _debug = new DebugService(() => DebuggeeFixture.Adapter);
        _debug.Stopped += (_, stop) => _stops.Writer.TryWrite(stop);
        _debug.Ended += (_, code) => _ended.TrySetResult(code);
        _debug.Output += (_, o) =>
        {
            lock (_output)
            {
                _output.Append(o.Text);
            }
        };
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_debug is not null)
        {
            await _debug.DisposeAsync();
        }
    }

    private string Output
    {
        get
        {
            lock (_output)
            {
                return _output.ToString();
            }
        }
    }

    private async Task<StopInfo> NextStopAsync(int seconds = 120)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        try
        {
            return await _stops.Reader.ReadAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            var breakpoints = string.Join("; ", _debug.Breakpoints.All.Select(b => $"{b.Path}:{b.Line} {_debug.Breakpoints.StateOf(b)}"));
            throw new TimeoutException($"The program did not stop. State: {_debug.State}. Breakpoints: {breakpoints}. Output:\n{Output}");
        }
    }

    private async Task<string> EnsureDebuggerAsync(RemoteTarget target)
    {
        var asked = false;
        var path = await new RemoteDebuggers(_cache).EnsureAsync(target, _ => { asked = true; return Task.FromResult(true); }, null, TestContext.Current.CancellationToken);
        Assert.EndsWith("/netcoredbg", path, StringComparison.Ordinal);
        _ = asked; // asked on the first run on a target only
        return path;
    }

    /// <summary>Attaches to the Debuggee waiting for <paramref name="signal"/> on <paramref name="target"/>, stops at [done], and detaches.</summary>
    private async Task AttachStopAndDetachAsync(RemoteTarget target, Func<Task> release, Func<int, bool> isDebuggee, SourceMap map)
    {
        var debugger = await EnsureDebuggerAsync(target);
        RemoteProcess? process = null;
        for (var i = 0; i < 100 && process is null; i++)
        {
            process = (await RemoteShell.ProcessesAsync(target, TestContext.Current.CancellationToken)).FirstOrDefault(p => p.IsDotnet && p.CommandLine.Contains("Debuggee.dll", StringComparison.Ordinal) && isDebuggee(p.Id));
            if (process is null)
            {
                await Task.Delay(200, TestContext.Current.CancellationToken);
            }
        }

        Assert.NotNull(process);
        _debug.Breakpoints.Set(new BreakpointSpec(debuggee.Program, DebuggeeFixture.Line(debuggee.Program, "done")));
        await _debug.AttachAsync(process.Id, TestContext.Current.CancellationToken, new RemoteAttach(RemoteShell.Adapter(target, debugger), map, target.Display), "Debuggee");
        Assert.Equal(target.Display, _debug.Processes.Single().Target);
        Assert.True(_debug.CanDetach);
        await release();

        var stop = await NextStopAsync();
        Assert.Equal("breakpoint", stop.Reason);
        Assert.Equal(debuggee.Program, _debug.SelectedFrame?.Source?.Path, ignoreCase: OperatingSystem.IsWindows());
        Assert.Equal("15", (await _debug.EvaluateAsync("total", "watch", TestContext.Current.CancellationToken)).Result);
        await _debug.DetachAsync();
        Assert.Equal(DebugState.Idle, _debug.State);
    }

    [Fact]
    public async Task Attach_to_a_program_in_WSL()
    {
        Assert.SkipWhen(RemoteFixture.WslDistribution is null, "No WSL distribution with .NET (Windows; CODALUS_TEST_WSL).");
        var distribution = RemoteFixture.WslDistribution!;
        var target = new WslTarget(distribution) { Home = TestHome };
        var signal = "/tmp/codalus-wsl-" + Guid.NewGuid().ToString("N")[..8];
        var dotnet = RemoteFixture.WslDotnet(distribution);
        using var program = Process.Start(new ProcessStartInfo("wsl.exe", ["-d", distribution, "--exec", dotnet, "exec", WslLaunch.ToLinuxPath(debuggee.Dll), "--wait-for", signal])
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        })!;
        try
        {
            await AttachStopAndDetachAsync(
                target,
                () => RemoteShell.RunAsync(target, $"touch {signal}", TestContext.Current.CancellationToken),
                _ => true,
                new SourceMap());
            await program.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
            Assert.Equal(15, program.ExitCode); // detached: it ran on to its end in WSL
        }
        finally
        {
            if (!program.HasExited)
            {
                program.Kill(entireProcessTree: true);
            }

            await RemoteShell.RunAsync(target, $"rm -f {signal}", CancellationToken.None);
        }
    }

    [Fact]
    public async Task Launch_in_WSL_through_the_WSL_profile()
    {
        Assert.SkipWhen(RemoteFixture.WslDistribution is null, "No WSL distribution with .NET (Windows; CODALUS_TEST_WSL).");
        var distribution = RemoteFixture.WslDistribution!;
        var info = new Codalus.Projects.Protocol.RunInfo(Path.Combine(debuggee.Folder, "Debuggee.csproj"), "net10.0", "Exe", debuggee.Dll, null, null, null, null);
        var profile = new LaunchProfile("WSL", LaunchProfile.Wsl, CommandLineArgs: "--depth", DistributionName: distribution, EnvironmentVariables: new Dictionary<string, string> { ["GREETING"] = "from WSL" });
        var plan = LaunchPlanner.Plan(info, profile);
        Assert.Equal("wsl.exe", plan.FileName);
        Assert.NotNull(plan.Wsl);

        var target = new WslTarget(distribution) { Home = TestHome };
        var debugger = await EnsureDebuggerAsync(target);
        var wsl = plan.Wsl!;
        var arguments = new LaunchArguments(RemoteFixture.WslDotnet(distribution), [.. wsl.Arguments], wsl.WorkingDirectory, new Dictionary<string, string>(plan.Environment));
        var depth = Path.Combine(debuggee.Folder, "Depth.cs");
        _debug.Breakpoints.Set(new BreakpointSpec(depth, DebuggeeFixture.Line(depth, "depth-end")));
        await _debug.LaunchAsync([new DebugLaunch("Debuggee", plan) { Remote = new RemoteLaunch(RemoteShell.Adapter(target, debugger), arguments, new SourceMap(), target.Display) }], TestContext.Current.CancellationToken);

        var stop = await NextStopAsync();
        Assert.Equal("breakpoint", stop.Reason);
        Assert.Equal(depth, _debug.SelectedFrame?.Source?.Path, ignoreCase: true);
        Assert.Equal("\"from WSL\"", (await _debug.EvaluateAsync("System.Environment.GetEnvironmentVariable(\"GREETING\")", "watch", TestContext.Current.CancellationToken)).Result);
        Assert.Equal("\"Unix\"", (await _debug.EvaluateAsync("System.Environment.OSVersion.Platform.ToString()", "watch", TestContext.Current.CancellationToken)).Result);
        await _debug.ContinueAsync();
        await _ended.Task.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Attach_over_SSH()
    {
        var folder = Path.Combine(Path.GetTempPath(), "codalus-ssh-" + Guid.NewGuid().ToString("N")[..8]);
        var ssh = RemoteFixture.FakeSsh(folder);
        Assert.SkipWhen(ssh is null, "The SSH stand-in runs on Linux and macOS.");
        var target = new SshTarget("stand-in.invalid", "tester", 2222, ssh!) { Home = TestHome };
        var signal = Path.Combine(folder, "go");
        var dotnet = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator).Select(d => Path.Combine(d, "dotnet")).First(File.Exists);
        using var program = Process.Start(new ProcessStartInfo(dotnet, ["exec", debuggee.Dll, "--wait-for", signal]) { UseShellExecute = false, RedirectStandardOutput = true })!;
        try
        {
            await AttachStopAndDetachAsync(target, () => File.WriteAllTextAsync(signal, "go", TestContext.Current.CancellationToken), pid => pid == program.Id, new SourceMap());
            await program.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
            Assert.Equal(15, program.ExitCode);
        }
        finally
        {
            if (!program.HasExited)
            {
                program.Kill();
            }

            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task Attach_in_a_container()
    {
        Assert.SkipWhen(RemoteFixture.Containers is null, $"No Podman with {RemoteFixture.Image} (on Linux, or in WSL on Windows).");
        var (tool, dotnetRoot) = RemoteFixture.Containers!.Value;
        var name = "codalus-test-" + Guid.NewGuid().ToString("N")[..8];
        var app = OperatingSystem.IsWindows() ? WslLaunch.ToLinuxPath(debuggee.Folder) : debuggee.Folder;
        var run = RemoteFixture.Run(tool[0],
        [
            .. tool.Skip(1), "run", "-d", "--rm", "--name", name,
            "-v", dotnetRoot + ":/dotnet:ro", "-v", app + ":/app:ro", "-e", "DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1",
            RemoteFixture.Image, "/dotnet/dotnet", "/app/bin/Debug/net10.0/Debuggee.dll", "--wait-for", "/tmp/go",
        ], 120);
        Assert.True(run.Code == 0, run.Output);
        var target = new ContainerTarget(name, tool) { Home = TestHome };
        try
        {
            // The program's PDB has this machine's paths, which the source map leaves as they are.
            await AttachStopAndDetachAsync(target, () => RemoteShell.RunAsync(target, "touch /tmp/go", TestContext.Current.CancellationToken), _ => true, new SourceMap());
        }
        finally
        {
            RemoteFixture.Run(tool[0], [.. tool.Skip(1), "rm", "-f", name], 60);
        }
    }

    [Fact]
    public void Ssh_destinations_parse_and_run_the_script_through_sh()
    {
        Assert.Equal(new SshTarget("host", "me", 2222), SshTarget.Parse("me@host:2222"));
        Assert.Equal(new SshTarget("host"), SshTarget.Parse("ssh://host/"));
        Assert.Equal(new SshTarget("::1", null, 22), SshTarget.Parse("[::1]:22"));
        Assert.Equal(new SshTarget("fe80::1"), SshTarget.Parse("fe80::1"));
        Assert.Null(SshTarget.Parse("me@host:99999"));
        Assert.Null(SshTarget.Parse("-oProxyCommand=x"));
        Assert.Null(SshTarget.Parse("a b"));

        var (program, arguments) = new SshTarget("host", "me", 2222).Shell("echo 'hi'");
        Assert.Equal("ssh", program);
        Assert.Equal(["-T", "-o", "ServerAliveInterval=15", "-p", "2222", "me@host", "sh -c 'echo '\\''hi'\\'''"], arguments);
        var container = new ContainerTarget("web", ["wsl", "-d", "Ubuntu", "--exec", "podman"]).Shell("true");
        Assert.Equal("wsl", container.Program);
        Assert.Equal(["-d", "Ubuntu", "--exec", "podman", "exec", "-i", "web", "sh", "-c", "true"], container.Arguments);
        Assert.Equal(["-d", "Ubuntu", "--exec", "sh", "-c", "true"], new WslTarget("Ubuntu").Shell("true").Arguments);
        Assert.Equal(["--exec", "sh", "-c", "true"], new WslTarget(string.Empty).Shell("true").Arguments);
    }

    [Fact]
    public void Process_listings_and_containers_are_read()
    {
        var processes = RemoteShell.ParseProcesses("1\t0\tsystemd\t/sbin/init\n42\t1\tdotnet\tdotnet App.dll --port 5\n43\t0\tsh\tsh -c # codalus-ps ...\n");
        Assert.Equal([42, 1], processes.Select(p => p.Id));
        Assert.True(processes[0].IsDotnet);
        Assert.Equal("dotnet App.dll --port 5", processes[0].CommandLine);

        var containers = RemoteTargets.ParseContainers("abc123\tweb\tmcr.microsoft.com/dotnet/aspnet:10.0\r\n");
        Assert.Equal(new ContainerInfo("abc123", "web", "mcr.microsoft.com/dotnet/aspnet:10.0"), Assert.Single(containers));
        Assert.Equal(["wsl", "-d", "Ubuntu", "--exec", "podman"], RemoteTargets.ContainerTool("wsl -d Ubuntu --exec podman"));
        Assert.Equal(["docker"], RemoteTargets.ContainerTool(" "));
        Assert.Equal("linux-x64", RemoteDebuggers.Platform("Linux", "x86_64"));
        Assert.Null(RemoteDebuggers.Platform("Darwin", "x86_64"));
    }

    [Fact]
    public void Source_maps_go_both_ways_and_infer_folders_from_the_solution()
    {
        var local = Path.Combine(Path.GetTempPath(), "codalus-map", "App");
        var program = Path.Combine(local, "Program.cs");
        var other = Path.Combine(local, "Other.cs");
        var map = new SourceMap(localFiles: [program, other, Path.Combine(Path.GetTempPath(), "codalus-map", "Lib", "Program.cs")]);

        // Unknown at first: breakpoints go out as they are.
        Assert.Equal(other, map.ToRemote(other));

        // A frame's file is found by its folder and name, which maps the folder for the next breakpoints.
        Assert.Equal(program, map.ToLocal("/src/App/Program.cs"));
        Assert.Equal("/src/App/Other.cs", map.ToRemote(other));
        Assert.Equal(other, map.ToLocal("/src/App/Other.cs"));

        // Given mappings win; a name alone (ambiguous here) maps to nothing.
        var given = new SourceMap([new("/app", local)]);
        Assert.Equal("/app/Program.cs", given.ToRemote(program));
        Assert.Equal(program, given.ToLocal("/app/Program.cs"));
        Assert.Equal("/elsewhere/Program.cs", new SourceMap(localFiles: [program, Path.Combine(Path.GetTempPath(), "x", "Program.cs")]).ToLocal("/elsewhere/Program.cs"));
    }

    [Fact]
    public void Windows_paths_become_WSL_paths()
    {
        Assert.Equal("/mnt/d/src/App", WslLaunch.ToLinuxPath(@"D:\src\App\"));
        Assert.Equal("/mnt/c", WslLaunch.ToLinuxPath(@"C:\"));
        Assert.Equal("/home/me/app", WslLaunch.ToLinuxPath(@"\\wsl$\Ubuntu\home\me\app"));
        Assert.Equal("/home/me", WslLaunch.ToLinuxPath(@"\\wsl.localhost\Ubuntu\home\me"));
        Assert.Equal("/drives/d/x", WslLaunch.ToLinuxPath(@"D:\x", "/drives"));
        Assert.Equal("'it'\\''s'", WslLaunch.ShellQuote("it's"));
        Assert.Equal("/a/b.dll", WslLaunch.ShellQuote("/a/b.dll"));
    }
}
