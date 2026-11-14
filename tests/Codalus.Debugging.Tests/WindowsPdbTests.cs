using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using Codalus.Debugging.Host.NetFx;

namespace Codalus.Debugging.Tests;

/// <summary>
/// M58 (ADR 0094): Windows PDBs, the format old-style .NET Framework projects and older libraries build, read by a
/// managed reader on every OS. The fixture is a real one: Microsoft.ApplicationInsights' assembly and the Windows PDB
/// its package ships (the build copies both to <c>windows-pdb/</c>). The module's lines, documents and locals come
/// from it as they do from a portable PDB, and a PDB of another build is refused.
/// </summary>
public sealed class WindowsPdbTests : IDisposable
{
    private static readonly string s_fixture = Path.Combine(AppContext.BaseDirectory, "windows-pdb");

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "codalus-winpdb-" + Guid.NewGuid().ToString("N"));

    public WindowsPdbTests()
    {
        Directory.CreateDirectory(_folder);
        foreach (var file in Directory.GetFiles(s_fixture))
        {
            File.Copy(file, Path.Combine(_folder, Path.GetFileName(file)));
        }
    }

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private string Dll => Path.Combine(_folder, "Microsoft.ApplicationInsights.dll");

    private string Pdb => Path.Combine(_folder, "Microsoft.ApplicationInsights.pdb");

    [Fact]
    public void The_fixture_is_a_Windows_PDB()
    {
        Assert.True(WindowsPdbSymbols.IsWindowsPdb(Pdb), "Microsoft.ApplicationInsights no longer ships a Windows PDB: the tests need another fixture.");
        Assert.False(WindowsPdbSymbols.IsWindowsPdb(Dll));
    }

    [Fact]
    public void A_modules_lines_documents_and_locals_come_from_its_Windows_PDB()
    {
        using var info = ClrModuleInfo.Open(Dll);
        Assert.True(info.HasSymbols, info.SymbolStatus);
        Assert.Equal("Symbols loaded.", info.SymbolStatus);
        Assert.Equal(Pdb, info.PdbPath);

        // TelemetryClient.TrackEvent(string, …): a method with a body of several statements and locals.
        var type = info.Metadata.TypeDefinitions.Select(info.Metadata.GetTypeDefinition).Single(t => info.Metadata.GetString(t.Name) == "TelemetryClient" && info.Metadata.GetString(t.Namespace) == "Microsoft.ApplicationInsights");
        var methods = type.GetMethods().Where(h => info.Metadata.GetString(info.Metadata.GetMethodDefinition(h).Name) == "TrackEvent").Select(h => MetadataTokens.GetToken(h)).ToList();
        Assert.NotEmpty(methods);
        var points = methods.Select(m => info.SequencePoints(m)).First(p => p.Count > 2);
        var visible = points.Where(p => !p.IsHidden).ToList();
        Assert.All(visible, p => Assert.EndsWith("TelemetryClient.cs", p.Document, StringComparison.OrdinalIgnoreCase));
        Assert.All(visible, p => Assert.True(p.StartLine > 0 && p.EndLine >= p.StartLine && p.StartColumn > 0, $"{p.StartLine}:{p.StartColumn}-{p.EndLine}:{p.EndColumn}"));
        Assert.Equal(points.Select(p => p.Offset).Order(), points.Select(p => p.Offset));

        // A breakpoint's place: the document's methods, and the line's statement.
        var document = visible[0].Document;
        Assert.NotNull(info.LineAt(methods.First(m => info.SequencePoints(m).Count > 2), visible[1].Offset));
        Assert.Contains(info.Locations(document, visible[1].StartLine), l => l.Line == visible[1].StartLine);

        // Some method of the module has named locals in scope at its last statement.
        var named = info.Metadata.MethodDefinitions.Select(h => MetadataTokens.GetToken(h))
            .Select(m => (Method: m, Points: info.SequencePoints(m)))
            .Where(m => m.Points.Count > 0)
            .Select(m => info.Locals(m.Method, m.Points[^1].Offset))
            .FirstOrDefault(l => l.Count > 0);
        Assert.NotNull(named);
        Assert.All(named, l => Assert.False(string.IsNullOrEmpty(l.Name)));
    }

    [Fact]
    public void A_Windows_PDB_given_as_the_modules_symbols_is_read_too()
    {
        // As a symbol path or a symbol server gives it: not next to the module.
        var elsewhere = Path.Combine(_folder, "symbols", "Microsoft.ApplicationInsights.pdb");
        Directory.CreateDirectory(Path.GetDirectoryName(elsewhere)!);
        File.Move(Pdb, elsewhere);
        using (var without = ClrModuleInfo.Open(Dll))
        {
            Assert.False(without.HasSymbols);
        }

        using var info = ClrModuleInfo.Open(Dll, elsewhere, "Symbols loaded from the symbol path.");
        Assert.True(info.HasSymbols, info.SymbolStatus);
        Assert.Equal("Symbols loaded from the symbol path.", info.SymbolStatus);
    }

    [Fact]
    public void Another_builds_Windows_PDB_is_refused_with_the_reason()
    {
        // The module's own PDB with its identity changed: the GUID in the PDB's information stream no longer matches.
        var bytes = File.ReadAllBytes(Pdb);
        var guid = Identity(Dll).ToByteArray();
        var at = bytes.AsSpan().IndexOf(guid);
        Assert.True(at > 0, "the PDB holds the module's GUID");
        bytes[at] ^= 0xFF;
        File.WriteAllBytes(Pdb, bytes);

        using var info = ClrModuleInfo.Open(Dll);
        Assert.False(info.HasSymbols);
        Assert.Contains("isn't this build's PDB", info.SymbolStatus, StringComparison.Ordinal);
    }

    private static Guid Identity(string dll)
    {
        using var pe = new System.Reflection.PortableExecutable.PEReader(File.OpenRead(dll));
        var entry = pe.ReadDebugDirectory().First(e => e.Type == System.Reflection.PortableExecutable.DebugDirectoryEntryType.CodeView);
        return pe.ReadCodeViewDebugDirectoryData(entry).Guid;
    }
}
