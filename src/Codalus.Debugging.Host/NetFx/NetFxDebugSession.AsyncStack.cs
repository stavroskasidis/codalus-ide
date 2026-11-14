namespace Codalus.Debugging.Host.NetFx;

/// <summary>
/// The async call stack (M61b, ADR 0100): below a thread's frames, the state machines awaiting the async method it
/// runs, read from the tasks' continuations as the classic IDE reads them. A method resumed by the thread pool is
/// called by the pool, not by the method awaiting it, so the thread's stack alone ends in the pool's code.
/// </summary>
internal sealed partial class NetFxDebugSession
{
    /// <summary>What a frame of the async call stack is named by: "[Async] Ns.Type.Method".</summary>
    public const string AsyncFramePrefix = "[Async] ";

    /// <summary>The longest chain of awaiting calls read: a cycle can't come from the runtime, but the read is bounded all the same.</summary>
    private const int MaxAsyncCallers = 64;

    /// <summary>The awaiting callers of each thread's stack read since the stop, with the handles that hold their state machines.</summary>
    private readonly Dictionary<int, IReadOnlyList<AsyncCallerFrame>> _asyncCallers = [];
    private readonly List<Action> _asyncCallerHandles = [];

    /// <summary>
    /// An awaiting caller, as a frame (M61b): the <c>MoveNext</c> of its state machine, at the await it waits in. It is
    /// on no thread's stack (or on another thread's, not yet waiting), so it has no locals, registers or machine code of
    /// its own: its variables are its state machine's fields (hoisted), read through <see cref="Argument"/> as
    /// <see cref="Context"/> reads any state machine's. Expressions run on <see cref="Thread"/>, the stopped thread
    /// whose stack it continues.
    /// </summary>
    private sealed class AsyncCallerFrame(IClrThread thread, IClrModule module, int moveNext, int? ilOffset, Func<IClrValue?> machine, IReadOnlyList<ClrType> typeArguments) : IClrFrame
    {
        public IClrThread Thread { get; } = thread;

        public IClrModule? Module { get; } = module;

        public int MethodToken { get; } = moveNext;

        /// <summary>The await it waits in (its yield offset), or 0 when that isn't known (<see cref="HasLocation"/>).</summary>
        public int IlOffset { get; } = ilOffset ?? 0;

        public bool HasLocation { get; } = ilOffset is not null;

        public string? Description => null;

        public IReadOnlyList<ClrType> TypeArguments { get; } = typeArguments;

        /// <summary>The state machine (<c>this</c> of <c>MoveNext</c>), read again each time through what holds it.</summary>
        public IClrValue? Argument(int index) => index == 0 ? machine() : null;

        public IClrValue? Local(int slot) => null;

        public void SetIp(int ilOffset) => throw new ClrException("An awaiting caller isn't running: its next statement can't be set.");
    }

    /// <summary>
    /// The awaiting callers of <paramref name="thread"/>'s stack (M61b), read once per stop. They are found from the
    /// outermost state machine on the stack whose task something awaits: its task's continuation names the state machine
    /// awaiting it, whose own task names the next, as far as the chain goes. A method started by its caller and not yet
    /// suspended has its caller on the stack, and no task yet.
    /// </summary>
    private IReadOnlyList<AsyncCallerFrame> AsyncCallers(IClrThread thread, IReadOnlyList<IClrFrame> frames)
    {
        if (_asyncCallers.TryGetValue(thread.Id, out var known))
        {
            return known;
        }

        IReadOnlyList<AsyncCallerFrame> callers = [];
        try
        {
            for (var i = frames.Count - 1; i >= 0 && callers.Count == 0; i--)
            {
                var frame = frames[i];
                if (frame.Module is { } module && Types.Info(module)?.AsyncKind(frame.MethodToken) == ClrAsyncKind.StateMachine
                    && SafeValue(() => frame.Argument(0)) is { Kind: ClrValueKind.Object } machine)
                {
                    callers = AwaitingChain(thread, machine);
                }
            }
        }
        catch (InvalidOperationException ex)
        {
            log.WriteLine("The async call stack: " + ex.Message);
        }

        _asyncCallers[thread.Id] = callers;
        return callers;
    }

