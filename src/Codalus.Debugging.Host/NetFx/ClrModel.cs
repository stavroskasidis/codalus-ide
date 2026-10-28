using System.Threading.Channels;

namespace Codalus.Debugging.Host.NetFx;

// The desktop-CLR adapter's view of a debugged process (M43b, ADR 0076): what NetFxDebugSession needs from ICorDebug,
// as small interfaces. CorDebugTarget implements them on Windows through ClrDebug's ICorDebug wrappers; the tests
// implement them with fakes, so everything above this line (breakpoints, stepping decisions, variables, evaluation, the
// DAP layer) runs and is tested on every OS. Metadata and symbols don't come from ICorDebug at all: the session reads
// the module's file and its portable PDB with System.Reflection.Metadata (ClrModuleInfo).

/// <summary>A debugged desktop-CLR process. Its events arrive in order; the process stays stopped after each until <see cref="Continue"/>.</summary>
internal interface IClrProcess : IDisposable
{
    int Id { get; }

    /// <summary>The runtime's version (<c>v4.0.30319</c>, <c>v2.0.50727</c>).</summary>
    string RuntimeVersion { get; }

    ChannelReader<ClrEvent> Events { get; }

    /// <summary>
    /// Counts the times the process ran: continued, stepped or a func-eval. ICorDebug neuters the values (and frames)
    /// it handed out when the process runs, so a value read before is read again after (<see cref="ClrLiveValue"/>).
    /// </summary>
    int Generation { get; }

    /// <summary>Lets the process run after an event or a pause.</summary>
    void Continue();

    /// <summary>Stops every thread (an asynchronous break); <see cref="Continue"/> lets it run again.</summary>
    void Pause();

    IReadOnlyList<IClrThread> Threads();

    /// <summary>A breakpoint at an IL offset of a method, active at once.</summary>
    IClrBreakpoint CreateBreakpoint(IClrModule module, int methodToken, int ilOffset);

    /// <summary>Starts a step on <paramref name="thread"/> (from its top frame) and lets the process run.</summary>
    void Step(IClrThread thread, ClrStep step);

    /// <summary>Cancels the step in progress, if any (a breakpoint or an exception stopped the program first).</summary>
    void CancelStep();

    /// <summary>Marks a module's code as the user's (Just My Code steppers stop only there) or not.</summary>
    void SetUserCode(IClrModule module, bool userCode);

    /// <summary>A static field's value (<paramref name="thread"/>'s top frame is its context).</summary>
    IClrValue GetStaticField(IClrThread thread, ClrType type, int fieldToken);

    /// <summary>
    /// Calls a method in the debuggee on <paramref name="thread"/> (func-eval), the other threads suspended, and waits
    /// for its result. Throws <see cref="ClrEvalException"/> when the method threw, and <see cref="TimeoutException"/>
    /// when it didn't return in time (it's aborted). The arguments may be <see cref="ClrLiveValue"/>s. The result (and
    /// the exception's value) stays valid when the process runs again: an object is held by a strong handle, a
    /// primitive is kept.
    /// </summary>
    IClrValue Call(IClrThread thread, IClrModule module, int methodToken, IReadOnlyList<ClrType> typeArguments, IReadOnlyList<IClrValue> arguments, TimeSpan timeout);

    /// <summary>A string created in the debuggee (func-eval); it stays valid, as a call's result does.</summary>
    IClrValue NewString(IClrThread thread, string text, TimeSpan timeout);

    /// <summary>A primitive value created in the debuggee, to pass to a call.</summary>
    IClrValue NewPrimitive(IClrThread thread, object value);

    /// <summary>Ends the process.</summary>
    void Terminate();

    /// <summary>Lets the process go on without the debugger.</summary>
    void Detach();
}

/// <summary>A module loaded in an application domain.</summary>
internal interface IClrModule
{
    /// <summary>The file it was loaded from; empty for dynamic and in-memory modules.</summary>
    string Path { get; }

    /// <summary>Dynamic (Reflection.Emit) or loaded from bytes: no file to read its metadata from.</summary>
    bool IsDynamic { get; }
}

internal interface IClrThread
{
    /// <summary>The operating system's thread id (DAP's thread id).</summary>
    int Id { get; }

    /// <summary>Its frames, innermost first (internal and native frames included, without a module).</summary>
    IReadOnlyList<IClrFrame> Frames();

    /// <summary>The exception the thread is throwing, while stopped on one.</summary>
    IClrValue? CurrentException();

    /// <summary>Its <c>System.Threading.Thread</c> object, for its name.</summary>
    IClrValue? ThreadObject();
}

