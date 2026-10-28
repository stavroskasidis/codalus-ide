// Spike: can ClrDebug drive CoreCLR's ICorDebug through dbgshim off Windows?
// Launch a program suspended, attach at runtime startup, bind a line breakpoint, read locals and fields,
// func-eval a property getter, step over a line, run to the end and read the exit code.
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using ClrDebug;

if (args.Length > 0 && args[0] == "--launcher-process")
{
    return Launcher.Run(args[1..]);
}
if (args.Length < 3)
{
    Console.Error.WriteLine("usage: Spike <dbgshim path> <program (apphost)> <line> [--attach|--hold|--hook|--launcher]");
    return 2;
}

var shimPath = Path.GetFullPath(args[0]);
var program = Path.GetFullPath(args[1]);
if (OperatingSystem.IsWindows() && !Path.HasExtension(program)) program += ".exe";
var line = int.Parse(args[2]);
var dll = Path.ChangeExtension(program, ".dll");
var symbols = new Symbols(dll);
var target = symbols.Find(line) ?? throw new Exception($"no code on line {line}");
Log($"breakpoint target: method 0x{target.Token:x8} IL_{target.Offset:x4} (line {line})");

var clock = Stopwatch.StartNew();
var shim = new DbgShim(NativeLibrary.Load(shimPath));
var mode = args.Length > 3 ? args[3] : "launch";
// --hold and --hook: started by us, held at runtime startup by the diagnostic port; --hook adds candidate (b)'s startup hook.
var holdMode = mode is "--hold" or "--hook";
var attachMode = holdMode || mode == "--attach";
// --launcher: candidate (a), a launcher process of ours starts the program through dbgshim and reports its exit code.
var launcherMode = mode == "--launcher";
Process? running = null;
Task forward = Task.CompletedTask;
StreamReader? control = null;
StreamWriter? controlWriter = null;
ChildExit? launchedExit = null;
int pid;
CreateProcessForLaunchResult launch = default!;
if (attachMode)
{
    // Attach to a program that is already running (it waits on stdin for us).
    var info = new ProcessStartInfo(program, holdMode ? "" : "wait") { RedirectStandardInput = true, RedirectStandardOutput = true };
    if (holdMode)
    {
        // The runtime holds before running any managed code until a diagnostics client resumes it.
        info.Environment["DOTNET_DefaultDiagnosticPortSuspend"] = "1";
    }
    if (mode == "--hook")
    {
        info.Environment["DOTNET_STARTUP_HOOKS"] = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../Hook/bin/Debug/netstandard2.0/Hook.dll"));
    }
    running = Process.Start(info)!;
    if (holdMode)
    {
        Log($"diagnostic port: {await DiagnosticIpc.WaitForPort(running.Id) ?? "none"} after {clock.ElapsedMilliseconds} ms");
    }
    else if (running.StandardOutput.ReadLine() != "ready") throw new Exception("debuggee didn't start");
    forward = Forward(running.StandardOutput);
    pid = running.Id;
    Log($"running: pid {pid}");
}
else if (launcherMode)
{
    var pipeName = $"spike-launcher-{Environment.ProcessId}";
    var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
    var spike = typeof(Symbols).Assembly.Location;
    var info = new ProcessStartInfo(Environment.ProcessPath!, [spike, "--launcher-process", shimPath, pipeName, program]) { RedirectStandardInput = true, RedirectStandardOutput = true };
    running = Process.Start(info)!;
    forward = Forward(running.StandardOutput);
    await server.WaitForConnectionAsync().WaitAsync(TimeSpan.FromSeconds(30));
    control = new StreamReader(server);
    controlWriter = new StreamWriter(server) { AutoFlush = true };
    var report = await control.ReadLineAsync() ?? throw new Exception("the launcher ended before reporting a pid");
    pid = int.Parse(report.Split(' ')[1]);
    Log($"launcher {running.Id} started the program suspended: pid {pid} after {clock.ElapsedMilliseconds} ms");
}
else
{
    launch = shim.CreateProcessForLaunch($"\"{program}\"", bSuspendProcess: true, lpEnvironment: IntPtr.Zero, lpCurrentDirectory: Path.GetDirectoryName(program));
    pid = launch.ProcessId;
    launchedExit = new ChildExit(pid);
    Log($"launched suspended: pid {pid}");
}

var events = new BlockingCollection<CorDebugManagedCallbackEventArgs>();
var callback = new CorDebugManagedCallback();
CorDebugProcess? process = null;
CorDebugModule? module = null;
CorDebugFunctionBreakpoint? bound = null;
var attached = new TaskCompletionSource<CorDebugProcess>(TaskCreationOptions.RunContinuationsAsynchronously);

