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

/// <summary>One debugged desktop-CLR process through ICorDebug (M43b): its callbacks become <see cref="ClrEvent"/>s.</summary>
[SupportedOSPlatform("windows")]
internal sealed class CorDebugTarget : IClrProcess
{
    private readonly CorDebug _corDebug;
    private readonly CorDebugManagedCallback _callback = new();
    private readonly Channel<ClrEvent> _events = Channel.CreateUnbounded<ClrEvent>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Lock _gate = new();
    private readonly Dictionary<(ulong, string), CorModule> _modules = [];
    private readonly Dictionary<int, CorThread> _threads = [];
    private readonly Dictionary<(ulong, int, int), CorBreakpoint> _breakpoints = [];
    private CorDebugProcess? _process;
    private System.Diagnostics.Process? _os;
    private CorDebugStepper? _stepper;
    private TaskCompletionSource<bool>? _eval;
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

    private CorDebugProcess Process => _process ?? throw new ClrException("The process isn't running.");

    // ------------------------------------------------------------------ starting

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
                Pump(stdout, "stdout", output);
                Pump(stderr, "stderr", output);
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

    private static void Pump(AnonymousPipeServerStream pipe, string category, Action<string, string> output) =>
        _ = Task.Run(async () =>
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
        catch (COMException ex)
        {
            _events.Writer.TryWrite(new ClrNotice($"[debugger] {e.Kind}: {ex.Message}") { Continued = true });
            ContinueFromCallback(e);
        }
    }

    private void Dispatch(CorDebugManagedCallbackEventArgs e)
    {
        var evaluating = _eval is not null;
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
            if (_os is { } os && os.WaitForExit(5000))
            {
                code = os.ExitCode;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }

        _eval?.TrySetCanceled();
        _events.Writer.TryWrite(new ClrExited(code));
        _events.Writer.TryComplete();
    }

    // ------------------------------------------------------------------ control

    public void Continue()
    {
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

            _process.Detach();
        });
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
            var function = corModule.Raw.GetFunctionFromToken(new mdMethodDef(methodToken));
            var breakpoint = new CorBreakpoint(function.ILCode.CreateBreakpoint(ilOffset));
            breakpoint.Activate(true);
            lock (_gate)
            {
                _breakpoints[(corModule.Raw.BaseAddress.Value, methodToken, ilOffset)] = breakpoint;
            }

            return (IClrBreakpoint)breakpoint;
        });

    private CorBreakpoint? Breakpoint(CorDebugBreakpoint breakpoint)
    {
        if (breakpoint.Raw is not ICorDebugFunctionBreakpoint raw)
        {
            return null;
        }

        var function = new CorDebugFunctionBreakpoint(raw);
        var key = (function.Function.Module.BaseAddress.Value, (int)function.Function.Token.Value, function.Offset);
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
        Evaluate(thread, timeout, eval =>
        {
            var function = ((CorModule)module).Raw.GetFunctionFromToken(new mdMethodDef(methodToken));
            var types = typeArguments.Select(t => t.Native as CorDebugType).ToList();
            var values = arguments.Select(a => ((CorValue)a).Original.Raw).ToArray();
            if (types.Count > 0 && types.All(t => t is not null))
            {
                eval.CallParameterizedFunction(function.Raw, types.Count, [.. types.Select(t => t!.Raw)], values.Length, values);
            }
            else
            {
                eval.CallFunction(function.Raw, values.Length, values);
            }
        });

    public IClrValue NewString(IClrThread thread, string text, TimeSpan timeout) =>
        Evaluate(thread, timeout, eval => eval.NewString(text));

    public IClrValue NewPrimitive(IClrThread thread, object value) =>
        Translate(() =>
        {
            var eval = ((CorThread)thread).Raw.CreateEval();
            var created = eval.CreateValue(CorValue.ElementTypeOf(value), null);
            var generic = new CorDebugGenericValue((ICorDebugGenericValue)created.Raw);
            CorValue.Write(generic, value);
            return CorValue.Wrap(this, created);
        });

    /// <summary>Runs a func-eval on <paramref name="thread"/>, the other threads suspended, and waits for it (aborting it after <paramref name="timeout"/>).</summary>
    private CorValue Evaluate(IClrThread thread, TimeSpan timeout, Action<CorDebugEval> start)
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
                    TryAbort(eval, completion);
                    throw new TimeoutException();
                }

                completed = completion.Task.Result;
            }
            catch (AggregateException ex) when (ex.InnerException is TaskCanceledException)
            {
                throw new ClrException("The process ended during the evaluation.");
            }

            var result = Translate(() => CorValue.Wrap(this, eval.Result));
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

    private static void TryAbort(CorDebugEval eval, TaskCompletionSource<bool> completion)
    {
        try
        {
            eval.Abort();
            if (!completion.Task.Wait(TimeSpan.FromSeconds(3)))
            {
                eval.RudeAbort();
                completion.Task.Wait(TimeSpan.FromSeconds(3));
            }
        }
        catch (Exception ex) when (ex is COMException or AggregateException)
        {
        }
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
    }
}

[SupportedOSPlatform("windows")]
internal sealed class CorModule(CorDebugModule module) : IClrModule
{
    public CorDebugModule Raw { get; } = module;

    public string Path { get; } = module.IsDynamic || module.IsInMemory ? string.Empty : module.Name ?? string.Empty;

