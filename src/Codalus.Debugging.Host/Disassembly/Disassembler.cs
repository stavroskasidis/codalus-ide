using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Iced.Intel;

namespace Codalus.Debugging.Host.Disassembly;

/// <summary>One machine instruction: where it is, its bytes and its text.</summary>
internal readonly record struct DisassembledCode(ulong Address, byte[] Bytes, string Text);

/// <summary>
/// Machine code as text, for the Disassembly window (M58, ADR 0094): x64 through Iced (MIT), arm64 through
/// <see cref="Arm64Disassembler"/>. The adapter runs as the architecture of the program it debugs (the runtime's
/// debugging library is the program's own), so that architecture is this process's.
/// </summary>
internal static class Disassembler
{
    /// <summary>The architecture of the debugged program's code: this process's.</summary>
    public static Architecture Architecture => RuntimeInformation.ProcessArchitecture;

    public static bool IsSupported(Architecture architecture) => architecture is Architecture.X64 or Architecture.Arm64;

    /// <summary>The longest instruction of an architecture, in bytes: how much to read to decode one.</summary>
    public static int MaxInstructionLength(Architecture architecture) => architecture == Architecture.Arm64 ? 4 : 15;

    /// <summary>
    /// The instructions of <paramref name="code"/>, which starts at <paramref name="address"/>, in order. Bytes that
    /// decode to nothing are an instruction of their own (<c>(bad)</c> on x64, <c>.inst</c> on arm64), and a tail too
    /// short for an instruction is left out.
    /// </summary>
    public static List<DisassembledCode> Decode(Architecture architecture, ReadOnlySpan<byte> code, ulong address)
    {
        var result = new List<DisassembledCode>();
        if (architecture == Architecture.Arm64)
        {
            for (var at = 0; at + 4 <= code.Length; at += 4)
            {
                var word = BinaryPrimitives.ReadUInt32LittleEndian(code[at..]);
                result.Add(new DisassembledCode(address + (ulong)at, code.Slice(at, 4).ToArray(), Arm64Disassembler.Decode(word, address + (ulong)at)));
            }

            return result;
        }

        if (architecture != Architecture.X64)
        {
            return result;
        }

        var bytes = code.ToArray();
        var reader = new ByteArrayCodeReader(bytes);
        var decoder = Decoder.Create(64, reader, address);
        var formatter = new IntelFormatter(new FormatterOptions
        {
            HexPrefix = "0x",
            HexSuffix = null,
            UppercaseHex = false,
            SpaceAfterOperandSeparator = true,
            RipRelativeAddresses = false,
            BranchLeadingZeros = false,
            ShowBranchSize = false,
        });
        var output = new StringOutput();
        var end = address + (ulong)bytes.Length;
        while (decoder.IP < end)
        {
            var start = decoder.IP;
            decoder.Decode(out var instruction);
            var length = (int)(decoder.IP - start);
            if (length <= 0)
            {
                break;
            }

            var text = "(bad)";
            if (!instruction.IsInvalid)
            {
                formatter.Format(instruction, output);
                text = output.ToStringAndReset();
            }
            else if (decoder.LastError == DecoderError.NoMoreBytes)
            {
                break; // the last bytes aren't a whole instruction
            }
            else
            {
                // One byte that starts no instruction (the decoder took more, looking for one): on from the next.
                length = 1;
                reader.Position = (int)(start - address) + 1;
                decoder.IP = start + 1;
            }

            result.Add(new DisassembledCode(start, bytes.AsSpan((int)(start - address), length).ToArray(), text));
        }

        return result;
    }

    /// <summary>The registers a frame shows, by the runtime's numbering (<c>CorDebugRegister</c>) for an architecture: the instruction and stack pointers first.</summary>
    public static IReadOnlyList<string> RegisterNames(Architecture architecture) => architecture switch
    {
        Architecture.X64 => ["rip", "rsp", "rbp", "rax", "rcx", "rdx", "rbx", "rsi", "rdi", "r8", "r9", "r10", "r11", "r12", "r13", "r14", "r15"],
        Architecture.Arm64 =>
        [
            "pc", "sp", "fp", "x0", "x1", "x2", "x3", "x4", "x5", "x6", "x7", "x8", "x9", "x10", "x11", "x12", "x13", "x14", "x15", "x16", "x17", "x18", "x19",
            "x20", "x21", "x22", "x23", "x24", "x25", "x26", "x27", "x28", "lr",
        ],
        _ => [],
    };
}