callback.OnAnyEvent += (_, e) =>
{
    if (Environment.GetEnvironmentVariable("SPIKE_TRACE") is not null) Log($"  event {e.Kind} (continue={e.Continue})");
    switch (e)
    {
        case LoadModuleCorDebugManagedCallbackEventArgs load:
            if (string.Equals(Path.GetFileName(load.Module.Name), Path.GetFileName(dll), StringComparison.OrdinalIgnoreCase))
            {
                module = load.Module;
                bound = module.GetFunctionFromToken((mdMethodDef)target.Token).ILCode.CreateBreakpoint(target.Offset);
                bound.Activate(true);
                Log($"module loaded, breakpoint bound: {load.Module.Name}");
            }
            break;
        case BreakpointCorDebugManagedCallbackEventArgs:
        case StepCompleteCorDebugManagedCallbackEventArgs:
        case EvalCompleteCorDebugManagedCallbackEventArgs:
        case EvalExceptionCorDebugManagedCallbackEventArgs:
        case ExitProcessCorDebugManagedCallbackEventArgs:
            e.Continue = false;
            events.Add(e); // the main thread decides when to continue
            return;
    }
    e.Continue = false;
    e.Controller.Continue(false);
};

// The runtime calls back once it has started (or failed to); ICorDebug comes from the debuggee's own runtime.
var startup = new PSTARTUP_CALLBACK((pCordb, _, hr) =>
{
    try
    {
        if (hr != HRESULT.S_OK)
        {
            attached.TrySetException(new Exception($"runtime startup callback failed: {hr}"));
            return;
        }
        var corDebug = new CorDebug(Extensions.GetObjectForIUnknown<ICorDebug>(pCordb));
        corDebug.Initialize();
        corDebug.SetManagedHandler(callback);
        attached.TrySetResult(corDebug.DebugActiveProcess(pid, win32Attach: false));
    }
    catch (Exception ex)
    {
        attached.TrySetException(ex);
    }
});
IntPtr registration = IntPtr.Zero;
if (holdMode)
{
    // Held by the diagnostic port: register for the runtime's startup, then let it start.
    registration = shim.RegisterForRuntimeStartup(pid, startup, IntPtr.Zero);
    // SPIKE_RESUME_DELAY_MS: an adapter slow to resume (the runtime prints its notice after 5 s held).
    if (int.TryParse(Environment.GetEnvironmentVariable("SPIKE_RESUME_DELAY_MS"), out var delay)) await Task.Delay(delay);
    Log($"resume: {DiagnosticIpc.ResumeRuntime(pid)} after {clock.ElapsedMilliseconds} ms");
    process = await attached.Task.WaitAsync(TimeSpan.FromSeconds(30));
    Log($"attached after {clock.ElapsedMilliseconds} ms");
}
else if (attachMode)
{
    // The runtime that is loaded: its module gives the version string that picks its own mscordbi.
    var clrs = shim.EnumerateCLRs(pid);
    var runtimeModule = clrs.Items[0].Path;
    var version = shim.CreateVersionStringFromModule(pid, runtimeModule);
    shim.CloseCLREnumeration(clrs);
    var corDebug = shim.CreateDebuggingInterfaceFromVersion(version);
    corDebug.Initialize();
    corDebug.SetManagedHandler(callback);
    process = corDebug.DebugActiveProcess(pid, win32Attach: false);
    Log($"attached to the running process after {clock.ElapsedMilliseconds} ms ({runtimeModule})");
    running!.StandardInput.WriteLine(); // let it go on to the breakpoint
}
else if (launcherMode)
{
    // Registered for the program's runtime startup before the launcher lets it run.
    registration = shim.RegisterForRuntimeStartup(pid, startup, IntPtr.Zero);
    await controlWriter!.WriteLineAsync("resume");
    process = await attached.Task.WaitAsync(TimeSpan.FromSeconds(30));
    Log($"attached at runtime startup after {clock.ElapsedMilliseconds} ms");
}
else
{
    registration = shim.RegisterForRuntimeStartup(pid, startup, IntPtr.Zero);
    shim.ResumeProcess(launch.ResumeHandle);
    shim.CloseResumeHandle(launch.ResumeHandle);
    process = await attached.Task.WaitAsync(TimeSpan.FromSeconds(30));
    Log($"attached at runtime startup after {clock.ElapsedMilliseconds} ms");
}

// 1. The breakpoint.
var hit = (BreakpointCorDebugManagedCallbackEventArgs)Next();
var thread = hit.Thread;
Log($"breakpoint hit on thread {thread.Id} at line {CurrentLine(thread)} after {clock.ElapsedMilliseconds} ms");

