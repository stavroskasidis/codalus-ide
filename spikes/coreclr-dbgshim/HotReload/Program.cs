// Hot reload spike: apply a Roslyn delta to a program under the debugger through ICorDebugModule2::ApplyChanges
// (the runtime refuses MetadataUpdater.ApplyUpdate while a debugger is attached, ADR 0040).
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Text;
using ClrDebug;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;

const string V1 = """
using System;
using System.Threading;
static class Program
{
    static int Main()
    {
        if (Environment.GetEnvironmentVariable("WAIT") == "1") { Console.WriteLine("ready"); Console.ReadLine(); }
        for (int i = 1; i <= 4; i++)
        {
            Console.WriteLine($"iteration {i}: {Message()}");
            Thread.Sleep(20);
        }
        return 7;
    }
    static string Message()
    {
        return "old";
    }
}
""";
// The edit: a new local and one more line, so the method's lines move.
const string V2 = """
using System;
using System.Threading;
static class Program
{
    static int Main()
    {
        if (Environment.GetEnvironmentVariable("WAIT") == "1") { Console.WriteLine("ready"); Console.ReadLine(); }
        for (int i = 1; i <= 4; i++)
        {
            Console.WriteLine($"iteration {i}: {Message()}");
            Thread.Sleep(20);
        }
        return 7;
    }
    static string Message()
    {
        var text = "new";
        return text + "!";
    }
}
""";
const int CallLine = 10;      // Console.WriteLine($"iteration {i}: {Message()}");
const int EditedLine = 18;   // return text + "!";  (only exists after the edit)

var shimPath = args[0];
var modifiable = !args.Contains("--not-modifiable");
var setEncFlag = !args.Contains("--no-enc-flag");
var late = args.Contains("--late");
var work = Path.Combine(Path.GetTempPath(), "hotreload-spike-" + Environment.ProcessId);
Directory.CreateDirectory(work);
var dll = Path.Combine(work, "App.dll");

// ---- Build v1 with Roslyn, as the build would (Debug, portable PDB with EnC information).
var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
    .Where(p => Path.GetFileName(p) is var n && (n.StartsWith("System.") || n is "netstandard.dll" or "mscorlib.dll"))
    .Select(p => MetadataReference.CreateFromFile(p)).ToArray();
var options = new CSharpCompilationOptions(OutputKind.ConsoleApplication, optimizationLevel: OptimizationLevel.Debug, deterministic: true);
var parse = new CSharpParseOptions(LanguageVersion.Latest);
var tree1 = CSharpSyntaxTree.ParseText(V1, parse, path: Path.Combine(work, "Program.cs"), encoding: Encoding.UTF8);
var compilation1 = CSharpCompilation.Create("App", [tree1], references, options);
using (var pe = File.Create(dll))
using (var pdb = File.Create(Path.ChangeExtension(dll, ".pdb")))
{
    var emitted = compilation1.Emit(pe, pdb, options: new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb, pdbFilePath: Path.ChangeExtension(dll, ".pdb")));
    if (!emitted.Success) throw new Exception(string.Join("\n", emitted.Diagnostics));
}
File.WriteAllText(Path.ChangeExtension(dll, ".runtimeconfig.json"),
    """{"runtimeOptions":{"tfm":"net10.0","framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"}}}""");
var symbols = new Symbols(File.ReadAllBytes(dll), File.ReadAllBytes(Path.ChangeExtension(dll, ".pdb")));
var mainToken = symbols.MethodToken("Main");
var messageToken = symbols.MethodToken("Message");
var callOffset = symbols.Offset(mainToken, CallLine);
Log($"built {dll}; Main 0x{mainToken:x8}, Message 0x{messageToken:x8}; DOTNET_MODIFIABLE_ASSEMBLIES={(modifiable ? "debug" : "(unset)")}");

