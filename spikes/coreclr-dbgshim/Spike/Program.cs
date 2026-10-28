// Spike: can ClrDebug drive CoreCLR's ICorDebug through dbgshim off Windows?
// Launch a program suspended, attach at runtime startup, bind a line breakpoint, read locals and fields,
// func-eval a property getter, step over a line, run to the end and read the exit code.
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using ClrDebug;

if (args.Length < 3)
{
    Console.Error.WriteLine("usage: Spike <dbgshim path> <program (apphost)> <line> [program args]");
    return 2;
}

var shimPath = args[0];
var program = Path.GetFullPath(args[1]);
var line = int.Parse(args[2]);
var dll = Path.ChangeExtension(program, ".dll");
var symbols = new Symbols(dll);
var target = symbols.Find(line) ?? throw new Exception($"no code on line {line}");
Log($"breakpoint target: method 0x{target.Token:x8} IL_{target.Offset:x4} (line {line})");

var clock = Stopwatch.StartNew();
var shim = new DbgShim(NativeLibrary.Load(shimPath));
var holdMode = args.Length > 3 && args[3] == "--hold";
var attachMode = holdMode || (args.Length > 3 && args[3] == "--attach");
Process? running = null;
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
    running = Process.Start(info)!;
    if (holdMode)
    {
        await WaitForDiagnosticSocket(running.Id);
    }
    else if (running.StandardOutput.ReadLine() != "ready") throw new Exception("debuggee didn't start");
    _ = Task.Run(() => { while (running.StandardOutput.ReadLine() is { } l) Console.WriteLine(l); });
    pid = running.Id;
    clock.Restart();
    Log($"running: pid {pid}");
}
else
{
    launch = shim.CreateProcessForLaunch($"\"{program}\"", bSuspendProcess: true, lpEnvironment: IntPtr.Zero, lpCurrentDirectory: Path.GetDirectoryName(program));
    pid = launch.ProcessId;
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
    Log($"resume: {ResumeRuntime(pid)} after {clock.ElapsedMilliseconds} ms");
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
if (attachMode)
{
    if (registration != IntPtr.Zero) shim.UnregisterForRuntimeStartup(registration);
    running!.WaitForExit();
    Log($"exit code: {running.ExitCode}");
}
else
{
    shim.UnregisterForRuntimeStartup(registration);
    Log($"exit code: {WaitPid(pid)}");
}
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

static int WaitPid(int pid)
{
    var result = waitpid(pid, out var status, 0);
    if (result != pid) Log($"waitpid failed: errno {Marshal.GetLastPInvokeError()}, parent of {pid} is {ParentOf(pid)}");
    return result == pid ? (status >> 8) & 0xff : -1;

    static string ParentOf(int p) => File.Exists($"/proc/{p}/stat") ? File.ReadAllText($"/proc/{p}/stat").Split(' ')[3] + $" (we are {Environment.ProcessId})" : "gone";

    [DllImport("libc", SetLastError = true)]
    static extern int waitpid(int pid, out int status, int options);
}

static async Task WaitForDiagnosticSocket(int pid)
{
    for (var n = 0; n < 200 && DiagnosticSocket(pid) is null; n++)
    {
        await Task.Delay(10);
    }
    Log($"diagnostic socket: {DiagnosticSocket(pid) ?? "none"}");
}

static string? DiagnosticSocket(int pid) =>
    Directory.GetFiles(Path.GetTempPath(), $"dotnet-diagnostic-{pid}-*-socket").FirstOrDefault();

// The diagnostics IPC protocol's Process/ResumeRuntime command (dotnet/diagnostics, documentation/design-docs/ipc-protocol.md).
static string ResumeRuntime(int pid)
{
    using var socket = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.Unix, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Unspecified);
    var path = DiagnosticSocket(pid) ?? throw new InvalidOperationException($"process {pid} never opened its diagnostic socket (did its runtime start?)");
    socket.Connect(new System.Net.Sockets.UnixDomainSocketEndPoint(path));
    var header = new byte[20];
    "DOTNET_IPC_V1\0"u8.CopyTo(header);
    BitConverter.TryWriteBytes(header.AsSpan(14), (ushort)20);
    header[16] = 0x04; // Process
    header[17] = 0x01; // ResumeRuntime
    socket.Send(header);
    var response = new byte[24];
    var read = socket.Receive(response);
    return read >= 18 && response[16] == 0xFF && response[17] == 0x00 ? "OK" : $"unexpected response ({read} bytes, {response[16]:x2} {response[17]:x2})";
}

static void Log(string message) => Console.WriteLine($"[spike] {message}");

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
