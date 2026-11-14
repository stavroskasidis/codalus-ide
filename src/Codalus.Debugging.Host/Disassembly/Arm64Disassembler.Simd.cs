using System.Globalization;
using System.Numerics;

namespace Codalus.Debugging.Host.Disassembly;

/// <summary>
/// Advanced SIMD (M58), vector and scalar: what RyuJIT emits for <c>Vector64</c>/<c>Vector128</c> and the
/// <c>AdvSimd</c>, <c>Aes</c>, <c>Sha1</c>, <c>Sha256</c>, <c>Rdm</c> and <c>Dp</c> intrinsics. Half-precision
/// floating point (FEAT_FP16) and the later extensions (SHA-3, SM3/SM4, complex numbers, matrix multiply) are
/// <c>.inst</c>.
/// </summary>
internal static partial class Arm64Disassembler
{
    // Which element sizes (bit 0 for bytes to bit 3 for doublewords) an instruction takes.
    private const byte B = 0b0001;
    private const byte Hs = 0b0110;
    private const byte Bhs = 0b0111;
    private const byte D = 0b1000;
    private const byte All = 0b1111;

    /// <summary>An instruction's name with the element sizes its vector form and its scalar form take.</summary>
    private readonly record struct SizedName(string? Name, byte VectorSizes = 0, byte ScalarSizes = 0);

    // "Three same", integer: opcodes 0 to 23, signed (or first) kinds then unsigned (or second). Opcode 3 is the logic.
    private static readonly SizedName[] s_threeSame =
    [
        new("shadd", Bhs), new("sqadd", All, All), new("srhadd", Bhs), default,
        new("shsub", Bhs), new("sqsub", All, All), new("cmgt", All, D), new("cmge", All, D),
        new("sshl", All, D), new("sqshl", All, All), new("srshl", All, D), new("sqrshl", All, All),
        new("smax", Bhs), new("smin", Bhs), new("sabd", Bhs), new("saba", Bhs),
        new("add", All, D), new("cmtst", All, D), new("mla", Bhs), new("mul", Bhs),
        new("smaxp", Bhs), new("sminp", Bhs), new("sqdmulh", Hs, Hs), new("addp", All),
        new("uhadd", Bhs), new("uqadd", All, All), new("urhadd", Bhs), default,
        new("uhsub", Bhs), new("uqsub", All, All), new("cmhi", All, D), new("cmhs", All, D),
        new("ushl", All, D), new("uqshl", All, All), new("urshl", All, D), new("uqrshl", All, All),
        new("umax", Bhs), new("umin", Bhs), new("uabd", Bhs), new("uaba", Bhs),
        new("sub", All, D), new("cmeq", All, D), new("mls", Bhs), new("pmul", B),
        new("umaxp", Bhs), new("uminp", Bhs), new("sqrdmulh", Hs, Hs), default,
    ];

    // "Three same", floating point: opcodes 24 to 31 for each of U and the size's high bit. A star: also has a scalar form.
    private static readonly string?[] s_threeSameFloat =
    [
        "fmaxnm", "fmla", "fadd", "*fmulx", "*fcmeq", null, "fmax", "*frecps",
        "fminnm", "fmls", "fsub", null, null, null, "fmin", "*frsqrts",
        "fmaxnmp", null, "faddp", "fmul", "*fcmge", "*facge", "fmaxp", "fdiv",
        "fminnmp", null, "*fabd", null, "*fcmgt", "*facgt", "fminp", null,
    ];

    private static readonly string[] s_vectorLogic = ["and", "bic", "orr", "orn", "eor", "bsl", "bit", "bif"];

    // "Three different": the result's elements are twice as wide as (or half of) the operands'.
    private static readonly string?[] s_threeDifferent =
    [
        "saddl", "saddw", "ssubl", "ssubw", "addhn", "sabal", "subhn", "sabdl",
        "smlal", "sqdmlal", "smlsl", "sqdmlsl", "smull", "sqdmull", "pmull", null,
        "uaddl", "uaddw", "usubl", "usubw", "raddhn", "uabal", "rsubhn", "uabdl",
        "umlal", null, "umlsl", null, "umull", null, null, null,
    ];

