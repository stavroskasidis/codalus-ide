using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Pdb;

namespace Codalus.Debugging.Host.NetFx;

/// <summary>
/// A module's Windows PDB (M58, ADR 0094): the format old-style .NET Framework projects build (<c>DebugType</c>
/// <c>full</c> or <c>pdbonly</c>) and libraries built years ago ship. Read by Mono.Cecil's managed reader (MIT, a port
/// of Microsoft's CCI reader), so it needs neither DIA nor the native <c>diasymreader</c>, and reads on every OS. It
/// gives what <see cref="ClrModuleInfo"/> reads from a portable PDB: a method's sequence points and its locals' names
/// by scope. The PDB must be the module's own (its GUID and age are checked against the module's debug directory).
/// </summary>
internal sealed class WindowsPdbSymbols : IDisposable
{
    /// <summary>How every Windows PDB (an MSF 7 file) starts.</summary>
    private static ReadOnlySpan<byte> Signature => "Microsoft C/C++ MSF 7.00"u8;

    private readonly Lock _gate = new();
    private readonly ModuleDefinition _module;
    private readonly Stream _pdb;

    private WindowsPdbSymbols(ModuleDefinition module, Stream pdb)
    {
        _module = module;
        _pdb = pdb;
    }

    /// <summary>Whether <paramref name="path"/> is a Windows PDB, by its first bytes.</summary>
    public static bool IsWindowsPdb(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            Span<byte> start = stackalloc byte[Signature.Length];
            return stream.ReadAtLeast(start, start.Length, throwOnEndOfStream: false) == start.Length && start.SequenceEqual(Signature);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Opens <paramref name="pdbPath"/> as <paramref name="modulePath"/>'s symbols. Throws
    /// <see cref="InvalidOperationException"/> when it isn't that module's PDB or can't be read.
    /// </summary>
    public static WindowsPdbSymbols Open(string modulePath, string pdbPath)
    {
        Stream? pdb = null;
        try
        {
            pdb = File.OpenRead(pdbPath);
            var module = ModuleDefinition.ReadModule(modulePath, new ReaderParameters(ReadingMode.Deferred)
            {
                ReadSymbols = true,
                SymbolReaderProvider = new NativePdbReaderProvider(),
                SymbolStream = pdb,
                ThrowIfSymbolsAreNotMatching = true,
            });
            return new WindowsPdbSymbols(module, pdb);
        }
        catch (Exception ex) when (ex is SymbolsNotMatchingException or SymbolsNotFoundException)
        {
            pdb?.Dispose();
            throw new InvalidOperationException($"{Path.GetFileName(pdbPath)} isn't this build's PDB.", ex);
        }
        catch (Exception ex) when (ex is BadImageFormatException or IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or IndexOutOfRangeException or InvalidCastException or NullReferenceException)
        {
            // Cecil's reader throws what it runs into on a damaged file.
            pdb?.Dispose();
            throw new InvalidOperationException(ex.Message, ex);
        }
    }

    /// <summary>The tokens of the methods the PDB has lines for.</summary>
    public IReadOnlyList<int> Methods()
    {
        lock (_gate)
        {
            var result = new List<int>();
            foreach (var type in _module.GetTypes())
            {
                foreach (var method in type.Methods)
                {
                    if (method.HasBody && Read(method) is { HasSequencePoints: true })
                    {
                        result.Add(method.MetadataToken.ToInt32());
                    }
                }
            }

            return result;
        }
    }

    /// <summary>A method's sequence points, hidden ones included; empty when the PDB has none for it.</summary>
    public IReadOnlyList<ClrSequencePoint> SequencePoints(int methodToken)
    {
        lock (_gate)
        {
            if (Method(methodToken) is not { } method || Read(method) is not { HasSequencePoints: true } information)
            {
                return [];
            }

            return [.. information.SequencePoints.Select(p => new ClrSequencePoint(p.Offset, p.StartLine, p.StartColumn, p.EndLine, p.EndColumn, p.Document?.Url ?? string.Empty))];
        }
    }

    /// <summary>The locals in scope at an IL offset, by slot, innermost scopes first (compiler-hidden ones left out).</summary>
    public IReadOnlyList<(int Slot, string Name)> Locals(int methodToken, int ilOffset)
    {
        lock (_gate)
        {
            if (Method(methodToken) is not { } method || Read(method)?.Scope is not { } root)
            {
                return [];
            }

            var scopes = new List<ScopeDebugInformation>();
            Collect(root, ilOffset, scopes);
            var result = new List<(int, string)>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var scope in Enumerable.Reverse(scopes))
            {
                foreach (var variable in scope.Variables)
                {
                    if (!variable.IsDebuggerHidden && variable.Name is { Length: > 0 } name && seen.Add(name))
                    {
                        result.Add((variable.Index, name));
                    }
                }
            }

            return result;
        }
    }

    /// <summary>The scopes around <paramref name="ilOffset"/>, outermost first.</summary>
    private static void Collect(ScopeDebugInformation scope, int ilOffset, List<ScopeDebugInformation> into)
    {
        if (scope.Start.Offset > ilOffset || (!scope.End.IsEndOfMethod && ilOffset >= scope.End.Offset))
        {
            return;
        }

        into.Add(scope);
        foreach (var inner in scope.Scopes)
        {
            Collect(inner, ilOffset, into);
        }
    }

    private MethodDefinition? Method(int token)
    {
        try
        {
            return _module.LookupToken(token) as MethodDefinition;
        }
        catch (Exception ex) when (ex is ArgumentException or BadImageFormatException or InvalidOperationException)
        {
            return null;
        }
    }

    private static MethodDebugInformation? Read(MethodDefinition method)
    {
        try
        {
            return method.DebugInformation;
        }
        catch (Exception ex) when (ex is BadImageFormatException or IOException or InvalidOperationException or ArgumentException or IndexOutOfRangeException or NullReferenceException)
        {
            return null; // a method the PDB describes in a way the reader doesn't know
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _module.Dispose();
            _pdb.Dispose();
        }
    }
}
