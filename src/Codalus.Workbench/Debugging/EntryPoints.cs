using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Codalus.Workbench.Debugging;

/// <summary>
/// A program's entry point's first line, read from its portable PDB (#547: Step Into New Instance for a program
/// netcoredbg attaches to in the terminal, whose <c>stopAtEntry</c> is a launch's): the PDB's entry point (the user's
/// <c>Main</c>, not the compiler's <c>&lt;Main&gt;</c> for an async one), or else the image's; for an async <c>Main</c>
/// and top-level statements that await, their state machine's <c>MoveNext</c>. The Codalus debugger finds it itself
/// (<c>ClrModuleInfo.EntryLocation</c>).
/// </summary>
public static class EntryPoints
{
    /// <summary>The source file and line where <paramref name="assembly"/>'s program starts, or null (no entry point, no portable PDB).</summary>
    public static (string Document, int Line)? FirstLine(string assembly)
    {
        try
        {
            using var stream = File.OpenRead(assembly);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata || pe.PEHeaders.CorHeader is not { } header || (header.Flags & CorFlags.NativeEntryPoint) != 0
                || MetadataTokens.EntityHandle(header.EntryPointTokenOrRelativeVirtualAddress) is not { Kind: HandleKind.MethodDefinition } image)
            {
                return null;
            }

            if (!pe.TryOpenAssociatedPortablePdb(assembly, p => File.Exists(p) ? File.OpenRead(p) : null, out var provider, out _) || provider is null)
            {
                return null;
            }

            using (provider)
            {
                var metadata = pe.GetMetadataReader();
                var pdb = provider.GetMetadataReader();
                var candidates = new List<MethodDefinitionHandle>();
                if (pdb.DebugMetadataHeader is { EntryPoint.IsNil: false } debug)
                {
                    candidates.Add(debug.EntryPoint);
                }

                candidates.Add((MethodDefinitionHandle)image);

                // An async Main's <Main> calls the user's Main, of its type (the PDB may name <Main> itself).
                foreach (var entry in candidates.ToList())
                {
                    var method = metadata.GetMethodDefinition(entry);
                    if (metadata.GetString(method.Name) is ['<', .. var inner, '>'])
                    {
                        candidates.AddRange(metadata.GetTypeDefinition(method.GetDeclaringType()).GetMethods()
                            .Where(m => metadata.StringComparer.Equals(metadata.GetMethodDefinition(m).Name, inner)));
                    }
                }
                foreach (var method in candidates.Distinct())
                {
                    if (FirstLine(pdb, method) is { } line)
                    {
                        return line;
                    }

                    foreach (var moveNext in StateMachineMoveNexts(metadata, method))
                    {
                        if (FirstLine(pdb, moveNext) is { } inMoveNext)
                        {
                            return inMoveNext;
                        }
                    }
                }

                return null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException or InvalidOperationException)
        {
            return null;
        }
    }

    private static (string Document, int Line)? FirstLine(MetadataReader pdb, MethodDefinitionHandle method)
    {
        var information = pdb.GetMethodDebugInformation(method.ToDebugInformationHandle());
        SequencePoint? first = null;
        foreach (var point in information.GetSequencePoints())
        {
            if (!point.IsHidden && (first is null || point.Offset < first.Value.Offset))
            {
                first = point;
            }
        }

        if (first is not { } found)
        {
            return null;
        }

        var document = found.Document.IsNil ? information.Document : found.Document;
        return document.IsNil ? null : (pdb.GetString(pdb.GetDocument(document).Name), found.StartLine);
    }

    /// <summary>The <c>MoveNext</c> of an async method's state machines: its type's nested <c>&lt;Name&gt;d__N</c> types.</summary>
    private static IEnumerable<MethodDefinitionHandle> StateMachineMoveNexts(MetadataReader metadata, MethodDefinitionHandle kickoff)
    {
        var method = metadata.GetMethodDefinition(kickoff);
        var prefix = "<" + metadata.GetString(method.Name) + ">";
        foreach (var nested in metadata.GetTypeDefinition(method.GetDeclaringType()).GetNestedTypes())
        {
            var type = metadata.GetTypeDefinition(nested);
            if (!metadata.GetString(type.Name).StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var candidate in type.GetMethods())
            {
                if (metadata.StringComparer.Equals(metadata.GetMethodDefinition(candidate).Name, "MoveNext"))
                {
                    yield return candidate;
                }
            }
        }
    }
}
