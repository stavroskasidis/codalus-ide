using System.Buffers.Binary;
using System.Text;
using Codalus.Debugging.Host.CoreClr;

namespace Codalus.Debugging.Tests;

/// <summary>
/// ADR 0116, on every OS: what the Codalus debugger on an Android device reads without dbgshim. The runtime's base
/// address from <c>/proc/&lt;pid&gt;/maps</c>, which the debugging library's export takes, and the slots through which
/// a library calls an import (the debugging libraries' <c>kill</c>, which Android refuses for the null signal), in a
/// small ELF64 library written here.
/// </summary>
public sealed class AndroidRuntimeTests
{
    [Fact]
    public void The_runtime_starts_at_its_lowest_mapping_of_that_exact_file()
    {
        string[] maps =
        [
            "6eb3070000-6eb3075000 r--p 00000000 fe:2f 9 /data/app/~~a==/com.contoso.app-b==/lib/arm64/libclrjit.so",
            "6eb3090000-6eb3200000 r-xp 00010000 fe:2f 7 /data/app/~~a==/com.contoso.app-b==/lib/arm64/libcoreclr.so",
            "6eb3080000-6eb3090000 r--p 00000000 fe:2f 7 /data/app/~~a==/com.contoso.app-b==/lib/arm64/libcoreclr.so",
            "6eb3200000-6eb3210000 rw-p 00180000 fe:2f 7 /data/app/~~a==/com.contoso.app-b==/lib/arm64/libcoreclr.so",
            "6ea0000000-6ea0010000 r--p 00000000 fe:2f 8 /data/local/tmp/other/libcoreclr.so",
            "6ea1000000-6ea1001000 rw-p 00000000 00:00 0 [anon:dalvik-main space]",
        ];

        Assert.Equal(0x6eb3080000UL, AndroidRuntime.BaseAddress(maps, "/data/app/~~a==/com.contoso.app-b==/lib/arm64/libcoreclr.so"));
        Assert.Equal(0x6ea0000000UL, AndroidRuntime.BaseAddress(maps, "/data/local/tmp/other/libcoreclr.so"));
        Assert.Null(AndroidRuntime.BaseAddress(maps, "/data/app/libcoreclr.so"));
    }

    [Theory]
    [InlineData(0x1000UL, false)] // a file: addresses are its segments', read through their file offsets
    [InlineData(0UL, true)] // as the loader maps it: addresses are offsets from its start
    public void A_librarys_import_slots_are_found_from_its_dynamic_section(ulong loadAddress, bool mapped)
    {
        var image = Library(loadAddress);
        Assert.Equal([loadAddress + 0x2008], ElfImports.Slots(image, "kill", mapped));
        Assert.Equal([loadAddress + 0x2010, loadAddress + 0x2018], ElfImports.Slots(image, "open", mapped));
        Assert.Empty(ElfImports.Slots(image, "ki", mapped)); // a name's prefix isn't the name
        Assert.Empty(ElfImports.Slots(image, "read", mapped));
        Assert.Empty(ElfImports.Slots("not an ELF file at all, but long enough to have a header's size......"u8, "kill", mapped));
    }

    /// <summary>
    /// An ELF64 library with one loadable segment at <paramref name="loadAddress"/>: <c>kill</c> imported through the
    /// procedure linkage (a jump slot), <c>open</c> through it and through the global offset table (a data relocation).
    /// </summary>
    private static byte[] Library(ulong loadAddress)
    {
        var image = new byte[0x400];
        Span<byte> span = image;
        "\u007fELF"u8.CopyTo(span);
        span[4] = 2; // 64-bit
        span[5] = 1; // little-endian
        BinaryPrimitives.WriteUInt64LittleEndian(span[0x20..], 0x40); // program headers
        BinaryPrimitives.WriteUInt16LittleEndian(span[0x36..], 56);
        BinaryPrimitives.WriteUInt16LittleEndian(span[0x38..], 2);

        void Header(int at, uint type, ulong offset, ulong size)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan()[at..], type);
            BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan()[(at + 8)..], offset);
            BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan()[(at + 16)..], loadAddress + offset);
            BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan()[(at + 32)..], size);
        }

        Header(0x40, 1, 0, (ulong)image.Length); // PT_LOAD
        Header(0x78, 2, 0x300, 6 * 16); // PT_DYNAMIC

        Encoding.ASCII.GetBytes("\0kill\0open\0").CopyTo(span[0x100..]); // the strings: kill at 1, open at 6
        void Symbol(int index, uint name) => BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan()[(0x140 + (index * 24))..], name);
        Symbol(1, 1);
        Symbol(2, 6);

        void Relocation(int at, ulong slot, ulong symbol, uint type)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan()[at..], loadAddress + slot);
            BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan()[(at + 8)..], (symbol << 32) | type);
        }

        Relocation(0x200, 0x2008, 1, 1026); // R_AARCH64_JUMP_SLOT kill
        Relocation(0x218, 0x2010, 2, 1026); // R_AARCH64_JUMP_SLOT open
        Relocation(0x260, 0x2018, 2, 1025); // R_AARCH64_GLOB_DAT open

        void Dynamic(int index, long tag, ulong value)
        {
            BinaryPrimitives.WriteInt64LittleEndian(image.AsSpan()[(0x300 + (index * 16))..], tag);
            BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan()[(0x300 + (index * 16) + 8)..], value);
        }

        Dynamic(0, 5, loadAddress + 0x100); // DT_STRTAB
        Dynamic(1, 6, loadAddress + 0x140); // DT_SYMTAB
        Dynamic(2, 23, loadAddress + 0x200); // DT_JMPREL
        Dynamic(3, 2, 48); // DT_PLTRELSZ
        Dynamic(4, 7, loadAddress + 0x260); // DT_RELA
        Dynamic(5, 8, 24); // DT_RELASZ
        return image;
    }
}