    // By an element of the last register ("mul v0.4s, v1.4s, v2.s[1]").
    private static readonly string?[] s_byElement =
    [
        null, "fmla", "smlal", "sqdmlal", null, "fmls", "smlsl", "sqdmlsl",
        "mul", "fmul", "smull", "sqdmull", "sqdmulh", "sqrdmulh", "sdot", null,
        "mla", null, "umlal", null, "mls", null, "umlsl", null,
        null, "fmulx", "umull", null, null, "sqrdmlah", "udot", "sqrdmlsh",
    ];

    private static string? Simd(uint i)
    {
        // Vector forms have bit 28 clear and Q in bit 30; scalar forms have bit 28 set under "01".
        var scalar = Bit(i, 28);
        if (scalar ? Bits(i, 31, 30) != 1 : Bit(i, 31))
        {
            return null;
        }

        var q = Bit(i, 30);
        var u = Bit(i, 29);
        var size = Bits(i, 23, 22);
        var rm = Bits(i, 20, 16);
        var rn = Bits(i, 9, 5);
        var rd = i & 31;
        if (Bit(i, 24))
        {
            if (!Bit(i, 10))
            {
                return SimdByElement(i, scalar, q, u, size, rn, rd);
            }

            if (Bit(i, 23))
            {
                return null;
            }

            return Bits(i, 22, 19) != 0 ? SimdShift(i, scalar, q, u, rn, rd) : scalar ? null : SimdImmediate(i, q, u, rd);
        }

        if (!Bit(i, 21))
        {
            if (Bit(i, 15))
            {
                return Bit(i, 10) ? SimdThreeSameExtra(i, scalar, q, u, size, rm, rn, rd) : null;
            }

            if (size != 0)
            {
                // Permutes are the one family here with a size.
                return !scalar && !u && Bits(i, 11, 10) == 0b10 ? SimdPermute(i, q, size, rm, rn, rd) : null;
            }

            if (Bit(i, 10))
            {
                return SimdCopy(i, scalar, q, u, rn, rd);
            }

            if (scalar)
            {
                return u || Bit(i, 11) ? null : Sha(Bits(i, 14, 12), rm, rn, rd);
            }

            if (u)
            {
                // ext: the bytes of two registers from a given one on.
                var first = Bits(i, 14, 11);
                return !q && first > 7 ? null : "ext " + Vector(rd, 0, q) + ", " + Vector(rn, 0, q) + ", " + Vector(rm, 0, q) + ", " + Imm(first);
            }

            return Bit(i, 11)
                ? SimdPermute(i, q, size, rm, rn, rd)
                : (Bit(i, 12) ? "tbx " : "tbl ") + Vector(rd, 0, q) + ", " + RegisterList(rn, Bits(i, 14, 13) + 1, ".16b") + ", " + Vector(rm, 0, q);
        }

        if (Bit(i, 10))
        {
            return SimdThreeSame(i, scalar, q, u, size, rm, rn, rd);
        }

        if (!Bit(i, 11))
        {
            return SimdThreeDifferent(i, scalar, q, u, size, rm, rn, rd);
        }

        return Bits(i, 20, 17) switch
        {
            0b0000 => SimdTwoRegister(i, scalar, q, u, size, rn, rd),
            0b1000 => scalar ? SimdPairwise(i, u, size, rn, rd) : SimdAcrossLanes(i, q, u, size, rn, rd),
            0b0100 when size == 0 && !u && (scalar || q) => Crypto(Bits(i, 16, 12), scalar, rn, rd),
            _ => null,
        };
    }

    /// <summary>One element of a vector register, as in <c>v1.s[2]</c>.</summary>
    private static string Element(uint r, uint size, uint index) => "v" + s_numbers[r] + "." + "bhsd"[(int)size] + "[" + s_numbers[index] + "]";

    private static bool Takes(byte sizes, uint size) => ((sizes >> (int)size) & 1) != 0;

