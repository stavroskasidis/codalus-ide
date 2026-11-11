namespace Codalus.Debugging.Host.Disassembly;

/// <summary>
/// Loads and stores (M58): every addressing mode of the general and the FP/SIMD registers, pairs, literals, the
/// exclusive and acquire/release forms, the LSE atomics, and Advanced SIMD's structure loads (<c>ld1</c>…).
/// </summary>
internal static partial class Arm64Disassembler
{
    private static readonly string[] s_atomics = ["add", "clr", "eor", "set", "smax", "smin", "umax", "umin"];
    private static readonly string[] s_arrangements = ["8b", "16b", "4h", "8h", "2s", "4s", "1d", "2d"];

    private static string? LoadOrStore(uint i, ulong pc)
    {
        var vector = Bit(i, 26);
        switch (Bits(i, 29, 28))
        {
            case 0b00:
                return vector ? SimdStructure(i) : Bit(i, 24) ? null : Exclusive(i);
            case 0b01:
                return !Bit(i, 24) ? LoadLiteral(i, pc, vector) : vector ? null : UnscaledOrdered(i); // With V set: the memory copies.
            case 0b10:
                return LoadStorePair(i, vector);
            default:
                return LoadStoreRegister(i, vector);
        }
    }

    /// <summary>Exclusive, acquire/release and compare-and-swap: one encoding family told apart by four bits.</summary>
    private static string? Exclusive(uint i)
    {
        var size = Bits(i, 31, 30);
        var ordered = Bit(i, 23);
        var load = Bit(i, 22);
        var pairOrSwap = Bit(i, 21);
        var rs = Bits(i, 20, 16);
        var acquireRelease = Bit(i, 15);
        var rt2 = Bits(i, 14, 10);
        var address = "[" + Xsp(Bits(i, 9, 5)) + "]";
        var rt = i & 31;
        var is64 = size == 3;
        var suffix = size switch { 0 => "b ", 1 => "h ", _ => " " };
        if (!ordered && !pairOrSwap)
        {
            return load
                ? (acquireRelease ? "ldaxr" : "ldxr") + suffix + Reg(is64, rt) + ", " + address
                : (acquireRelease ? "stlxr" : "stxr") + suffix + s_w[rs] + ", " + Reg(is64, rt) + ", " + address;
        }

        if (!ordered && size >= 2)
        {
            return load
                ? (acquireRelease ? "ldaxp " : "ldxp ") + Reg(is64, rt) + ", " + Reg(is64, rt2) + ", " + address
                : (acquireRelease ? "stlxp " : "stxp ") + s_w[rs] + ", " + Reg(is64, rt) + ", " + Reg(is64, rt2) + ", " + address;
        }

        if (!ordered)
        {
            // casp compares and swaps two consecutive registers, so each pair starts at an even one.
            if (rt2 != 31 || (rs & 1) != 0 || (rt & 1) != 0)
            {
                return null;
            }

            var wide = size == 1;
            return "casp" + (load ? "a" : string.Empty) + (acquireRelease ? "l " : " ")
                + Reg(wide, rs) + ", " + Reg(wide, rs + 1) + ", " + Reg(wide, rt) + ", " + Reg(wide, rt + 1) + ", " + address;
        }

        if (!pairOrSwap)
        {
            // ldar and stlr; the same without the acquire/release bit are the limited-ordering forms (LOR), not decoded.
            return acquireRelease ? (load ? "ldar" : "stlr") + suffix + Reg(is64, rt) + ", " + address : null;
        }

        return rt2 != 31
            ? null
            : "cas" + (load ? "a" : string.Empty) + (acquireRelease ? "l" : string.Empty) + suffix + Reg(is64, rs) + ", " + Reg(is64, rt) + ", " + address;
    }

    private static string? LoadLiteral(uint i, ulong pc, bool vector)
    {
        var opc = Bits(i, 31, 30);
        var rt = i & 31;
        var target = Target(pc, Bits(i, 23, 5), 19);
        if (vector)
        {
            return opc == 3 ? null : "ldr " + Scalar(opc + 2, rt) + ", " + target;
        }

        return opc switch
        {
            0 => "ldr " + s_w[rt] + ", " + target,
            1 => "ldr " + s_x[rt] + ", " + target,
            2 => "ldrsw " + s_x[rt] + ", " + target,
            _ => "prfm " + Prefetch(rt) + ", " + target,
        };
    }