    public bool IsDynamic { get; } = module.IsDynamic || module.IsInMemory;
}

[SupportedOSPlatform("windows")]
internal sealed class CorBreakpoint(CorDebugFunctionBreakpoint breakpoint) : IClrBreakpoint
{
    public void Activate(bool active) => CorDebugTarget.Translate(() => breakpoint.Activate(active));
}

[SupportedOSPlatform("windows")]
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

    /// <summary>The IL frame at <paramref name="index"/> now (after a func-eval, frames are read again).</summary>
    public CorDebugILFrame? IlFrame(int index) => Frames().ElementAtOrDefault(index) is CorFrame { Cached: { } il } ? il : null;

    public IClrValue? CurrentException() => CorDebugTarget.Translate(() => Raw.CurrentException is { } value ? CorValue.Wrap(target, value) : null);

    public IClrValue? ThreadObject() => CorDebugTarget.Translate(() => Raw.Object is { } value ? CorValue.Wrap(target, value) : null);
}

[SupportedOSPlatform("windows")]
internal sealed class CorFrame : IClrFrame
{
    private readonly CorThread _thread;
    private readonly int _index;
    private readonly int _generation;

    public CorFrame(CorThread thread, int index, CorDebugILFrame? frame, string? description)
    {
        _thread = thread;
        _index = index;
        Cached = frame;
        Description = description;
        _generation = thread.Target.Generation;
        if (frame is not null)
        {
            var function = frame.Function;
            Module = thread.Target.Module(function.Module);
            MethodToken = (int)function.Token.Value;
            IlOffset = frame.IP.pnOffset;
        }
    }

    public CorDebugILFrame? Cached { get; }

    public IClrThread Thread => _thread;

    public IClrModule? Module { get; }

    public int MethodToken { get; }

    public int IlOffset { get; }

    public string? Description { get; }

    /// <summary>The frame as ICorDebug has it now: the cached one until the process ran (a func-eval), then read again.</summary>
    private CorDebugILFrame Il => (_generation == _thread.Target.Generation ? Cached : _thread.IlFrame(_index)) ?? throw new ClrException("The frame is no longer valid.");

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

/// <summary>A value of the debuggee: references dereferenced and boxes opened, read lazily (M43b).</summary>
[SupportedOSPlatform("windows")]
internal sealed class CorValue : IClrValue
{
    private readonly CorDebugTarget _target;
    private readonly CorDebugValue _value;
    private readonly Lazy<ClrType> _type;

    private CorValue(CorDebugTarget target, CorDebugValue original, CorDebugValue value, ClrValueKind kind, ulong address)
    {
        _target = target;
        Original = original;
        _value = value;
        Kind = kind;
        Address = address;
        _type = new(() => TypeOfValue(target, value));
    }

    /// <summary>The value as ICorDebug gave it (a reference for objects): what a func-eval takes.</summary>
    public CorDebugValue Original { get; }

    public ClrValueKind Kind { get; }

    public ulong Address { get; }

    public ClrType Type => _type.Value;

    public bool IsValueType => Kind == ClrValueKind.Object && _value.Raw is ICorDebugObjectValue o && new CorDebugObjectValue(o).IsValueClass;

    public object? Primitive => Kind == ClrValueKind.Primitive ? CorDebugTarget.Translate(() => Read(new CorDebugGenericValue((ICorDebugGenericValue)_value.Raw), _value.Type)) : null;

    public string? Text => Kind == ClrValueKind.String ? CorDebugTarget.Translate(() =>
    {
        var text = new CorDebugStringValue((ICorDebugStringValue)_value.Raw);
        return text.GetString(text.Length);
    }) : null;

    private CorDebugArrayValue Array => new((ICorDebugArrayValue)_value.Raw);

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
        var objectValue = new CorDebugObjectValue((ICorDebugObjectValue)_value.Raw);
        return Wrap(_target, objectValue.GetFieldValue(cls.Raw, new mdFieldDef(fieldToken)));
    });

    public void SetPrimitive(object value) => CorDebugTarget.Translate(() =>
    {
        if (Kind != ClrValueKind.Primitive)
        {
            throw new ClrException("Only primitive values can be changed.");
        }

        Write(new CorDebugGenericValue((ICorDebugGenericValue)_value.Raw), value);
    });

    /// <summary>Wraps a value as ICorDebug gives it: null references, strings, arrays, primitives and objects (boxes opened).</summary>
    public static CorValue Wrap(CorDebugTarget target, CorDebugValue value)
    {
        var original = value;
        ulong address = 0;
        if (value.Raw is ICorDebugReferenceValue referenceRaw)
        {
            var reference = new CorDebugReferenceValue(referenceRaw);
            if (reference.IsNull)
            {
                return new CorValue(target, original, value, ClrValueKind.Null, 0);
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
        return new CorValue(target, original, value, kind, address);
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
                return new ClrType(target.Module(cls.Module), (int)cls.Token.Value, null, [.. type.TypeParameters.Select(t => TypeOf(target, t))]) { Native = type };
            case CorElementType.SZArray:
                return ClrType.ArrayOf(TypeOf(target, type.FirstTypeParameter)) with { Native = type };
            case CorElementType.Array:
                return ClrType.ArrayOf(TypeOf(target, type.FirstTypeParameter), type.Rank) with { Native = type };
            default:
                return Named(type.Type) with { Native = type };
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