    private static string? SimdThreeSame(uint i, bool scalar, bool q, bool u, uint size, uint rm, uint rn, uint rd)
    {
        var opcode = Bits(i, 15, 11);
        if (opcode >= 24)
        {
            // Floating point: the size's low bit picks single or double.
            var name = s_threeSameFloat[((u ? 2u : 0u) | (size >> 1)) << 3 | (opcode - 24)];
            var width = (size & 1) + 2;
            if (name is null || (scalar ? name[0] != '*' : width == 3 && !q))
            {
                return null;
            }

            name = name.TrimStart('*') + " ";
            return scalar
                ? name + Scalar(width, rd) + ", " + Scalar(width, rn) + ", " + Scalar(width, rm)
                : name + Vector(rd, width, q) + ", " + Vector(rn, width, q) + ", " + Vector(rm, width, q);
        }

        if (opcode == 3)
        {
            if (scalar)
            {
                return null;
            }

            // Whole-register logic; orr of a register with itself is how a vector is copied.
            return !u && size == 2 && rn == rm
                ? "mov " + Vector(rd, 0, q) + ", " + Vector(rn, 0, q)
                : s_vectorLogic[(u ? 4u : 0u) | size] + " " + Vector(rd, 0, q) + ", " + Vector(rn, 0, q) + ", " + Vector(rm, 0, q);
        }

        var op = s_threeSame[(u ? 24u : 0u) + opcode];
        if (scalar)
        {
            return Takes(op.ScalarSizes, size) ? op.Name + " " + Scalar(size, rd) + ", " + Scalar(size, rn) + ", " + Scalar(size, rm) : null;
        }

        return Takes(op.VectorSizes, size) && (size != 3 || q)
            ? op.Name + " " + Vector(rd, size, q) + ", " + Vector(rn, size, q) + ", " + Vector(rm, size, q)
            : null;
    }

    /// <summary>The v8.1 rounding multiply-accumulates and the dot products, which sit beside "three same".</summary>
    private static string? SimdThreeSameExtra(uint i, bool scalar, bool q, bool u, uint size, uint rm, uint rn, uint rd)
    {
        var opcode = Bits(i, 14, 11);
        if (opcode <= 1 && u && size is 1 or 2)
        {
            var name = opcode == 0 ? "sqrdmlah " : "sqrdmlsh ";
            return scalar
                ? name + Scalar(size, rd) + ", " + Scalar(size, rn) + ", " + Scalar(size, rm)
                : name + Vector(rd, size, q) + ", " + Vector(rn, size, q) + ", " + Vector(rm, size, q);
        }

        return opcode == 2 && size == 2 && !scalar
            ? (u ? "udot " : "sdot ") + Vector(rd, 2, q) + ", " + Vector(rn, 0, q) + ", " + Vector(rm, 0, q)
            : null;
    }

    private static string? SimdThreeDifferent(uint i, bool scalar, bool q, bool u, uint size, uint rm, uint rn, uint rd)
    {
        var opcode = Bits(i, 15, 12);
        var name = s_threeDifferent[(u ? 16u : 0u) | opcode];
        if (name is null)
        {
            return null;
        }

        var saturating = opcode is 9 or 11 or 13;
        if (scalar)
        {
            return saturating && size is 1 or 2 ? name + " " + Scalar(size + 1, rd) + ", " + Scalar(size, rn) + ", " + Scalar(size, rm) : null;
        }

        if (opcode == 14)
        {
            // pmull: bytes to halfwords, or (with AES) doublewords to one 128-bit polynomial.
            if (size is 1 or 2)
            {
                return null;
            }

            var half = q ? "pmull2 " : "pmull ";
            return size == 0
                ? half + Vector(rd, 1, true) + ", " + Vector(rn, 0, q) + ", " + Vector(rm, 0, q)
                : half + "v" + s_numbers[rd] + ".1q, " + Vector(rn, 3, q) + ", " + Vector(rm, 3, q);
        }

        if (size == 3 || (saturating && size == 0))
        {
            return null;
        }

        // The "2" forms read the operands' high halves, or (narrowing) write the result's.
        name += q ? "2 " : " ";
        return opcode switch
        {
            1 or 3 => name + Vector(rd, size + 1, true) + ", " + Vector(rn, size + 1, true) + ", " + Vector(rm, size, q),
            4 or 6 => name + Vector(rd, size, q) + ", " + Vector(rn, size + 1, true) + ", " + Vector(rm, size + 1, true),
            _ => name + Vector(rd, size + 1, true) + ", " + Vector(rn, size, q) + ", " + Vector(rm, size, q),
        };
    }

