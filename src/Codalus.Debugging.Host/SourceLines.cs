using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Codalus.Debugging.Host;

/// <summary>
/// Source lines for a method and IL offset, from the module's portable PDB: embedded in the module, or next to it.
/// A dump from another machine usually has neither: its frames show the method and IL offset only.
/// </summary>
internal sealed class SourceLines : IDisposable
{
    private readonly Dictionary<string, MetadataReaderProvider?> _pdbs = new(StringComparer.OrdinalIgnoreCase);

    public (string? File, int? Line) Find(string modulePath, int methodToken, int ilOffset)
    {
        if (ilOffset < 0 || Pdb(modulePath) is not { } provider)
        {
            return (null, null);
        }

        try
        {
            var reader = provider.GetMetadataReader();
            var handle = MetadataTokens.MethodDebugInformationHandle(methodToken & 0x00FFFFFF);
            SequencePoint? best = null;
            foreach (var point in reader.GetMethodDebugInformation(handle).GetSequencePoints())
            {
                if (point.IsHidden || point.Offset > ilOffset)
                {
                    continue;
                }

                if (best is null || point.Offset >= best.Value.Offset)
                {
                    best = point;
                }
            }

            return best is { } found ? (reader.GetString(reader.GetDocument(found.Document).Name), found.StartLine) : (null, null);
        }
        catch (Exception ex) when (ex is BadImageFormatException or ArgumentException or InvalidOperationException)
        {
            return (null, null);
        }
    }

    private MetadataReaderProvider? Pdb(string modulePath)
    {
        lock (_pdbs)
        {
            if (_pdbs.TryGetValue(modulePath, out var known))
            {
                return known;
            }

            MetadataReaderProvider? provider = null;
            try
            {
                if (File.Exists(modulePath))
                {
                    using var pe = new PEReader(File.OpenRead(modulePath));
                    if (pe.ReadDebugDirectory().FirstOrDefault(e => e.Type == DebugDirectoryEntryType.EmbeddedPortablePdb) is { DataSize: > 0 } embedded)
                    {
                        provider = pe.ReadEmbeddedPortablePdbDebugDirectoryData(embedded);
                    }
                }

                var beside = Path.ChangeExtension(modulePath, ".pdb");
                if (provider is null && File.Exists(beside))
                {
                    provider = MetadataReaderProvider.FromPortablePdbStream(File.OpenRead(beside));
                }
            }
            catch (Exception ex) when (ex is IOException or BadImageFormatException or UnauthorizedAccessException)
            {
                provider = null;
            }

            return _pdbs[modulePath] = provider;
        }
    }

    public void Dispose()
    {
        lock (_pdbs)
        {
            foreach (var provider in _pdbs.Values)
            {
                provider?.Dispose();
            }

            _pdbs.Clear();
        }
    }
}
