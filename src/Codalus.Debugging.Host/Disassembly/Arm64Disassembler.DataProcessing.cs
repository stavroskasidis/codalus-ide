namespace Codalus.Debugging.Host.Disassembly;

/// <summary>
/// Data processing on registers (M58): logic and arithmetic with a shifted or extended operand, carries, conditional
/// compares and selects, multiplies and divides, variable shifts, bit counts and reversals, and CRC32.
/// </summary>
internal static partial class Arm64Disassembler
{
    private static string? DataProcessingRegister(uint i)
    {
        var is64 = Bit(i, 31);
        var rm = Bits(i, 20, 16);
        var rn = Bits(i, 9, 5);
        var rd = i & 31;
        if (!Bit(i, 28))
        {
            if (Bit(i, 24) && Bit(i, 21))
            {
                return AddSubtractExtended(i, is64, rm, rn, rd);
            }

            // A shifted second operand, for the logical and the add/subtract instructions alike.
            var shift = Bits(i, 23, 22);
            var amount = Bits(i, 15, 10);
            if (!is64 && amount > 31)
            {
                return null;
            }

            var operand = Reg(is64, rm) + (shift == 0 && amount == 0 ? string.Empty : ", " + s_shifts[shift] + " " + Imm(amount));
            var opc = Bits(i, 30, 29);
            if (!Bit(i, 24))
            {
                var negate = Bit(i, 21);
                if (opc == 1 && rn == 31 && (negate || (shift == 0 && amount == 0)))
                {
                    return (negate ? "mvn " : "mov ") + Reg(is64, rd) + ", " + operand;
                }

                if (opc == 3 && !negate && rd == 31)
                {
                    return "tst " + Reg(is64, rn) + ", " + operand;
                }

                var name = opc switch { 0 => negate ? "bic " : "and ", 1 => negate ? "orn " : "orr ", 2 => negate ? "eon " : "eor ", _ => negate ? "bics " : "ands " };
                return name + Reg(is64, rd) + ", " + Reg(is64, rn) + ", " + operand;
            }

            if (shift == 3)
            {
                return null;
            }

            var subtract = Bit(i, 30);
            var setFlags = Bit(i, 29);
            if (setFlags && rd == 31)
            {
                return (subtract ? "cmp " : "cmn ") + Reg(is64, rn) + ", " + operand;
            }

            if (subtract && rn == 31)
            {
                return (setFlags ? "negs " : "neg ") + Reg(is64, rd) + ", " + operand;
            }

            return (subtract ? "sub" : "add") + (setFlags ? "s " : " ") + Reg(is64, rd) + ", " + Reg(is64, rn) + ", " + operand;
        }

        if (Bit(i, 24))
        {
            return MultiplyAdd(i, is64, rm, rn, rd);
        }

        switch (Bits(i, 23, 21))
        {
            case 0b000:
            {
                // adc and sbc; ngc when the first operand is the zero register.
                if (Bits(i, 15, 10) != 0)
                {
                    return null; // rmif, setf8 and setf16.
                }

                var subtract = Bit(i, 30);
                var flags = Bit(i, 29) ? "s " : " ";
                return subtract && rn == 31
                    ? "ngc" + flags + Reg(is64, rd) + ", " + Reg(is64, rm)
                    : (subtract ? "sbc" : "adc") + flags + Reg(is64, rd) + ", " + Reg(is64, rn) + ", " + Reg(is64, rm);
            }

            case 0b010:
            {
                // ccmp and ccmn: compare when the condition holds, else set the flags to the immediate.
                if (!Bit(i, 29) || Bit(i, 10) || Bit(i, 4))
                {
                    return null;
                }

                return (Bit(i, 30) ? "ccmp " : "ccmn ") + Reg(is64, rn) + ", " + (Bit(i, 11) ? Imm(rm) : Reg(is64, rm)) + ", " + Imm(i & 15) + ", " + s_conditions[Bits(i, 15, 12)];
            }

            case 0b100:
                return ConditionalSelect(i, is64, rm, rn, rd);
            case 0b110:
                return Bit(i, 30) ? OneSource(i, is64, rm, rn, rd) : TwoSource(i, is64, rm, rn, rd);
            default:
                return null;
        }
    }

    private static string? AddSubtractExtended(uint i, bool is64, uint rm, uint rn, uint rd)
    {
        var option = Bits(i, 15, 13);
        var amount = Bits(i, 12, 10);
        if (Bits(i, 23, 22) != 0 || amount > 4)
        {
            return null;
        }

        var subtract = Bit(i, 30);
        var setFlags = Bit(i, 29);

        // Only uxtx and sxtx read all 64 bits of the second register. With the stack pointer as an operand the
        // extension of the register's own size is written "lsl", or left out when it shifts by nothing.
        var operand = Reg(is64 && (option & 3) == 3, rm);
        if (option == (is64 ? 3u : 2u) && (rn == 31 || (rd == 31 && !setFlags)))
        {
            operand += amount != 0 ? ", lsl " + Imm(amount) : string.Empty;
        }
        else
        {
            operand += ", " + s_extends[option] + (amount != 0 ? " " + Imm(amount) : string.Empty);
        }

        if (setFlags && rd == 31)
        {
            return (subtract ? "cmp " : "cmn ") + RegSp(is64, rn) + ", " + operand;
        }

        return (subtract ? "sub" : "add") + (setFlags ? "s " : " ") + (setFlags ? Reg(is64, rd) : RegSp(is64, rd)) + ", " + RegSp(is64, rn) + ", " + operand;
    }