    private static string? SimdPermute(uint i, bool q, uint size, uint rm, uint rn, uint rd)
    {
        var name = Bits(i, 14, 12) switch { 1 => "uzp1 ", 2 => "trn1 ", 3 => "zip1 ", 5 => "uzp2 ", 6 => "trn2 ", 7 => "zip2 ", _ => null };
        return name is null || (size == 3 && !q) ? null : name + Vector(rd, size, q) + ", " + Vector(rn, size, q) + ", " + Vector(rm, size, q);
    }

    /// <summary>Copies between elements and general registers: <c>dup</c>, and the <c>ins</c> and <c>umov</c> LLVM prints as <c>mov</c>.</summary>
    private static string? SimdCopy(uint i, bool scalar, bool q, bool u, uint rn, uint rd)
    {
        // The lowest set bit of imm5 is the element's size; the bits above it are the index.
        var imm5 = Bits(i, 20, 16);
        var imm4 = Bits(i, 14, 11);
        if ((imm5 & 15) == 0)
        {
            return null;
        }

        var size = (uint)BitOperations.TrailingZeroCount(imm5);
        var index = imm5 >> ((int)size + 1);
        if (scalar)
        {
            return !u && imm4 == 0 ? "mov " + Scalar(size, rd) + ", " + Element(rn, size, index) : null;
        }

        if (u)
        {
            return q ? "mov " + Element(rd, size, index) + ", " + Element(rn, size, imm4 >> (int)size) : null;
        }

        switch (imm4)
        {
            case 0b0000:
                return size == 3 && !q ? null : "dup " + Vector(rd, size, q) + ", " + Element(rn, size, index);
            case 0b0001:
                return size == 3 && !q ? null : "dup " + Vector(rd, size, q) + ", " + Reg(size == 3, rn);
            case 0b0011:
                return q ? "mov " + Element(rd, size, index) + ", " + Reg(size == 3, rn) : null;
            case 0b0101:
                return size <= (q ? 2u : 1u) ? "smov " + Reg(q, rd) + ", " + Element(rn, size, index) : null;
            case 0b0111:
                // To a W register from 8, 16 or 32 bits, to an X register from 64; the full-width ones are "mov".
                return q
                    ? size == 3 ? "mov " + s_x[rd] + ", " + Element(rn, size, index) : null
                    : size <= 2 ? (size == 2 ? "mov " : "umov ") + s_w[rd] + ", " + Element(rn, size, index) : null;
            default:
                return null;
        }
    }