// ---- Launch held at runtime startup, register, resume, attach (the spike's --hold path).
var clock = Stopwatch.StartNew();
var shim = new DbgShim(NativeLibrary.Load(shimPath));
var info = new ProcessStartInfo("dotnet", $"\"{dll}\"") { RedirectStandardOutput = true, RedirectStandardInput = true };
if (late) info.Environment["WAIT"] = "1"; else info.Environment["DOTNET_DefaultDiagnosticPortSuspend"] = "1";
if (modifiable)
{
    info.Environment["DOTNET_MODIFIABLE_ASSEMBLIES"] = "debug";
}
var running = Process.Start(info)!;
var output = new BlockingCollection<string>();
var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
_ = Task.Run(() => { while (running.StandardOutput.ReadLine() is { } l) { if (l == "ready") ready.TrySetResult(); else if (!l.StartsWith("DOTNET_") && !l.StartsWith("The runtime has been configured")) { Console.WriteLine("  | " + l); output.Add(l); } } });

var events = new BlockingCollection<CorDebugManagedCallbackEventArgs>();
var callback = new CorDebugManagedCallback();
CorDebugModule? module = null;
CorDebugFunctionBreakpoint? callBreakpoint = null;
callback.OnAnyEvent += (_, e) =>
{
    e.Continue = false;
    if (e is LoadModuleCorDebugManagedCallbackEventArgs load && Path.GetFileName(load.Module.Name) == "App.dll")
    {
        module = load.Module;
        // Edit and Continue has to be asked for as the module loads, before its code is jitted.
        var hr = setEncFlag ? module.TrySetJITCompilerFlags(CorDebugJITCompilerFlags.CORDEBUG_JIT_ENABLE_ENC).ToString() : "not asked";
        Log($"App.dll loaded; JIT flags ENABLE_ENC: {hr}, now {module.JITCompilerFlags}");
        callBreakpoint = module.GetFunctionFromToken((mdMethodDef)mainToken).ILCode.CreateBreakpoint(callOffset);
        callBreakpoint.Activate(true);
    }
    if (e is BreakpointCorDebugManagedCallbackEventArgs or ExitProcessCorDebugManagedCallbackEventArgs)
    {
        events.Add(e);
        return;
    }
    e.Controller.Continue(false);
};
var attached = new TaskCompletionSource<CorDebugProcess>(TaskCreationOptions.RunContinuationsAsynchronously);
var pid = running.Id;
IntPtr registration = IntPtr.Zero;
CorDebugProcess process;
if (late)
{
    // Attach to a program that is already running its Main (as to dotnet watch's server).
    await ready.Task.WaitAsync(TimeSpan.FromSeconds(30));
    var clrs = shim.EnumerateCLRs(pid);
    var version = shim.CreateVersionStringFromModule(pid, clrs.Items[0].Path);
    shim.CloseCLREnumeration(clrs);
    var lateDebug = shim.CreateDebuggingInterfaceFromVersion(version);
    lateDebug.Initialize();
    lateDebug.SetManagedHandler(callback);
    process = lateDebug.DebugActiveProcess(pid, win32Attach: false);
    Thread.Sleep(300); // let the attach's module events arrive
    running.StandardInput.WriteLine();
}
else
{
await DiagnosticIpc.WaitForPort(pid);
registration = shim.RegisterForRuntimeStartup(pid, (pCordb, _, hr) =>
{
    try
    {
        var corDebug = new CorDebug(Extensions.GetObjectForIUnknown<ICorDebug>(pCordb));
        corDebug.Initialize();
        corDebug.SetManagedHandler(callback);
        attached.TrySetResult(corDebug.DebugActiveProcess(pid, win32Attach: false));
    }
    catch (Exception ex)
    {
        attached.TrySetException(ex);
    }
}, IntPtr.Zero);
DiagnosticIpc.ResumeRuntime(pid);
process = await attached.Task.WaitAsync(TimeSpan.FromSeconds(30));
}
Log($"attached after {clock.ElapsedMilliseconds} ms");

// ---- 1. Stopped before the first call to Message().
var hit = (BreakpointCorDebugManagedCallbackEventArgs)Next();
Log($"stopped at line {CallLine} (iteration 1, before Message() runs)");

// ---- 2. The delta: Roslyn's EmitDifference against the build that is running.
var tree2 = CSharpSyntaxTree.ParseText(V2, parse, path: tree1.FilePath, encoding: Encoding.UTF8);
var compilation2 = compilation1.ReplaceSyntaxTree(tree1, tree2);
var baseline = EmitBaseline.CreateInitialBaseline(
    compilation1,
    ModuleMetadata.CreateFromImage(File.ReadAllBytes(dll)),
    symbols.EncDebugInformation,
    symbols.LocalSignature,
    hasPortableDebugInformation: true);
