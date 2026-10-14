using System.Net;
using System.Reflection;
using System.Text.Json.Nodes;
using Mono.Debugging.Client;
using Mono.Debugging.Soft;

namespace Codalus.Debugging.Mono;

/// <summary>
/// The Mono debug adapter's session (ADR 0065): Debug Adapter Protocol requests on Mono.Debugging's
/// <see cref="SoftDebuggerSession"/>. It attaches to a listening debugger agent (an Android app; iOS in M20c). The
/// mapping follows vscode-mono-debug's MonoDebugSession (Microsoft, MIT).
/// </summary>
internal sealed class MonoAdapter : IDisposable
{
    private const int MaxChildren = 200;
    private static readonly TimeSpan s_valueWait = TimeSpan.FromSeconds(5);

    private readonly DapServer _server;
    private readonly Lock _gate = new();
    private readonly ManualResetEventSlim _suspended = new(true);
    private readonly Handles<global::Mono.Debugging.Client.StackFrame> _frames = new();
    private readonly Handles<ObjectValue[]> _variables = new();
    private readonly Dictionary<string, List<(int Id, Breakpoint Breakpoint)>> _breakpoints = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Catchpoint> _catchpoints = [];
    private readonly DebuggerSessionOptions _options = new() { EvaluationOptions = EvaluationOptions.DefaultOptions.Clone() };
    private SoftDebuggerSession? _session;
    private string[] _exceptionFilters = [];
    private bool _pauseRequested;
    private bool _terminated;
    private int _nextBreakpointId = 1;
    private ExceptionInfo? _exception;
    private volatile bool _connected;

    public MonoAdapter(DapServer server)
    {
        _server = server;
        _options.EvaluationOptions.EvaluationTimeout = 5000;
        _options.EvaluationOptions.MemberEvaluationTimeout = 5000;
        _options.EvaluationOptions.AllowTargetInvoke = true;
        _options.EvaluationOptions.GroupPrivateMembers = false;
        _options.EvaluationOptions.GroupStaticMembers = true;
        DebuggerLoggingService.CustomLogger = new Logger(this);
    }

    public Task<JsonNode?> HandleAsync(string command, JsonObject arguments) => Task.Run(() => Handle(command, arguments));

    private JsonNode? Handle(string command, JsonObject arguments) => command switch
    {
        "initialize" => Initialize(),
        "attach" => Attach(arguments),
        "launch" => throw new DapError("The Mono adapter attaches to a running app: use attach."),
        "configurationDone" => null,
        "setBreakpoints" => SetBreakpoints(arguments),
        "setExceptionBreakpoints" => SetExceptionBreakpoints(arguments),
        "setFunctionBreakpoints" => new JsonObject { ["breakpoints"] = new JsonArray() },
        "threads" => Threads(),
        "stackTrace" => StackTrace(arguments),
        "scopes" => Scopes(arguments),
        "variables" => Variables(arguments),
        "evaluate" => Evaluate(arguments),
        "exceptionInfo" => ExceptionInfoOf(),
        "continue" => Resume(s => s.Continue(), new JsonObject { ["allThreadsContinued"] = true }),
        "next" => Resume(s => s.NextLine()),
        "stepIn" => Resume(s => s.StepLine()),
        "stepOut" => Resume(s => s.Finish()),
        "pause" => Pause(),
        "disconnect" => Disconnect(arguments),
        "source" => throw new DapError("No source is available for this location."),
        _ => throw new DapError($"The Mono adapter doesn't support {command}."),
    };

    /// <summary>Sent after the initialize response, as the protocol asks.</summary>
    public void AfterResponse(string command)
    {
        if (command == "initialize")
        {
            _server.Event("initialized");
        }
    }