    private static string? SimdTwoRegister(uint i, bool scalar, bool q, bool u, uint size, uint rn, uint rd)
    {
        var opcode = Bits(i, 16, 12);
        var wide = size == 3;
        if (!scalar && wide && !q)
        {
            return null; // No one-doubleword vectors here.
        }

        // Shapes shared by many: both operands alike; alike with a zero to compare against.
        string? Same(string name, byte vectorSizes, byte scalarSizes = 0, string zero = "") => scalar
            ? Takes(scalarSizes, size) ? name + " " + Scalar(size, rd) + ", " + Scalar(size, rn) + zero : null
            : Takes(vectorSizes, size) ? name + " " + Vector(rd, size, q) + ", " + Vector(rn, size, q) + zero : null;

        // Narrowing: to elements half as wide, into the low half or (the "2" forms) the high half.
        string? Narrow(string name, bool hasScalar) => wide
            ? null
            : scalar
                ? hasScalar ? name + " " + Scalar(size, rd) + ", " + Scalar(size + 1, rn) : null
                : name + (q ? "2 " : " ") + Vector(rd, size, q) + ", " + Vector(rn, size + 1, true);

        switch (opcode)
        {
            case 0b00000:
                return Same(u ? "rev32" : "rev64", u ? (byte)0b0011 : Bhs);
            case 0b00001:
                return u ? null : Same("rev16", B);
            case 0b00010 or 0b00110:
            {
                // Pairwise long adds: half as many elements, twice as wide.
                var name = (u ? "u" : "s") + (opcode == 2 ? "addlp " : "adalp ");
                return scalar || wide ? null : name + Vector(rd, size + 1, q) + ", " + Vector(rn, size, q);
            }

            case 0b00011:
                return Same(u ? "usqadd" : "suqadd", All, All);
            case 0b00100:
                return Same(u ? "clz" : "cls", Bhs);
            case 0b00101:
                // cnt, and under U the size picks between two byte-wise operations: mvn (not) and rbit.
                return !u
                    ? Same("cnt", B)
                    : scalar || size > 1 ? null : (size == 0 ? "mvn " : "rbit ") + Vector(rd, 0, q) + ", " + Vector(rn, 0, q);
            case 0b00111:
                return Same(u ? "sqneg" : "sqabs", All, All);
            case 0b01000:
                return Same(u ? "cmge" : "cmgt", All, D, ", #0");
            case 0b01001:
                return Same(u ? "cmle" : "cmeq", All, D, ", #0");
            case 0b01010:
                return u ? null : Same("cmlt", All, D, ", #0");
            case 0b01011:
                return Same(u ? "neg" : "abs", All, D);
            case 0b10010:
                return Narrow(u ? "sqxtun" : "xtn", u);
            case 0b10011:
                // shll: widen and shift left by the element's size.
                return !u || scalar || wide ? null : (q ? "shll2 " : "shll ") + Vector(rd, size + 1, true) + ", " + Vector(rn, size, q) + ", " + Imm(8 << (int)size);
            case 0b10100:
                return Narrow(u ? "uqxtn" : "sqxtn", true);
            default:
                return SimdTwoRegisterFloat(opcode, scalar, q, u, size, rn, rd);
        }
    }

    private static string? SimdTwoRegisterFloat(uint opcode, bool scalar, bool q, bool u, uint size, uint rn, uint rd)
    {
        // The size's high bit is part of the opcode here; its low bit picks single or double.
        var high = size >= 2;
        var width = (size & 1) + 2;
        var isDouble = width == 3;
        if (opcode is 0b10110 or 0b10111 && !high)
        {
            // Between precisions: fcvtn narrows (fcvtxn rounding to odd), fcvtl widens.
            if (opcode == 0b10111)
            {
                return u || scalar ? null : (q ? "fcvtl2 " : "fcvtl ") + Vector(rd, width, true) + ", " + Vector(rn, width - 1, q);
            }

            if (u && !isDouble)
            {
                return null;
            }

            return scalar
                ? u ? "fcvtxn " + Scalar(2, rd) + ", " + Scalar(3, rn) : null
                : (u ? "fcvtxn" : "fcvtn") + (q ? "2 " : " ") + Vector(rd, width - 1, q) + ", " + Vector(rn, width, true);
        }

        // A star: also has a scalar form. An exclamation mark: only the scalar form.
        var name = (opcode, high, u) switch
        {
            (0b11000, false, false) => "frintn",
            (0b11000, false, true) => "frinta",
            (0b11000, true, false) => "frintp",
            (0b11001, false, false) => "frintm",
            (0b11001, false, true) => "frintx",
            (0b11001, true, false) => "frintz",
            (0b11001, true, true) => "frinti",
            (0b11010, false, false) => "*fcvtns",
            (0b11010, false, true) => "*fcvtnu",
            (0b11010, true, false) => "*fcvtps",
            (0b11010, true, true) => "*fcvtpu",
            (0b11011, false, false) => "*fcvtms",
            (0b11011, false, true) => "*fcvtmu",
            (0b11011, true, false) => "*fcvtzs",
            (0b11011, true, true) => "*fcvtzu",
            (0b11100, false, false) => "*fcvtas",
            (0b11100, false, true) => "*fcvtau",
            (0b11100, true, false) => isDouble ? null : "urecpe",
            (0b11100, true, true) => isDouble ? null : "ursqrte",
            (0b11101, false, false) => "*scvtf",
            (0b11101, false, true) => "*ucvtf",
            (0b11101, true, false) => "*frecpe",
            (0b11101, true, true) => "*frsqrte",
            (0b01100, true, false) => "*fcmgt",
            (0b01100, true, true) => "*fcmge",
            (0b01101, true, false) => "*fcmeq",
            (0b01101, true, true) => "*fcmle",
            (0b01110, true, false) => "*fcmlt",
            (0b01111, true, false) => "fabs",
            (0b01111, true, true) => "fneg",
            (0b11111, true, false) => "!frecpx",
            (0b11111, true, true) => "fsqrt",
            _ => null,
        };
        if (name is null || (scalar ? name[0] is not ('*' or '!') : name[0] == '!' || (isDouble && !q)))
        {
            return null;
        }

        var zero = opcode is 0b01100 or 0b01101 or 0b01110 ? ", #0.0" : string.Empty;
        name = name.TrimStart('*', '!') + " ";
        return scalar
            ? name + Scalar(width, rd) + ", " + Scalar(width, rn) + zero
            : name + Vector(rd, width, q) + ", " + Vector(rn, width, q) + zero;
    }