if (Environment.GetEnvironmentVariable("SPIKE_HIT_ONLY") is null)
{
// 2. Locals by the PDB's names, and the object's fields.
var frame = (CorDebugILFrame)thread.ActiveFrame;
var names = symbols.Locals(target.Token, frame.IP.pnOffset);
foreach (var (slot, name) in names)
{
    Log($"  local {name} = {Describe(frame.GetLocalVariable(slot))}");
}
var orderSlot = names.First(n => n.Name == "order").Slot;
var orderObject = (CorDebugObjectValue)((CorDebugReferenceValue)frame.GetLocalVariable(orderSlot)).Dereference();
foreach (var (field, token) in symbols.Fields("Order"))
{
    Log($"  order.{field} = {Describe(orderObject.GetFieldValue(orderObject.Class.Raw, (mdFieldDef)token))}");
}

// 3. Func-eval of a property getter (order.Label).
var getter = module!.GetFunctionFromToken((mdMethodDef)symbols.Method("Order", "get_Label"));
var eval = thread.CreateEval();
eval.CallFunction(getter.Raw, 1, [frame.GetLocalVariable(orderSlot).Raw]);
process.Continue(false);
var evalDone = Next();
Log(evalDone is EvalCompleteCorDebugManagedCallbackEventArgs
    ? $"  func-eval order.Label = {Describe(eval.Result)}"
    : "  func-eval order.Label threw");

// 4. Step over the current line (twice: the loop body, then onto the next line).
for (var n = 0; n < 2; n++)
{
    frame = (CorDebugILFrame)thread.ActiveFrame; // frames are neutered once the process runs
    var range = symbols.Range((int)frame.FunctionToken.Value, frame.IP.pnOffset);
    var stepper = frame.CreateStepper();
    stepper.SetRangeIL(true);
    stepper.StepRange(false, [new COR_DEBUG_STEP_RANGE { startOffset = range.Start, endOffset = range.End }], 1);
    process.Continue(false);
    var step = Next();
    thread = ((StepCompleteCorDebugManagedCallbackEventArgs)step).Thread;
    frame = (CorDebugILFrame)thread.ActiveFrame;
    var totalSlot = symbols.Locals(target.Token, frame.IP.pnOffset).First(l => l.Name == "total").Slot;
    Log($"step over -> line {CurrentLine(thread)}, total = {Describe(frame.GetLocalVariable(totalSlot))}");
}

}

// 5. Deactivate the breakpoint and run to the end.
bound!.Activate(false);
process.Continue(false);
var exit = Next();
Log($"process exited ({exit.GetType().Name}) after {clock.ElapsedMilliseconds} ms");
if (registration != IntPtr.Zero) shim.UnregisterForRuntimeStartup(registration);
int exitCode;
if (launcherMode)
{
    var report = await control!.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30));
    exitCode = report is { } r && r.StartsWith("exit ") ? int.Parse(r[5..]) : -1;
    running!.WaitForExit();
    Log($"launcher exited with {running.ExitCode}");
}
else if (attachMode)
{
    running!.WaitForExit();
    exitCode = running.ExitCode;
}
else
{
    exitCode = launchedExit!.Wait(Log);
}
await forward.WaitAsync(TimeSpan.FromSeconds(10));
Log($"exit code: {exitCode}");
return 0;

CorDebugManagedCallbackEventArgs Next()
{
    if (!events.TryTake(out var e, TimeSpan.FromSeconds(30)))
    {
        throw new TimeoutException("no debug event in 30 s");
    }
    return e;
}

int CurrentLine(CorDebugThread t)
{
    var f = (CorDebugILFrame)t.ActiveFrame;
    return symbols.Line((int)f.FunctionToken.Value, f.IP.pnOffset);
}

// The program's output, passed on as it comes (all of it before the exit code is printed).
static Task Forward(StreamReader output) => Task.Run(() =>
{
    while (output.ReadLine() is { } l) Console.WriteLine(l);
});

