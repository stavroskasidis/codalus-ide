using System.Globalization;
using System.Numerics;

namespace Codalus.Debugging.Host.Disassembly;

/// <summary>
/// Turns one AArch64 (A64) instruction into text for the Disassembly window (M58, ADR 0094): the window shows the
/// machine code the JIT made of a method, so this covers what RyuJIT emits on arm64 (the base instruction set, scalar
/// floating point, the atomics, CRC32 and the Advanced SIMD and crypto instructions behind the hardware intrinsics)
/// and prints everything else as <c>.inst 0x…</c>, the way an assembler would take it back. The text follows LLVM's:
/// lowercase mnemonics, its preferred aliases (<c>mov</c>, <c>cmp</c>, <c>lsl</c>, <c>cset</c>…), decimal immediates
/// except where LLVM prints hex (bit masks, <c>brk</c>). The one difference is on purpose: a PC-relative operand is
/// the address it points at, not an offset, because the window shows addresses. It is a decoder of our own, with no
/// tables read at run time and no reflection, because the debug host ships trimmed and a library for one
/// architecture's text would be most of its size.
/// </summary>
internal static partial class Arm64Disassembler
{
    private static readonly string[] s_numbers = [.. Enumerable.Range(0, 32).Select(n => n.ToString(CultureInfo.InvariantCulture))];
    private static readonly string[] s_x = [.. Enumerable.Range(0, 32).Select(n => n == 31 ? "xzr" : "x" + n.ToString(CultureInfo.InvariantCulture))];
    private static readonly string[] s_w = [.. Enumerable.Range(0, 32).Select(n => n == 31 ? "wzr" : "w" + n.ToString(CultureInfo.InvariantCulture))];
    private static readonly string[] s_conditions = ["eq", "ne", "hs", "lo", "mi", "pl", "vs", "vc", "hi", "ls", "ge", "lt", "gt", "le", "al", "nv"];
    private static readonly string[] s_shifts = ["lsl", "lsr", "asr", "ror"];
    private static readonly string[] s_extends = ["uxtb", "uxth", "uxtw", "uxtx", "sxtb", "sxth", "sxtw", "sxtx"];

    /// <summary>
    /// The text of <paramref name="instruction"/> (the four bytes read as a little-endian word) when it sits at
    /// <paramref name="address"/>, which resolves branch targets, <c>adr</c>/<c>adrp</c> and literal loads to
    /// addresses. An encoding that is unallocated, or outside what this decodes, is <c>.inst 0x…</c>; it never throws.
    /// </summary>
    public static string Decode(uint instruction, ulong address) =>
        DecodeKnown(instruction, address) ?? ".inst 0x" + instruction.ToString("x8", CultureInfo.InvariantCulture);

    /// <summary>The top-level split of the A64 encoding space (bits 28-25); null where nothing here decodes the word.</summary>
    private static string? DecodeKnown(uint i, ulong pc) => ((i >> 25) & 0xF) switch
    {
        0b0000 => (i >> 16) == 0 ? "udf " + Imm(i & 0xFFFF) : null,
        0b1000 or 0b1001 => DataProcessingImmediate(i, pc),
        0b1010 or 0b1011 => BranchOrSystem(i, pc),
        0b0100 or 0b0110 or 0b1100 or 0b1110 => LoadOrStore(i, pc),
        0b0101 or 0b1101 => DataProcessingRegister(i),
        0b0111 or 0b1111 => FloatingPointOrSimd(i),
        _ => null, // SVE, SME and the reserved space.
    };

    // ---- Fields, registers and immediates ----

    private static uint Bits(uint i, int high, int low) => (i >> low) & ((1u << (high - low + 1)) - 1);

    private static bool Bit(uint i, int bit) => ((i >> bit) & 1) != 0;

    private static long SignExtend(uint value, int bits) => (long)((ulong)value << (64 - bits)) >> (64 - bits);

    /// <summary>A general register where 31 is the zero register.</summary>
    private static string Reg(bool is64, uint r) => is64 ? s_x[r] : s_w[r];

    /// <summary>A general register where 31 is the stack pointer.</summary>
    private static string RegSp(bool is64, uint r) => r != 31 ? Reg(is64, r) : is64 ? "sp" : "wsp";

