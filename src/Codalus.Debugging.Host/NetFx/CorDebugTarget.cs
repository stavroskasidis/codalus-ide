using System.Globalization;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Threading.Channels;
using System.Xml.Linq;
using ClrDebug;

namespace Codalus.Debugging.Host.NetFx;

/// <summary>
/// Starts and attaches to .NET Framework processes through the installed desktop CLR's own debugging API (M43b,
/// ADR 0076): <c>mscoree</c>'s <c>CLRCreateInstance</c> gives <c>ICLRMetaHost</c>, the runtime's
/// <c>ICLRRuntimeInfo</c> gives <c>ICorDebug</c> from its <c>mscordbi</c> (the "legacy" CLSID, which is how a
/// debugger asks a .NET Framework runtime for it), and ClrDebug (MIT) wraps the COM interfaces. Nothing is shipped
/// for it: mscoree and mscordbi come with Windows' .NET Framework.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class CorDebugger : IClrDebugger
{
    public const string V4 = "v4.0.30319";
    public const string V2 = "v2.0.50727";

    public IClrProcess Launch(NetFxDebugArguments arguments, Action<string, string> output)
    {
        var program = Path.GetFullPath(arguments.Program!);
        if (!File.Exists(program))
        {
            throw new ClrException($"{program} doesn't exist: build the project first.");
        }

        if (DesktopClr.BitnessProblem(program, Environment.Is64BitProcess) is { } problem)
        {
            throw new ClrException(problem);
        }

        var metaHost = new CLRMetaHost();
        var version = RuntimeVersion(metaHost, program);
        var target = new CorDebugTarget(CreateCorDebug(metaHost, version), version, arguments.JustMyCode);
        try
        {
            target.Launch(program, arguments, output);
            return target;
        }
        catch
        {
            target.Dispose();
            throw;
        }
    }

    public IClrProcess Attach(int processId)
    {
        System.Diagnostics.Process process;
        try
        {
            process = System.Diagnostics.Process.GetProcessById(processId);
        }
        catch (ArgumentException)
        {
            throw new ClrException($"There's no process {processId.ToString(CultureInfo.InvariantCulture)}.");
        }

        var runtime = DesktopClr.LoadedRuntime(process) ?? throw new ClrException($"Process {processId.ToString(CultureInfo.InvariantCulture)} ({process.ProcessName}) isn't running the desktop CLR (.NET Framework).");
        if (IsWow64(process) && Environment.Is64BitProcess)
        {
            throw new ClrException($"{process.ProcessName} is a 32-bit process, and the .NET Framework debugger here debugs 64-bit processes only. Build it for AnyCPU without Prefer32Bit, or for x64.");
        }

        var version = runtime == DesktopClr.Clr2 ? V2 : V4;
        var target = new CorDebugTarget(CreateCorDebug(new CLRMetaHost(), version), version, justMyCode: true);
        try
        {
            target.Attach(process);
            return target;
        }
        catch
        {
            target.Dispose();
            throw;
        }
    }

    private static bool IsWow64(System.Diagnostics.Process process)
    {
        try
        {
            return NativeMethods.IsWow64Process(process.Handle, out var wow64) && wow64;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>The runtime a program starts on: the first installed <c>supportedRuntime</c> of its .config, else the version it was built for.</summary>
    private static string RuntimeVersion(CLRMetaHost metaHost, string program)
    {
        foreach (var supported in SupportedRuntimes(program + ".config"))
        {
            var version = supported.StartsWith("v4", StringComparison.OrdinalIgnoreCase) ? V4 : supported.StartsWith("v2", StringComparison.OrdinalIgnoreCase) ? V2 : supported;
            if (IsInstalled(metaHost, version))
            {
                return version;
            }
        }

        string built;
        try
        {
            built = metaHost.GetVersionFromFile(program);
        }
        catch (COMException ex)
        {
            throw new ClrException($"{Path.GetFileName(program)} isn't a .NET Framework program ({ex.Message}).", ex);
        }

        var wanted = built.StartsWith("v4", StringComparison.OrdinalIgnoreCase) ? V4 : built.StartsWith("v2", StringComparison.OrdinalIgnoreCase) ? V2 : built;
        if (!IsInstalled(metaHost, wanted))
        {
            throw new ClrException(wanted == V2
                ? $"{Path.GetFileName(program)} targets .NET Framework 2.0 to 3.5 (CLR 2), which isn't installed: turn on .NET Framework 3.5 in Windows Features."
                : $"The .NET Framework runtime {wanted} isn't installed.");
        }

        return wanted;
    }

    internal static IEnumerable<string> SupportedRuntimes(string configPath)
    {
        if (!File.Exists(configPath))
        {
            return [];
        }

        try
        {
            return [.. XDocument.Load(configPath).Root?.Element("startup")?.Elements("supportedRuntime").Select(e => (string?)e.Attribute("version")).OfType<string>() ?? []];
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static bool IsInstalled(CLRMetaHost metaHost, string version)
    {
        try
        {
            return metaHost.GetRuntime(version, typeof(ICLRRuntimeInfo).GUID) is not null;
        }
        catch (COMException)
        {
            return false;
        }
    }

    private static CorDebug CreateCorDebug(CLRMetaHost metaHost, string version)
    {
        try
        {
            var runtime = new CLRRuntimeInfo((ICLRRuntimeInfo)metaHost.GetRuntime(version, typeof(ICLRRuntimeInfo).GUID));
            var corDebug = new CorDebug((ICorDebug)runtime.GetInterface(ClrDebug.Extensions.CLSID_CLRDebuggingLegacy, typeof(ICorDebug).GUID));
            corDebug.Initialize();
            return corDebug;
        }
        catch (COMException ex)
        {
            throw new ClrException($"The .NET Framework {version} debugging API (mscordbi) isn't available: {ex.Message}", ex);
        }
    }
}

/// <summary>
/// One debugged process through ICorDebug: the desktop CLR's (M43b), or CoreCLR's through dbgshim (M54, ADR 0086).
/// Its callbacks become <see cref="ClrEvent"/>s.
/// </summary>
internal sealed class CorDebugTarget : IClrProcess
{
    private readonly CorDebug _corDebug;
    private readonly CorDebugManagedCallback _callback = new();
    private readonly Channel<ClrEvent> _events = Channel.CreateUnbounded<ClrEvent>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Lock _gate = new();
    private readonly Dictionary<(ulong, string), CorModule> _modules = [];
    private readonly Dictionary<int, CorThread> _threads = [];
    private readonly Dictionary<(ulong Module, int Method, int Offset, int Version), CorBreakpoint> _breakpoints = [];
    private readonly List<CorDebugHandleValue> _handles = [];
    private readonly List<Task> _pumps = [];
    private CorDebugProcess? _process;
    private System.Diagnostics.Process? _os;
    private CorDebugStepper? _stepper;
    private TaskCompletionSource<bool>? _eval;
    private readonly List<string> _evalCallbacks = [];
    private long _evalStarted;
    private bool _exited;

    public CorDebugTarget(CorDebug corDebug, string version, bool justMyCode)
    {
        _corDebug = corDebug;
        RuntimeVersion = version;
        JustMyCode = justMyCode;
        _callback.OnAnyEvent += OnEvent;
        _corDebug.SetManagedHandler(_callback);
    }

    public int Id { get; private set; }

    public string RuntimeVersion { get; }

    public bool JustMyCode { get; }

    /// <summary>Counts the times the process ran: frames are re-read after it has (ICorDebug neuters them).</summary>
    public int Generation { get; private set; }

    public ChannelReader<ClrEvent> Events => _events.Reader;

    /// <summary>
    /// A CoreCLR program the launcher started (ADR 0086): its exit code comes from the launcher, which is its parent, and
    /// a detach tells the launcher to leave it running.
    /// </summary>
    public CoreClr.LaunchedProgram? Launched { get; set; }

    private CorDebugProcess Process => _process ?? throw new ClrException("The process isn't running.");

    // ------------------------------------------------------------------ starting

    [SupportedOSPlatform("windows")]
    public void Launch(string program, NetFxDebugArguments arguments, Action<string, string> output)
    {
        var commandLine = DesktopClr.CommandLine(program, arguments.Args ?? []);
        var environment = EnvironmentBlock(arguments.Env);
        var cwd = string.IsNullOrEmpty(arguments.Cwd) ? Path.GetDirectoryName(program) : arguments.Cwd;
        AnonymousPipeServerStream? stdout = null, stderr = null, stdin = null;
        try
        {
            STARTUPINFOW? startup = null;
            var flags = CreateProcessFlags.CREATE_UNICODE_ENVIRONMENT;
            if (arguments.NewConsole)
            {
                flags |= CreateProcessFlags.CREATE_NEW_CONSOLE;
            }
            else
            {
                // The Debug Console: the program's output through pipes, and no input (its stdin is at its end).
                stdout = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
                stderr = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
                stdin = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);
                flags |= CreateProcessFlags.CREATE_NO_WINDOW;
                startup = new STARTUPINFOW
                {
                    cb = Marshal.SizeOf<STARTUPINFOW>(),
                    dwFlags = STARTF.STARTF_USESTDHANDLES,
                    hStdInput = stdin.ClientSafePipeHandle.DangerousGetHandle(),
                    hStdOutput = stdout.ClientSafePipeHandle.DangerousGetHandle(),
                    hStdError = stderr.ClientSafePipeHandle.DangerousGetHandle(),
                };
            }

            try
            {
                _process = _corDebug.CreateProcess(commandLine, bInheritHandles: !arguments.NewConsole, dwCreationFlags: flags, lpEnvironment: environment, lpCurrentDirectory: cwd, lpStartupInfo: startup);
            }
            catch (COMException ex)
            {
                throw new ClrException($"{Path.GetFileName(program)} couldn't be started under the debugger: {ex.Message}", ex);
            }

            Id = _process.Id;
            _os = TryGetProcess(Id);
            if (stdout is not null)
            {
                stdout.DisposeLocalCopyOfClientHandle();
                stderr!.DisposeLocalCopyOfClientHandle();
                stdin!.DisposeLocalCopyOfClientHandle();
                stdin.Dispose();
                stdin = null;
                _pumps.Add(Pump(stdout, "stdout", output));
                _pumps.Add(Pump(stderr, "stderr", output));
                stdout = stderr = null;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(environment);
            stdout?.Dispose();
            stderr?.Dispose();
            stdin?.Dispose();
        }
    }

    public void Attach(System.Diagnostics.Process process)
    {
        try
        {
            _process = _corDebug.DebugActiveProcess(process.Id, win32Attach: false);
        }
        catch (COMException ex)
        {
            throw new ClrException($"The debugger couldn't attach to {process.ProcessName} ({process.Id.ToString(CultureInfo.InvariantCulture)}): {ex.Message}", ex);
        }

        Id = process.Id;
        _os = process;
        try
        {
            _ = process.Handle;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }

    /// <summary>The process, with its handle open now (its exit code is read after it ends).</summary>
    private static System.Diagnostics.Process? TryGetProcess(int id)
    {
        try
        {
            var process = System.Diagnostics.Process.GetProcessById(id);
            _ = process.Handle;
            return process;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    /// <summary>A Unicode environment block: this process's environment with the launch's on top.</summary>
    private static IntPtr EnvironmentBlock(Dictionary<string, string>? overrides)
    {
        var variables = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            variables[(string)entry.Key] = (string?)entry.Value ?? string.Empty;
        }

        foreach (var (name, value) in overrides ?? [])
        {
            variables[name] = value;
        }

        var block = new StringBuilder();
        foreach (var (name, value) in variables)
        {
            block.Append(name).Append('=').Append(value).Append('\0');
        }

        block.Append('\0');
        return Marshal.StringToHGlobalUni(block.ToString());
    }

    private static Task Pump(AnonymousPipeServerStream pipe, string category, Action<string, string> output) =>
        Task.Run(async () =>
        {
            using (pipe)
            using (var reader = new StreamReader(pipe, Encoding.Default))
            {
                var buffer = new char[4096];
                int read;
                while ((read = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0)
                {
                    output(category, new string(buffer, 0, read));
                }
            }
        });

    // ------------------------------------------------------------------ callbacks

    /// <summary>
    /// ICorDebug's callbacks (on its own thread, the process stopped): those the session needs are queued without
    /// continuing (the session continues); the others continue here. During a func-eval everything continues, except
    /// the eval's completion, and modules and threads are still reported.
    /// </summary>
    private void OnEvent(object? sender, CorDebugManagedCallbackEventArgs e)
    {
        e.Continue = false;
        try
        {
            Dispatch(e);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or InvalidCastException or ArgumentException or NullReferenceException)
        {
            // Never leave the process stopped in a callback nobody will continue.
            _events.Writer.TryWrite(new ClrNotice($"[debugger] {e.Kind}: {ex.Message}") { Continued = true });
            ContinueFromCallback(e);
        }
    }

    private void Dispatch(CorDebugManagedCallbackEventArgs e)
    {
        var evaluating = _eval is not null;
        if (evaluating)
        {
            RecordEvalCallback(e);
        }

        switch (e)
        {
            case CreateProcessCorDebugManagedCallbackEventArgs created:
                _process ??= created.Process;
                try
                {
                    // Debugger.Log (and Debug.WriteLine's default listener) reports to the debugger only when asked.
                    created.Process.EnableLogMessages(true);
                }
                catch (COMException)
                {
                }

                ContinueFromCallback(e);
                return;
            case CreateAppDomainCorDebugManagedCallbackEventArgs domain:
                domain.AppDomain.Attach();
                ContinueFromCallback(e);
                return;
            case LoadModuleCorDebugManagedCallbackEventArgs load:
                Queue(new ClrModuleLoaded(Module(load.Module)), e, evaluating);
                return;
            case UnloadModuleCorDebugManagedCallbackEventArgs unload:
                Queue(new ClrModuleUnloaded(Module(unload.Module)), e, evaluating);
                lock (_gate)
                {
                    _modules.Remove(Key(unload.Module));
                }

                return;
            case CreateThreadCorDebugManagedCallbackEventArgs thread:
                Queue(new ClrThreadStarted(Thread(thread.Thread)), e, evaluating);
                return;
            case ExitThreadCorDebugManagedCallbackEventArgs exit:
                var exited = Thread(exit.Thread);
                lock (_gate)
                {
                    _threads.Remove(exited.Id);
                }

                Queue(new ClrThreadExited(exited), e, evaluating);
                return;
            case LogMessageCorDebugManagedCallbackEventArgs message:
                Queue(new ClrLogMessage(message.Message), e, evaluating);
                return;
            case EvalCompleteCorDebugManagedCallbackEventArgs:
                _eval?.TrySetResult(true);
                return;
            case EvalExceptionCorDebugManagedCallbackEventArgs:
                _eval?.TrySetResult(false);
                return;
            case ExitProcessCorDebugManagedCallbackEventArgs:
                Exit();
                return;
        }

        if (evaluating)
        {
            ContinueFromCallback(e); // a breakpoint, a step or an exception while evaluating: not the user's stop
            return;
        }

        switch (e)
        {
            case BreakpointCorDebugManagedCallbackEventArgs hit when Breakpoint(hit.Breakpoint) is { } breakpoint:
                Queue(new ClrBreakpointHit(Thread(hit.Thread), breakpoint), e, false);
                return;
            case StepCompleteCorDebugManagedCallbackEventArgs step:
                _stepper = null;
                Queue(new ClrStepCompleted(Thread(step.Thread)), e, false);
                return;
            case BreakCorDebugManagedCallbackEventArgs userBreak:
                Queue(new ClrUserBreak(Thread(userBreak.Thread)), e, false);
                return;
            case Exception2CorDebugManagedCallbackEventArgs exception when exception.EventType != CorDebugExceptionCallbackType.DEBUG_EXCEPTION_CATCH_HANDLER_FOUND:
                var stage = exception.EventType switch
                {
                    CorDebugExceptionCallbackType.DEBUG_EXCEPTION_USER_FIRST_CHANCE => ClrExceptionStage.UserFirstChance,
                    CorDebugExceptionCallbackType.DEBUG_EXCEPTION_UNHANDLED => ClrExceptionStage.Unhandled,
                    _ => ClrExceptionStage.FirstChance,
                };
                Queue(new ClrExceptionThrown(Thread(exception.Thread), stage), e, false);
                return;
            default:
                ContinueFromCallback(e);
                return;
        }
    }

    private void Queue(ClrEvent clrEvent, CorDebugManagedCallbackEventArgs e, bool continueNow)
    {
        if (continueNow)
        {
            _events.Writer.TryWrite(clrEvent with { Continued = true });
            ContinueFromCallback(e);
        }
        else
        {
            _events.Writer.TryWrite(clrEvent);
        }
    }

    private void ContinueFromCallback(CorDebugManagedCallbackEventArgs e)
    {
        try
        {
            Generation++;
            (e.Controller ?? (CorDebugController?)_process)?.Continue(false);
        }
        catch (COMException)
        {
            // The process is ending.
        }
    }

    private void Exit()
    {
        if (_exited)
        {
            return;
        }

        _exited = true;
        int? code = null;
        try
        {
            if (Launched is { } launched)
            {
                code = launched.WaitForExit(TimeSpan.FromSeconds(5));
            }
            else if (_os is { } os && os.WaitForExit(5000))
            {
                code = os.ExitCode;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }

        try
        {
            // The program's last output before its end: the pipes are at their end once it has exited.
            Task.WaitAll([.. _pumps], TimeSpan.FromSeconds(3));
        }
        catch (AggregateException)
        {
        }

        _eval?.TrySetCanceled();
        _events.Writer.TryWrite(new ClrExited(code));
        _events.Writer.TryComplete();
    }

    // ------------------------------------------------------------------ control

    public void Continue()
    {
        ReleaseHandles();
        Generation++;
        Translate(() => Process.Continue(false));
    }

    public void Pause() => Translate(() => Process.Stop(0));

    public void Terminate()
    {
        if (_exited || _process is null)
        {
            return;
        }

        Translate(() =>
        {
            try
            {
                _process.Stop(0);
            }
            catch (COMException)
            {
            }

            _process.Terminate(0);
        });
    }

    public void Detach()
    {
        if (_exited || _process is null)
        {
            return;
        }

        Translate(() =>
        {
            CancelStep();
            try
            {
                _process.Stop(0);
            }
            catch (COMException)
            {
            }

            ReleaseHandles();

            _process.Detach();
        });
        try
        {
            Launched?.Detach();
        }
        catch (ClrException)
        {
        }

        _exited = true;
        _events.Writer.TryWrite(new ClrExited(null));
        _events.Writer.TryComplete();
    }

    public IReadOnlyList<IClrThread> Threads() =>
        Translate(() => Process.Threads.Select(Thread).Cast<IClrThread>().ToList());

    public IClrBreakpoint CreateBreakpoint(IClrModule module, int methodToken, int ilOffset) =>
        Translate(() =>
        {
            var corModule = (CorModule)module;
            // The method's current version: an edit (M55) makes a new one, which later calls run.
            var function = corModule.Raw.GetFunctionFromToken(new mdMethodDef(methodToken));
            var breakpoint = new CorBreakpoint(function.ILCode.CreateBreakpoint(ilOffset));
            breakpoint.Activate(true);
            lock (_gate)
            {
                _breakpoints[(corModule.Raw.BaseAddress.Value, methodToken, ilOffset, function.VersionNumber)] = breakpoint;
            }

            return (IClrBreakpoint)breakpoint;
        });

    public bool EnableEditAndContinue(IClrModule module) =>
        ((CorModule)module).Raw.TrySetJITCompilerFlags(CorDebugJITCompilerFlags.CORDEBUG_JIT_ENABLE_ENC) == HRESULT.S_OK;

    /// <summary><c>IMetaDataImport</c>'s interface id.</summary>
    private static readonly Guid s_metadataImport = new("7DAC8207-D3AE-4C75-9B67-92801A497D44");

    public unsafe void ApplyChanges(IClrModule module, byte[] metadataDelta, byte[] ilDelta)
    {
        // mscordbi reads the module's metadata for the edit through an importer it creates when it first needs one (a
        // stop's stack walk does); applied before that (a program that hasn't stopped yet), ApplyChanges crashed the
        // adapter in mscordbi (Windows, .NET 11 RC3). Asking for the importer first creates it.
        ((CorModule)module).Raw.TryGetMetaDataInterface(s_metadataImport, out _);
        HRESULT result;
        fixed (byte* metadata = metadataDelta)
        fixed (byte* il = ilDelta)
        {
            result = ((CorModule)module).Raw.TryApplyChanges(metadataDelta.Length, (IntPtr)metadata, ilDelta.Length, (IntPtr)il);
        }

        if (result != HRESULT.S_OK)
        {
            throw new ClrException(Enum.IsDefined(result) ? result.ToString() : "0x" + ((int)result).ToString("X8", CultureInfo.InvariantCulture));
        }
    }

    public int CodeVersion(IClrModule module, int methodToken) =>
        Translate(() => ((CorModule)module).Raw.GetFunctionFromToken(new mdMethodDef(methodToken)).VersionNumber);

    private CorBreakpoint? Breakpoint(CorDebugBreakpoint breakpoint)
    {
        if (breakpoint.Raw is not ICorDebugFunctionBreakpoint raw)
        {
            return null;
        }

        var function = new CorDebugFunctionBreakpoint(raw);
        var key = (function.Function.Module.BaseAddress.Value, (int)function.Function.Token.Value, function.Offset, function.Function.VersionNumber);
        lock (_gate)
        {
            return _breakpoints.GetValueOrDefault(key);
        }
    }

    public void Step(IClrThread thread, ClrStep step) =>
        Translate(() =>
        {
            CancelStep();
            var corThread = ((CorThread)thread).Raw;
            CorDebugFrame? frame = null;
            try
            {
                frame = corThread.ActiveFrame;
            }
            catch (COMException)
            {
            }

            var stepper = frame?.Raw is ICorDebugILFrame ? frame.CreateStepper() : corThread.CreateStepper();
            stepper.SetUnmappedStopMask(CorDebugUnmappedStop.STOP_NONE);
            stepper.SetInterceptMask(CorDebugIntercept.INTERCEPT_NONE);
            if (step.JustMyCode)
            {
                try
                {
                    stepper.SetJMC(true);
                }
                catch (COMException)
                {
                    // Not a Just My Code stepper (CLR 2's): it steps everywhere, and the session steps out of code that isn't the user's.
                }
            }

            if (step.Kind == ClrStepKind.Out)
            {
                stepper.StepOut();
            }
            else if (step.Ranges.Count > 0)
            {
                var size = frame?.Function.ILCode.Size ?? int.MaxValue;
                var ranges = step.Ranges.Select(r => new COR_DEBUG_STEP_RANGE { startOffset = r.Start, endOffset = Math.Min(r.End, size) }).ToArray();
                stepper.SetRangeIL(true);
                stepper.StepRange(step.Kind == ClrStepKind.Into, ranges, ranges.Length);
            }
            else
            {
                stepper.Step(step.Kind == ClrStepKind.Into);
            }

            _stepper = stepper;
            ReleaseHandles();
            Generation++;
            Process.Continue(false);
        });

    public void CancelStep()
    {
        if (_stepper is { } stepper)
        {
            _stepper = null;
            try
            {
                stepper.Deactivate();
            }
            catch (COMException)
            {
            }
        }
    }

    public void SetUserCode(IClrModule module, bool userCode) =>
        Translate(() => ((CorModule)module).Raw.SetJMCStatus(userCode, 0, []));

    public IClrValue GetStaticField(IClrThread thread, ClrType type, int fieldToken) =>
        Translate(() =>
        {
            var frame = ((CorThread)thread).Raw.ActiveFrame;
            var raw = type.Native is CorDebugType native && native.TypeParameters.Length > 0
                ? native.GetStaticFieldValue(new mdFieldDef(fieldToken), frame?.Raw)
                : ((CorModule)type.Module!).Raw.GetClassFromToken(new mdTypeDef(type.Token)).GetStaticFieldValue(fieldToken, frame?.Raw);
            return CorValue.Wrap(this, raw);
        });

    // ------------------------------------------------------------------ func-eval

    public IClrValue Call(IClrThread thread, IClrModule module, int methodToken, IReadOnlyList<ClrType> typeArguments, IReadOnlyList<IClrValue> arguments, TimeSpan timeout) =>
        (IClrValue?)Evaluate(thread, timeout, eval =>
        {
            var function = ((CorModule)module).Raw.GetFunctionFromToken(new mdMethodDef(methodToken));
            var types = typeArguments.Select(t => t.Native as CorDebugType).ToList();
            var values = arguments.Select(a => ((CorValue)ClrLiveValue.Unwrap(a)).Original.Raw).ToArray();
            if (types.Count > 0 && types.All(t => t is not null))
            {
                eval.CallParameterizedFunction(function.Raw, types.Count, [.. types.Select(t => t!.Raw)], values.Length, values);
            }
            else
            {
                eval.CallFunction(function.Raw, values.Length, values);
            }
        }) ?? ClrVoidValue.Instance;

    public IClrValue NewString(IClrThread thread, string text, TimeSpan timeout) =>
        Evaluate(thread, timeout, eval => eval.NewString(text)) ?? throw new ClrException("The string wasn't created.");

    public IClrValue NewPrimitive(IClrThread thread, object value) =>
        Translate(() => (IClrValue)CorValue.Wrap(this, CreatePrimitive(((CorThread)thread).Raw, value)));

    /// <summary>A primitive value created in the debuggee (no func-eval runs).</summary>
    internal CorDebugValue CreatePrimitive(CorDebugThread thread, object value)
    {
        var created = thread.CreateEval().CreateValue(CorValue.ElementTypeOf(value), null);
        CorValue.Write(new CorDebugGenericValue((ICorDebugGenericValue)created.Raw), value);
        return created;
    }

    /// <summary>Keeps a func-eval result's handle until the process is continued (or stepped).</summary>
    internal void Hold(CorDebugHandleValue handle)
    {
        lock (_gate)
        {
            _handles.Add(handle);
        }
    }

    /// <summary>Releases the handles of func-eval results: the session drops its values when the program runs on.</summary>
    private void ReleaseHandles()
    {
        List<CorDebugHandleValue> handles;
        lock (_gate)
        {
            handles = [.. _handles];
            _handles.Clear();
        }

        foreach (var handle in handles)
        {
            try
            {
                handle.Dispose();
            }
            catch (COMException)
            {
            }
        }
    }

    /// <summary>Runs a func-eval on <paramref name="thread"/>, the other threads suspended, and waits for it (aborting it after <paramref name="timeout"/>).</summary>
    /// <summary>A func-eval's result; null when the method returns nothing.</summary>
    private CorValue? Evaluate(IClrThread thread, TimeSpan timeout, Action<CorDebugEval> start)
    {
        var corThread = ((CorThread)thread).Raw;
        CorDebugEval eval;
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            eval = corThread.CreateEval();
            start(eval);
        }
        catch (COMException ex)
        {
            throw new ClrException(EvalProblem(ex), ex);
        }

        lock (_gate)
        {
            _evalCallbacks.Clear();
        }

        _evalStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        _eval = completion;
        try
        {
            Translate(() =>
            {
                Process.SetAllThreadsDebugState(CorDebugThreadState.THREAD_SUSPEND, corThread.Raw);
                Generation++;
                Process.Continue(false);
            });

            bool completed;
            try
            {
                if (!completion.Task.Wait(timeout))
                {
                    var abort = TryAbort(eval, completion);
                    throw new ClrEvalTimeoutException(EvalTimeoutDiagnostic(thread, timeout, abort));
                }

                completed = completion.Task.Result;
            }
            catch (AggregateException ex) when (ex.InnerException is TaskCanceledException)
            {
                throw new ClrException("The process ended during the evaluation.");
            }

            if (eval.TryGetResult(out var raw) == HRESULT.CORDBG_S_FUNC_EVAL_HAS_NO_RESULT && completed)
            {
                return null; // a method that returns nothing (a property's setter)
            }

            var result = Translate(() => CorValue.Result(this, corThread, raw ?? eval.Result));
            return completed ? result : throw new ClrEvalException(result, "The evaluation threw an exception.");
        }
        finally
        {
            _eval = null;
            if (!_exited)
            {
                try
                {
                    Process.SetAllThreadsDebugState(CorDebugThreadState.THREAD_RUN, null);
                }
                catch (COMException)
                {
                }
            }
        }
    }

    /// <summary>Aborts a func-eval that ran too long, and says how that went (the timeout's diagnostic).</summary>
    private static string TryAbort(CorDebugEval eval, TaskCompletionSource<bool> completion)
    {
        try
        {
            eval.Abort();
            if (completion.Task.Wait(TimeSpan.FromSeconds(3)))
            {
                return "aborted";
            }

            eval.RudeAbort();
            return completion.Task.Wait(TimeSpan.FromSeconds(3)) ? "rudely aborted" : "still running after a rude abort";
        }
        catch (Exception ex) when (ex is COMException or AggregateException)
        {
            return "the abort failed: " + ex.Message;
        }
    }

    // TODO(issue): DataTipDebuggerTests' throwing getter once timed out on Windows (docs/ci-flakes.md); this says what
    // the debugger saw meanwhile, in the CLR DAP log CI keeps.
    private void RecordEvalCallback(CorDebugManagedCallbackEventArgs e)
    {
        var name = e.GetType().Name.Replace("CorDebugManagedCallbackEventArgs", "", StringComparison.Ordinal);
        if (e is Exception2CorDebugManagedCallbackEventArgs exception)
        {
            name += "(" + exception.EventType + ")";
        }

        lock (_gate)
        {
            if (_evalCallbacks.Count < 50)
            {
                _evalCallbacks.Add(name + " at " + System.Diagnostics.Stopwatch.GetElapsedTime(_evalStarted).TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture) + " ms");
            }
        }
    }

    private string EvalTimeoutDiagnostic(IClrThread thread, TimeSpan timeout, string abort)
    {
        string callbacks;
        lock (_gate)
        {
            callbacks = _evalCallbacks.Count == 0 ? "none" : string.Join(", ", _evalCallbacks);
        }

        return $"A func-eval on thread {thread.Id.ToString(CultureInfo.InvariantCulture)} didn't return in {timeout.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture)} s ({abort}, {System.Diagnostics.Stopwatch.GetElapsedTime(_evalStarted).TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)} s in all). Callbacks meanwhile: {callbacks}.";
    }

    private static string EvalProblem(COMException ex) => (HRESULT)ex.HResult switch
    {
        HRESULT.CORDBG_E_ILLEGAL_AT_GC_UNSAFE_POINT or HRESULT.CORDBG_E_FUNC_EVAL_BAD_START_POINT
            => "The program isn't at a point where methods can be called (step or stop at a breakpoint first).",
        _ => "The evaluation couldn't start: " + ex.Message,
    };

    // ------------------------------------------------------------------ wrappers

    internal CorModule Module(CorDebugModule module)
    {
        var key = Key(module);
        lock (_gate)
        {
            if (!_modules.TryGetValue(key, out var wrapper))
            {
                _modules[key] = wrapper = new CorModule(module);
            }

            return wrapper;
        }
    }

    private static (ulong, string) Key(CorDebugModule module) => (module.BaseAddress.Value, module.Name ?? string.Empty);

    internal CorThread Thread(CorDebugThread thread)
    {
        lock (_gate)
        {
            if (!_threads.TryGetValue(thread.Id, out var wrapper))
            {
                _threads[thread.Id] = wrapper = new CorThread(this, thread);
            }

            return wrapper;
        }
    }

    /// <summary>Runs an ICorDebug call, turning its COM errors into <see cref="ClrException"/>s with the HRESULT's name.</summary>
    internal static T Translate<T>(Func<T> call)
    {
        try
        {
            return call();
        }
        catch (COMException ex)
        {
            throw new ClrException(Describe(ex), ex);
        }
    }

    internal static void Translate(Action call) => Translate(() =>
    {
        call();
        return true;
    });

    private static string Describe(COMException ex) =>
        Enum.IsDefined((HRESULT)ex.HResult) ? $"{(HRESULT)ex.HResult} ({ex.Message})" : ex.Message;

    public void Dispose()
    {
        _callback.OnAnyEvent -= OnEvent;
        try
        {
            if (!_exited && _process is not null)
            {
                Terminate();
            }

            _corDebug.Terminate();
        }
        catch (Exception ex) when (ex is COMException or ClrException)
        {
        }

        _os?.Dispose();
        Launched?.Dispose();
    }
}

internal sealed class CorModule(CorDebugModule module) : IClrModule
{
    public CorDebugModule Raw { get; } = module;

    public string Path { get; } = module.IsDynamic || module.IsInMemory ? string.Empty : module.Name ?? string.Empty;

    public bool IsDynamic { get; } = module.IsDynamic || module.IsInMemory;
}

internal sealed class CorBreakpoint(CorDebugFunctionBreakpoint breakpoint) : IClrBreakpoint
{
    public void Activate(bool active) => CorDebugTarget.Translate(() => breakpoint.Activate(active));
}

internal sealed class CorThread(CorDebugTarget target, CorDebugThread thread) : IClrThread
{
    public CorDebugThread Raw { get; } = thread;

    public int Id { get; } = thread.Id;

    public CorDebugTarget Target => target;

    public IReadOnlyList<IClrFrame> Frames() => CorDebugTarget.Translate(() =>
    {
        var result = new List<IClrFrame>();
        var index = 0;
        foreach (var chain in Raw.Chains)
        {
            if (!chain.IsManaged)
            {
                result.Add(new CorFrame(this, index++, null, "[Native Code]"));
                continue;
            }

            foreach (var frame in chain.Frames)
            {
                if (frame.Raw is ICorDebugILFrame il)
                {
                    result.Add(new CorFrame(this, index++, new CorDebugILFrame(il), null));
                }
                else if (frame.Raw is ICorDebugInternalFrame internalFrame)
                {
                    var type = new CorDebugInternalFrame(internalFrame).FrameType;
                    result.Add(new CorFrame(this, index++, null, type switch
                    {
                        CorDebugInternalFrameType.STUBFRAME_M2U => "[Managed to Native Transition]",
                        CorDebugInternalFrameType.STUBFRAME_U2M => "[Native to Managed Transition]",
                        CorDebugInternalFrameType.STUBFRAME_APPDOMAIN_TRANSITION => "[AppDomain Transition]",
                        CorDebugInternalFrameType.STUBFRAME_FUNC_EVAL => "[Function Evaluation]",
                        CorDebugInternalFrameType.STUBFRAME_CLASS_INIT => "[Class Initialization]",
                        CorDebugInternalFrameType.STUBFRAME_EXCEPTION => "[Exception]",
                        _ => "[External Code]",
                    }));
                }
                else
                {
                    result.Add(new CorFrame(this, index++, null, "[Native Code]"));
                }
            }
        }

        return (IReadOnlyList<IClrFrame>)result;
    });

    /// <summary>
    /// <paramref name="frame"/>'s IL frame now (after a func-eval, frames are read again): the frame of the same method
    /// at the same place on the stack, else the same method at the same index, else the innermost of the method.
    /// </summary>
    public CorDebugILFrame? IlFrame(CorFrame frame)
    {
        var frames = Frames().OfType<CorFrame>().Where(f => f.Cached is not null && f.MethodToken == frame.MethodToken && ReferenceEquals(f.Module, frame.Module)).ToList();
        return (frames.FirstOrDefault(f => f.StackStart != 0 && f.StackStart == frame.StackStart) ?? frames.FirstOrDefault(f => f.Index == frame.Index) ?? frames.FirstOrDefault())?.Cached;
    }

    public IClrValue? CurrentException() => CorDebugTarget.Translate(() => Raw.CurrentException is { } value ? CorValue.Wrap(target, value) : null);

    public IClrValue? ThreadObject() => CorDebugTarget.Translate(() => Raw.Object is { } value ? CorValue.Wrap(target, value) : null);
}

internal sealed class CorFrame : IClrFrame
{
    private readonly CorThread _thread;
    private readonly int _generation;
    private CorDebugILFrame? _reread;
    private int _rereadGeneration;

    public CorFrame(CorThread thread, int index, CorDebugILFrame? frame, string? description)
    {
        _thread = thread;
        Index = index;
        Cached = frame;
        Description = description;
        _generation = thread.Target.Generation;
        if (frame is not null)
        {
            var function = frame.Function;
            Module = thread.Target.Module(function.Module);
            MethodToken = (int)function.Token.Value;
            IlOffset = frame.IP.pnOffset;
            try
            {
                CodeVersion = function.VersionNumber;
            }
            catch (COMException)
            {
            }
            try
            {
                StackStart = frame.StackRange.pStart.Value;
            }
            catch (COMException)
            {
            }
        }
    }

    public CorDebugILFrame? Cached { get; }

    public int Index { get; }

    /// <summary>Where the frame starts on the stack: the same frame when frames are read again.</summary>
    public ulong StackStart { get; }

    public IClrThread Thread => _thread;

    public IClrModule? Module { get; }

    public int MethodToken { get; }

    public int IlOffset { get; }

    public string? Description { get; }

    public int CodeVersion { get; } = 1;

    /// <summary>The frame as ICorDebug has it now: the cached one until the process ran (a func-eval), then read again.</summary>
    private CorDebugILFrame Il
    {
        get
        {
            var generation = _thread.Target.Generation;
            if (generation != _generation && (_reread is null || _rereadGeneration != generation))
            {
                _reread = _thread.IlFrame(this);
                _rereadGeneration = generation;
            }

            return (generation == _generation ? Cached : _reread) ?? throw new ClrException("The frame is no longer valid.");
        }
    }

    public IReadOnlyList<ClrType> TypeArguments
    {
        get
        {
            try
            {
                return [.. Il.TypeParameters.Select(t => CorValue.TypeOf(_thread.Target, t))];
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException)
            {
                return [];
            }
        }
    }

    public IClrValue? Argument(int index) => Read(() => Il.GetArgument(index));

    public IClrValue? Local(int slot) => Read(() => Il.GetLocalVariable(slot));

    private CorValue? Read(Func<CorDebugValue> read)
    {
        try
        {
            return CorValue.Wrap(_thread.Target, read());
        }
        catch (COMException)
        {
            return null; // optimized away, or not there at this offset
        }
    }
}

/// <summary>
/// A value of the debuggee: references dereferenced and boxes opened, read lazily (M43b). ICorDebug neuters it when
/// the process runs (a func-eval included); the session reads such values again (<see cref="ClrLiveValue"/>). A
/// func-eval's result (<see cref="Result"/>) stays valid itself: an object is held by a strong handle and dereferenced
/// again, a primitive is kept and created again when it's passed to another call.
/// </summary>
internal sealed class CorValue : IClrValue
{
    private readonly CorDebugTarget _target;
    private readonly Func<CorDebugValue>? _reacquire;
    private readonly object? _snapshot;
    private CorDebugValue _original;
    private CorDebugValue _value;
    private ulong _address;
    private int _generation;
    private ClrType? _type;

    private CorValue(CorDebugTarget target, CorDebugValue original, Func<CorDebugValue>? reacquire = null, object? snapshot = null)
    {
        _target = target;
        _reacquire = reacquire;
        _snapshot = snapshot;
        (_original, _value, Kind, _address) = Open(original);
        _generation = target.Generation;
    }

    /// <summary>The value as ICorDebug has it now (a reference for objects): what a func-eval takes.</summary>
    public CorDebugValue Original
    {
        get
        {
            Refresh();
            return _original;
        }
    }

    /// <summary>The opened value now (dereferenced, unboxed).</summary>
    private CorDebugValue Value
    {
        get
        {
            Refresh();
            return _value;
        }
    }

    /// <summary>A func-eval's result after the process ran: its handle dereferenced again, or its primitive created again.</summary>
    private void Refresh()
    {
        if (_reacquire is not null && _generation != _target.Generation)
        {
            CorDebugTarget.Translate(() =>
            {
                (_original, _value, _, _address) = Open(_reacquire());
                _generation = _target.Generation;
            });
        }
    }

    public ClrValueKind Kind { get; }

    public ulong Address
    {
        get
        {
            Refresh();
            return _address;
        }
    }

    public ClrType Type => _type ??= CorDebugTarget.Translate(() => TypeOfValue(_target, Value));

    public bool IsValueType => Kind == ClrValueKind.Object && CorDebugTarget.Translate(() => Value.Raw is ICorDebugObjectValue o && new CorDebugObjectValue(o).IsValueClass);

    public object? Primitive => Kind != ClrValueKind.Primitive ? null : _snapshot ?? CorDebugTarget.Translate(() =>
    {
        var value = Value;
        return Read(new CorDebugGenericValue((ICorDebugGenericValue)value.Raw), value.Type);
    });

    public string? Text => Kind == ClrValueKind.String ? CorDebugTarget.Translate(() =>
    {
        var text = new CorDebugStringValue((ICorDebugStringValue)Value.Raw);
        return text.GetString(text.Length);
    }) : null;

    private CorDebugArrayValue Array => new((ICorDebugArrayValue)Value.Raw);

    public int Length => Kind == ClrValueKind.Array ? CorDebugTarget.Translate(() => Array.Count) : 0;

    public IReadOnlyList<int> Dimensions => Kind == ClrValueKind.Array ? CorDebugTarget.Translate(() =>
    {
        var array = Array;
        return (IReadOnlyList<int>)array.GetDimensions(array.Rank);
    }) : [];

    public IClrValue Element(int index) => CorDebugTarget.Translate(() => Wrap(_target, Array.GetElementAtPosition(index)));

    public IClrValue Field(IClrModule module, int typeToken, int fieldToken) => CorDebugTarget.Translate(() =>
    {
        var cls = ((CorModule)module).Raw.GetClassFromToken(new mdTypeDef(typeToken));
        var objectValue = new CorDebugObjectValue((ICorDebugObjectValue)Value.Raw);
        return Wrap(_target, objectValue.GetFieldValue(cls.Raw, new mdFieldDef(fieldToken)));
    });

    public void SetPrimitive(object value) => CorDebugTarget.Translate(() =>
    {
        if (Kind != ClrValueKind.Primitive || _snapshot is not null)
        {
            throw new ClrException("Only primitive values can be changed.");
        }

        Write(new CorDebugGenericValue((ICorDebugGenericValue)Value.Raw), value);
    });

    public void SetReference(IClrValue? value) => CorDebugTarget.Translate(() =>
    {
        if (_snapshot is not null || Original.Raw is not ICorDebugReferenceValue reference || Original is CorDebugHandleValue)
        {
            throw new ClrException("Only a field, an element or a variable of a class type can point elsewhere.");
        }

        if (value is not null && value.Kind != ClrValueKind.Null && value.IsValueType)
        {
            throw new ClrException("A value type's value can't be stored in a reference.");
        }

        new CorDebugReferenceValue(reference).Value = value?.Address ?? 0;
        _generation = -1; // read again
    });

    /// <summary>Wraps a value as ICorDebug gives it: null references, strings, arrays, primitives and objects (boxes opened).</summary>
    public static CorValue Wrap(CorDebugTarget target, CorDebugValue value) => new(target, value);

    /// <summary>
    /// A func-eval's result (or the exception it threw), kept valid when the process runs again, as ICorDebug's own
    /// values aren't: an object (a string, an array, a box) is held by a strong handle (released when the process is
    /// continued), a primitive is read now and created again when it's passed to another call. A value type's value
    /// can't have a handle: it's valid until the next func-eval.
    /// </summary>
    public static CorValue Result(CorDebugTarget target, CorDebugThread thread, CorDebugValue value)
    {
        if (value.Raw is ICorDebugReferenceValue referenceRaw && new CorDebugReferenceValue(referenceRaw) is { IsNull: false } reference
            && reference.Dereference().Raw is ICorDebugHeapValue2 heap
            && heap.CreateHandle(CorDebugHandleType.HANDLE_STRONG, out var raw) == HRESULT.S_OK && raw is not null)
        {
            var handle = new CorDebugHandleValue(raw);
            target.Hold(handle);
            return new CorValue(target, handle, () => handle);
        }

        var result = new CorValue(target, value);
        if (result.Kind == ClrValueKind.Primitive && result.Primitive is { } primitive && IsCreatable(primitive))
        {
            return new CorValue(target, value, () => target.CreatePrimitive(thread, primitive), primitive);
        }

        return result;
    }

    private static bool IsCreatable(object value) => value is bool or char or sbyte or byte or short or ushort or int or uint or long or ulong or float or double;

    /// <summary>A value opened: a reference dereferenced (its address kept, for reference equality), a box opened, and its kind.</summary>
    private static (CorDebugValue Original, CorDebugValue Value, ClrValueKind Kind, ulong Address) Open(CorDebugValue value)
    {
        var original = value;
        ulong address = 0;
        if (value.Raw is ICorDebugReferenceValue referenceRaw)
        {
            var reference = new CorDebugReferenceValue(referenceRaw);
            if (reference.IsNull)
            {
                return (original, value, ClrValueKind.Null, 0);
            }

            address = reference.Value.Value;
            value = reference.Dereference();
        }

        if (value.Raw is ICorDebugBoxValue box)
        {
            value = new CorDebugBoxValue(box).Object;
        }

        var kind = value.Type switch
        {
            CorElementType.String => ClrValueKind.String,
            CorElementType.SZArray or CorElementType.Array => ClrValueKind.Array,
            CorElementType.Boolean or CorElementType.Char or CorElementType.I1 or CorElementType.U1 or CorElementType.I2 or CorElementType.U2
                or CorElementType.I4 or CorElementType.U4 or CorElementType.I8 or CorElementType.U8 or CorElementType.R4 or CorElementType.R8
                or CorElementType.I or CorElementType.U or CorElementType.Ptr or CorElementType.FnPtr => value.Raw is ICorDebugGenericValue ? ClrValueKind.Primitive : ClrValueKind.Object,
            _ => ClrValueKind.Object,
        };
        return (original, value, kind, address);
    }

    private static ClrType TypeOfValue(CorDebugTarget target, CorDebugValue value)
    {
        try
        {
            if (value.ExactType is { } exact)
            {
                return TypeOf(target, exact);
            }
        }
        catch (COMException)
        {
        }

        return Named(value.Type);
    }

    public static ClrType TypeOf(CorDebugTarget target, CorDebugType type)
    {
        switch (type.Type)
        {
            case CorElementType.Class or CorElementType.ValueType:
                var cls = type.Class;
                return new ClrType(target.Module(cls.Module), (int)cls.Token.Value, null, [.. type.TypeParameters.Select(t => TypeOf(target, t))]) { Native = type, BaseOf = () => BaseOf(target, type) };
            case CorElementType.SZArray:
                return ClrType.ArrayOf(TypeOf(target, type.FirstTypeParameter)) with { Native = type };
            case CorElementType.Array:
                return ClrType.ArrayOf(TypeOf(target, type.FirstTypeParameter), type.Rank) with { Native = type };
            default:
                return Named(type.Type) with { Native = type };
        }
    }

    private static ClrType? BaseOf(CorDebugTarget target, CorDebugType type)
    {
        try
        {
            return type.Base is { } baseType ? TypeOf(target, baseType) : null;
        }
        catch (COMException)
        {
            return null;
        }
    }

    private static ClrType Named(CorElementType type) => ClrType.Named(type switch
    {
        CorElementType.Boolean => "System.Boolean",
        CorElementType.Char => "System.Char",
        CorElementType.I1 => "System.SByte",
        CorElementType.U1 => "System.Byte",
        CorElementType.I2 => "System.Int16",
        CorElementType.U2 => "System.UInt16",
        CorElementType.I4 => "System.Int32",
        CorElementType.U4 => "System.UInt32",
        CorElementType.I8 => "System.Int64",
        CorElementType.U8 => "System.UInt64",
        CorElementType.R4 => "System.Single",
        CorElementType.R8 => "System.Double",
        CorElementType.String => "System.String",
        CorElementType.I or CorElementType.Ptr or CorElementType.FnPtr => "System.IntPtr",
        CorElementType.U => "System.UIntPtr",
        _ => "System.Object",
    });

    public static CorElementType ElementTypeOf(object value) => value switch
    {
        bool => CorElementType.Boolean,
        char => CorElementType.Char,
        sbyte => CorElementType.I1,
        byte => CorElementType.U1,
        short => CorElementType.I2,
        ushort => CorElementType.U2,
        int => CorElementType.I4,
        uint => CorElementType.U4,
        long => CorElementType.I8,
        ulong => CorElementType.U8,
        float => CorElementType.R4,
        double => CorElementType.R8,
        _ => throw new ClrException($"A {value.GetType().Name} can't be created in the debuggee."),
    };

    private static unsafe object Read(CorDebugGenericValue value, CorElementType type)
    {
        var bytes = new byte[Math.Max(8, value.Size)];
        fixed (byte* pointer = bytes)
        {
            value.GetValue((IntPtr)pointer);
        }

        return type switch
        {
            CorElementType.Boolean => bytes[0] != 0,
            CorElementType.Char => BitConverter.ToChar(bytes),
            CorElementType.I1 => (sbyte)bytes[0],
            CorElementType.U1 => bytes[0],
            CorElementType.I2 => BitConverter.ToInt16(bytes),
            CorElementType.U2 => BitConverter.ToUInt16(bytes),
            CorElementType.I4 => BitConverter.ToInt32(bytes),
            CorElementType.U4 => BitConverter.ToUInt32(bytes),
            CorElementType.I8 => BitConverter.ToInt64(bytes),
            CorElementType.U8 => BitConverter.ToUInt64(bytes),
            CorElementType.R4 => BitConverter.ToSingle(bytes),
            CorElementType.R8 => BitConverter.ToDouble(bytes),
            CorElementType.U => value.Size == 8 ? new UIntPtr(BitConverter.ToUInt64(bytes)) : new UIntPtr(BitConverter.ToUInt32(bytes)),
            _ => value.Size == 8 ? new IntPtr(BitConverter.ToInt64(bytes)) : new IntPtr(BitConverter.ToInt32(bytes)),
        };
    }

    public static unsafe void Write(CorDebugGenericValue target, object value)
    {
        var bytes = value switch
        {
            bool b => [(byte)(b ? 1 : 0)],
            char c => BitConverter.GetBytes(c),
            sbyte sb => [(byte)sb],
            byte by => [by],
            short s => BitConverter.GetBytes(s),
            ushort us => BitConverter.GetBytes(us),
            int i => BitConverter.GetBytes(i),
            uint ui => BitConverter.GetBytes(ui),
            long l => BitConverter.GetBytes(l),
            ulong ul => BitConverter.GetBytes(ul),
            float f => BitConverter.GetBytes(f),
            double d => BitConverter.GetBytes(d),
            _ => throw new ClrException($"A {value.GetType().Name} can't be written to the debuggee."),
        };

        var buffer = new byte[Math.Max(bytes.Length, target.Size)];
        bytes.CopyTo(buffer, 0);
        fixed (byte* pointer = buffer)
        {
            target.SetValue((IntPtr)pointer);
        }
    }
}

[SupportedOSPlatform("windows")]
internal static partial class NativeMethods
{
    private const int StdInput = -10;
    private const int StdOutput = -11;
    private const int StdError = -12;
    private const uint HandleFlagInherit = 1;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWow64Process(IntPtr process, [MarshalAs(UnmanagedType.Bool)] out bool wow64);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr GetStdHandle(int which);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetHandleInformation(IntPtr handle, uint mask, uint flags);

    /// <summary>
    /// Keeps this process's standard handles (the DAP pipes to the IDE) out of the programs it starts: a debuggee
    /// started with inherited handles would otherwise hold the IDE's pipe open after the adapter exits.
    /// </summary>
    public static void DoNotInheritStandardHandles()
    {
        foreach (var which in (int[])[StdInput, StdOutput, StdError])
        {
            var handle = GetStdHandle(which);
            if (handle != IntPtr.Zero && handle != new IntPtr(-1))
            {
                SetHandleInformation(handle, HandleFlagInherit, 0);
            }
        }
    }
}
