using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Codalus.Platform.Internal;
using Codalus.Plugins.Proposed;
using Codalus.Rpc;

namespace Codalus.Plugins.DotNetTest;

/// <summary>
/// Microsoft.Testing.Platform in server mode (ADR 0010; protocol 1.0, <c>microsoft/testfx</c>
/// <c>docs/mstest-runner-protocol</c>): the test application is started with
/// <c>--server jsonrpc --client-host 127.0.0.1 --client-port N</c> and connects back to us. One process per request.
/// </summary>
internal sealed class MtpRunner(IDebug debug, Action<string> log)
{
    private static readonly TimeSpan s_connectTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan s_killAfter = TimeSpan.FromSeconds(5);

    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };

    private static JsonTypeInfo<JsonElement> ElementInfo => JsonOptions.GetTypeInfo<JsonElement>();

    public async Task<IReadOnlyList<RunnerTest>> DiscoverAsync(TestTarget target, RunOptions options, CancellationToken cancellationToken)
    {
        var tests = new List<RunnerTest>();
        await using var session = await StartAsync(target, options with { Mode = RunMode.Run }, extraArguments: [], cancellationToken).ConfigureAwait(false);
        await session.RequestAsync("testing/discoverTests", runId => Params(runId, null), (node, _) =>
        {
            if (ToRunnerTest(node, target) is { } test)
            {
                lock (tests)
                {
                    tests.Add(test);
                }
            }
        }, cancellationToken).ConfigureAwait(false);
        return tests;
    }

    /// <summary>Runs <paramref name="tests"/> (null: all) of one project.</summary>
    public async Task RunAsync(TestTarget target, IReadOnlyList<RunnerTest>? tests, RunOptions options, IRunReporter reporter, CancellationToken cancellationToken)
    {
        var known = tests?.ToDictionary(t => t.Key, StringComparer.Ordinal) ?? [];
        var extra = new List<string>();
        string? coverageFile = null;
        if (options.Mode == RunMode.Coverage && options.Coverage is { } coverage)
        {
            // coverlet.mtp writes the report from inside the app; otherwise coverlet.console (StartAsync) writes it after the app exits.
            coverageFile = CoverageTools.OutputFile(target, coverage);
            if (CoverageTools.ProjectHasCoverletMtp(target))
            {
                extra.AddRange(["--coverlet", "--coverlet-output-format", "cobertura", "--coverlet-output", coverageFile]);
            }
        }

        await RunSessionAsync(target, tests, known, options, extra, reporter, cancellationToken).ConfigureAwait(false);

        // The session has ended, so the process (and the console wrapping it) has exited and written its report.
        if (coverageFile is not null && File.Exists(coverageFile))
        {
            reporter.CoverageFile(coverageFile);
        }
        else if (coverageFile is not null)
        {
            log($"Coverage: no report at {coverageFile}.");
        }
    }

    private async Task RunSessionAsync(TestTarget target, IReadOnlyList<RunnerTest>? tests, Dictionary<string, RunnerTest> known, RunOptions options, List<string> extra, IRunReporter reporter, CancellationToken cancellationToken)
    {
        await using var session = await StartAsync(target, options, extra, cancellationToken).ConfigureAwait(false);
        session.Output += (_, text) => reporter.Output(text);
        await session.RequestAsync("testing/runTests", runId => Params(runId, tests?.Select(t => (JsonElement)t.Payload)), (node, _) =>
        {
            var uid = node.TryGetProperty("uid", out var u) ? u.GetString() : null;
            if (uid is null)
            {
                return;
            }

            if (!known.TryGetValue(uid, out var test))
            {
                if (ToRunnerTest(node, target) is not { } discovered)
                {
                    return;
                }

                known[uid] = test = discovered;
                reporter.Discovered(test);
            }

            var state = node.TryGetProperty("execution-state", out var s) ? s.GetString() : null;
            if (node.TryGetProperty("retry.is-superseded", out var superseded) && superseded.ValueKind == JsonValueKind.True)
            {
                return;
            }

            switch (state)
            {
                case "in-progress":
                    reporter.Started(test);
                    break;
                case "passed":
                    reporter.Finished(test, RunnerOutcome.Passed, Duration(node), [], Output(node));
                    break;
                case "skipped":
                    reporter.Finished(test, RunnerOutcome.Skipped, Duration(node), Message(node) is { } reason ? [reason] : [], Output(node));
                    break;
                case "failed":
                    reporter.Finished(test, RunnerOutcome.Failed, Duration(node), [Message(node) ?? new TestMessage("The test failed.")], Output(node));
                    break;
                case "error" or "timed-out" or "canceled":
                    reporter.Finished(test, RunnerOutcome.Errored, Duration(node), [Message(node) ?? new TestMessage("The test " + state + ".")], Output(node));
                    break;
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    private static JsonElement Params(string runId, IEnumerable<JsonElement>? tests)
    {
        var parameters = new Dictionary<string, object?> { ["runId"] = runId };
        if (tests is not null)
        {
            parameters["tests"] = tests.ToList();
        }

        return JsonSerializer.SerializeToElement(parameters, JsonOptions);
    }

    private static TimeSpan? Duration(JsonElement node) =>
        node.TryGetProperty("time.duration-ms", out var ms) && ms.ValueKind == JsonValueKind.Number ? TimeSpan.FromMilliseconds(ms.GetDouble()) : null;

    private static string? Output(JsonElement node)
    {
        var stdout = node.TryGetProperty("standardOutput", out var o) ? o.GetString() : null;
        var stderr = node.TryGetProperty("standardError", out var e) ? e.GetString() : null;
        return string.IsNullOrEmpty(stderr) ? stdout : stdout + "[stderr] " + stderr;
    }

    private static TestMessage? Message(JsonElement node)
    {
        var message = node.TryGetProperty("error.message", out var m) ? m.GetString() : null;
        var stack = node.TryGetProperty("error.stacktrace", out var st) ? st.GetString() : null;
        var expected = node.TryGetProperty("assert.expected", out var ex) ? ex.GetString() : null;
        var actual = node.TryGetProperty("assert.actual", out var ac) ? ac.GetString() : null;
        if (message is null && stack is null)
        {
            return node.TryGetProperty("node-type", out _) && node.TryGetProperty("explanation", out var why) && why.GetString() is { } explanation
                ? new TestMessage(explanation)
                : null;
        }

        return new TestMessage(message ?? string.Empty) { StackTrace = stack, Expected = expected, Actual = actual };
    }

    /// <summary>A test node (<c>node-type: action</c>) as a runner test; groups are skipped (the tree is built from names).</summary>
    internal static RunnerTest? ToRunnerTest(JsonElement node, TestTarget target)
    {
        string? Property(string name) => node.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

        if (Property("node-type") is "group" || Property("uid") is not { } uid)
        {
            return null;
        }

        var display = Property("display-name") ?? uid;
        var fqn = Property("vstest.TestCase.FullyQualifiedName");
        var type = Property("location.type") ?? Property("vstest.TestCase.ManagedType");
        var method = Property("location.method") ?? Property("vstest.TestCase.ManagedMethod");
        if (type is null || method is null)
        {
            (type, method) = ManagedNames.FromFullyQualifiedName(fqn ?? display);
        }
        else
        {
            method = ManagedNames.MethodName(method);
        }

        var file = Property("location.file") ?? Property("vstest.TestCase.CodeFilePath");
        int? line = node.TryGetProperty("location.line-start", out var l) && l.ValueKind == JsonValueKind.Number ? l.GetInt32() : null;
        var traits = new List<TestTrait>();
        if (node.TryGetProperty("traits", out var traitArray) && traitArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var trait in traitArray.EnumerateArray())
            {
                if (trait.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                // Either { "key": k, "value": v } or { "k": "v" }.
                if (trait.TryGetProperty("key", out var key) && key.GetString() is { } k)
                {
                    traits.Add(new TestTrait(k, trait.TryGetProperty("value", out var v) ? v.GetString() : null));
                }
                else
                {
                    traits.AddRange(trait.EnumerateObject().Select(p => new TestTrait(p.Name, p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : null)));
                }
            }
        }

        return new RunnerTest(uid, target.Project, target.TargetFramework, fqn ?? (type + "." + method), display, type, method, file, line, traits, node.Clone());
    }

    // ------------------------------------------------------------------ process and connection

    private async Task<Session> StartAsync(TestTarget target, RunOptions options, IReadOnlyList<string> extraArguments, CancellationToken cancellationToken)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var (program, arguments) = Command(target);
        arguments.AddRange(["--server", "jsonrpc", "--client-host", "127.0.0.1", "--client-port", port.ToString(CultureInfo.InvariantCulture), "--no-banner"]);
        arguments.AddRange(extraArguments);
        var wrapped = false;
        if (options.Mode == RunMode.Coverage && options.Coverage is { } coverage && !CoverageTools.ProjectHasCoverletMtp(target))
        {
            (program, arguments) = CoverageTools.WrapWithConsole(target, program, arguments, coverage);
            wrapped = true;
        }

        var session = new Session(listener, log, wrapped, cancellationToken);
        try
        {
            if (options.Mode == RunMode.Debug)
            {
                log($"Debugging {program} {string.Join(' ', arguments)}");
                session.Debug = await debug.LaunchAsync(new DebugLaunchOptions(program, arguments, target.WorkingDirectory) { Environment = options.Environment }, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                session.Start(program, arguments, target.WorkingDirectory, options.Environment);
            }

            await session.ConnectAsync(s_connectTimeout, cancellationToken).ConfigureAwait(false);
            return session;
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>The apphost (or <c>dotnet exec</c>) as <c>dotnet run</c> would start the project.</summary>
    private static (string Program, List<string> Arguments) Command(TestTarget target)
    {
        if (target.RunCommand is { Length: > 0 } command && !command.EndsWith("dotnet", StringComparison.OrdinalIgnoreCase) && !command.EndsWith("dotnet.exe", StringComparison.OrdinalIgnoreCase) && File.Exists(command))
        {
            return (command, [.. CommandLine.Split(target.RunArguments)]);
        }

        return (DotnetHost.Path, ["exec", target.AssemblyPath]);
    }

    /// <param name="wrapped">Whether the process is <c>coverlet.console</c> running the test application (CoverageTools.WrapWithConsole).</param>
    /// <param name="cancellationToken">The request's: cancelling it stops waiting for a wrapping console to write its report.</param>
    private sealed class Session(TcpListener listener, Action<string> log, bool wrapped, CancellationToken cancellationToken) : IAsyncDisposable
    {
        private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _applicationExited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Action<JsonElement, bool>> _runs = new(StringComparer.Ordinal);
        private Process? _process;
        private TcpClient? _client;
        private JsonRpcConnection? _connection;

        public event EventHandler<string>? Output;

        public IDebugSession? Debug { get; set; }

        public void Start(string program, IReadOnlyList<string> arguments, string workingDirectory, IReadOnlyDictionary<string, string> environment)
        {
            var start = new ProcessStartInfo(program)
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var argument in arguments)
            {
                start.ArgumentList.Add(argument);
            }

            foreach (var (name, value) in environment)
            {
                start.Environment[name] = value;
            }

            var process = new Process { StartInfo = start, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, e) => Forward(e.Data);
            process.ErrorDataReceived += (_, e) => Forward(e.Data);
            process.Exited += (_, _) => _exited.TrySetResult();
            process.Start();
            process.StandardInput.Close();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            _process = process;
        }

        private void Forward(string? line)
        {
            if (line is not null)
            {
                if (wrapped && line.Trim() == CoverageTools.ConsoleApplicationExited)
                {
                    _applicationExited.TrySetResult();
                }

                Output?.Invoke(this, line + "\n");
            }
        }

        public async Task ConnectAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linked.CancelAfter(timeout);
            var accept = listener.AcceptTcpClientAsync(linked.Token).AsTask();
            var exitedFirst = Debug is { } debug ? debug.Ended.ContinueWith(_ => { }, TaskScheduler.Default) : _exited.Task;
            if (await Task.WhenAny(accept, exitedFirst).ConfigureAwait(false) != accept)
            {
                throw new InvalidOperationException("The test application exited before it connected (is it a Microsoft.Testing.Platform application?).");
            }

            _client = await accept.ConfigureAwait(false);
            listener.Stop();
            var stream = _client.GetStream();
            var connection = new JsonRpcConnection(stream, stream);
            connection.OnNotification("testing/testUpdates/tests", ElementInfo, OnTestUpdates);
            connection.OnNotification("client/log", ElementInfo, OnLog);
            connection.OnNotification("client/showMessage", ElementInfo, OnLog);
            connection.OnNotification("telemetry/update", ElementInfo, _ => { });
            connection.OnRequest("telemetry/update", (_, _) => Task.FromResult<JsonElement?>(null));
            connection.Start();
            _connection = connection;

            var initialize = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
            {
                ["processId"] = Environment.ProcessId,
                ["clientInfo"] = new Dictionary<string, string> { ["name"] = "codalus", ["version"] = "1.0.0" },
                ["capabilities"] = new Dictionary<string, object> { ["testing"] = new Dictionary<string, object> { ["debuggerProvider"] = false, ["isStateful"] = true } },
            }, JsonOptions);
            await connection.InvokeRawAsync("initialize", initialize, linked.Token).ConfigureAwait(false);
        }

        private void OnLog(JsonElement parameters)
        {
            var level = parameters.TryGetProperty("level", out var l) ? l.GetString() : null;
            var message = parameters.TryGetProperty("message", out var m) ? m.GetString() : null;
            if (message is not null)
            {
                Output?.Invoke(this, level is "Warning" or "Error" or "Critical" ? $"[{level}] {message}\n" : message + "\n");
            }
        }

        private void OnTestUpdates(JsonElement parameters)
        {
            if (!parameters.TryGetProperty("runId", out var runIdElement) || runIdElement.GetString() is not { } runId || !_runs.TryGetValue(runId, out var handler))
            {
                return;
            }

            if (!parameters.TryGetProperty("changes", out var changes) || changes.ValueKind != JsonValueKind.Array)
            {
                handler(default, true); // the terminal update: no more changes for this run
                return;
            }

            foreach (var change in changes.EnumerateArray())
            {
                if (change.TryGetProperty("node", out var node))
                {
                    handler(node, false);
                }
            }
        }

        /// <summary>Sends a discovery or run request and streams its node updates until the terminal update.</summary>
        public async Task RequestAsync(string method, Func<string, JsonElement> parameters, Action<JsonElement, bool> onNode, CancellationToken cancellationToken)
        {
            var runId = Guid.NewGuid().ToString();
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _runs[runId] = (node, last) =>
            {
                if (last)
                {
                    done.TrySetResult();
                }
                else
                {
                    onNode(node, false);
                }
            };
            try
            {
                await _connection!.InvokeRawAsync(method, parameters(runId), cancellationToken).ConfigureAwait(false);

                // Updates are notifications: the terminal one may arrive just after the response.
                await Task.WhenAny(done.Task, Task.Delay(TimeSpan.FromSeconds(5), CancellationToken.None)).ConfigureAwait(false);
            }
            catch (JsonRpcRemoteException ex)
            {
                throw new InvalidOperationException($"The test application failed: {ex.Message}", ex);
            }
            finally
            {
                _runs.TryRemove(runId, out _);
            }
        }

        public async ValueTask DisposeAsync()
        {
            listener.Stop();
            if (_connection is { } connection)
            {
                try
                {
                    await connection.NotifyAsync("exit", JsonSerializer.SerializeToElement(new Dictionary<string, object>(), JsonOptions), ElementInfo, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException or JsonRpcConnectionLostException or InvalidOperationException)
                {
                }
            }

            if (_process is { } process)
            {
                await _exited.Task.WaitAsync(s_killAfter).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

                // Coverlet's console outlives the test application: once the application has exited it reads the hits,
                // restores the assemblies and writes the report, which takes a loaded machine more than s_killAfter.
                // Killing it then lost the report, so it may finish unless the request is cancelled.
                if (!_exited.Task.IsCompleted && _applicationExited.Task.IsCompleted)
                {
                    await _exited.Task.WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                }

                if (!_exited.Task.IsCompleted)
                {
                    try
                    {
                        ProcessTree.Kill(process);
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or AggregateException)
                    {
                        log($"Could not stop test process {process.Id}: {ex.Message}");
                    }
                }

                process.Dispose();
            }

            if (Debug is { } debug && !debug.Ended.IsCompleted)
            {
                await ((Task)debug.Ended).WaitAsync(s_killAfter).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                if (!debug.Ended.IsCompleted)
                {
                    await debug.StopAsync().ConfigureAwait(false);
                }
            }

            if (_connection is not null)
            {
                await _connection.DisposeAsync().ConfigureAwait(false);
            }

            _client?.Dispose();
        }
    }
}

/// <summary>The <c>dotnet</c> host: the one running this process when it is dotnet, else from <c>DOTNET_HOST_PATH</c>, <c>DOTNET_ROOT</c> or <c>PATH</c>.</summary>
internal static class DotnetHost
{
    private static readonly Lazy<string> s_path = new(Resolve);

    public static string Path => s_path.Value;

    private static string Resolve()
    {
        var exe = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        if (Environment.ProcessPath is { } current && System.IO.Path.GetFileName(current).Equals(exe, StringComparison.OrdinalIgnoreCase))
        {
            return current;
        }

        if (Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host && File.Exists(host))
        {
            return host;
        }

        if (Environment.GetEnvironmentVariable("DOTNET_ROOT") is { Length: > 0 } root && File.Exists(System.IO.Path.Combine(root, exe)))
        {
            return System.IO.Path.Combine(root, exe);
        }

        return (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(d => System.IO.Path.Combine(d, exe))
            .FirstOrDefault(File.Exists) ?? exe;
    }
}