    /// <summary>ldapur and stlur (RCpc 2): acquire and release with an offset, which RyuJIT uses for volatile fields.</summary>
    private static string? UnscaledOrdered(uint i)
    {
        var size = Bits(i, 31, 30);
        var opc = Bits(i, 23, 22);
        if (Bit(i, 21) || Bits(i, 11, 10) != 0 || (opc >= 2 && size + opc > 4))
        {
            return null;
        }

        var name = opc switch
        {
            0 => "stlur" + size switch { 0 => "b ", 1 => "h ", _ => " " },
            1 => "ldapur" + size switch { 0 => "b ", 1 => "h ", _ => " " },
            _ => "ldapurs" + "bhw"[(int)size] + " ",
        };
        return name + Reg(opc == 2 || size == 3, i & 31) + ", " + BaseOffset(Bits(i, 9, 5), SignExtend(Bits(i, 20, 12), 9));
    }

    private static string? LoadStorePair(uint i, bool vector)
    {
        var opc = Bits(i, 31, 30);
        var mode = Bits(i, 24, 23);
        var load = Bit(i, 22);
        var rt2 = Bits(i, 14, 10);
        var rn = Bits(i, 9, 5);
        var rt = i & 31;
        string name = mode == 0 ? (load ? "ldnp " : "stnp ") : load ? "ldp " : "stp ";
        string first;
        string second;
        int scale;
        if (vector)
        {
            if (opc == 3)
            {
                return null;
            }

            scale = (int)opc + 2;
            first = Scalar(opc + 2, rt);
            second = Scalar(opc + 2, rt2);
        }
        else
        {
            if (opc == 3 || (opc == 1 && (!load || mode == 0)))
            {
                return null; // stgp (MTE) shares ldpsw's bits.
            }

            scale = opc == 2 ? 3 : 2;
            first = Reg(opc != 0, rt);
            second = Reg(opc != 0, rt2);
            if (opc == 1)
            {
                name = "ldpsw ";
            }
        }

        var offset = SignExtend(Bits(i, 21, 15), 7) << scale;
        return name + first + ", " + second + ", " + mode switch
        {
            1 => "[" + Xsp(rn) + "], " + Imm(offset),
            3 => "[" + Xsp(rn) + ", " + Imm(offset) + "]!",
            _ => BaseOffset(rn, offset),
        };
    }

    private static string BaseOffset(uint rn, long offset) =>
        offset == 0 ? "[" + Xsp(rn) + "]" : "[" + Xsp(rn) + ", " + Imm(offset) + "]";

