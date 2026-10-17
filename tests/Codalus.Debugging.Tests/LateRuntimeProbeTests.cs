#pragma warning disable CA1861, CA1416
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Codalus.Platform;

namespace Codalus.Debugging.Tests;

// PROBE (scratch branch only, never merged): what netcoredbg and the program do on macOS when the program's runtime
// starts after netcoredbg's 5 seconds.
public sealed class LateRuntimeProbeTests(DebuggeeFixture debuggee) : IClassFixture<DebuggeeFixture>
{
    public static TheoryData<string> Variants => new() { "direct", "sh-0", "sh-2", "sh-7", "bash-7", "copiedsh-7" };

    [Theory]
    [MemberData(nameof(Variants))]
    public async Task Probe(string variant)
    {
        Assert.SkipWhen(DebuggeeFixture.Adapter is null, "netcoredbg is not installed.");
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix.");
        var ct = TestContext.Current.CancellationToken;
        var folder = Environment.GetEnvironmentVariable("CODALUS_PROBE_DIR") ?? Path.Combine(Path.GetTempPath(), "probe");
        Directory.CreateDirectory(folder);
        var report = new StringBuilder();
        var clock = Stopwatch.StartNew();
        void Note(string text)
        {
            lock (report)
            {
                report.Append(CultureInfo.InvariantCulture, $"[{clock.Elapsed.TotalSeconds:0.00}s] {text}\n");
            }
        }

        string program;
        string[] args;
        var dotnet = DebuggeeFixture.DotnetHost;
        string[] tail = [dotnet, "exec", debuggee.Dll, "--depth"];
        switch (variant)
        {
            case "direct":
                program = dotnet;
                args = ["exec", debuggee.Dll, "--depth"];
                break;
            case "bash-7":
                program = File.Exists("/opt/homebrew/bin/bash") ? "/opt/homebrew/bin/bash" : "/bin/bash";
                args = ["-c", "sleep 7; exec \"$0\" \"$@\"", .. tail];
                break;
            case "copiedsh-7":
                program = Path.Combine(folder, "copied-bash");
                File.Copy("/bin/bash", program, overwrite: true);
                File.SetUnixFileMode(program, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                args = ["-c", "sleep 7; exec \"$0\" \"$@\"", .. tail];
                break;
            default:
                program = "/bin/sh";
                args = ["-c", $"sleep {variant[3..]}; exec \"$0\" \"$@\"", .. tail];
                break;
        }

        Note($"variant {variant}: {program} {string.Join(' ', args)}");
        var info = DebugAdapterStartInfo.Local(DebuggeeFixture.Adapter!);
        info = info with { Arguments = [.. info.Arguments, "--engineLogging=" + Path.Combine(folder, $"{variant}-dap.log"), "--log=" + Path.Combine(folder, $"{variant}-netcoredbg.log")] };
        var before = ProcessTree.Children(Environment.ProcessId).ToHashSet();
        await using var adapter = await DebugAdapter.StartAsync(info, ct);
        var netcoredbg = ProcessTree.Children(Environment.ProcessId).Where(id => !before.Contains(id)).ToList();
        Note($"netcoredbg pid(s): {string.Join(',', netcoredbg)}");
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        adapter.Log += (_, text) => Note("LOG " + text.TrimEnd());
        adapter.EventReceived += e =>
        {
            Note($"EVENT {e.Name} {(e.Body.ValueKind == JsonValueKind.Undefined ? string.Empty : e.Body.GetRawText())}");
            if (e.Name == DapEvents.Stopped)
            {
                stopped.TrySetResult();
            }

            return Task.CompletedTask;
        };
        await adapter.Initialized.WaitAsync(TimeSpan.FromSeconds(30), ct);
        var depth = Path.Combine(debuggee.Folder, "Depth.cs");
        var loop = DebuggeeFixture.Line(depth, "depth-loop");
        var bps = await adapter.SetBreakpointsAsync(depth, [new SourceBreakpoint(loop)], ct);
        Note("breakpoints " + string.Join(',', bps.Select(b => b.Verified)));
        await adapter.LaunchAsync(new LaunchArguments(program, args, debuggee.Folder, []), ct);
        Note("launch answered");
        var done = Task.Run(async () =>
        {
            try
            {
                await adapter.ConfigurationDoneAsync(ct);
                Note("configurationDone (with the wait) succeeded");
            }
            catch (Exception ex)
            {
                Note("configurationDone (with the wait) failed: " + ex.GetType().Name + ": " + ex.Message + (ex.InnerException is { } inner ? " / " + inner.Message : string.Empty));
            }
        }, ct);

        var snapshots = Task.Run(async () =>
        {
            foreach (var at in new[] { 3.0, 6.5, 12.0, 25.0 })
            {
                var delay = TimeSpan.FromSeconds(at) - clock.Elapsed;
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, CancellationToken.None);
                }

                Note($"SNAPSHOT at {at}s\n{Snapshot(netcoredbg)}");
            }
        }, CancellationToken.None);

