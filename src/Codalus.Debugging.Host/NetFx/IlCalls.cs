using System.Buffers.Binary;

namespace Codalus.Debugging.Host.NetFx;

/// <summary>A call instruction of a method body: its offset, where the next instruction starts, and the method's token.</summary>
internal readonly record struct IlCall(int Offset, int End, int Token, bool IsConstructor);

/// <summary>
/// Walks a method body's IL for its calls (M58): Step Into Specific lists a line's, and a step out reads the value of
/// the one that just returned. Only the instructions' sizes are needed, so the table is the operands' sizes by opcode
/// (ECMA-335, III).
/// </summary>
internal static class IlCalls
{
    private const byte Call = 0x28;
    private const byte CallVirt = 0x6F;
    private const byte NewObj = 0x73;
    private const byte Switch = 0x45;
    private const byte Prefix = 0xFE;

    /// <summary>The <c>call</c>, <c>callvirt</c> and <c>newobj</c> instructions of <paramref name="il"/>, in order; those read before a malformed instruction when there is one.</summary>
    public static IReadOnlyList<IlCall> Of(ReadOnlySpan<byte> il)
    {
        var calls = new List<IlCall>();
        var at = 0;
        while (Size(il, at) is > 0 and var size)
        {
            if (il[at] is Call or CallVirt or NewObj)
            {
                calls.Add(new IlCall(at, at + size, BinaryPrimitives.ReadInt32LittleEndian(il[(at + 1)..]), il[at] == NewObj));
            }

            at += size;
        }

        return calls;
    }

    /// <summary>
    /// Whether the instructions from <paramref name="from"/> up to <paramref name="to"/> run one after another, each
    /// once: both are instruction starts and none between them branches, returns, throws or leaves (M58: a frame that
    /// went from one to the other took no other way).
    /// </summary>
    public static bool IsStraight(ReadOnlySpan<byte> il, int from, int to)
    {
        if (from < 0 || from >= to || to > il.Length)
        {
            return false;
        }

        var at = 0;
        while (at < to && Size(il, at) is > 0 and var size)
        {
            if (at >= from && TransfersControl(il, at))
            {
                return false;
            }

            if (at < from && at + size > from)
            {
                return false; // from is inside an instruction
            }

            at += size;
        }

        return at == to;
    }

    /// <summary>The size of the instruction at <paramref name="at"/>; 0 at the end or at a malformed one.</summary>
    private static int Size(ReadOnlySpan<byte> il, int at)
    {
        if (at >= il.Length)
        {
            return 0;
        }

        var op = il[at];
        int size;
        if (op == Prefix)
        {
            if (at + 1 >= il.Length)
            {
                return 0;
            }

            size = 2 + TwoByteOperand(il[at + 1]);
        }
        else if (op == Switch)
        {
            if (at + 5 > il.Length)
            {
                return 0;
            }

            var targets = BinaryPrimitives.ReadUInt32LittleEndian(il[(at + 1)..]);
            if (targets > (uint)(il.Length - at) / 4)
            {
                return 0;
            }

            size = 5 + (4 * (int)targets);
        }
        else
        {
            size = 1 + OneByteOperand(op);
        }

        return at + size > il.Length ? 0 : size;
    }

    /// <summary>jmp, ret, the branches, switch, throw, endfinally, leave, and the two-byte endfilter and rethrow.</summary>
    private static bool TransfersControl(ReadOnlySpan<byte> il, int at) => il[at] switch
    {
        0x27 or 0x2A or Switch or 0x7A or 0xDC or 0xDD or 0xDE => true,
        >= 0x2B and <= 0x44 => true,
        Prefix => il[at + 1] is 0x11 or 0x1A,
        _ => false,
    };

    /// <summary>
    /// The call that just returned to a frame at <paramref name="ilOffset"/>. The runtime reports such a frame at the
    /// instruction boundary before the call (the JIT keeps boundaries where the evaluation stack is empty and after
    /// each call, and the code right after a call is looked up from inside the call's own): so it's the first call at
    /// or after the offset. Seen on .NET 11: back from the second of two nested calls the offset is the first one's
    /// end, and back from a statement's only call it is the statement's start.
    /// </summary>
    public static IlCall? ReturnedAt(ReadOnlySpan<byte> il, int ilOffset)
    {
        foreach (var call in Of(il))
        {
            if (call.Offset >= ilOffset)
            {
                return call;
            }
        }

        return null;
    }

    private static int OneByteOperand(byte op) => op switch
    {
        >= 0x0E and <= 0x13 => 1, // ldarg.s, ldarga.s, starg.s, ldloc.s, ldloca.s, stloc.s
        0x1F => 1, // ldc.i4.s
        0x20 => 4, // ldc.i4
        0x21 => 8, // ldc.i8
        0x22 => 4, // ldc.r4
        0x23 => 8, // ldc.r8
        0x27 or 0x28 or 0x29 => 4, // jmp, call, calli
        >= 0x2B and <= 0x37 => 1, // br.s … blt.un.s
        >= 0x38 and <= 0x44 => 4, // br … blt.un
        >= 0x6F and <= 0x75 => 4, // callvirt, cpobj, ldobj, ldstr, newobj, castclass, isinst
        0x79 => 4, // unbox
        >= 0x7B and <= 0x81 => 4, // ldfld, ldflda, stfld, ldsfld, ldsflda, stsfld, stobj
        0x8C or 0x8D or 0x8F => 4, // box, newarr, ldelema
        0xA3 or 0xA4 or 0xA5 => 4, // ldelem, stelem, unbox.any
        0xC2 or 0xC6 or 0xD0 => 4, // refanyval, mkrefany, ldtoken
        0xDD => 4, // leave
        0xDE => 1, // leave.s
        _ => 0,
    };

    private static int TwoByteOperand(byte op) => op switch
    {
        0x06 or 0x07 => 4, // ldftn, ldvirtftn
        >= 0x09 and <= 0x0E => 2, // ldarg, ldarga, starg, ldloc, ldloca, stloc
        0x12 => 1, // unaligned.
        0x15 or 0x16 => 4, // initobj, constrained.
        0x19 => 1, // no.
        0x1C => 4, // sizeof
        _ => 0,
    };
}