    /// <summary>
    /// The state machines awaiting <paramref name="awaited"/>'s task, the next first, each as a frame. One found on
    /// another thread's stack, not suspended yet, has no task: its callers are the state machines below it there.
    /// </summary>
    private List<AsyncCallerFrame> AwaitingChain(IClrThread thread, IClrValue awaited)
    {
        var result = new List<AsyncCallerFrame>();
        var seen = new HashSet<ulong>();
        var current = awaited;
        StackCaller? onStack = null;
        while (result.Count < MaxAsyncCallers)
        {
            IClrValue? holder = null;
            IClrValue? machine = null;
            IClrFrame? running = null;
            var currentTask = MachineTask(current);

            // By its task, or by itself on a stack (a struct state machine, in its box, has no address of its own).
            if (!seen.Add(currentTask is { Address: not 0 and var taskAddress } ? taskAddress : current.Address))
            {
                break;
            }

            if (currentTask is { Kind: ClrValueKind.Object, Address: not 0 } task)
            {
                onStack = null;
                if (SafeValue(() => Types.Field(task, "m_continuationObject")) is { Kind: not ClrValueKind.Null } continuation)
                {
                    (holder, machine) = ContinuationMachine(continuation, depth: 0) ?? default;
                }
                else
                {
                    // Its awaiting call hasn't handed the task its continuation yet (M61's race), or hasn't even taken
                    // it (M61b): it is on another thread's stack.
                    onStack = AwaiterOnAStack(task, thread, current);
                }
            }
            else if (onStack is not null)
            {
                // Running on another thread's stack, called by the state machine below it there.
                onStack = CallerMachine(onStack.Stack, onStack.Index + 1);
            }

            if (onStack is not null)
            {
                (running, machine, holder) = (onStack.Frame, onStack.Machine, onStack.Machine);
            }

            if (machine is not { Kind: ClrValueKind.Object } || holder is null || Caller(thread, holder, machine, running) is not { } caller)
            {
                break;
            }

            result.Add(caller);
            current = machine;
        }

        return result;
    }

    /// <summary>
    /// The state machine a task's continuation runs (M61b), and the object on the heap that holds it (to keep it beyond
    /// the stop): a state machine's box (<c>AsyncStateMachineBox</c>'s <c>StateMachine</c>; the .NET Framework's
    /// <c>MoveNextRunner</c>'s <c>m_stateMachine</c>), a delegate whose target is one (<c>MoveNextAction</c>), what wraps
    /// one (<c>ContinuationWrapper</c>; <c>AwaitTaskContinuation</c> for a synchronization context or a task scheduler),
    /// or the first of a list of continuations that is one. Null for any other continuation (<c>ContinueWith</c>, a
    /// blocking wait, the runtime's own for runtime async).
    /// </summary>
    // TODO(issue): through Task.WhenAll and WhenAny (the combinator's task awaits each one: the classic IDE shows the
    // call awaiting the combinator), and ContinueWith's task; the chain ends there for now (ADR 0100, M61b).
    private (IClrValue Holder, IClrValue Machine)? ContinuationMachine(IClrValue continuation, int depth)
    {
        if (depth > 8 || continuation is not { Kind: ClrValueKind.Object or ClrValueKind.Array, Address: not 0 })
        {
            return null;
        }

        if (continuation.Kind == ClrValueKind.Array)
        {
            return null;
        }

        if ((SafeValue(() => Types.Field(continuation, "StateMachine")) ?? SafeValue(() => Types.Field(continuation, "m_stateMachine"))) is { Kind: ClrValueKind.Object } machine)
        {
            return (continuation, machine);
        }

        // A delegate's target; a wrapper's continuation; an await continuation's action.
        foreach (var field in (string[])["_target", "_continuation", "m_continuation", "m_action"])
        {
            if (SafeValue(() => Types.Field(continuation, field)) is { Kind: ClrValueKind.Object } inner && ContinuationMachine(inner, depth + 1) is { } found)
            {
                return found;
            }
        }

        // Several continuations: a List<object>.
        if (SafeValue(() => Types.Field(continuation, "_items")) is { Kind: ClrValueKind.Array } items
            && SafeValue(() => Types.Field(continuation, "_size"))?.Primitive is int size)
        {
            for (var i = 0; i < Math.Min(size, items.Length); i++)
            {
                var index = i;
                if (SafeValue(() => items.Element(index)) is { } item && ContinuationMachine(item, depth + 1) is { } found)
                {
                    return found;
                }
            }
        }

        return null;
    }

    /// <summary>A state machine's task (its builder's), or null when it has none yet (it hasn't suspended).</summary>
    private IClrValue? MachineTask(IClrValue machine) =>
        SafeValue(() => Types.Field(machine, "<>t__builder")) is { } builder
            ? SafeValue(() => Types.Field(builder, "m_task") ?? (Types.Field(builder, "m_builder") is { } inner ? Types.Field(inner, "m_task") : null))
            : null;