    private static string? LoadStoreRegister(uint i, bool vector)
    {
        var size = Bits(i, 31, 30);
        var opc = Bits(i, 23, 22);
        var rn = Bits(i, 9, 5);
        var rt = i & 31;
        if (!Bit(i, 24) && Bit(i, 21) && Bits(i, 11, 10) != 2)
        {
            // Not a load or store of the usual kind: the atomics, or (bit 10) ldraa and ldrab, which authenticate the address.
            return vector || Bit(i, 10) ? null : Atomic(i, size, rn, rt);
        }

        // What moves: "ldr", "str" and their sized and sign-extending kinds differ only in these three.
        bool load;
        string suffix;
        string register;
        var scale = (int)size;
        var prefetch = false;
        if (vector)
        {
            if (size != 0 && opc >= 2)
            {
                return null;
            }

            load = (opc & 1) != 0;
            suffix = string.Empty;
            scale = opc >= 2 ? 4 : (int)size;
            register = Scalar((uint)scale, rt);
        }
        else if (opc < 2)
        {
            load = opc == 1;
            suffix = size switch { 0 => "b", 1 => "h", _ => string.Empty };
            register = Reg(size == 3, rt);
        }
        else if (size == 3)
        {
            if (opc == 3)
            {
                return null;
            }

            load = true;
            prefetch = true;
            suffix = string.Empty;
            register = Prefetch(rt);
        }
        else
        {
            if (opc == 3 && size == 2)
            {
                return null;
            }

            load = true;
            suffix = size switch { 0 => "sb", 1 => "sh", _ => "sw" };
            register = Reg(opc == 2, rt);
        }

        if (Bit(i, 24))
        {
            // An unsigned offset, in units of what moves.
            return (prefetch ? "prfm" : load ? "ldr" : "str") + suffix + " " + register + ", " + BaseOffset(rn, (long)Bits(i, 21, 10) << scale);
        }

        if (!Bit(i, 21))
        {
            var offset = SignExtend(Bits(i, 20, 12), 9);
            switch (Bits(i, 11, 10))
            {
                case 0:
                    return (prefetch ? "prfum" : load ? "ldur" : "stur") + suffix + " " + register + ", " + BaseOffset(rn, offset);
                case 2:
                    return vector || prefetch ? null : (load ? "ldtr" : "sttr") + suffix + " " + register + ", " + BaseOffset(rn, offset);
                case 1:
                    return prefetch ? null : (load ? "ldr" : "str") + suffix + " " + register + ", [" + Xsp(rn) + "], " + Imm(offset);
                default:
                    return prefetch ? null : (load ? "ldr" : "str") + suffix + " " + register + ", [" + Xsp(rn) + ", " + Imm(offset) + "]!";
            }
        }

        // A register offset: 64-bit as is or shifted (lsl), or 32-bit zero- or sign-extended first.
        var option = Bits(i, 15, 13);
        if ((option & 2) == 0 || (prefetch && rt >= 24))
        {
            return null; // The reserved extensions, and the range prefetch (rprfm) in prfm's reserved operations.
        }

        var index = Reg((option & 1) != 0, Bits(i, 20, 16));
        var amount = Bit(i, 12) ? " " + Imm(scale) : string.Empty;
        var extend = option == 3 ? (amount.Length == 0 ? string.Empty : ", lsl" + amount) : ", " + s_extends[option] + amount;
        return (prefetch ? "prfm" : load ? "ldr" : "str") + suffix + " " + register + ", [" + Xsp(rn) + ", " + index + extend + "]";
    }

    /// <summary>The LSE atomics: <c>ldadd</c> and its family, <c>swp</c>, and <c>ldapr</c> (RCpc), which shares their encoding.</summary>
    private static string? Atomic(uint i, uint size, uint rn, uint rt)
    {
        var acquire = Bit(i, 23);
        var release = Bit(i, 22);
        var rs = Bits(i, 20, 16);
        var opc = Bits(i, 14, 12);
        var is64 = size == 3;
        var address = "[" + Xsp(rn) + "]";
        var suffix = (release ? "l" : string.Empty) + size switch { 0 => "b ", 1 => "h ", _ => " " };
        if (Bit(i, 15))
        {
            if (opc == 4 && acquire && !release && rs == 31)
            {
                return "ldapr" + suffix + Reg(is64, rt) + ", " + address;
            }

            return opc == 0 ? "swp" + (acquire ? "a" : string.Empty) + suffix + Reg(is64, rs) + ", " + Reg(is64, rt) + ", " + address : null;
        }

        // Without a register to load into, and without acquire, it is the store form: stadd, stclr…
        return rt == 31 && !acquire
            ? "st" + s_atomics[opc] + suffix + Reg(is64, rs) + ", " + address
            : "ld" + s_atomics[opc] + (acquire ? "a" : string.Empty) + suffix + Reg(is64, rs) + ", " + Reg(is64, rt) + ", " + address;
    }

    /// <summary>A prefetch operation: what for (load, instruction, store), which cache level, and keep or stream; a number for the reserved ones.</summary>
    private static string Prefetch(uint operation)
    {
        var kind = Bits(operation, 4, 3);
        var level = Bits(operation, 2, 1);
        return kind == 3 || level == 3
            ? Imm(operation)
            : kind switch { 0 => "pld", 1 => "pli", _ => "pst" } + "l" + s_numbers[level + 1] + ((operation & 1) != 0 ? "strm" : "keep");
    }

    /// <summary>An FP/SIMD register read as a scalar: 0 to 4 for <c>b</c>, <c>h</c>, <c>s</c>, <c>d</c>, <c>q</c>.</summary>
    private static string Scalar(uint size, uint r) => "bhsdq"[(int)size] + s_numbers[r];

