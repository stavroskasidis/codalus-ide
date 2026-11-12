using System.Collections.Immutable;
using System.Globalization;
using System.Reflection.PortableExecutable;

namespace Codalus.Debugging.Symbols;

/// <summary>A checksum of the PDB a module was built with: the algorithm's name (<c>SHA256</c>) and the hash.</summary>
public sealed record SymbolChecksum(string Algorithm, ImmutableArray<byte> Hash)
{
    /// <summary>As symbol servers read it: <c>SHA256:&lt;lowercase hex&gt;</c>.</summary>
    public override string ToString() => Algorithm + ":" + Convert.ToHexStringLower(Hash.AsSpan());
}

/// <summary>
/// Which PDB belongs to a module, as the module's debug directory says: the PDB's file name, the GUID (and, for a
/// Windows PDB, the age) the two share, and the PDB's checksums. <see cref="Key"/> is where a symbol server or a symbol
/// store keeps that PDB.
/// </summary>
/// <param name="PdbFileName">The PDB's file name, without the build machine's folder the module records with it.</param>
/// <param name="Guid">The id the compiler gave the module and its PDB.</param>
/// <param name="Age">A Windows PDB's age (how many times it was rewritten); 1 for a portable PDB.</param>
/// <param name="IsPortable">Whether the PDB is a portable PDB (the format .NET SDK projects build), not a Windows PDB.</param>
/// <param name="Checksums">The PDB's checksums; empty when the compiler recorded none.</param>
public sealed record SymbolIdentity(string PdbFileName, Guid Guid, int Age, bool IsPortable, IReadOnlyList<SymbolChecksum> Checksums)
{
    /// <summary>The minor version of a CodeView debug directory entry that names a portable PDB ("PM").</summary>
    private const ushort PortableCodeViewVersion = 0x504D;

    /// <summary>
    /// The PDB's key in the Simple Symbol Query Protocol (dotnet/symstore, <c>SSQP_Key_Conventions.md</c>):
    /// <c>name.pdb/&lt;guid&gt;FFFFFFFF/name.pdb</c> for a portable PDB, <c>name.pdb/&lt;guid&gt;&lt;age&gt;/name.pdb</c> for a
    /// Windows one. It is the path under a server's URL, and under a symbol store's or the cache's folder.
    /// </summary>
    public string Key => BuildKey(PdbFileName, Guid, Age, IsPortable);

    /// <summary>
    /// The <c>SymbolChecksum</c> request header's value (<c>SHA256:&lt;hex&gt;</c>, several joined with <c>;</c>), which
    /// NuGet.org's server needs to answer for a portable PDB; null for a Windows PDB or without checksums.
    /// </summary>
    public string? ChecksumHeader => IsPortable && Checksums.Count > 0 ? string.Join(';', Checksums) : null;

    /// <summary>
    /// Reads <paramref name="modulePath"/>'s debug directory. Null when it names no PDB (built without symbols, or with
    /// an embedded PDB only). Throws <see cref="IOException"/>, <see cref="UnauthorizedAccessException"/> or
    /// <see cref="BadImageFormatException"/> when the file can't be read as a module.
    /// </summary>
    public static SymbolIdentity? Read(string modulePath)
    {
        // Shared for everything: the debuggee has the module loaded, and a build may be replacing it.
        using var stream = new FileStream(modulePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var pe = new PEReader(stream);
        return Read(pe);
    }

    /// <summary>Reads the debug directory of a module that is already open; see <see cref="Read(string)"/>.</summary>
    public static SymbolIdentity? Read(PEReader pe)
    {
        var entries = pe.ReadDebugDirectory();
        var codeView = entries.FirstOrDefault(e => e.Type == DebugDirectoryEntryType.CodeView);
        if (codeView.DataSize == 0)
        {
            return null;
        }

        var data = pe.ReadCodeViewDebugDirectoryData(codeView);
        var name = FileName(data.Path);
        if (!IsFileName(name))
        {
            return null;
        }

        List<SymbolChecksum> checksums = [];
        foreach (var entry in entries.Where(e => e.Type == DebugDirectoryEntryType.PdbChecksum))
        {
            var checksum = pe.ReadPdbChecksumDebugDirectoryData(entry);
            checksums.Add(new SymbolChecksum(checksum.AlgorithmName, checksum.Checksum));
        }

        return new SymbolIdentity(name, data.Guid, data.Age, codeView.MinorVersion == PortableCodeViewVersion, checksums);
    }

    /// <summary>
    /// The symbol-server key of a PDB. <paramref name="pdbPath"/> is the path the module records, of which only the file
    /// name counts, lowercased; the GUID is 32 lowercase hex digits; a Windows PDB's age is lowercase hex without
    /// leading zeros, and a portable PDB has <c>FFFFFFFF</c> in its place.
    /// </summary>
    public static string BuildKey(string pdbPath, Guid guid, int age, bool isPortable)
    {
        var name = FileName(pdbPath).ToLowerInvariant();
        var id = guid.ToString("N") + (isPortable ? "FFFFFFFF" : ((uint)age).ToString("x", CultureInfo.InvariantCulture));
        return name + "/" + id + "/" + name;
    }

    /// <summary>The last segment of a path written on any system: a module on Linux may record <c>C:\build\App.pdb</c>.</summary>
    internal static string FileName(string path) => path[(path.LastIndexOfAny(['/', '\\']) + 1)..];

    /// <summary>
    /// Whether a name read from a module is safe as a file name under the cache: the module is someone else's file, and
    /// its PDB name becomes part of a path and of a URL.
    /// </summary>
    private static bool IsFileName(string name) =>
        name.Trim('.', ' ').Length > 0 && !name.Any(c => char.IsControl(c) || c is '<' or '>' or ':' or '"' or '|' or '?' or '*');
}