    private static JsonObject Initialize() => new()
    {
        ["supportsConfigurationDoneRequest"] = true,
        ["supportsConditionalBreakpoints"] = true,
        ["supportsHitConditionalBreakpoints"] = true,
        ["supportsLogPoints"] = true,
        ["supportsEvaluateForHovers"] = true,
        ["supportsExceptionInfoRequest"] = true,
        ["supportsTerminateRequest"] = false,
        ["exceptionBreakpointFilters"] = new JsonArray(
            new JsonObject { ["filter"] = "all", ["label"] = "All Exceptions", ["default"] = false, ["description"] = "Break when an exception is thrown, even if it is caught later." },
            new JsonObject { ["filter"] = "uncaught", ["label"] = "Unhandled Exceptions", ["default"] = true, ["description"] = "Break on exceptions nothing handles." }),
    };

    // ------------------------------------------------------------------ attach

    private JsonNode? Attach(JsonObject arguments)
    {
        var port = Int(arguments, "port") ?? throw new DapError("attach needs the debugger agent's 'port'.");
        var address = String(arguments, "address") is { Length: > 0 } a ? a : "127.0.0.1";
        if (!IPAddress.TryParse(address, out var ip))
        {
            ip = Dns.GetHostAddresses(address).FirstOrDefault() ?? throw new DapError($"'{address}' isn't an address.");
        }

        var wait = TimeSpan.FromSeconds(Int(arguments, "connectTimeout") ?? 60);
        var greet = Bool(arguments, "greet") ?? true;
        var appName = String(arguments, "name") ?? "app";
        _options.ProjectAssembliesOnly = Bool(arguments, "justMyCode") ?? true;

        var startInfo = new SoftDebuggerStartInfo(new ListeningAgentArgs(new IPEndPoint(ip, port), appName, greet, wait));
        if (String(arguments, "assemblies") is { Length: > 0 } folder && Directory.Exists(folder))
        {
            SetAssemblies(startInfo, folder);
        }

        // Answered at once: the connection is made in the background (retried while the app starts), so the breakpoints
        // and exception settings that follow are in the session before the app's code runs.
        var session = NewSession();
        session.TargetReady += (_, _) =>
        {
            _connected = true;
            ApplyExceptionFilters();
        };
        session.ExceptionHandler = ex =>
        {
            if (!_connected)
            {
                Output("important", $"The app's debugger agent at {address}:{port} didn't answer: {ex.Message}");
                Terminated();
            }
            else
            {
                Log(ex.ToString());
            }

            return true;
        };
        lock (_gate)
        {
            _session = session;
            _suspended.Reset();
        }

        session.Run(startInfo, _options);
        _ = Task.Run(async () =>
        {
            // Mono.Debugging gives up after its attempts without always saying so: end the session then.
            await Task.Delay(wait + TimeSpan.FromSeconds(15)).ConfigureAwait(false);
            if (!_connected)
            {
                Output("important", $"The app's debugger agent at {address}:{port} didn't answer within {wait.TotalSeconds:0} seconds.");
                Terminated();
            }
        });
        return null;
    }

    /// <summary>
    /// Tells the debugger which assemblies are the user's, from the app's build output (their PDBs next to them): Just
    /// My Code, and breakpoints resolved on the debugger's side.
    /// </summary>
    private void SetAssemblies(SoftDebuggerStartInfo startInfo, string folder)
    {
        var paths = new Dictionary<string, string>();
        var names = new List<AssemblyName>();
        foreach (var assembly in Directory.EnumerateFiles(folder, "*.dll"))
        {
            if (!File.Exists(Path.ChangeExtension(assembly, ".pdb")))
            {
                continue;
            }

            try
            {
                var name = AssemblyName.GetAssemblyName(assembly);
                if (name.FullName is { Length: > 0 } full && paths.TryAdd(full, assembly))
                {
                    names.Add(name);
                }
            }
            catch (Exception ex) when (ex is BadImageFormatException or IOException)
            {
                Log($"Skipped {Path.GetFileName(assembly)}: {ex.Message}");
            }
        }

        startInfo.AssemblyPathMap = paths;
        startInfo.UserAssemblyNames = names;
    }