    /// <summary>Reductions of a whole vector to one scalar: <c>addv</c>, <c>umaxv</c>, <c>fminv</c>…</summary>
    private static string? SimdAcrossLanes(uint i, bool q, bool u, uint size, uint rn, uint rd)
    {
        var opcode = Bits(i, 16, 12);
        if (opcode is 0b01100 or 0b01111)
        {
            // Floating point: four singles only.
            var name = (opcode, size) switch
            {
                (0b01100, 0) => "fmaxnmv ",
                (0b01100, 2) => "fminnmv ",
                (0b01111, 0) => "fmaxv ",
                (0b01111, 2) => "fminv ",
                _ => null,
            };
            return name is null || !u || !q ? null : name + Scalar(2, rd) + ", " + Vector(rn, 2, true);
        }

        if (size == 3 || (size == 2 && !q))
        {
            return null;
        }

        var sign = u ? "u" : "s";
        return opcode switch
        {
            0b00011 => sign + "addlv " + Scalar(size + 1, rd) + ", " + Vector(rn, size, q),
            0b01010 => sign + "maxv " + Scalar(size, rd) + ", " + Vector(rn, size, q),
            0b11010 => sign + "minv " + Scalar(size, rd) + ", " + Vector(rn, size, q),
            0b11011 when !u => "addv " + Scalar(size, rd) + ", " + Vector(rn, size, q),
            _ => null,
        };
    }

    /// <summary>The scalar pairwise reductions: both elements of a two-element vector into one.</summary>
    private static string? SimdPairwise(uint i, bool u, uint size, uint rn, uint rd)
    {
        var opcode = Bits(i, 16, 12);
        if (!u)
        {
            return opcode == 0b11011 && size == 3 ? "addp " + Scalar(3, rd) + ", " + Vector(rn, 3, true) : null;
        }

        var name = (opcode, size >> 1) switch
        {
            (0b01100, 0) => "fmaxnmp ",
            (0b01100, 1) => "fminnmp ",
            (0b01101, 0) => "faddp ",
            (0b01111, 0) => "fmaxp ",
            (0b01111, 1) => "fminp ",
            _ => null,
        };
        var width = (size & 1) + 2;
        return name is null ? null : name + Scalar(width, rd) + ", " + Vector(rn, width, width == 3);
    }