internal interface IClrFrame
{
    IClrThread Thread { get; }

    /// <summary>The module of an IL frame; null for an internal or native frame.</summary>
    IClrModule? Module { get; }

    int MethodToken { get; }

    /// <summary>The IL offset the frame is at (or returns to).</summary>
    int IlOffset { get; }

    /// <summary>What a frame without a module is (<c>[Managed to Native Transition]</c>).</summary>
    string? Description { get; }

    /// <summary>The generic type's and method's arguments (class arguments first).</summary>
    IReadOnlyList<ClrType> TypeArguments { get; }

    /// <summary>An argument by index (<c>this</c> is 0 in an instance method); null when it isn't available.</summary>
    IClrValue? Argument(int index);

    /// <summary>A local variable by its slot; null when it isn't available (optimized code).</summary>
    IClrValue? Local(int slot);
}

internal enum ClrValueKind
{
    Null,
    Primitive,
    String,
    Array,

    /// <summary>An object or a value type's value (a struct, an enum, a boxed primitive whose type is a struct of the core library).</summary>
    Object,
}

/// <summary>
/// A value of the debuggee, as ICorDebug hands it out: valid until the process runs again (a func-eval included),
/// neutered after, except a func-eval's result. What the session keeps beyond that is a <see cref="ClrLiveValue"/>.
/// </summary>
internal interface IClrValue
{
    ClrValueKind Kind { get; }

    /// <summary>A primitive's value: bool, char, sbyte to ulong, float, double, IntPtr or UIntPtr.</summary>
    object? Primitive { get; }

    string? Text { get; }

    ClrType Type { get; }

    bool IsValueType { get; }

    /// <summary>An array's element count (all dimensions).</summary>
    int Length { get; }

    IReadOnlyList<int> Dimensions { get; }

    /// <summary>An array's element at a flat index.</summary>
    IClrValue Element(int index);

    /// <summary>An instance field, by its declaring type's module and token.</summary>
    IClrValue Field(IClrModule module, int typeToken, int fieldToken);

    /// <summary>Where a reference points (0 for null and values): reference equality.</summary>
    ulong Address { get; }

    /// <summary>Changes a primitive in place (a local, an argument, a field, an element).</summary>
    void SetPrimitive(object value);
}

/// <summary>
/// A value that outlives func-evals (M43b): ICorDebug neuters the values it handed out whenever the process runs, a
/// func-eval included, so a variable read before a getter ran (a local, <c>this</c>'s field, an array's element) is
/// read again from where it came from (the frame, its parent's field or element, the static field, the thread's
/// exception) once the process has run. The session wraps every value it keeps (variables to expand, an expression's
/// intermediate values, a stop's exception) in one; its fields and elements are live values too.
/// </summary>
internal sealed class ClrLiveValue : IClrValue
{
    private readonly IClrProcess _process;
    private readonly Func<IClrValue?> _read;
    private IClrValue _current;
    private int _generation;

    private ClrLiveValue(IClrProcess process, IClrValue current, Func<IClrValue?> read)
    {
        _process = process;
        _current = current;
        _read = read;
        _generation = process.Generation;
    }

    /// <summary>A value read now by <paramref name="read"/>, and again by it after the process ran; null when there's none.</summary>
    public static IClrValue? Read(IClrProcess process, Func<IClrValue?> read) =>
        read() is { } value ? Of(process, value, read) : null;

    /// <summary><paramref name="value"/>, just read, and read again by <paramref name="read"/> after the process ran.</summary>
    public static IClrValue Of(IClrProcess process, IClrValue value, Func<IClrValue?> read) =>
        value is ClrLiveValue ? value : new ClrLiveValue(process, value, read);

    /// <summary>The value ICorDebug has for a (live) value now: what a func-eval takes as an argument.</summary>
    public static IClrValue Unwrap(IClrValue value) => value is ClrLiveValue live ? live.Current : value;

    /// <summary>The value, read again when the process has run since it was read.</summary>
    public IClrValue Current
    {
        get
        {
            var generation = _process.Generation;
            if (generation != _generation)
            {
                _current = _read() is { } value ? Unwrap(value) : throw new ClrException("The value isn't available any more.");
                _generation = generation;
            }

            return _current;
        }
    }

    public ClrValueKind Kind => Current.Kind;

    public object? Primitive => Current.Primitive;

    public string? Text => Current.Text;

    public ClrType Type => Current.Type;

    public bool IsValueType => Current.IsValueType;

    public int Length => Current.Length;