    /// <summary>
    /// The frame an awaiting state machine is shown as: its <c>MoveNext</c>, at the await its state names (the PDB's
    /// yield offset of that await), or where it runs when it is on another thread's stack. The state machine is read
    /// through a strong handle on <paramref name="holder"/> (its box, or itself), kept until the program runs on.
    /// </summary>
    private AsyncCallerFrame? Caller(IClrThread thread, IClrValue holder, IClrValue machine, IClrFrame? running)
    {
        if (Types.Definition(machine.Type) is not { } type || Types.Info(type.Module) is not { } info
            || info.Methods(type.Token).FirstOrDefault(m => m.Name == "MoveNext") is not { } moveNext
            || info.AsyncKind(moveNext.Token) != ClrAsyncKind.StateMachine)
        {
            return null;
        }

        Func<IClrValue?> read;
        if (ReferenceEquals(holder, machine))
        {
            if (Process.Keep(machine, out var release) is not { } kept)
            {
                return null;
            }

            _asyncCallerHandles.Add(release);
            read = () => kept;
        }
        else
        {
            if (Process.Keep(holder, out var release) is not { } kept)
            {
                return null;
            }

            _asyncCallerHandles.Add(release);
            var field = SafeValue(() => Types.Field(kept, "StateMachine")) is not null ? "StateMachine" : "m_stateMachine";
            read = () => Types.Field(kept, field);
        }

        int? offset = null;
        if (SafeValue(() => Types.Field(machine, "<>1__state"))?.Primitive is int state && state >= 0
            && info.AsyncYieldOffsets(moveNext.Token) is var yields && state < yields.Count)
        {
            offset = yields[state];
        }
        else if (running is not null && ReferenceEquals(running.Module, type.Module) && running.MethodToken == moveNext.Token)
        {
            offset = running.IlOffset;
        }

        return new AsyncCallerFrame(thread, type.Module, moveNext.Token, offset, read, machine.Type.TypeArguments);
    }

    /// <summary>
    /// The DAP frames of the async call stack (M61b): each awaiting caller by its method's name (<c>Program.Main</c>,
    /// not <c>Program.&lt;Main&gt;d__0.MoveNext</c>) after <see cref="AsyncFramePrefix"/>, at its await's line; one
    /// that isn't the user's code is <c>subtle</c> under Just My Code, as the thread's own frames are.
    /// </summary>
    private StackFrame AsyncStackFrame(int id, AsyncCallerFrame frame)
    {
        var module = frame.Module!;
        var info = Types.Info(module);
        var name = info is null ? $"[{Path.GetFileName(module.Path)}]" : AsyncMethodName(info, frame.MethodToken);
        var point = info is not null && frame.HasLocation ? info.LineAt(frame.MethodToken, frame.IlOffset) : null;
        var file = point?.Document;
        var hasSource = file is { Length: > 0 } && File.Exists(file);
        var userCode = info is { HasSymbols: true, IsOptimized: false } && !info.IsNonUserCode(frame.MethodToken);
        return new StackFrame(
            id,
            AsyncFramePrefix + name,
            hasSource ? new Source(Path.GetFileName(file), file) : null,
            hasSource ? point!.Value.StartLine : 0,
            hasSource ? Math.Max(1, point!.Value.StartColumn) : 0,
            hasSource ? point!.Value.EndLine : null,
            hasSource ? point!.Value.EndColumn : null,
            PresentationHint: hasSource && (userCode || !_justMyCode) ? null : "subtle",
            ModuleId: ModuleId(module));
    }

    /// <summary>A state machine's <c>MoveNext</c> by the method it is: its outer type and the name in <c>&lt;Name&gt;d__N</c>.</summary>
    private static string AsyncMethodName(ClrModuleInfo info, int moveNext)
    {
        var name = info.MethodDisplayName(moveNext);
        return name.LastIndexOf(".<", StringComparison.Ordinal) is var at and > 0 && name.IndexOf('>', at) is var close and > 0
            ? name[..at] + "." + name[(at + 2)..close]
            : name;
    }

    /// <summary>Lets go of the awaiting callers read since the stop (the program runs on, or the frames are read anew).</summary>
    private void ClearAsyncCallers()
    {
        _asyncCallers.Clear();
        foreach (var release in _asyncCallerHandles)
        {
            release();
        }

        _asyncCallerHandles.Clear();
    }
}