    /// <summary>movi, mvni, the immediate orr and bic, and the vector fmov: one byte expanded across the register.</summary>
    private static string? SimdImmediate(uint i, bool q, bool invert, uint rd)
    {
        var mode = Bits(i, 15, 12);
        var imm8 = (Bits(i, 18, 16) << 5) | Bits(i, 9, 5);
        if (Bit(i, 11))
        {
            return null;
        }

        if (mode == 0b1111)
        {
            return invert && !q ? null : "fmov " + Vector(rd, invert ? 3u : 2u, q) + ", " + FloatImmediate(imm8);
        }

        if (mode == 0b1110)
        {
            if (!invert)
            {
                return "movi " + Vector(rd, 0, q) + ", " + Imm(imm8);
            }

            // Each bit of the byte is a whole byte of the 64-bit value. The padding is LLVM's ("%#016llx"): sixteen
            // characters with the prefix, so fourteen digits at least, and sixteen zeros for zero.
            var value = 0ul;
            for (var bit = 0; bit < 8; bit++)
            {
                if (((imm8 >> bit) & 1) != 0)
                {
                    value |= 0xFFul << (bit * 8);
                }
            }

            return "movi " + (q ? Vector(rd, 3, true) : Scalar(3, rd)) + ", #" + (value == 0 ? "0000000000000000" : "0x" + value.ToString("x14", CultureInfo.InvariantCulture));
        }

        if ((mode & 0b1110) == 0b1100)
        {
            // "msl" shifts ones in from the right.
            return (invert ? "mvni " : "movi ") + Vector(rd, 2, q) + ", " + Imm(imm8) + ", msl " + Imm((mode & 1) != 0 ? 16 : 8);
        }

        var is32 = (mode & 0b1000) == 0;
        var shift = 8 * ((mode >> 1) & (is32 ? 3u : 1u));
        var name = (mode & 1) == 0 ? (invert ? "mvni " : "movi ") : invert ? "bic " : "orr ";
        return name + Vector(rd, is32 ? 2u : 1u, q) + ", " + Imm(imm8) + (shift != 0 ? ", lsl " + Imm(shift) : string.Empty);
    }

    /// <summary>Shifts by an immediate, whose high bits also say the element's size.</summary>
    private static string? SimdShift(uint i, bool scalar, bool q, bool u, uint rn, uint rd)
    {
        var opcode = Bits(i, 15, 11);
        var encoded = Bits(i, 22, 16);
        var size = (uint)(31 - BitOperations.LeadingZeroCount(Bits(i, 22, 19)));
        var bits = 8u << (int)size;
        var right = Imm((2 * bits) - encoded);
        var left = Imm(encoded - bits);
        if (!scalar && size == 3 && !q)
        {
            return null;
        }

        string? Same(string name, string amount, byte scalarSizes) => scalar
            ? Takes(scalarSizes, size) ? name + " " + Scalar(size, rd) + ", " + Scalar(size, rn) + ", " + amount : null
            : name + " " + Vector(rd, size, q) + ", " + Vector(rn, size, q) + ", " + amount;

        string? Narrow(string name, bool hasScalar) => size == 3
            ? null
            : scalar
                ? hasScalar ? name + " " + Scalar(size, rd) + ", " + Scalar(size + 1, rn) + ", " + right : null
                : name + (q ? "2 " : " ") + Vector(rd, size, q) + ", " + Vector(rn, size + 1, true) + ", " + right;

        switch (opcode)
        {
            case 0b00000:
                return Same(u ? "ushr" : "sshr", right, D);
            case 0b00010:
                return Same(u ? "usra" : "ssra", right, D);
            case 0b00100:
                return Same(u ? "urshr" : "srshr", right, D);
            case 0b00110:
                return Same(u ? "ursra" : "srsra", right, D);
            case 0b01000:
                return u ? Same("sri", right, D) : null;
            case 0b01010:
                return Same(u ? "sli" : "shl", left, D);
            case 0b01100:
                return u ? Same("sqshlu", left, All) : null;
            case 0b01110:
                return Same(u ? "uqshl" : "sqshl", left, All);
            case 0b10000:
                return Narrow(u ? "sqshrun" : "shrn", u);
            case 0b10001:
                return Narrow(u ? "sqrshrun" : "rshrn", u);
            case 0b10010:
                return Narrow(u ? "uqshrn" : "sqshrn", true);
            case 0b10011:
                return Narrow(u ? "uqrshrn" : "sqrshrn", true);
            case 0b10100:
                // sshll and ushll widen. LLVM prints a shift by nothing as it is, not as sxtl or uxtl.
                return scalar || size == 3
                    ? null
                    : (u ? "ushll" : "sshll") + (q ? "2 " : " ") + Vector(rd, size + 1, true) + ", " + Vector(rn, size, q) + ", " + left;
            case 0b11100:
                return size < 2 ? null : Same(u ? "ucvtf" : "scvtf", right, All);
            case 0b11111:
                return size < 2 ? null : Same(u ? "fcvtzu" : "fcvtzs", right, All);
            default:
                return null;
        }
    }