    private SoftDebuggerSession NewSession()
    {
        var session = new SoftDebuggerSession { Breakpoints = new BreakpointStore() };
        session.LogWriter = (_, text) => Log(text);
        session.OutputWriter = (isError, text) => Output(isError ? "stderr" : "stdout", text);
        session.TargetHitBreakpoint += (_, e) => OnStopped("breakpoint", e.Thread);
        session.TargetStopped += (_, e) => OnStopped(_pauseRequested ? "pause" : "step", e.Thread);
        session.TargetInterrupted += (_, e) => OnStopped("pause", e.Thread);
        session.TargetExceptionThrown += (_, e) => OnStopped("exception", e.Thread, exception: true);
        session.TargetUnhandledException += (_, e) => OnStopped("exception", e.Thread, exception: true);
        session.TargetExited += (_, _) => Terminated();
        session.TargetThreadStarted += (_, e) => _server.Event("thread", new JsonObject { ["reason"] = "started", ["threadId"] = (int)e.Thread.Id });
        session.TargetThreadStopped += (_, e) => _server.Event("thread", new JsonObject { ["reason"] = "exited", ["threadId"] = (int)e.Thread.Id });
        session.TargetEvent += (_, e) => Log($"target event {e.Type}");
        session.Breakpoints.BreakpointStatusChanged += (_, e) => Log($"breakpoint {e.Breakpoint.FileName}:{e.Breakpoint.Line}: {e.Breakpoint.GetStatus(session)} {e.Breakpoint.GetStatusMessage(session)}");

        // Breakpoints set before the session existed move into it.
        foreach (var list in _breakpoints.Values)
        {
            foreach (var (_, breakpoint) in list)
            {
                session.Breakpoints.Add(breakpoint);
            }
        }

        return session;
    }

    private void OnStopped(string reason, ThreadInfo? thread, bool exception = false)
    {
        _pauseRequested = false;
        _frames.Reset();
        _variables.Reset();
        _exception = null;
        string? text = null;
        if (exception && thread?.Backtrace is { FrameCount: > 0 } backtrace)
        {
            try
            {
                _exception = backtrace.GetFrame(0).GetException();
                text = _exception is null ? null : $"{_exception.Type}: {_exception.Message}";
            }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
            {
                Log("The exception couldn't be read: " + ex.Message);
            }
        }

        _suspended.Set();
        _server.Event("stopped", new JsonObject
        {
            ["reason"] = reason,
            ["threadId"] = thread is null ? null : (int)thread.Id,
            ["allThreadsStopped"] = true,
            ["text"] = text,
            ["description"] = text,
        });
    }

    private void Terminated()
    {
        lock (_gate)
        {
            if (_terminated)
            {
                return;
            }

            _terminated = true;
        }

        _suspended.Set();
        _server.Event("exited", new JsonObject { ["exitCode"] = 0 });
        _server.Event("terminated");
    }

    // ------------------------------------------------------------------ breakpoints

    private JsonObject SetBreakpoints(JsonObject arguments)
    {
        var path = (arguments["source"] as JsonObject)?["path"]?.GetValue<string>() ?? throw new DapError("setBreakpoints needs a source path.");
        var wanted = arguments["breakpoints"] as JsonArray ?? [];
        lock (_gate)
        {
            if (_breakpoints.Remove(path, out var old))
            {
                foreach (var (_, breakpoint) in old)
                {
                    _session?.Breakpoints.Remove(breakpoint);
                }
            }

            var added = new List<(int, Breakpoint)>();
            var result = new JsonArray();
            foreach (var node in wanted.OfType<JsonObject>())
            {
                var line = node["line"]?.GetValue<int>() ?? 0;
                var breakpoint = new Breakpoint(path, line, node["column"]?.GetValue<int>() ?? 1);
                if (String(node, "condition") is { Length: > 0 } condition)
                {
                    breakpoint.ConditionExpression = condition;
                }

                if (String(node, "hitCondition") is { Length: > 0 } hit && int.TryParse(hit.TrimStart('>', '=', ' '), out var count))
                {
                    breakpoint.HitCountMode = hit.StartsWith("==", StringComparison.Ordinal) ? HitCountMode.EqualTo
                        : hit.StartsWith('%') ? HitCountMode.MultipleOf
                        : HitCountMode.GreaterThanOrEqualTo;
                    breakpoint.HitCount = count;
                }

                if (String(node, "logMessage") is { Length: > 0 } message)
                {
                    breakpoint.HitAction = HitAction.PrintExpression;
                    breakpoint.TraceExpression = message;
                }

                var id = _nextBreakpointId++;
                added.Add((id, breakpoint));
                _session?.Breakpoints.Add(breakpoint);
                result.Add(new JsonObject { ["id"] = id, ["verified"] = true, ["line"] = line });
            }

            _breakpoints[path] = added;
            return new JsonObject { ["breakpoints"] = result };
        }
    }