static string Describe(CorDebugValue value)
{
    switch (value)
    {
        case CorDebugReferenceValue reference:
            return reference.IsNull ? "null" : Describe(reference.Dereference());
        case CorDebugStringValue s:
            return $"\"{s.GetString(s.Length)}\"";
        case CorDebugBoxValue box:
            return Describe(box.Object);
        case CorDebugObjectValue o when o.Class is { } c:
            return $"{{object of class 0x{(int)c.Token:x8}}}";
        case CorDebugGenericValue g when value.Type is CorElementType.I4:
            var buffer = Marshal.AllocHGlobal(4);
            try
            {
                g.GetValue(buffer);
                return Marshal.ReadInt32(buffer).ToString();
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        default:
            return $"<{value.Type}>";
    }
}

static void Log(string message) => Console.WriteLine($"[spike] {message}");

/// <summary>
/// Candidate (a) of ADR 0086's launch: this process starts the program through dbgshim, suspended, so the program is
/// its child and not the debugger's (mscordbi in the debugger reaps its own children off Windows). Over the pipe the
/// debugger names: "pid N", then it waits for "resume" (the debugger has registered for the runtime's startup), then
/// "exit C" once the program has ended. The program inherits this process's standard handles and environment.
/// </summary>
static class Launcher
{
    public static int Run(string[] args)
    {
        var (shimPath, pipeName, program) = (args[0], args[1], args[2]);
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut);
        pipe.Connect(10_000);
        var reader = new StreamReader(pipe);
        var writer = new StreamWriter(pipe) { AutoFlush = true };
        var shim = new DbgShim(NativeLibrary.Load(shimPath));
        var launch = shim.CreateProcessForLaunch($"\"{program}\"", bSuspendProcess: true, lpEnvironment: IntPtr.Zero, lpCurrentDirectory: Path.GetDirectoryName(program));
        var exit = new ChildExit(launch.ProcessId);
        writer.WriteLine($"pid {launch.ProcessId}");
        if (reader.ReadLine() != "resume")
        {
            Process.GetProcessById(launch.ProcessId).Kill(); // the debugger went away
            return 1;
        }
        shim.ResumeProcess(launch.ResumeHandle);
        shim.CloseResumeHandle(launch.ResumeHandle);
        var code = exit.Wait(m => Console.Error.WriteLine($"[launcher] {m}"));
        writer.WriteLine($"exit {code}");
        return code;
    }
}

/// <summary>Portable PDB and metadata reading through System.Reflection.Metadata, as ClrModuleInfo does.</summary>
sealed class Symbols
{
    private readonly MetadataReader _metadata;
    private readonly MetadataReader _pdb;

    public Symbols(string dll)
    {
        var pe = new PEReader(File.OpenRead(dll));
        _metadata = pe.GetMetadataReader();
        _pdb = MetadataReaderProvider.FromPortablePdbStream(File.OpenRead(Path.ChangeExtension(dll, ".pdb"))).GetMetadataReader();
    }

    public (int Token, int Offset)? Find(int line)
    {
        foreach (var handle in _pdb.MethodDebugInformation)
        {
            foreach (var point in _pdb.GetMethodDebugInformation(handle).GetSequencePoints())
            {
                if (!point.IsHidden && point.StartLine == line)
                {
                    return (MetadataTokens.GetToken(handle.ToDefinitionHandle()), point.Offset);
                }
            }
        }
        return null;
    }

    public int Line(int token, int offset) => Point(token, offset).StartLine;

    public (int Start, int End) Range(int token, int offset)
    {
        var points = Points(token);
        var index = points.FindLastIndex(p => p.Offset <= offset);
        var start = points[index].Offset;
        var end = index + 1 < points.Count ? points[index + 1].Offset : int.MaxValue;
        return (start, end);
    }

    public List<(int Slot, string Name)> Locals(int token, int offset)
    {
        var method = MetadataTokens.MethodDefinitionHandle(token & 0xffffff);
        var result = new List<(int, string)>();
        foreach (var scopeHandle in _pdb.GetLocalScopes(method))
        {
            var scope = _pdb.GetLocalScope(scopeHandle);
            if (offset < scope.StartOffset || offset >= scope.EndOffset)
            {
                continue;
            }
            foreach (var variableHandle in scope.GetLocalVariables())
            {
                var variable = _pdb.GetLocalVariable(variableHandle);
                if ((variable.Attributes & LocalVariableAttributes.DebuggerHidden) == 0)
                {
                    result.Add((variable.Index, _pdb.GetString(variable.Name)));
                }
            }
        }
        return result;
    }

    public IEnumerable<(string Name, int Token)> Fields(string type)
    {
        var definition = Type(type);
        foreach (var handle in definition.GetFields())
        {
            yield return (_metadata.GetString(_metadata.GetFieldDefinition(handle).Name), MetadataTokens.GetToken(handle));
        }
    }

    public int Method(string type, string name) =>
        MetadataTokens.GetToken(Type(type).GetMethods().First(m => _metadata.GetString(_metadata.GetMethodDefinition(m).Name) == name));

    private TypeDefinition Type(string name) =>
        _metadata.GetTypeDefinition(_metadata.TypeDefinitions.First(t => _metadata.GetString(_metadata.GetTypeDefinition(t).Name) == name));

    private SequencePoint Point(int token, int offset)
    {
        var points = Points(token);
        return points[points.FindLastIndex(p => p.Offset <= offset)];
    }

    private List<SequencePoint> Points(int token) =>
        _pdb.GetMethodDebugInformation(MetadataTokens.MethodDebugInformationHandle(token & 0xffffff)).GetSequencePoints().Where(p => !p.IsHidden).ToList();
}