    private static string? SimdByElement(uint i, bool scalar, bool q, bool u, uint size, uint rn, uint rd)
    {
        var opcode = Bits(i, 15, 12);
        var name = s_byElement[(u ? 16u : 0u) | opcode];
        if (name is null)
        {
            return null;
        }

        // The index is H:L:M for halfwords, H:L for words and H for doublewords; the bits it leaves widen the register.
        var h = Bits(i, 11, 11);
        var l = Bits(i, 21, 21);
        var m = Bits(i, 20, 20);
        var rm = Bits(i, 19, 16);
        if (name[0] == 'f')
        {
            if (size < 2 || (size == 3 && (l != 0 || (!q && !scalar))))
            {
                return null;
            }

            var element = Element((m << 4) | rm, size, size == 3 ? h : (h << 1) | l);
            return scalar
                ? name + " " + Scalar(size, rd) + ", " + Scalar(size, rn) + ", " + element
                : name + " " + Vector(rd, size, q) + ", " + Vector(rn, size, q) + ", " + element;
        }

        if (opcode == 0b1110)
        {
            // Dot products: four bytes against four bytes into each word.
            return size != 2 || scalar
                ? null
                : name + " " + Vector(rd, 2, q) + ", " + Vector(rn, 0, q) + ", v" + s_numbers[(m << 4) | rm] + ".4b[" + s_numbers[(h << 1) | l] + "]";
        }

        if (size is 0 or 3)
        {
            return null;
        }

        var operand = size == 1 ? Element(rm, 1, (h << 2) | (l << 1) | m) : Element((m << 4) | rm, 2, (h << 1) | l);
        var saturating = name.StartsWith("sq", StringComparison.Ordinal);
        if (opcode is 2 or 3 or 6 or 7 or 10 or 11)
        {
            // Widening: smull, umlal, sqdmlal…
            return scalar
                ? saturating ? name + " " + Scalar(size + 1, rd) + ", " + Scalar(size, rn) + ", " + operand : null
                : name + (q ? "2 " : " ") + Vector(rd, size + 1, true) + ", " + Vector(rn, size, q) + ", " + operand;
        }

        return scalar
            ? saturating ? name + " " + Scalar(size, rd) + ", " + Scalar(size, rn) + ", " + operand : null
            : name + " " + Vector(rd, size, q) + ", " + Vector(rn, size, q) + ", " + operand;
    }

    /// <summary>AES rounds (vector encoding) and the SHA-1 and SHA-256 instructions with two registers (scalar encoding).</summary>
    private static string? Crypto(uint opcode, bool scalar, uint rn, uint rd)
    {
        if (!scalar)
        {
            var name = opcode switch { 4 => "aese ", 5 => "aesd ", 6 => "aesmc ", 7 => "aesimc ", _ => null };
            return name is null ? null : name + Vector(rd, 0, true) + ", " + Vector(rn, 0, true);
        }

        return opcode switch
        {
            0 => "sha1h " + Scalar(2, rd) + ", " + Scalar(2, rn),
            1 => "sha1su1 " + Vector(rd, 2, true) + ", " + Vector(rn, 2, true),
            2 => "sha256su0 " + Vector(rd, 2, true) + ", " + Vector(rn, 2, true),
            _ => null,
        };
    }

    /// <summary>The SHA-1 and SHA-256 instructions with three registers.</summary>
    private static string? Sha(uint opcode, uint rm, uint rn, uint rd)
    {
        var message = Vector(rm, 2, true);
        return opcode switch
        {
            0 => "sha1c " + Scalar(4, rd) + ", " + Scalar(2, rn) + ", " + message,
            1 => "sha1p " + Scalar(4, rd) + ", " + Scalar(2, rn) + ", " + message,
            2 => "sha1m " + Scalar(4, rd) + ", " + Scalar(2, rn) + ", " + message,
            3 => "sha1su0 " + Vector(rd, 2, true) + ", " + Vector(rn, 2, true) + ", " + message,
            4 => "sha256h " + Scalar(4, rd) + ", " + Scalar(4, rn) + ", " + message,
            5 => "sha256h2 " + Scalar(4, rd) + ", " + Scalar(4, rn) + ", " + message,
            6 => "sha256su1 " + Vector(rd, 2, true) + ", " + Vector(rn, 2, true) + ", " + message,
            _ => null,
        };
    }
}
