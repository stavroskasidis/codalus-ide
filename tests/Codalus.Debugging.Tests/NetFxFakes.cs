using System.Threading.Channels;
using Codalus.Debugging.Host.NetFx;

namespace Codalus.Debugging.Tests;

// A stand-in for ICorDebug (M43b): the desktop-CLR adapter's session runs on every OS against these, with the real
// metadata and portable PDB of samples/Debugging/NetFxDebuggee.

internal sealed class FakeDebugger(FakeProcess process) : IClrDebugger
{
    public NetFxDebugArguments? Launched { get; private set; }

    public int? Attached { get; private set; }

    public Action<string, string>? Output { get; private set; }

    public IClrProcess Launch(NetFxDebugArguments arguments, Action<string, string> output)
    {
        Launched = arguments;
        Output = output;
        return process;
    }

    public IClrProcess Attach(int processId)
    {
        Attached = processId;
        return process;
    }
}

internal sealed class FakeModule(string path) : IClrModule
{
    public string Path { get; } = path;

    public bool IsDynamic => false;
}

internal sealed class FakeBreakpoint(IClrModule module, int method, int offset) : IClrBreakpoint
{
    public IClrModule Module { get; } = module;

    public int Method { get; } = method;

    public int Offset { get; } = offset;

    public bool Active { get; private set; } = true;

    public void Activate(bool active) => Active = active;
}

internal sealed class FakeThread(int id) : IClrThread
{
    public int Id { get; } = id;

    public List<IClrFrame> FrameList { get; } = [];

    public IClrValue? Exception { get; set; }

    public IReadOnlyList<IClrFrame> Frames() => [.. FrameList];

    public IClrValue? CurrentException() => Exception;

    public IClrValue? ThreadObject() => null;
}

internal sealed class FakeFrame(IClrThread thread, IClrModule? module, int method, int offset) : IClrFrame
{
    public IClrThread Thread { get; } = thread;

    public IClrModule? Module { get; } = module;

    public int MethodToken { get; } = method;

    public int IlOffset { get; } = offset;

    public string? Description { get; init; }

    public IReadOnlyList<ClrType> TypeArguments => [];

    public List<IClrValue?> Arguments { get; } = [];

    public Dictionary<int, IClrValue> Locals { get; } = [];

    public IClrValue? Argument(int index) => index < Arguments.Count ? Arguments[index] : null;

    public IClrValue? Local(int slot) => Locals.GetValueOrDefault(slot);
}

internal sealed class FakeValue : IClrValue
{
    private static ulong s_address = 0x1000;

    private FakeValue(ClrValueKind kind, ClrType type)
    {
        Kind = kind;
        Type = type;
        Address = kind is ClrValueKind.Object or ClrValueKind.Array or ClrValueKind.String ? Interlocked.Add(ref s_address, 0x10) : 0;
    }

    public ClrValueKind Kind { get; }

    public object? Primitive { get; private set; }

    public string? Text { get; private init; }

    public ClrType Type { get; }

    public bool IsValueType { get; private init; }

    public List<IClrValue> Items { get; } = [];

    public Dictionary<int, IClrValue> Fields { get; } = [];

    public int Length => Items.Count;

    public IReadOnlyList<int> Dimensions => Kind == ClrValueKind.Array ? [Items.Count] : [];

    public ulong Address { get; }

    public static FakeValue Of(object value) => new(ClrValueKind.Primitive, ClrType.Named(value switch
    {
        bool => "System.Boolean",
        char => "System.Char",
        long => "System.Int64",
        double => "System.Double",
        _ => "System.Int32",
    }))
    { Primitive = value };

    public static FakeValue String(string text) => new(ClrValueKind.String, ClrType.Named("System.String")) { Text = text };

    public static FakeValue Null() => new(ClrValueKind.Null, ClrType.Named("System.Object"));

    public static FakeValue Object(IClrModule module, int typeToken, bool valueType = false) => new(ClrValueKind.Object, new ClrType(module, typeToken)) { IsValueType = valueType };

