using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace Codalus.Debugging.Host.CoreClr;

/// <summary>
/// Redirects a function a loaded 64-bit ELF library imports (ADR 0115): the slot the library calls it through (its
/// procedure linkage's, or its global offset table's) is found from the library's dynamic section as the loader mapped
/// it, and rewritten. The loader made those slots read-only once it had filled them, so the page is made writable for
/// the write and read-only again after.
/// </summary>
internal static unsafe class ElfImports
{
    private const int ProgramDynamic = 2;
    private const long TagPltSize = 2, TagStrings = 5, TagSymbols = 6, TagRela = 7, TagRelaSize = 8, TagJumpRelocations = 23;
    private const int Read = 1, Write = 2;

    /// <summary>
    /// Makes the library mapped at <paramref name="address"/> call <paramref name="replacement"/> where it calls its
    /// import <paramref name="name"/>. False when the library doesn't import it (or isn't an ELF library).
    /// </summary>
    public static bool Redirect(nint address, string name, nint replacement)
    {
        var slots = Slots(new ReadOnlySpan<byte>((void*)address, int.MaxValue), name, mapped: true);
        foreach (var slot in slots)
        {
            var at = address + (nint)slot;
            var page = at & ~(nint)(Environment.SystemPageSize - 1);
            if (mprotect(page, (nuint)Environment.SystemPageSize, Read | Write) != 0)
            {
                return false;
            }

            *(nint*)at = replacement;
            _ = mprotect(page, (nuint)Environment.SystemPageSize, Read);
        }

        return slots.Count > 0;
    }

    /// <summary>
    /// The offsets, from the library's start, of the slots through which it calls its import <paramref name="name"/>.
    /// </summary>
    /// <param name="image">The library: as the loader mapped it (<paramref name="mapped"/>), or its file.</param>
    /// <param name="mapped">Whether addresses in <paramref name="image"/> are offsets from its start (a mapped library whose first segment is at 0) or need its segments' file offsets.</param>
    internal static IReadOnlyList<ulong> Slots(ReadOnlySpan<byte> image, string name, bool mapped)
    {
        var slots = new List<ulong>();
        if (image.Length < 64 || !image[..4].SequenceEqual("\u007fELF"u8) || image[4] != 2 || image[5] != 1)
        {
            return slots; // not a little-endian ELF64
        }

        var headers = (int)BinaryPrimitives.ReadUInt64LittleEndian(image[0x20..]);
        var headerSize = BinaryPrimitives.ReadUInt16LittleEndian(image[0x36..]);
        var headerCount = BinaryPrimitives.ReadUInt16LittleEndian(image[0x38..]);
        var loads = new List<(ulong Address, ulong Offset, ulong Size)>();
        ulong dynamic = 0, dynamicSize = 0;
        for (var i = 0; i < headerCount; i++)
        {
            var header = image.Slice(headers + (i * headerSize), headerSize);
            var type = BinaryPrimitives.ReadUInt32LittleEndian(header);
            var offset = BinaryPrimitives.ReadUInt64LittleEndian(header[8..]);
            var virtualAddress = BinaryPrimitives.ReadUInt64LittleEndian(header[16..]);
            var fileSize = BinaryPrimitives.ReadUInt64LittleEndian(header[32..]);
            if (type == 1)
            {
                loads.Add((virtualAddress, offset, fileSize));
            }
            else if (type == ProgramDynamic)
            {
                (dynamic, dynamicSize) = (virtualAddress, fileSize);
            }
        }

        // An address in the library, as an index into the image.
        int At(ulong virtualAddress)
        {
            if (mapped)
            {
                return checked((int)virtualAddress);
            }

            foreach (var (address, offset, size) in loads)
            {
                if (virtualAddress >= address && virtualAddress < address + size)
                {
                    return checked((int)(virtualAddress - address + offset));
                }
            }

            return -1;
        }

        ulong strings = 0, symbols = 0, rela = 0, relaSize = 0, jumps = 0, jumpsSize = 0;
        var entries = At(dynamic);
        for (var i = 0UL; entries >= 0 && i + 16 <= dynamicSize; i += 16)
        {
            var tag = BinaryPrimitives.ReadInt64LittleEndian(image[(entries + (int)i)..]);
            var value = BinaryPrimitives.ReadUInt64LittleEndian(image[(entries + (int)i + 8)..]);
            switch (tag)
            {
                case 0: i = dynamicSize; break;
                case TagStrings: strings = value; break;
                case TagSymbols: symbols = value; break;
                case TagRela: rela = value; break;
                case TagRelaSize: relaSize = value; break;
                case TagJumpRelocations: jumps = value; break;
                case TagPltSize: jumpsSize = value; break;
            }
        }

        if (strings == 0 || symbols == 0)
        {
            return slots;
        }

        var wanted = Encoding.ASCII.GetBytes(name);
        foreach (var (table, size) in new[] { (jumps, jumpsSize), (rela, relaSize) })
        {
            var start = table == 0 ? -1 : At(table);
            for (var i = 0UL; start >= 0 && i + 24 <= size; i += 24)
            {
                var entry = image[(start + (int)i)..];
                var info = BinaryPrimitives.ReadUInt64LittleEndian(entry[8..]);
                var symbol = At(symbols + ((info >> 32) * 24));
                if ((info >> 32) == 0 || symbol < 0)
                {
                    continue;
                }

                var text = At(strings + BinaryPrimitives.ReadUInt32LittleEndian(image[symbol..]));
                if (text >= 0 && image[text..].StartsWith(wanted) && image[text + wanted.Length] == 0)
                {
                    slots.Add(BinaryPrimitives.ReadUInt64LittleEndian(entry));
                }
            }
        }

        return slots;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int mprotect(nint address, nuint length, int protection);
}