    private static string? ConditionalSelect(uint i, bool is64, uint rm, uint rn, uint rd)
    {
        if (Bit(i, 29) || Bit(i, 11))
        {
            return null;
        }

        var invert = Bit(i, 30);
        var increment = Bit(i, 10);
        var condition = Bits(i, 15, 12);
        var d = Reg(is64, rd);

        // Both sources the same register is the short form on the opposite condition ("al" and "nv" have none).
        if (rn == rm && condition < 14 && (invert || increment))
        {
            var opposite = s_conditions[condition ^ 1];
            if (invert && increment)
            {
                return "cneg " + d + ", " + Reg(is64, rn) + ", " + opposite;
            }

            return rn == 31
                ? (invert ? "csetm " : "cset ") + d + ", " + opposite
                : (invert ? "cinv " : "cinc ") + d + ", " + Reg(is64, rn) + ", " + opposite;
        }

        var name = invert ? (increment ? "csneg " : "csinv ") : increment ? "csinc " : "csel ";
        return name + d + ", " + Reg(is64, rn) + ", " + Reg(is64, rm) + ", " + s_conditions[condition];
    }

    private static string? TwoSource(uint i, bool is64, uint rm, uint rn, uint rd)
    {
        if (Bit(i, 29))
        {
            return null;
        }

        var opcode = Bits(i, 15, 10);
        var name = opcode switch
        {
            0b000010 => "udiv ",
            0b000011 => "sdiv ",
            0b001000 => "lsl ",
            0b001001 => "lsr ",
            0b001010 => "asr ",
            0b001011 => "ror ",
            _ => null,
        };
        if (name is not null)
        {
            return name + Reg(is64, rd) + ", " + Reg(is64, rn) + ", " + Reg(is64, rm);
        }

        // crc32 and crc32c: W registers, but the 64-bit data form has the size bit set.
        if ((opcode & 0b111000) != 0b010000 || is64 != ((opcode & 3) == 3))
        {
            return null;
        }

        return "crc32" + ((opcode & 4) != 0 ? "c" : string.Empty) + "bhwx"[(int)(opcode & 3)] + " " + s_w[rd] + ", " + s_w[rn] + ", " + Reg(is64, rm);
    }

    private static string? OneSource(uint i, bool is64, uint rm, uint rn, uint rd)
    {
        if (Bit(i, 29) || rm != 0)
        {
            return null; // With rm set these sign and authenticate pointers.
        }

        var name = Bits(i, 15, 10) switch
        {
            0b000000 => "rbit ",
            0b000001 => "rev16 ",
            0b000010 => is64 ? "rev32 " : "rev ",
            0b000011 => is64 ? "rev " : null,
            0b000100 => "clz ",
            0b000101 => "cls ",
            _ => null,
        };
        return name is null ? null : name + Reg(is64, rd) + ", " + Reg(is64, rn);
    }

    /// <summary>madd and msub and the widening multiplies, by their short names when nothing is added (the addend is the zero register).</summary>
    private static string? MultiplyAdd(uint i, bool is64, uint rm, uint rn, uint rd)
    {
        if (Bits(i, 30, 29) != 0)
        {
            return null;
        }

        var ra = Bits(i, 14, 10);
        var subtract = Bit(i, 15);
        var op31 = Bits(i, 23, 21);
        switch (op31)
        {
            case 0b000:
            {
                var operands = Reg(is64, rd) + ", " + Reg(is64, rn) + ", " + Reg(is64, rm);
                return ra == 31
                    ? (subtract ? "mneg " : "mul ") + operands
                    : (subtract ? "msub " : "madd ") + operands + ", " + Reg(is64, ra);
            }

            case 0b001 or 0b101 when is64:
            {
                var sign = op31 == 1 ? "s" : "u";
                var operands = s_x[rd] + ", " + s_w[rn] + ", " + s_w[rm];
                return ra == 31
                    ? sign + (subtract ? "mnegl " : "mull ") + operands
                    : sign + (subtract ? "msubl " : "maddl ") + operands + ", " + s_x[ra];
            }

            case 0b010 or 0b110 when is64 && !subtract:
                return (op31 == 2 ? "smulh " : "umulh ") + s_x[rd] + ", " + s_x[rn] + ", " + s_x[rm];
            default:
                return null;
        }
    }
}
