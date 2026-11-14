using System.Globalization;

namespace Codalus.Debugging.Host.Disassembly;

/// <summary>
/// Scalar floating point (M58): moves, arithmetic, compares and selects, rounding, and the conversions to and from
/// integers and between precisions. Half precision is decoded only where the base instruction set has it
/// (<c>fcvt</c>); half-precision arithmetic (FEAT_FP16) is <c>.inst</c>, as RyuJIT does not emit it.
/// </summary>
internal static partial class Arm64Disassembler
{
    private static readonly string?[] s_fpOneSource =
    [
        "fmov", "fabs", "fneg", "fsqrt", null, null, null, null,
        "frintn", "frintp", "frintm", "frintz", "frinta", null, "frintx", "frinti",
    ];

    private static readonly string?[] s_fpTwoSource = ["fmul", "fdiv", "fadd", "fsub", "fmax", "fmin", "fmaxnm", "fminnm", "fnmul"];

    private static string? FloatingPointOrSimd(uint i)
    {
        // Bit 28 with bit 30 clear is scalar floating point; the rest of the space is Advanced SIMD.
        if ((i & 0x5F000000) == 0x1E000000)
        {
            return Bit(i, 21) ? FloatingPoint(i) : FixedPointConversion(i);
        }

        if ((i & 0x5F000000) == 0x1F000000)
        {
            // fmadd and its family: rd = ra ± rn × rm.
            var type = Bits(i, 23, 22);
            if (Bit(i, 31) || Bit(i, 29) || type > 1)
            {
                return null;
            }

            var name = Bit(i, 21) ? (Bit(i, 15) ? "fnmsub " : "fnmadd ") : Bit(i, 15) ? "fmsub " : "fmadd ";
            return name + Scalar(type + 2, i & 31) + ", " + Scalar(type + 2, Bits(i, 9, 5)) + ", " + Scalar(type + 2, Bits(i, 20, 16)) + ", " + Scalar(type + 2, Bits(i, 14, 10));
        }

        return Simd(i);
    }

    private static string? FloatingPoint(uint i)
    {
        if (Bit(i, 29))
        {
            return null;
        }

        var is64 = Bit(i, 31);
        var type = Bits(i, 23, 22);
        var rm = Bits(i, 20, 16);
        var rn = Bits(i, 9, 5);
        var rd = i & 31;
        if (Bits(i, 15, 10) == 0)
        {
            return IntegerConversion(i, is64, type, rn, rd);
        }

        // Everything below works on registers of one precision; half and the reserved type come only with fcvt.
        if (is64)
        {
            return null;
        }

        var size = type + 2;
        if (Bits(i, 14, 10) == 0b10000)
        {
            var opcode = Bits(i, 20, 15);
            if (opcode is 4 or 5 or 7)
            {
                // fcvt between precisions: the opcode names the destination's, the type the source's.
                var to = opcode switch { 4 => 2u, 5 => 3u, _ => 1u };
                var from = type switch { 0 => 2u, 1 => 3u, 3 => 1u, _ => 0u };
                return from == 0 || from == to ? null : "fcvt " + Scalar(to, rd) + ", " + Scalar(from, rn);
            }

            return type > 1 || opcode > 15 || s_fpOneSource[opcode] is not { } name ? null : name + " " + Scalar(size, rd) + ", " + Scalar(size, rn);
        }

        if (type > 1)
        {
            return null;
        }

        if (Bits(i, 13, 10) == 0b1000)
        {
            // fcmp and fcmpe, against a register or zero.
            if (Bits(i, 15, 14) != 0 || (i & 7) != 0)
            {
                return null;
            }

            return (Bit(i, 4) ? "fcmpe " : "fcmp ") + Scalar(size, rn) + ", " + (Bit(i, 3) ? "#0.0" : Scalar(size, rm));
        }

        if (Bits(i, 12, 10) == 0b100)
        {
            return rn != 0 ? null : "fmov " + Scalar(size, rd) + ", " + FloatImmediate(Bits(i, 20, 13));
        }

        var condition = s_conditions[Bits(i, 15, 12)];
        switch (Bits(i, 11, 10))
        {
            case 0b01:
                return (Bit(i, 4) ? "fccmpe " : "fccmp ") + Scalar(size, rn) + ", " + Scalar(size, rm) + ", " + Imm(i & 15) + ", " + condition;
            case 0b10:
            {
                var opcode = Bits(i, 15, 12);
                return opcode > 8 ? null : s_fpTwoSource[opcode] + " " + Scalar(size, rd) + ", " + Scalar(size, rn) + ", " + Scalar(size, rm);
            }

            case 0b11:
                return "fcsel " + Scalar(size, rd) + ", " + Scalar(size, rn) + ", " + Scalar(size, rm) + ", " + condition;
            default:
                return null;
        }
    }