    /// <summary>A vector register with its arrangement, as in <c>v0.16b</c>: the element's size (0 to 3) and whether all 128 bits are used.</summary>
    private static string Vector(uint r, uint size, bool q) => "v" + s_numbers[r] + "." + s_arrangements[(size << 1) | (q ? 1u : 0u)];

    /// <summary>
    /// Advanced SIMD's structure loads and stores: <c>ld1</c> to <c>ld4</c> and <c>st1</c> to <c>st4</c> of whole
    /// registers, of one element (<c>ld1 { v0.s }[1], [x0]</c>), and the loads that fill every element (<c>ld1r</c>).
    /// </summary>
    private static string? SimdStructure(uint i)
    {
        if (Bit(i, 31))
        {
            return null;
        }

        var q = Bit(i, 30);
        var postIndex = Bit(i, 23);
        var load = Bit(i, 22);
        var rm = Bits(i, 20, 16);
        var size = Bits(i, 11, 10);
        var rn = Bits(i, 9, 5);
        var rt = i & 31;
        string name;
        string list;
        uint bytes;
        if (!Bit(i, 24))
        {
            // Whole registers: the opcode says how many and whether their elements interleave.
            var (count, interleave) = Bits(i, 15, 12) switch
            {
                0b0000 => (4u, 4u),
                0b0010 => (4u, 1u),
                0b0100 => (3u, 3u),
                0b0110 => (3u, 1u),
                0b0111 => (1u, 1u),
                0b1000 => (2u, 2u),
                0b1010 => (2u, 1u),
                _ => (0u, 0u),
            };
            if (count == 0 || Bit(i, 21) || (!postIndex && rm != 0) || (size == 3 && !q && interleave != 1))
            {
                return null;
            }

            name = (load ? "ld" : "st") + s_numbers[interleave];
            list = RegisterList(rt, count, "." + s_arrangements[(size << 1) | (q ? 1u : 0u)]);
            bytes = count * (q ? 16u : 8u);
        }
        else
        {
            // One element of each register, or (ld1r…) one value replicated into all of them.
            var count = (Bits(i, 13, 13) << 1 | Bits(i, 21, 21)) + 1;
            var scale = Bits(i, 15, 14);
            var s = Bits(i, 12, 12);
            var index = (q ? 8u : 0u) | (s << 2) | size;
            if (!postIndex && rm != 0)
            {
                return null;
            }

            if (scale == 3)
            {
                if (!load || s != 0)
                {
                    return null;
                }

                name = "ld" + s_numbers[count] + "r";
                list = RegisterList(rt, count, "." + s_arrangements[(size << 1) | (q ? 1u : 0u)]);
                bytes = count << (int)size;
            }
            else
            {
                if (scale == 1)
                {
                    if ((size & 1) != 0)
                    {
                        return null;
                    }

                    index >>= 1;
                }
                else if (scale == 2)
                {
                    if ((size & 2) != 0 || (size == 1 && s != 0))
                    {
                        return null;
                    }

                    if (size == 1)
                    {
                        scale = 3;
                        index >>= 3;
                    }
                    else
                    {
                        index >>= 2;
                    }
                }

                name = (load ? "ld" : "st") + s_numbers[count];
                list = RegisterList(rt, count, "." + "bhsd"[(int)scale]) + "[" + s_numbers[index] + "]";
                bytes = count << (int)scale;
            }
        }

        // Post-indexed by a register, or (register 31) by the bytes transferred.
        var writeBack = !postIndex ? string.Empty : rm == 31 ? ", " + Imm(bytes) : ", " + s_x[rm];
        return name + " " + list + ", [" + Xsp(rn) + "]" + writeBack;
    }

    /// <summary>Consecutive vector registers in braces, wrapping from v31 to v0.</summary>
    private static string RegisterList(uint first, uint count, string arrangement)
    {
        var list = "{ v" + s_numbers[first] + arrangement;
        for (var n = 1u; n < count; n++)
        {
            list += ", v" + s_numbers[(first + n) & 31] + arrangement;
        }

        return list + " }";
    }
}