var oldMessage = compilation1.GetTypeByMetadataName("Program")!.GetMembers("Message").Single();
var newMessage = compilation2.GetTypeByMetadataName("Program")!.GetMembers("Message").Single();
using var metadataDelta = new MemoryStream();
using var ilDelta = new MemoryStream();
using var pdbDelta = new MemoryStream();
var difference = compilation2.EmitDifference(baseline, [new SemanticEdit(SemanticEditKind.Update, oldMessage, newMessage)],
    _ => false, metadataDelta, ilDelta, pdbDelta, CancellationToken.None);
if (!difference.Success) throw new Exception(string.Join("\n", difference.Diagnostics));
Log($"delta: metadata {metadataDelta.Length} B, IL {ilDelta.Length} B, PDB {pdbDelta.Length} B; updated methods: {string.Join(", ", difference.UpdatedMethods.Select(h => $"0x{MetadataTokens.GetToken(h):x8}"))}");

// ---- 3. Apply it through the debugger while the process is stopped.
var applied = Apply(module!, metadataDelta.ToArray(), ilDelta.ToArray());
Log($"ApplyChanges: {applied}");
if (applied != HRESULT.S_OK)
{
    callBreakpoint!.Activate(false);
    process.Continue(false);
    Finish();
    return 1;
}
var message = module!.GetFunctionFromToken((mdMethodDef)messageToken);
Log($"Message() is now version {message.CurrentVersionNumber}");

// ---- 4. A breakpoint on a line that only exists in the new version, placed from the PDB delta.
var deltaPoints = DeltaSequencePoints(pdbDelta.ToArray(), messageToken);
var edited = deltaPoints.First(p => p.StartLine == EditedLine);
var editedBreakpoint = message.ILCode.CreateBreakpoint(edited.Offset);
editedBreakpoint.Activate(true);
Log($"breakpoint on edited line {EditedLine} at IL_{edited.Offset:x4} of version {message.ILCode.VersionNumber}");

process.Continue(false);
hit = (BreakpointCorDebugManagedCallbackEventArgs)Next();
var frame = (CorDebugILFrame)hit.Thread.ActiveFrame;
var line = deltaPoints.Last(p => p.Offset <= frame.IP.pnOffset).StartLine;
Log($"stopped in Message() at line {line} (IL_{frame.IP.pnOffset:x4}, code version {frame.Code.VersionNumber}); local text = {Describe(frame.GetLocalVariable(0))}");

// ---- 5. The unchanged method's breakpoint still works, then run to the end.
process.Continue(false);
hit = (BreakpointCorDebugManagedCallbackEventArgs)Next();
Log($"stopped at line {CallLine} again (iteration 2)");

// ---- 6. A second edit, on the first one's baseline (generation 2).
var tree3 = CSharpSyntaxTree.ParseText(V2.Replace("\"new\"", "\"newer\""), parse, path: tree1.FilePath, encoding: Encoding.UTF8);
var compilation3 = compilation2.ReplaceSyntaxTree(tree2, tree3);
var newerMessage = compilation3.GetTypeByMetadataName("Program")!.GetMembers("Message").Single();
using var metadataDelta2 = new MemoryStream();
using var ilDelta2 = new MemoryStream();
using var pdbDelta2 = new MemoryStream();
var difference2 = compilation3.EmitDifference(difference.Baseline!, [new SemanticEdit(SemanticEditKind.Update, newMessage, newerMessage)],
    _ => false, metadataDelta2, ilDelta2, pdbDelta2, CancellationToken.None);
if (!difference2.Success) throw new Exception(string.Join("\n", difference2.Diagnostics));
Log($"second ApplyChanges: {Apply(module!, metadataDelta2.ToArray(), ilDelta2.ToArray())}; Message() is now version {message.CurrentVersionNumber}");
callBreakpoint!.Activate(false);
editedBreakpoint.Activate(false);
process.Continue(false);
Finish();
var lines = output.ToArray();
Log(lines.Length == 4 && lines[0].EndsWith(": new!") && lines.Skip(1).All(l => l.EndsWith(": newer!"))
    ? "RESULT: every call ran the latest edit"
    : "RESULT: unexpected output");
return 0;

