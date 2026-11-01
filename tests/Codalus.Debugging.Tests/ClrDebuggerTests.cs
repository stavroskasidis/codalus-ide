using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Text;
using Codalus.Platform;

namespace Codalus.Debugging.Tests;

/// <summary>
/// M54 (ADR 0086), on every OS: CoreCLR's debugger adapter of our own (<c>codalus-debughost --clr-dap</c>) against the
/// real runtime through dbgshim, and the launcher it starts programs with (<c>--launch</c>). A launch stops on
/// <c>Main</c>'s first line, binds breakpoints as modules load, reads locals and members, evaluates through func-eval,
/// steps, and ends with the program's own exit code (on macOS too, which netcoredbg reports as 0); the launch's
/// arguments, environment and working directory reach the program; an attach binds its breakpoints on the attach's
/// module loads. The launcher ends a program when the adapter goes away, before or after it ran, unless told to detach.
/// </summary>
public sealed class ClrDebuggerTests(DebuggeeFixture fixture) : IClassFixture<DebuggeeFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DapJsonContext s_json = DapJsonContext.Default;

    private static string Host => Path.Combine(AppContext.BaseDirectory, "codalus-debughost.dll");

    private static DebugAdapterStartInfo Adapter() => new(DebuggeeFixture.DotnetHost, [Host, CoreClrAdapter.AdapterArgument], AppContext.BaseDirectory);

    private int Line(string marker) => DebuggeeFixture.Line(fixture.Program, marker);

    [Fact]
    public async Task A_func_eval_that_times_out_logs_every_threads_stack_before_its_abort()
    {
        // The diagnostic for DataTipDebuggerTests' throwing getter, which stalls on Windows after its catch handler is
        // found (docs/ci-flakes.md, runs 41084559512 and 41251924698): what the eval thread waits on, and where the others are.
        var logs = Directory.CreateTempSubdirectory("codalus-clr-dap-").FullName;
        try
        {
            await using (var adapter = await DebugAdapter.StartAsync(Adapter() with { Environment = new Dictionary<string, string> { ["CODALUS_CLR_DEBUGGER_LOG"] = logs } }, Ct))
            {
                var events = new EventLog(adapter);
                await adapter.LaunchAsync(new NetFxDebugArguments(DebuggeeFixture.DotnetHost, ["exec", fixture.Dll], fixture.Folder, Type: "coreclr"), s_json.NetFxDebugArguments, Ct);
                await adapter.Initialized.WaitAsync(TimeSpan.FromSeconds(60), Ct);
                await adapter.SetBreakpointsAsync(fixture.Program, [new SourceBreakpoint(Line("first"))], Ct);
                await adapter.ConfigurationDoneAsync(Ct);
                var stop = await events.StoppedAsync();
                var top = (await adapter.StackTraceAsync(stop.ThreadId!.Value, 1, Ct)).StackFrames[0];

                // Sleep(-1) never returns: the eval times out after 5 s, and the session aborts it. (On Linux the managed wait
                // doesn't take the abort: the eval runs on, and the program with it; TODO(issue) at CorDebugTarget.TryAbort.)
                var error = await Assert.ThrowsAsync<DapException>(() => adapter.EvaluateAsync("System.Threading.Thread.Sleep(-1)", top.Id, "watch", Ct));
                Assert.Contains("timed out", error.Message, StringComparison.Ordinal);
                await adapter.DisconnectAsync(terminateDebuggee: true, Ct);
            }

            var log = await File.ReadAllTextAsync(Assert.Single(Directory.GetFiles(logs, "clr-dap-*.log")), Ct);
            const string Header = "The threads before the abort, innermost frame first:";
            Assert.True(System.Text.RegularExpressions.Regex.IsMatch(log, @"A func-eval on thread \d+ didn't return in 5 s \([^)]*abort") && log.Contains(Header, StringComparison.Ordinal), "The adapter's log:\n" + log);
            var threads = log[(log.IndexOf(Header, StringComparison.Ordinal) + Header.Length)..];

            // The eval thread: Sleep above the evaluation's frame, Main (where the program stopped) below it.
            var evalThread = threads.Split("\n  thread ").First(t => t.Contains("[Function Evaluation]", StringComparison.Ordinal));
            var evaluation = evalThread.IndexOf("[Function Evaluation]", StringComparison.Ordinal);
            Assert.Contains("Sleep", evalThread[..evaluation], StringComparison.Ordinal);
            Assert.Contains("Program", evalThread[evaluation..], StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(logs, recursive: true);
        }
    }

    [Fact]
    public async Task A_launch_stops_on_the_first_line_and_at_breakpoints_evaluates_steps_and_ends_with_its_exit_code()
    {
        await using var adapter = await DebugAdapter.StartAsync(Adapter(), Ct);
        var events = new EventLog(adapter);
        await adapter.LaunchAsync(new NetFxDebugArguments(DebuggeeFixture.DotnetHost, ["exec", fixture.Dll], fixture.Folder, Type: "coreclr"), s_json.NetFxDebugArguments, Ct);
        await adapter.Initialized.WaitAsync(TimeSpan.FromSeconds(60), Ct);
        await adapter.SetBreakpointsAsync(fixture.Program, [new SourceBreakpoint(Line("first")), new SourceBreakpoint(Line("loop"))], Ct);
        await adapter.SetBreakpointsAsync(fixture.Types, [new SourceBreakpoint(DebuggeeFixture.Line(fixture.Types, "describe"))], Ct);
        await adapter.ConfigurationDoneAsync(Ct);

        // Main's first line: the program ran no code of its own before the debugger was there.
        var first = await events.StoppedAsync();
        Assert.Equal("breakpoint", first.Reason);
        var thread = first.ThreadId!.Value;
        Assert.Equal(Line("first"), (await adapter.StackTraceAsync(thread, 1, Ct)).StackFrames[0].Line);

        // The loop: locals, evaluation (an indexer through func-eval, operators), and a step over.
        await adapter.ContinueAsync(thread, Ct);
        var loop = await events.StoppedAsync();
        var top = (await adapter.StackTraceAsync(loop.ThreadId!.Value, 1, Ct)).StackFrames[0];
        Assert.Equal(Line("loop"), top.Line);
        var locals = await adapter.VariablesAsync(Assert.Single(await adapter.ScopesAsync(top.Id, Ct)).VariablesReference, Ct);
        Assert.Equal("0", locals.Single(v => v.Name == "total").Value);
        Assert.Equal("0", locals.Single(v => v.Name == "i").Value);
        Assert.Equal("5", (await adapter.EvaluateAsync("numbers.Count", top.Id, "watch", Ct)).Result);
        Assert.Equal("3", (await adapter.EvaluateAsync("numbers[2] + total", top.Id, "watch", Ct)).Result);
        await adapter.NextAsync(loop.ThreadId.Value, Ct);
        Assert.Equal("step", (await events.StoppedAsync()).Reason);

        // In Describe: a record's members, and its getter through func-eval.
        await adapter.SetBreakpointsAsync(fixture.Program, [], Ct);
        await adapter.ContinueAsync(loop.ThreadId.Value, Ct);
        var describe = await events.StoppedAsync();
        var frame = (await adapter.StackTraceAsync(describe.ThreadId!.Value, 1, Ct)).StackFrames[0];
        Assert.Equal("Debuggee.Describer.Describe", frame.Name);
        Assert.Equal("\"Ada\"", (await adapter.EvaluateAsync("person.Name", frame.Id, "watch", Ct)).Result);
        Assert.Equal("37", (await adapter.EvaluateAsync("person.Age + 1", frame.Id, "watch", Ct)).Result);
        Assert.Equal("\"ADA\"", (await adapter.EvaluateAsync("person.Name.ToUpper()", frame.Id, "watch", Ct)).Result);

        // And the end: the program's own exit code (its total) and its output, on every OS.
        await adapter.SetBreakpointsAsync(fixture.Types, [], Ct);
        await adapter.ContinueAsync(describe.ThreadId.Value, Ct);
        var exited = await events.NextAsync(DapEvents.Exited);
        Assert.Equal(15, exited.Body.GetProperty("exitCode").GetInt32());
        await events.NextAsync(DapEvents.Terminated);
        Assert.Contains("total=15 Ada is 36", events.Output, StringComparison.Ordinal);
        Assert.Contains("done", events.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_launch_gives_the_program_its_arguments_environment_and_working_directory()
    {
        // The file the program waits for, by a path with spaces: it goes on only if the argument came through whole.
        var folder = Directory.CreateDirectory(Path.Combine(fixture.Folder, "a folder with spaces")).FullName;
        var wait = Path.Combine(folder, "go on.txt");
        File.WriteAllText(wait, string.Empty);
        await using var adapter = await DebugAdapter.StartAsync(Adapter(), Ct);
        var events = new EventLog(adapter);
        var env = new Dictionary<string, string> { ["CODALUS_CLR_TEST"] = "from the launch ü" };
        await adapter.LaunchAsync(new NetFxDebugArguments(DebuggeeFixture.DotnetHost, ["exec", fixture.Dll, "--wait-for", wait], folder, env, Type: "coreclr"), s_json.NetFxDebugArguments, Ct);
        await adapter.Initialized.WaitAsync(TimeSpan.FromSeconds(60), Ct);
        await adapter.SetBreakpointsAsync(fixture.Program, [new SourceBreakpoint(Line("loop"))], Ct);
        await adapter.ConfigurationDoneAsync(Ct);

        var stopped = await events.StoppedAsync();
        var top = (await adapter.StackTraceAsync(stopped.ThreadId!.Value, 1, Ct)).StackFrames[0];
        Assert.Equal(Line("loop"), top.Line);
        Assert.Equal("\"from the launch ü\"", (await adapter.EvaluateAsync("Environment.GetEnvironmentVariable(\"CODALUS_CLR_TEST\")", top.Id, "watch", Ct)).Result);
        Assert.Equal(Quoted(folder), (await adapter.EvaluateAsync("Environment.CurrentDirectory", top.Id, "watch", Ct)).Result);
        Assert.Contains("waiting", events.Output, StringComparison.Ordinal);

        await adapter.SetBreakpointsAsync(fixture.Program, [], Ct);
        await adapter.ContinueAsync(stopped.ThreadId.Value, Ct);
        Assert.Equal(15, (await events.NextAsync(DapEvents.Exited)).Body.GetProperty("exitCode").GetInt32());
    }

    [Fact]
    public async Task An_attach_binds_breakpoints_as_the_modules_are_reported_and_stops_there()
    {
        var go = Path.Combine(fixture.Folder, "attach-" + Guid.NewGuid().ToString("N"));
        using var program = Process.Start(new ProcessStartInfo(DebuggeeFixture.DotnetHost, ["exec", fixture.Dll, "--wait-for", go])
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            WorkingDirectory = fixture.Folder,
        })!;
        try
        {
            Assert.Equal("waiting", await program.StandardOutput.ReadLineAsync(Ct).AsTask().WaitAsync(TimeSpan.FromSeconds(60), Ct));
            await using var adapter = await DebugAdapter.StartAsync(Adapter(), Ct);
            var events = new EventLog(adapter);
            await adapter.AttachAsync(new NetFxDebugArguments(ProcessId: program.Id, Type: "coreclr", Request: "attach"), s_json.NetFxDebugArguments, Ct);
            await adapter.Initialized.WaitAsync(TimeSpan.FromSeconds(60), Ct);
            var set = await adapter.SetBreakpointsAsync(fixture.Program, [new SourceBreakpoint(Line("loop"))], Ct);
            await adapter.ConfigurationDoneAsync(Ct);

            // The breakpoint is bound once the attach has reported the program's module, before it goes on.
            await WaitUntilAsync(() => events.All.Any(e => e.Name == DapEvents.Breakpoint && e.Body.GetProperty("breakpoint").GetProperty("verified").GetBoolean()) || set[0].Verified, "the breakpoint to bind");
            File.WriteAllText(go, string.Empty);
            var stopped = await events.StoppedAsync();
            var top = (await adapter.StackTraceAsync(stopped.ThreadId!.Value, 1, Ct)).StackFrames[0];
            Assert.Equal(Line("loop"), top.Line);
            Assert.Equal("0", (await adapter.EvaluateAsync("total", top.Id, "watch", Ct)).Result);

            await adapter.DisconnectAsync(terminateDebuggee: false, Ct);
            await program.WaitForExitAsync(Ct).WaitAsync(TimeSpan.FromSeconds(60), Ct);
            Assert.Equal(15, program.ExitCode);
        }
        finally
        {
            if (!program.HasExited)
            {
                ProcessTree.Kill(program);
            }
        }
    }

    [Fact]
    public async Task The_launcher_reports_the_exit_code_of_the_program_it_resumed()
    {
        await using var launcher = await LauncherSession.StartAsync(fixture.Dll, [], fixture.Folder);
        await launcher.SendAsync("resume");
        Assert.Equal("exit 15", await launcher.ReadAsync());
        Assert.Equal(15, await launcher.ExitCodeAsync());
        Assert.Contains("total=15", launcher.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_launcher_kills_a_program_never_resumed_when_the_debugger_goes_away()
    {
        await using var launcher = await LauncherSession.StartAsync(fixture.Dll, [], fixture.Folder);
        using var program = Process.GetProcessById(launcher.ProgramId);
        launcher.ClosePipe();
        var code = await launcher.ExitCodeAsync();
        Assert.True(code == 1, $"exit code {code}: {launcher.Output}");
        await WaitUntilAsync(() => HasEnded(program), "the program to end");
        Assert.DoesNotContain("total=", launcher.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_launcher_ends_a_running_program_when_the_debugger_goes_away_unless_it_detached()
    {
        // Gone away: the waiting program is ended.
        var never = Path.Combine(fixture.Folder, "never-" + Guid.NewGuid().ToString("N"));
        await using (var launcher = await LauncherSession.StartAsync(fixture.Dll, ["--wait-for", never], fixture.Folder))
        {
            using var program = Process.GetProcessById(launcher.ProgramId);
            await launcher.SendAsync("resume");
            await WaitUntilAsync(() => launcher.Output.Contains("waiting", StringComparison.Ordinal), "the program to start");
            launcher.ClosePipe();
            var code = await launcher.ExitCodeAsync();
            Assert.True(code is not 0 and not -532462766, $"exit code {code}: {launcher.Output}");
            await WaitUntilAsync(() => HasEnded(program), "the program to end");
        }

        // Detached: it goes on, and its exit code is still the launcher's.
        var go = Path.Combine(fixture.Folder, "go-" + Guid.NewGuid().ToString("N"));
        await using (var launcher = await LauncherSession.StartAsync(fixture.Dll, ["--wait-for", go], fixture.Folder))
        {
            await launcher.SendAsync("resume");
            await WaitUntilAsync(() => launcher.Output.Contains("waiting", StringComparison.Ordinal), "the program to start");
            await launcher.SendAsync("detach");
            launcher.ClosePipe();
            await Task.Delay(500, Ct);
            Assert.False(HasEnded(Process.GetProcessById(launcher.ProgramId)));
            File.WriteAllText(go, string.Empty);
            var code = await launcher.ExitCodeAsync();
            Assert.True(code == 15, $"exit code {code}: {launcher.Output}");
        }
    }

    private static string Quoted(string text) => "\"" + text.Replace("\\", "\\\\", StringComparison.Ordinal) + "\"";

    private static bool HasEnded(Process process)
    {
        try
        {
            process.Refresh();
            return process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(60), $"Timed out waiting for {what}.");
            await Task.Delay(50, Ct);
        }
    }

    /// <summary>The launcher on its own, with this test as the adapter on the other end of its pipe.</summary>
    private sealed class LauncherSession : IAsyncDisposable
    {
        private readonly NamedPipeServerStream _pipe;
        private readonly StreamReader _reader;
        private readonly StreamWriter _writer;
        private readonly Process _launcher;
        private readonly StringBuilder _output = new();
        private bool _closed;

        private LauncherSession(NamedPipeServerStream pipe, Process launcher)
        {
            _pipe = pipe;
            _launcher = launcher;
            _reader = new StreamReader(pipe, Encoding.UTF8, false, leaveOpen: true);
            _writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
        }

        public int ProgramId { get; private set; }

        public string Output
        {
            get
            {
                lock (_output)
                {
                    return _output.ToString();
                }
            }
        }

        public static async Task<LauncherSession> StartAsync(string dll, string[] arguments, string folder)
        {
            var name = "codalus-test-" + Guid.NewGuid().ToString("N")[..8];
            var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            var launcher = Process.Start(new ProcessStartInfo(DebuggeeFixture.DotnetHost, [Host, CoreClrAdapter.LaunchArgument, name, DebuggeeFixture.DotnetHost, "exec", dll, .. arguments])
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = folder,
            })!;
            // Connected first: setting AutoFlush flushes, which an unconnected pipe refuses.
            await pipe.WaitForConnectionAsync(Ct).WaitAsync(TimeSpan.FromSeconds(60), Ct);
            var session = new LauncherSession(pipe, launcher);
            launcher.OutputDataReceived += (_, e) => session.Append(e.Data);
            launcher.ErrorDataReceived += (_, e) => session.Append(e.Data);
            launcher.BeginOutputReadLine();
            launcher.BeginErrorReadLine();
            var report = await session.ReadAsync();
            Assert.StartsWith("pid ", report, StringComparison.Ordinal);
            session.ProgramId = int.Parse(report.AsSpan(4), CultureInfo.InvariantCulture);
            return session;
        }

        public Task SendAsync(string message) => _writer.WriteLineAsync(message);

        public async Task<string> ReadAsync() =>
            await _reader.ReadLineAsync(Ct).AsTask().WaitAsync(TimeSpan.FromSeconds(60), Ct) ?? throw new EndOfStreamException("The launcher closed its pipe.");

        public void ClosePipe()
        {
            if (_closed)
            {
                return;
            }

            _closed = true;
            try
            {
                _writer.Dispose();
            }
            catch (IOException)
            {
                // The launcher went away first.
            }

            _reader.Dispose();
            _pipe.Dispose();
        }

        public async Task<int> ExitCodeAsync()
        {
            await _launcher.WaitForExitAsync(Ct).WaitAsync(TimeSpan.FromSeconds(60), Ct);
            return _launcher.ExitCode;
        }

        public async ValueTask DisposeAsync()
        {
            ClosePipe();
            if (!_launcher.HasExited)
            {
                ProcessTree.Kill(_launcher);
            }

            await _launcher.WaitForExitAsync(CancellationToken.None);
            _launcher.Dispose();
        }

        private void Append(string? line)
        {
            if (line is not null)
            {
                lock (_output)
                {
                    _output.AppendLine(line);
                }
            }
        }
    }
}