    private JsonObject? SetExceptionBreakpoints(JsonObject arguments)
    {
        _exceptionFilters = [.. (arguments["filters"] as JsonArray ?? []).Select(f => f?.GetValue<string>()).OfType<string>()];
        ApplyExceptionFilters();
        return null;
    }

    private void ApplyExceptionFilters()
    {
        lock (_gate)
        {
            if (_session is not { } session)
            {
                return;
            }

            foreach (var catchpoint in _catchpoints)
            {
                session.Breakpoints.Remove(catchpoint);
            }

            _catchpoints.Clear();
            if (_exceptionFilters.Contains("all"))
            {
                _catchpoints.Add(session.Breakpoints.AddCatchpoint("System.Exception", includeSubclasses: true));
            }

            // Unhandled exceptions stop the session whatever the filters say (Mono raises TargetUnhandledException).
        }
    }

    // ------------------------------------------------------------------ inspecting

    private JsonObject Threads()
    {
        WaitForSuspend();
        var threads = new JsonArray();
        if (_session?.GetProcesses().FirstOrDefault() is { } process)
        {
            foreach (var thread in process.GetThreads())
            {
                threads.Add(new JsonObject { ["id"] = (int)thread.Id, ["name"] = thread.Name is { Length: > 0 } name ? name : thread.Id == 1 ? "Main Thread" : $"Thread #{thread.Id}" });
            }
        }

        return new JsonObject { ["threads"] = threads };
    }

    private ThreadInfo FindThread(int id) =>
        _session?.GetProcesses().FirstOrDefault()?.GetThreads().FirstOrDefault(t => t.Id == id) ?? _session?.ActiveThread
        ?? throw new DapError($"There is no thread {id}.");

    private JsonObject StackTrace(JsonObject arguments)
    {
        WaitForSuspend();
        var thread = FindThread(Int(arguments, "threadId") ?? 0);
        var start = Int(arguments, "startFrame") ?? 0;
        var levels = Int(arguments, "levels") is > 0 and var l ? l : 100;
        var frames = new JsonArray();
        var backtrace = thread.Backtrace;
        var total = backtrace?.FrameCount ?? 0;
        for (var i = start; backtrace is not null && i < Math.Min(total, start + levels); i++)
        {
            global::Mono.Debugging.Client.StackFrame frame;
            try
            {
                frame = backtrace.GetFrame(i);
            }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
            {
                Log($"Frame {i} couldn't be read: {ex.Message}");
                continue;
            }

            var location = frame.SourceLocation;
            var file = location.FileName;
            var hasSource = !string.IsNullOrEmpty(file) && File.Exists(file);
            frames.Add(new JsonObject
            {
                ["id"] = _frames.Create(frame),
                ["name"] = location.MethodName ?? "?",
                ["source"] = hasSource ? new JsonObject { ["name"] = Path.GetFileName(file), ["path"] = file } : null,
                ["line"] = hasSource ? location.Line : 0,
                ["column"] = hasSource ? Math.Max(1, location.Column) : 0,
                ["presentationHint"] = hasSource && !frame.IsExternalCode ? "normal" : "subtle",
            });
        }

        return new JsonObject { ["stackFrames"] = frames, ["totalFrames"] = total };
    }