    public IReadOnlyList<int> Dimensions => Current.Dimensions;

    public ulong Address => Current.Address;

    public IClrValue Element(int index) => new ClrLiveValue(_process, Current.Element(index), () => Current.Element(index));

    public IClrValue Field(IClrModule module, int typeToken, int fieldToken) =>
        new ClrLiveValue(_process, Current.Field(module, typeToken, fieldToken), () => Current.Field(module, typeToken, fieldToken));

    public void SetPrimitive(object value) => Current.SetPrimitive(value);
}

/// <summary>
/// A runtime type: a class or value type of a module (<see cref="Module"/> and <see cref="Token"/>, with its type
/// arguments); an array of <see cref="Element"/>; or a primitive, <c>string</c> or <c>object</c> by
/// <see cref="Name"/> (<c>System.Int32</c>), which the session finds in the core library.
/// </summary>
internal sealed record ClrType(IClrModule? Module, int Token, string? Name = null, IReadOnlyList<ClrType>? Arguments = null, ClrType? Element = null, int Rank = 0)
{
    public IReadOnlyList<ClrType> TypeArguments => Arguments ?? [];

    /// <summary>The runtime's own type object (ICorDebugType), to pass back to it; not part of the type's identity.</summary>
    public object? Native { get; init; }

    public bool Equals(ClrType? other) =>
        other is not null && ReferenceEquals(Module, other.Module) && Token == other.Token && Name == other.Name && Rank == other.Rank
        && Equals(Element, other.Element) && TypeArguments.SequenceEqual(other.TypeArguments);

    public override int GetHashCode() => HashCode.Combine(Token, Name, Rank);

    public bool IsArray => Element is not null;

    public static ClrType Named(string name) => new(null, 0, name);

    public static ClrType ArrayOf(ClrType element, int rank = 1) => new(null, 0, null, null, element, rank);
}

internal interface IClrBreakpoint
{
    void Activate(bool active);
}

/// <summary>A step: into, over or out; <see cref="Ranges"/> are the IL ranges of the current line (over and into).</summary>
internal sealed record ClrStep(ClrStepKind Kind, IReadOnlyList<(int Start, int End)> Ranges, bool JustMyCode);

internal enum ClrStepKind
{
    Into,
    Over,
    Out,
}

/// <summary>An exception notification's stage (ICorDebug's <c>CorDebugExceptionCallbackType</c>).</summary>
internal enum ClrExceptionStage
{
    FirstChance,

    /// <summary>First chance, thrown or passing through user code (Just My Code).</summary>
    UserFirstChance,
    CatchHandlerFound,
    Unhandled,
}

internal abstract record ClrEvent
{
    /// <summary>The process went on already (an event during a func-eval): the session mustn't continue it.</summary>
    public bool Continued { get; init; }
}

internal sealed record ClrModuleLoaded(IClrModule Module) : ClrEvent;

internal sealed record ClrModuleUnloaded(IClrModule Module) : ClrEvent;

internal sealed record ClrThreadStarted(IClrThread Thread) : ClrEvent;

internal sealed record ClrThreadExited(IClrThread Thread) : ClrEvent;

internal sealed record ClrBreakpointHit(IClrThread Thread, IClrBreakpoint Breakpoint) : ClrEvent;

internal sealed record ClrStepCompleted(IClrThread Thread) : ClrEvent;

/// <summary><c>Debugger.Break()</c>.</summary>
internal sealed record ClrUserBreak(IClrThread Thread) : ClrEvent;

internal sealed record ClrExceptionThrown(IClrThread Thread, ClrExceptionStage Stage) : ClrEvent;

/// <summary><c>Debugger.Log</c> (and <c>Debug.WriteLine</c> through the default trace listener).</summary>
internal sealed record ClrLogMessage(string Message) : ClrEvent;

/// <summary>Something the session reports and lets run (the process isn't waiting for <see cref="IClrProcess.Continue"/>).</summary>
internal sealed record ClrNotice(string Message) : ClrEvent;

/// <summary>The process ended; no <see cref="IClrProcess.Continue"/> follows.</summary>
internal sealed record ClrExited(int? ExitCode) : ClrEvent;

/// <summary>A failure of the debuggee's runtime, with the message to show.</summary>
internal class ClrException(string message, Exception? inner = null) : InvalidOperationException(message, inner);

/// <summary>A func-eval threw <see cref="Exception"/> in the debuggee.</summary>
internal sealed class ClrEvalException(IClrValue exception, string message) : ClrException(message)
{
    public IClrValue Exception { get; } = exception;
}
