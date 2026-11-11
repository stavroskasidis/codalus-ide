using System.Runtime.InteropServices;
using Codalus.Debugging.Host.Disassembly;

namespace Codalus.Debugging.Tests;

/// <summary>
/// M58 (ADR 0094): machine code as the Disassembly window shows it, whatever architecture the tests run on: x64 through
/// Iced, arm64 through <see cref="Arm64Disassembler"/> (its own instructions are in Arm64DisassemblerTests).
/// </summary>
public sealed class DisassemblerTests
{
    [Fact]
    public void X64_code_is_decoded_an_instruction_at_a_time_with_its_bytes_and_address()
    {
        // push rbp; mov rbp, rsp; mov eax, 0x2a; call +0x10; ret
        byte[] code = [0x55, 0x48, 0x89, 0xe5, 0xb8, 0x2a, 0x00, 0x00, 0x00, 0xe8, 0x10, 0x00, 0x00, 0x00, 0xc3];
        var instructions = Disassembler.Decode(Architecture.X64, code, 0x1000);
        Assert.Equal(["push rbp", "mov rbp, rsp", "mov eax, 0x2a", "call 0x101e", "ret"], instructions.Select(i => i.Text));
        Assert.Equal([0x1000UL, 0x1001UL, 0x1004UL, 0x1009UL, 0x100eUL], instructions.Select(i => i.Address));
        Assert.Equal([1, 3, 5, 5, 1], instructions.Select(i => i.Bytes.Length));
        Assert.Equal(code, instructions.SelectMany(i => i.Bytes));
    }

    [Fact]
    public void Bytes_that_are_no_instruction_and_a_cut_off_tail_do_not_stop_the_decoding()
    {
        // An undefined opcode (0x06 in 64-bit mode), then ret; then a mov whose immediate is cut off.
        var instructions = Disassembler.Decode(Architecture.X64, [0x06, 0xc3, 0xb8, 0x01], 0x2000);
        Assert.Equal(["(bad)", "ret"], instructions.Select(i => i.Text));
        Assert.Empty(Disassembler.Decode(Architecture.X64, [], 0));
    }

    [Fact]
    public void Arm64_code_is_decoded_four_bytes_at_a_time()
    {
        // mov x0, x19; ret; and two bytes left over.
        var instructions = Disassembler.Decode(Architecture.Arm64, [0xe0, 0x03, 0x13, 0xaa, 0xc0, 0x03, 0x5f, 0xd6, 0x00, 0x00], 0x4000);
        Assert.Equal(["mov x0, x19", "ret"], instructions.Select(i => i.Text));
        Assert.Equal([0x4000UL, 0x4004UL], instructions.Select(i => i.Address));
    }

    [Fact]
    public void Only_x64_and_arm64_are_decoded_and_each_names_its_registers()
    {
        Assert.True(Disassembler.IsSupported(Architecture.X64));
        Assert.True(Disassembler.IsSupported(Architecture.Arm64));
        Assert.False(Disassembler.IsSupported(Architecture.X86));
        Assert.Empty(Disassembler.Decode(Architecture.X86, [0xc3], 0));
        Assert.Equal(["rip", "rsp", "rbp"], Disassembler.RegisterNames(Architecture.X64).Take(3));
        Assert.Equal(["pc", "sp", "fp", "x0"], Disassembler.RegisterNames(Architecture.Arm64).Take(4));
        Assert.Equal("lr", Disassembler.RegisterNames(Architecture.Arm64)[^1]);
    }
}