    private JsonObject Scopes(JsonObject arguments)
    {
        var frame = _frames.Get(Int(arguments, "frameId") ?? 0) ?? throw new DapError("That stack frame is gone.");
        var scopes = new JsonArray();
        if (frame.Index == 0 && _exception?.Instance is { } exception)
        {
            scopes.Add(new JsonObject { ["name"] = "Exception", ["variablesReference"] = _variables.Create([exception]), ["expensive"] = false });
        }

        var locals = new[] { frame.GetThisReference() }.Concat(frame.GetParameters()).Concat(frame.GetLocalVariables()).Where(v => v is not null).ToArray();
        scopes.Add(new JsonObject { ["name"] = "Locals", ["presentationHint"] = "locals", ["variablesReference"] = _variables.Create(locals), ["expensive"] = false });
        return new JsonObject { ["scopes"] = scopes };
    }

    private JsonObject Variables(JsonObject arguments)
    {
        WaitForSuspend();
        var variables = new JsonArray();
        if (_variables.Get(Int(arguments, "variablesReference") ?? 0) is { } values)
        {
            var shown = values.Take(MaxChildren).ToArray();
            WaitFor(shown);
            foreach (var value in shown)
            {
                variables.Add(Variable(value));
            }

            if (values.Length > MaxChildren)
            {
                variables.Add(new JsonObject { ["name"] = "…", ["value"] = $"{values.Length - MaxChildren} more", ["variablesReference"] = 0 });
            }
        }

        return new JsonObject { ["variables"] = variables };
    }

    private JsonObject Variable(ObjectValue value) => new()
    {
        ["name"] = value.Name,
        ["value"] = Display(value),
        ["type"] = value.TypeName,
        ["evaluateName"] = value.ChildSelector is { Length: > 0 } ? null : value.Name,
        ["variablesReference"] = value.HasChildren ? _variables.Create(Children(value)) : 0,
    };

    private static ObjectValue[] Children(ObjectValue value)
    {
        try
        {
            return value.GetAllChildren();
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or TimeoutException)
        {
            return [];
        }
    }

    private static string Display(ObjectValue value)
    {
        var text = value.DisplayValue ?? value.Value ?? string.Empty;
        return text.Length > 1 && text[0] == '{' && text[^1] == '}' ? text[1..^1] : text;
    }

    private static void WaitFor(IEnumerable<ObjectValue> values)
    {
        foreach (var value in values)
        {
            value.WaitHandle.WaitOne(s_valueWait);
        }
    }

    private JsonObject Evaluate(JsonObject arguments)
    {
        var expression = String(arguments, "expression") ?? throw new DapError("Nothing to evaluate.");
        WaitForSuspend();
        var frame = Int(arguments, "frameId") is { } id ? _frames.Get(id) : null;
        frame ??= _session?.ActiveThread?.Backtrace is { FrameCount: > 0 } bt ? bt.GetFrame(0) : null;
        if (frame is null)
        {
            throw new DapError("The program isn't stopped.");
        }

        var value = frame.GetExpressionValue(expression, _options.EvaluationOptions);
        value.WaitHandle.WaitOne(s_valueWait);
        var flags = value.Flags;
        if (flags.HasFlag(ObjectValueFlags.Error) || flags.HasFlag(ObjectValueFlags.NotSupported) || flags.HasFlag(ObjectValueFlags.Unknown))
        {
            throw new DapError(value.DisplayValue is { Length: > 0 } message ? message : $"'{expression}' can't be evaluated here.");
        }

        return new JsonObject
        {
            ["result"] = Display(value),
            ["type"] = value.TypeName,
            ["variablesReference"] = value.HasChildren ? _variables.Create(Children(value)) : 0,
        };
    }

    private JsonObject ExceptionInfoOf()
    {
        var exception = _exception ?? throw new DapError("The program didn't stop on an exception.");
        return new JsonObject
        {
            ["exceptionId"] = exception.Type,
            ["description"] = exception.Message,
            ["breakMode"] = "always",
            ["details"] = new JsonObject
            {
                ["message"] = exception.Message,
                ["typeName"] = exception.Type,
                ["fullTypeName"] = exception.Type,
                ["stackTrace"] = string.Join('\n', exception.StackTrace?.Select(f => $"   at {f.DisplayText}{(f.File is { Length: > 0 } file ? $" in {file}:line {f.Line}" : string.Empty)}") ?? []),
                ["innerException"] = exception.InnerException is { } inner ? new JsonArray(new JsonObject { ["typeName"] = inner.Type, ["message"] = inner.Message }) : null,
            },
        };
    }