    public static FakeValue Array(ClrType element, params IClrValue[] items)
    {
        var array = new FakeValue(ClrValueKind.Array, ClrType.ArrayOf(element));
        array.Items.AddRange(items);
        return array;
    }

    public FakeValue With(int fieldToken, IClrValue value)
    {
        Fields[fieldToken] = value;
        return this;
    }

    public IClrValue Element(int index) => Items[index];

    public IClrValue Field(IClrModule module, int typeToken, int fieldToken) =>
        Fields.TryGetValue(fieldToken, out var value) ? value : throw new ClrException($"The fake has no field {fieldToken:X8}.");

    public void SetPrimitive(object value) => Primitive = Kind == ClrValueKind.Primitive ? value : throw new ClrException("Not a primitive.");
}

internal sealed class FakeProcess : IClrProcess
{
    public Channel<ClrEvent> Channel { get; } = System.Threading.Channels.Channel.CreateUnbounded<ClrEvent>();

    public int Id => 4242;

    public string RuntimeVersion => "v4.0.30319";

    public ChannelReader<ClrEvent> Events => Channel.Reader;

    public List<FakeThread> ThreadList { get; } = [];

    public List<FakeBreakpoint> Breakpoints { get; } = [];

    public List<ClrStep> Steps { get; } = [];

    public List<(IClrModule Module, bool UserCode)> UserCode { get; } = [];

    public Dictionary<int, IClrValue> StaticFields { get; } = [];

    /// <summary>Func-eval: the method's module, token and arguments → its result.</summary>
    public Func<IClrModule, int, IReadOnlyList<IClrValue>, IClrValue>? OnCall { get; set; }

    public List<int> Calls { get; } = [];

    public int Continues;

    public int Pauses { get; private set; }

    public int CancelledSteps { get; private set; }

    public bool Terminated { get; private set; }

    public bool Detached { get; private set; }

    public void Send(ClrEvent e) => Channel.Writer.TryWrite(e);

    /// <summary>Waits until the session has continued the process <paramref name="total"/> times in all.</summary>
    public async Task WaitForContinuesAsync(int total, CancellationToken cancellationToken)
    {
        for (var i = 0; i < 300 && Volatile.Read(ref Continues) < total; i++)
        {
            await Task.Delay(100, cancellationToken);
        }

        Assert.True(Volatile.Read(ref Continues) >= total, $"The process was continued {Continues} times, not {total}.");
    }

    public void Continue() => Interlocked.Increment(ref Continues);

    public void Pause() => Pauses++;

    public IReadOnlyList<IClrThread> Threads() => [.. ThreadList];

    public IClrBreakpoint CreateBreakpoint(IClrModule module, int methodToken, int ilOffset)
    {
        var breakpoint = new FakeBreakpoint(module, methodToken, ilOffset);
        lock (Breakpoints)
        {
            Breakpoints.Add(breakpoint);
        }

        return breakpoint;
    }

    public void Step(IClrThread thread, ClrStep step)
    {
        lock (Steps)
        {
            Steps.Add(step);
        }

        Continue();
    }

    public void CancelStep() => CancelledSteps++;

    public void SetUserCode(IClrModule module, bool userCode) => UserCode.Add((module, userCode));

    public IClrValue GetStaticField(IClrThread thread, ClrType type, int fieldToken) =>
        StaticFields.TryGetValue(fieldToken, out var value) ? value : throw new ClrException("No such static field in the fake.");

    public IClrValue Call(IClrThread thread, IClrModule module, int methodToken, IReadOnlyList<ClrType> typeArguments, IReadOnlyList<IClrValue> arguments, TimeSpan timeout)
    {
        lock (Calls)
        {
            Calls.Add(methodToken);
        }

        return OnCall?.Invoke(module, methodToken, arguments) ?? throw new ClrException("The fake can't call that.");
    }

    public IClrValue NewString(IClrThread thread, string text, TimeSpan timeout) => FakeValue.String(text);

    public IClrValue NewPrimitive(IClrThread thread, object value) => FakeValue.Of(value);

    public void Terminate()
    {
        Terminated = true;
        Send(new ClrExited(1));
    }

    public void Detach() => Detached = true;

    public void Dispose()
    {
    }
}