void Finish()
{
    var exit = Next();
    Log($"{exit.Kind} after {clock.ElapsedMilliseconds} ms");
    if (registration != IntPtr.Zero) shim.UnregisterForRuntimeStartup(registration);
    running.WaitForExit();
    Thread.Sleep(100);
    Log($"exit code {running.ExitCode}");
}

CorDebugManagedCallbackEventArgs Next() =>
    events.TryTake(out var e, TimeSpan.FromSeconds(30)) ? e : throw new TimeoutException("no debug event in 30 s");

static unsafe HRESULT Apply(CorDebugModule module, byte[] metadata, byte[] il)
{
    fixed (byte* m = metadata)
    fixed (byte* i = il)
    {
        return module.TryApplyChanges(metadata.Length, (IntPtr)m, il.Length, (IntPtr)i);
    }
}

static List<SequencePoint> DeltaSequencePoints(byte[] pdbDelta, int methodToken)
{
    // A PDB delta's MethodDebugInformation rows are the methods its EncMap lists, in order.
    var reader = MetadataReaderProvider.FromPortablePdbImage(ImmutableArray.Create(pdbDelta)).GetMetadataReader();
    var rows = reader.GetEditAndContinueMapEntries().Where(h => h.Kind == HandleKind.MethodDebugInformation).ToList();
    var index = rows.FindIndex(h => MetadataTokens.GetRowNumber(h) == (methodToken & 0xffffff));
    var handle = MetadataTokens.MethodDebugInformationHandle(index + 1);
    return reader.GetMethodDebugInformation(handle).GetSequencePoints().Where(p => !p.IsHidden).ToList();
}

static string Describe(CorDebugValue value) => value switch
{
    CorDebugReferenceValue { IsNull: true } => "null",
    CorDebugReferenceValue r => Describe(r.Dereference()),
    CorDebugStringValue s => $"\"{s.GetString(s.Length)}\"",
    _ => $"<{value.Type}>",
};

static void Log(string message) => Console.WriteLine($"[hotreload] {message}");

sealed class Symbols
{
    private static readonly Guid EncLocalSlotMap = new("755F52A8-91C5-45BE-B4B8-209571E552BD");
    private static readonly Guid EncLambdaAndClosureMap = new("A643004C-0240-496F-A783-30D64F4979DE");
    private static readonly Guid EncStateMachineStateMap = new("8B78CD68-2EDE-420B-980B-E15884B8AAA3");
    private readonly PEReader _pe;
    private readonly MetadataReader _metadata;
    private readonly MetadataReader _pdb;

    public Symbols(byte[] image, byte[] pdb)
    {
        _pe = new PEReader(ImmutableArray.Create(image));
        _metadata = _pe.GetMetadataReader();
        _pdb = MetadataReaderProvider.FromPortablePdbImage(ImmutableArray.Create(pdb)).GetMetadataReader();
    }

    public int MethodToken(string name) => MetadataTokens.GetToken(
        _metadata.MethodDefinitions.First(m => _metadata.GetString(_metadata.GetMethodDefinition(m).Name) == name));

    public int Offset(int token, int line) => _pdb
        .GetMethodDebugInformation(MetadataTokens.MethodDebugInformationHandle(token & 0xffffff))
        .GetSequencePoints().First(p => !p.IsHidden && p.StartLine == line).Offset;

    // What Roslyn needs to keep local slots, lambdas and state machines stable across the edit.
    public EditAndContinueMethodDebugInformation EncDebugInformation(MethodDefinitionHandle method)
    {
        ImmutableArray<byte> Blob(Guid kind)
        {
            foreach (var handle in _pdb.GetCustomDebugInformation(method))
            {
                var information = _pdb.GetCustomDebugInformation(handle);
                if (_pdb.GetGuid(information.Kind) == kind)
                {
                    return _pdb.GetBlobContent(information.Value);
                }
            }
            return default;
        }
        return EditAndContinueMethodDebugInformation.Create(Blob(EncLocalSlotMap), Blob(EncLambdaAndClosureMap), Blob(EncStateMachineStateMap));
    }

    public StandaloneSignatureHandle LocalSignature(MethodDefinitionHandle method)
    {
        var rva = _metadata.GetMethodDefinition(method).RelativeVirtualAddress;
        return rva == 0 ? default : _pe.GetMethodBody(rva).LocalSignature;
    }
}