    private static string Xsp(uint r) => r == 31 ? "sp" : s_x[r];

    private static string Dec(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Hex(ulong value) => "0x" + value.ToString("x", CultureInfo.InvariantCulture);

    private static string Imm(long value) => "#" + Dec(value);

    /// <summary>An immediate LLVM prints in hex whatever its size, where zero is plain <c>#0</c>.</summary>
    private static string ImmHex(ulong value) => value == 0 ? "#0" : "#" + Hex(value);

    // ---- Data processing, immediate ----

    private static string? DataProcessingImmediate(uint i, ulong pc)
    {
        var is64 = Bit(i, 31);
        var rd = i & 31;
        var rn = Bits(i, 9, 5);
        switch (Bits(i, 25, 23))
        {
            case 0b000 or 0b001:
            {
                // adr and adrp: a 21-bit offset in bytes, or in 4 KB pages from this instruction's page.
                var offset = SignExtend((Bits(i, 23, 5) << 2) | Bits(i, 30, 29), 21);
                return is64
                    ? "adrp " + s_x[rd] + ", " + Hex((pc & ~0xFFFul) + (ulong)(offset << 12))
                    : "adr " + s_x[rd] + ", " + Hex(pc + (ulong)offset);
            }

            case 0b010:
            {
                var subtract = Bit(i, 30);
                var setFlags = Bit(i, 29);
                var shifted = Bit(i, 22);
                var imm12 = Bits(i, 21, 10);
                var imm = Imm(imm12) + (shifted ? ", lsl #12" : string.Empty);
                if (setFlags && rd == 31)
                {
                    return (subtract ? "cmp " : "cmn ") + RegSp(is64, rn) + ", " + imm;
                }

                if (!subtract && !setFlags && !shifted && imm12 == 0 && (rd == 31 || rn == 31))
                {
                    return "mov " + RegSp(is64, rd) + ", " + RegSp(is64, rn);
                }

                return (subtract ? "sub" : "add") + (setFlags ? "s " : " ") + (setFlags ? Reg(is64, rd) : RegSp(is64, rd)) + ", " + RegSp(is64, rn) + ", " + imm;
            }

            case 0b100:
                return LogicalImmediate(i, is64, rd, rn);
            case 0b101:
                return MoveWide(i, is64, rd);
            case 0b110:
                return Bitfield(i, is64, rd, rn);
            case 0b111:
            {
                // extr; ror when both sources are one register.
                var rm = Bits(i, 20, 16);
                var lsb = Bits(i, 15, 10);
                if (Bits(i, 30, 29) != 0 || Bit(i, 21) || Bit(i, 22) != is64 || (!is64 && lsb > 31))
                {
                    return null;
                }

                return rn == rm
                    ? "ror " + Reg(is64, rd) + ", " + Reg(is64, rn) + ", " + Imm(lsb)
                    : "extr " + Reg(is64, rd) + ", " + Reg(is64, rn) + ", " + Reg(is64, rm) + ", " + Imm(lsb);
            }

            default:
                return null; // Add and subtract with tags (MTE).
        }
    }

    private static string? LogicalImmediate(uint i, bool is64, uint rd, uint rn)
    {
        if (!TryDecodeBitMask(Bits(i, 22, 22), Bits(i, 15, 10), Bits(i, 21, 16), is64, out var mask))
        {
            return null;
        }

        var opc = Bits(i, 30, 29);
        if (opc == 1 && rn == 31 && !IsAnyMoveWideAlias(mask, is64))
        {
            return "mov " + RegSp(is64, rd) + ", " + Imm(is64 ? (long)mask : (int)mask);
        }

        if (opc == 3 && rd == 31)
        {
            return "tst " + Reg(is64, rn) + ", #" + Hex(mask);
        }

        var name = opc switch { 0 => "and ", 1 => "orr ", 2 => "eor ", _ => "ands " };
        return name + (opc == 3 ? Reg(is64, rd) : RegSp(is64, rd)) + ", " + Reg(is64, rn) + ", #" + Hex(mask);
    }

    /// <summary>
    /// The value of a logical instruction's immediate: a run of ones, rotated, in an element of 2 to 64 bits that
    /// repeats across the register (the Arm ARM's DecodeBitMasks). False for the patterns that are reserved.
    /// </summary>
    private static bool TryDecodeBitMask(uint n, uint imms, uint immr, bool is64, out ulong mask)
    {
        mask = 0;
        var combined = (n << 6) | (~imms & 0x3F);
        var length = 31 - BitOperations.LeadingZeroCount(combined);
        if (length < 1 || (length == 6 && !is64))
        {
            return false;
        }

        var size = 1 << length;
        var levels = (uint)size - 1;
        var ones = imms & levels;
        var rotate = (int)(immr & levels);
        if (ones == levels)
        {
            return false;
        }

        var element = (1ul << (int)(ones + 1)) - 1;
        if (rotate != 0)
        {
            element = (element >> rotate) | (element << (size - rotate));
            if (size < 64)
            {
                element &= (1ul << size) - 1;
            }
        }

        for (var width = size; width < 64; width *= 2)
        {
            element |= element << width;
        }

        mask = is64 ? element : element & 0xFFFFFFFF;
        return true;
    }

    private static bool IsAnyMovzAlias(ulong value, bool is64)
    {
        for (var shift = 0; shift <= (is64 ? 48 : 16); shift += 16)
        {
            if ((value & ~(0xFFFFul << shift)) == 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether movz or movn can load <paramref name="value"/>: then <c>mov</c> means that one, and an orr stays an orr.</summary>
    private static bool IsAnyMoveWideAlias(ulong value, bool is64) =>
        IsAnyMovzAlias(value, is64) || IsAnyMovzAlias(is64 ? ~value : ~value & 0xFFFFFFFF, is64);

    private static string? MoveWide(uint i, bool is64, uint rd)
    {
        var opc = Bits(i, 30, 29);
        var shift = (int)Bits(i, 22, 21) * 16;
        var imm16 = Bits(i, 20, 5);
        if (opc == 1 || (!is64 && shift > 16))
        {
            return null;
        }

        // LLVM's rule: mov unless the immediate is zero with a shift (plain "#0" wins), and for movn unless a movz could do it.
        var value = (ulong)imm16 << shift;
        if (opc == 0)
        {
            value = is64 ? ~value : ~value & 0xFFFFFFFF;
        }

        var isAlias = opc switch
        {
            0 => !IsAnyMovzAlias(value, is64) && !(imm16 == 0 && shift != 0),
            2 => !(imm16 == 0 && shift != 0),
            _ => false,
        };
        if (isAlias)
        {
            return "mov " + Reg(is64, rd) + ", " + Imm(is64 ? (long)value : (int)value);
        }

        var name = opc switch { 0 => "movn ", 2 => "movz ", _ => "movk " };
        return name + Reg(is64, rd) + ", " + Imm(imm16) + (shift != 0 ? ", lsl " + Imm(shift) : string.Empty);
    }

    private static string? Bitfield(uint i, bool is64, uint rd, uint rn)
    {
        var opc = Bits(i, 30, 29);
        var immr = Bits(i, 21, 16);
        var imms = Bits(i, 15, 10);
        if (opc == 3 || Bit(i, 22) != is64 || (!is64 && (immr > 31 || imms > 31)))
        {
            return null;
        }

        var width = is64 ? 64u : 32u;
        var d = Reg(is64, rd);
        var n = Reg(is64, rn);
        if (opc == 1)
        {
            // bfm: an insert when the field wraps, else an extract into the low bits.
            return imms < immr
                ? "bfi " + d + ", " + n + ", " + Imm((width - immr) % width) + ", " + Imm(imms + 1)
                : "bfxil " + d + ", " + n + ", " + Imm(immr) + ", " + Imm(imms - immr + 1);
        }

        var signed = opc == 0;
        if (immr == 0)
        {
            // Extensions read a W register whatever the destination's size.
            var extend = (imms, signed, is64) switch
            {
                (7, true, _) => "sxtb ",
                (15, true, _) => "sxth ",
                (31, true, true) => "sxtw ",
                (7, false, false) => "uxtb ",
                (15, false, false) => "uxth ",
                _ => null,
            };
            if (extend is not null)
            {
                return extend + d + ", " + s_w[rn];
            }
        }

        if (imms == width - 1)
        {
            return (signed ? "asr " : "lsr ") + d + ", " + n + ", " + Imm(immr);
        }

        if (!signed && imms + 1 == immr)
        {
            return "lsl " + d + ", " + n + ", " + Imm(width - 1 - imms);
        }

        return imms < immr
            ? (signed ? "sbfiz " : "ubfiz ") + d + ", " + n + ", " + Imm(width - immr) + ", " + Imm(imms + 1)
            : (signed ? "sbfx " : "ubfx ") + d + ", " + n + ", " + Imm(immr) + ", " + Imm(imms - immr + 1);
    }

    // ---- Branches, exceptions and system instructions ----

    private static string? BranchOrSystem(uint i, ulong pc)
    {
        if ((i & 0x7C000000) == 0x14000000)
        {
            return (Bit(i, 31) ? "bl " : "b ") + Target(pc, i & 0x3FFFFFF, 26);
        }

        if ((i & 0x7E000000) == 0x34000000)
        {
            return (Bit(i, 24) ? "cbnz " : "cbz ") + Reg(Bit(i, 31), i & 31) + ", " + Target(pc, Bits(i, 23, 5), 19);
        }

        if ((i & 0x7E000000) == 0x36000000)
        {
            // The tested bit picks the register's size: LLVM prints W for bits 0 to 31.
            var bit = (Bits(i, 31, 31) << 5) | Bits(i, 23, 19);
            return (Bit(i, 24) ? "tbnz " : "tbz ") + Reg(bit > 31, i & 31) + ", " + Imm(bit) + ", " + Target(pc, Bits(i, 18, 5), 14);
        }

        if ((i & 0xFF000010) == 0x54000000)
        {
            return "b." + s_conditions[i & 15] + " " + Target(pc, Bits(i, 23, 5), 19);
        }

        if ((i & 0xFF000000) == 0xD4000000)
        {
            var name = (i & 0x00E0001F) switch
            {
                0x00000001 => "svc ",
                0x00000002 => "hvc ",
                0x00000003 => "smc ",
                0x00200000 => "brk ",
                0x00400000 => "hlt ",
                _ => null,
            };
            return name is null ? null : name + ImmHex(Bits(i, 20, 5));
        }

        if ((i & 0xFFC00000) == 0xD5000000)
        {
            return System(i);
        }

        if ((i & 0xFE000000) == 0xD6000000)
        {
            var rn = Bits(i, 9, 5);
            return (i & 0xFFFFFC1F) switch
            {
                0xD61F0000 => "br " + s_x[rn],
                0xD63F0000 => "blr " + s_x[rn],
                0xD65F0000 => rn == 30 ? "ret" : "ret " + s_x[rn],
                _ => i switch { 0xD69F03E0 => "eret", 0xD6BF03E0 => "drps", _ => null }, // The rest authenticate pointers.
            };
        }

        return null;
    }

    /// <summary>A branch's target: the instruction's address plus a signed count of instructions.</summary>
    private static string Target(ulong pc, uint field, int bits) => Hex(pc + (ulong)(SignExtend(field, bits) << 2));

    private static string? System(uint i)
    {
        var read = Bit(i, 21);
        var op0 = Bits(i, 20, 19);
        var op1 = Bits(i, 18, 16);
        var crn = Bits(i, 15, 12);
        var crm = Bits(i, 11, 8);
        var op2 = Bits(i, 7, 5);
        var rt = i & 31;
        if (op0 == 0)
        {
            if (read || op1 != 3 || rt != 31)
            {
                return null; // msr to a PSTATE field and the transactional instructions.
            }

            return crn switch
            {
                2 => Hint((crm << 3) | op2),
                3 => Barrier(crm, op2),
                _ => null,
            };
        }

        if (op0 == 1)
        {
            // sys and sysl, with the names of the cache operations user code can run.
            var operation = read ? null : (op1, crn, crm, op2) switch
            {
                (0, 7, 6, 1) => "dc ivac",
                (0, 7, 6, 2) => "dc isw",
                (0, 7, 10, 2) => "dc csw",
                (0, 7, 14, 2) => "dc cisw",
                (3, 7, 4, 1) => "dc zva",
                (3, 7, 10, 1) => "dc cvac",
                (3, 7, 11, 1) => "dc cvau",
                (3, 7, 14, 1) => "dc civac",
                (3, 7, 5, 1) => "ic ivau",
                _ => null,
            };
            if (operation is not null)
            {
                return operation + ", " + s_x[rt];
            }

            var operands = Imm(op1) + ", c" + s_numbers[crn] + ", c" + s_numbers[crm] + ", " + Imm(op2);
            return read
                ? "sysl " + s_x[rt] + ", " + operands
                : "sys " + operands + (rt == 31 ? string.Empty : ", " + s_x[rt]);
        }

        var register = SystemRegister(op0, op1, crn, crm, op2, read);
        return read ? "mrs " + s_x[rt] + ", " + register : "msr " + register + ", " + s_x[rt];
    }

    private static string Hint(uint number) => number switch
    {
        0 => "nop",
        1 => "yield",
        2 => "wfe",
        3 => "wfi",
        4 => "sev",
        5 => "sevl",
        6 => "dgh",
        20 => "csdb",
        // Pointer authentication and branch targets live in the hint space, so code that has them runs everywhere.
        7 => "xpaclri",
        8 => "pacia1716",
        10 => "pacib1716",
        12 => "autia1716",
        14 => "autib1716",
        24 => "paciaz",
        25 => "paciasp",
        26 => "pacibz",
        27 => "pacibsp",
        28 => "autiaz",
        29 => "autiasp",
        30 => "autibz",
        31 => "autibsp",
        32 => "bti",
        34 => "bti c",
        36 => "bti j",
        38 => "bti jc",
        _ => "hint " + Imm(number),
    };

    private static string? Barrier(uint crm, uint op2)
    {
        var option = crm switch
        {
            1 => "oshld",
            2 => "oshst",
            3 => "osh",
            5 => "nshld",
            6 => "nshst",
            7 => "nsh",
            9 => "ishld",
            10 => "ishst",
            11 => "ish",
            13 => "ld",
            14 => "st",
            15 => "sy",
            _ => Imm(crm),
        };
        return op2 switch
        {
            2 => crm == 15 ? "clrex" : "clrex " + Imm(crm),
            4 => crm switch { 0 => "ssbb", 4 => "pssbb", _ => "dsb " + option },
            5 => "dmb " + option,
            6 => crm == 15 ? "isb" : "isb " + Imm(crm),
            _ => null,
        };
    }

    /// <summary>
    /// A system register's name: the ones user-mode code reads and writes by name, the rest in the generic form every
    /// assembler takes (<c>S3_3_C13_C0_2</c>). A register that only reads has no name as <c>msr</c>'s target, as in LLVM.
    /// </summary>
    private static string SystemRegister(uint op0, uint op1, uint crn, uint crm, uint op2, bool read)
    {
        var name = (op0, op1, crn, crm, op2) switch
        {
            (3, 3, 4, 2, 0) => "NZCV",
            (3, 3, 4, 2, 1) => "DAIF",
            (3, 3, 4, 4, 0) => "FPCR",
            (3, 3, 4, 4, 1) => "FPSR",
            (3, 3, 13, 0, 2) => "TPIDR_EL0",
            (3, 3, 13, 0, 3) => "TPIDRRO_EL0",
            (3, 3, 14, 0, 0) => "CNTFRQ_EL0",
            (3, 3, 0, 0, 1) when read => "CTR_EL0",
            (3, 3, 0, 0, 7) when read => "DCZID_EL0",
            (3, 3, 14, 0, 1) when read => "CNTPCT_EL0",
            (3, 3, 14, 0, 2) when read => "CNTVCT_EL0",
            _ => null,
        };
        return name ?? "S" + s_numbers[op0] + "_" + s_numbers[op1] + "_C" + s_numbers[crn] + "_C" + s_numbers[crm] + "_" + s_numbers[op2];
    }
}