    /// <summary>Conversions between a general register and a floating-point one, and the moves of their bits (<c>fmov</c>).</summary>
    private static string? IntegerConversion(uint i, bool is64, uint type, uint rn, uint rd)
    {
        var rounding = Bits(i, 20, 19);
        var opcode = Bits(i, 18, 16);
        if (opcode >= 6)
        {
            // fmov copies bits, so the sizes must match; the one odd form moves the top half of a vector register.
            var toFloat = opcode == 7;
            if (rounding == 1 && type == 2 && is64)
            {
                return toFloat ? "fmov v" + s_numbers[rd] + ".d[1], " + s_x[rn] : "fmov " + s_x[rd] + ", v" + s_numbers[rn] + ".d[1]";
            }

            if (rounding != 0 || type != (is64 ? 1u : 0u))
            {
                return null;
            }

            return toFloat ? "fmov " + Scalar(type + 2, rd) + ", " + Reg(is64, rn) : "fmov " + Reg(is64, rd) + ", " + Scalar(type + 2, rn);
        }

        if (type > 1)
        {
            return null;
        }

        var name = (rounding, opcode) switch
        {
            (0, 0) => "fcvtns ",
            (0, 1) => "fcvtnu ",
            (0, 2) => "scvtf ",
            (0, 3) => "ucvtf ",
            (0, 4) => "fcvtas ",
            (0, 5) => "fcvtau ",
            (1, 0) => "fcvtps ",
            (1, 1) => "fcvtpu ",
            (2, 0) => "fcvtms ",
            (2, 1) => "fcvtmu ",
            (3, 0) => "fcvtzs ",
            (3, 1) => "fcvtzu ",
            _ => null,
        };
        if (name is null)
        {
            return null;
        }

        return opcode is 2 or 3
            ? name + Scalar(type + 2, rd) + ", " + Reg(is64, rn)
            : name + Reg(is64, rd) + ", " + Scalar(type + 2, rn);
    }

    /// <summary>The same conversions with a count of fraction bits, for fixed-point values.</summary>
    private static string? FixedPointConversion(uint i)
    {
        var is64 = Bit(i, 31);
        var type = Bits(i, 23, 22);
        var scale = Bits(i, 15, 10);
        var rn = Bits(i, 9, 5);
        var rd = i & 31;
        if (Bit(i, 29) || type > 1 || (!is64 && scale < 32))
        {
            return null;
        }

        var fraction = ", " + Imm(64 - scale);
        return Bits(i, 20, 16) switch
        {
            0b00010 => "scvtf " + Scalar(type + 2, rd) + ", " + Reg(is64, rn) + fraction,
            0b00011 => "ucvtf " + Scalar(type + 2, rd) + ", " + Reg(is64, rn) + fraction,
            0b11000 => "fcvtzs " + Reg(is64, rd) + ", " + Scalar(type + 2, rn) + fraction,
            0b11001 => "fcvtzu " + Reg(is64, rd) + ", " + Scalar(type + 2, rn) + fraction,
            _ => null,
        };
    }

    /// <summary>
    /// An 8-bit floating-point immediate as LLVM prints it, with eight decimals: a sign, three bits of exponent and
    /// four of fraction, so every value from 0.125 to 31 in sixteenths of a power of two.
    /// </summary>
    private static string FloatImmediate(uint imm8)
    {
        var exponent = (int)(((imm8 >> 4) & 7) ^ 4) - 3;
        var value = (16 + (imm8 & 15)) / 16.0 * Math.Pow(2, exponent);
        return ((imm8 & 0x80) != 0 ? "#-" : "#") + value.ToString("F8", CultureInfo.InvariantCulture);
    }
}