    // ------------------------------------------------------------------ running

    private JsonObject? Resume(Action<SoftDebuggerSession> action, JsonObject? body = null)
    {
        WaitForSuspend();
        lock (_gate)
        {
            if (_session is { IsRunning: false, HasExited: false } session)
            {
                _suspended.Reset();
                action(session);
            }
        }

        return body;
    }

    private JsonObject? Pause()
    {
        lock (_gate)
        {
            if (_session is { IsRunning: true } session)
            {
                _pauseRequested = true;
                session.Stop();
            }
        }

        return null;
    }

    private JsonObject? Disconnect(JsonObject arguments)
    {
        SoftDebuggerSession? session;
        lock (_gate)
        {
            session = _session;
            _session = null;
        }

        if (session is not null)
        {
            try
            {
                if (Bool(arguments, "terminateDebuggee") == true)
                {
                    session.Exit();
                }
                else
                {
                    // Detach: the app goes on without breakpoints.
                    session.Breakpoints.Clear();
                    if (!session.IsRunning && !session.HasExited)
                    {
                        session.Continue();
                    }
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or IOException)
            {
                Log("Disconnecting: " + ex.Message);
            }

            session.Dispose();
        }

        _suspended.Set();
        return null;
    }

    /// <summary>Requests that read the program's state wait until it has stopped (the stopped event comes first).</summary>
    private void WaitForSuspend() => _suspended.Wait(TimeSpan.FromSeconds(10));

    // ------------------------------------------------------------------ helpers

    private void Output(string category, string text)
    {
        if (!string.IsNullOrEmpty(text))
        {
            _server.Event("output", new JsonObject { ["category"] = category, ["output"] = text.EndsWith('\n') ? text : text + "\n" });
        }
    }

    private void Log(string text)
    {
        if (Environment.GetEnvironmentVariable("CODALUS_MONODBG_LOGFILE") is { Length: > 0 })
        {
            Console.Out.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {text.TrimEnd()}");
        }

        if (Environment.GetEnvironmentVariable("CODALUS_MONODBG_LOG") is { Length: > 0 })
        {
            Output("console", "[monodbg] " + text.TrimEnd());
        }
    }

    private static string? String(JsonObject node, string name) =>
        node[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static int? Int(JsonObject node, string name) =>
        node[name] is JsonValue value ? value.TryGetValue<int>(out var number) ? number : value.TryGetValue<double>(out var d) ? (int)d : null : null;

    private static bool? Bool(JsonObject node, string name) =>
        node[name] is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : null;

    public void Dispose()
    {
        _session?.Dispose();
        _suspended.Dispose();
    }

    private sealed class Logger(MonoAdapter adapter) : ICustomLogger
    {
        public string GetNewDebuggerLogFilename() => string.Empty;

        public void LogAndShowException(string message, Exception ex) => adapter.Log($"{message}: {ex}");

        public void LogError(string message, Exception ex) => adapter.Log($"{message}: {ex}");

        public void LogMessage(string messageFormat, params object[] args) => adapter.Log(args.Length == 0 ? messageFormat : string.Format(System.Globalization.CultureInfo.InvariantCulture, messageFormat, args));
    }
}

/// <summary>Numbers for objects the client refers back to (frames, variable lists); reset whenever the program stops.</summary>
internal sealed class Handles<T>
    where T : class
{
    private readonly Lock _gate = new();
    private readonly Dictionary<int, T> _items = [];
    private int _next = 1000;

    public int Create(T item)
    {
        lock (_gate)
        {
            _items[++_next] = item;
            return _next;
        }
    }

    public T? Get(int id)
    {
        lock (_gate)
        {
            return _items.GetValueOrDefault(id);
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _items.Clear();
        }
    }
}