        var finished = await Task.WhenAny(Task.WhenAll(done, stopped.Task), Task.Delay(TimeSpan.FromSeconds(80), ct));
        Note($"stopped: {stopped.Task.IsCompleted}, configurationDone finished: {done.IsCompleted}");
        await snapshots;
        if (stopped.Task.IsCompleted)
        {
            try
            {
                var threads = await adapter.ThreadsAsync(ct);
                var frames = await adapter.StackTraceAsync(threads[0].Id, 1, ct);
                Note("top frame line " + frames.StackFrames.FirstOrDefault()?.Line);
                var pid = await adapter.EvaluateAsync("System.Environment.ProcessId", frames.StackFrames.FirstOrDefault()?.Id, "watch", ct);
                Note("program pid " + pid.Result);
            }
            catch (Exception ex)
            {
                Note("inspect failed: " + ex.Message);
            }
        }

        Note($"FINAL SNAPSHOT\n{Snapshot(netcoredbg)}");
        try
        {
            await adapter.DisconnectAsync(terminateDebuggee: true, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
        }
        catch (Exception ex)
        {
            Note("disconnect: " + ex.Message);
        }

        File.WriteAllText(Path.Combine(folder, $"{variant}.txt"), report.ToString());
        foreach (var id in netcoredbg)
        {
            KillTree(id);
        }
    }

    private static void KillTree(int id)
    {
        foreach (var child in ProcessTree.Children(id))
        {
            KillTree(child);
        }

        try
        {
            using var p = Process.GetProcessById(id);
            p.Kill();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }

    private static string Snapshot(IReadOnlyList<int> netcoredbg)
    {
        var text = new StringBuilder();
        var tree = new List<int>();
        void Walk(int id, int level)
        {
            foreach (var child in ProcessTree.Children(id))
            {
                tree.Add(child);
                text.Append(CultureInfo.InvariantCulture, $"{new string(' ', level * 2)}child {child}\n");
                Walk(child, level + 1);
            }
        }

        foreach (var id in netcoredbg)
        {
            text.Append(CultureInfo.InvariantCulture, $"netcoredbg {id}\n");
            Walk(id, 1);
        }

        var ids = netcoredbg.Concat(tree).Select(i => i.ToString(CultureInfo.InvariantCulture)).ToList();
        text.Append(Run("ps", ["-o", "pid,ppid,stat,etime,wchan,args", "-p", string.Join(',', ids)]));
        var tmp = Path.GetTempPath();
        text.Append(CultureInfo.InvariantCulture, $"TMPDIR {tmp}\n");
        try
        {
            foreach (var file in Directory.EnumerateFileSystemEntries(tmp).Where(f => tree.Concat(netcoredbg).Any(id => Path.GetFileName(f).Contains("-" + id.ToString(CultureInfo.InvariantCulture) + "-", StringComparison.Ordinal))))
            {
                text.Append("  ").Append(Path.GetFileName(file)).Append('\n');
            }
        }
        catch (Exception ex)
        {
            text.Append("  ").Append(ex.Message).Append('\n');
        }

        foreach (var id in tree)
        {
            text.Append(CultureInfo.InvariantCulture, $"starttime of {id}: ").Append(Run("ps", ["-o", "lstart=", "-p", id.ToString(CultureInfo.InvariantCulture)]));
            if (OperatingSystem.IsMacOS())
            {
                text.Append(Run("sample", [id.ToString(CultureInfo.InvariantCulture), "1", "-mayDie"], 20).Split('\n').Take(80).Aggregate(new StringBuilder(), (b, l) => b.Append(l).Append('\n')));
            }
        }

        return text.ToString();
    }

    private static string Run(string file, string[] args, int seconds = 10)
    {
        try
        {
            var start = new ProcessStartInfo(file, args) { RedirectStandardOutput = true, RedirectStandardError = true };
            using var p = Process.Start(start)!;
            var output = p.StandardOutput.ReadToEndAsync();
            var error = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(seconds * 1000))
            {
                p.Kill();
            }

            return output.Result + error.Result;
        }
        catch (Exception ex)
        {
            return $"{file}: {ex.Message}\n";
        }
    }
}
