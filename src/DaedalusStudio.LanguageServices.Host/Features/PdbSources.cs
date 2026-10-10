using System.Collections.Concurrent;
using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.Json;

namespace DaedalusStudio.LanguageServices.Host.Features;

/// <summary>
/// The original source of a type in a referenced assembly (M12a, metadata navigation), from its portable PDB: the
/// assembly's embedded PDB or a <c>.pdb</c> next to it. Sources embedded in the PDB are used directly; otherwise, when
/// Source Link is allowed (setting <c>navigation.sourceLink</c>), the document is downloaded from the PDB's Source Link
/// URL and cached. Without a PDB, or for types without method bodies (interfaces, reference assemblies), the caller
/// decompiles instead.
/// </summary>
internal static class PdbSources
{
    private static readonly Guid s_embeddedSource = new("0E8A571B-6926-466E-B4AD-8AB04611F5FE");
    private static readonly Guid s_sourceLink = new("CC110556-A091-4D38-9FEC-25AB9A351A6A");
    private static readonly ConcurrentDictionary<(string Assembly, string Type, bool SourceLink), SourceDocument?> s_cache = new();
    private static readonly HttpClient s_http = new() { Timeout = TimeSpan.FromSeconds(15) };

    /// <summary>A source document: its text, where it came from (<c>embedded</c> or <c>sourceLink</c>) and its original path.</summary>
    public sealed record SourceDocument(string Text, string Origin, string Path);

    /// <summary>Whether Source Link downloads are allowed (the shell's <c>navigation.sourceLink</c> setting).</summary>
    public static bool AllowSourceLink { get; set; }

    /// <summary>Where downloaded sources are cached (tests point it elsewhere).</summary>
    public static string CacheDirectory { get; set; } = Path.Combine(Path.GetTempPath(), "daedalus-sourcelink");

    public static SourceDocument? Find(string assemblyPath, string typeMetadataName) =>
        s_cache.GetOrAdd((assemblyPath, typeMetadataName, AllowSourceLink), key => Load(key.Assembly, key.Type, key.SourceLink));

    private static SourceDocument? Load(string assemblyPath, string typeMetadataName, bool sourceLink)
    {
        try
        {
            using var stream = File.OpenRead(assemblyPath);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata)
            {
                return null;
            }

            var metadata = pe.GetMetadataReader();
            if (FindType(metadata, typeMetadataName) is not { } type)
            {
                return null;
            }

            using var provider = OpenPdb(pe, assemblyPath);
            if (provider is null)
            {
                return null;
            }

            var pdb = provider.GetMetadataReader();
            if (DocumentOf(metadata, pdb, type) is not { } documentHandle)
            {
                return null;
            }

            var document = pdb.GetDocument(documentHandle);
            var name = pdb.GetString(document.Name);
            if (EmbeddedSource(pdb, documentHandle) is { } embedded)
            {
                return new SourceDocument(embedded, Protocol.MetadataOrigins.Embedded, name);
            }

            if (sourceLink && SourceLinkUrl(pdb, name) is { } url && Download(url) is { } downloaded)
            {
                return new SourceDocument(downloaded, Protocol.MetadataOrigins.SourceLink, name);
            }
        }
        catch (Exception ex) when (ex is IOException or BadImageFormatException or UnauthorizedAccessException or InvalidOperationException)
        {
        }

        return null;
    }

    private static TypeDefinitionHandle? FindType(MetadataReader metadata, string metadataName)
    {
        var dot = metadataName.LastIndexOf('.');
        var ns = dot < 0 ? string.Empty : metadataName[..dot];
        var name = dot < 0 ? metadataName : metadataName[(dot + 1)..];
        foreach (var handle in metadata.TypeDefinitions)
        {
            var type = metadata.GetTypeDefinition(handle);
            if (type.GetDeclaringType().IsNil && metadata.StringComparer.Equals(type.Name, name) && metadata.StringComparer.Equals(type.Namespace, ns))
            {
                return handle;
            }
        }

        return null;
    }

    private static MetadataReaderProvider? OpenPdb(PEReader pe, string assemblyPath)
    {
        var entries = pe.ReadDebugDirectory();
        if (entries.FirstOrDefault(e => e.Type == DebugDirectoryEntryType.EmbeddedPortablePdb) is { DataSize: > 0 } embedded)
        {
            return pe.ReadEmbeddedPortablePdbDebugDirectoryData(embedded);
        }

        var candidates = new List<string> { Path.ChangeExtension(assemblyPath, ".pdb") };
        if (entries.FirstOrDefault(e => e.Type == DebugDirectoryEntryType.CodeView) is { DataSize: > 0 } codeView)
        {
            var path = pe.ReadCodeViewDebugDirectoryData(codeView).Path;
            candidates.Add(Path.Combine(Path.GetDirectoryName(assemblyPath)!, Path.GetFileName(path.Replace('\\', Path.DirectorySeparatorChar))));
        }

        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase).Where(File.Exists))
        {
            var bytes = File.ReadAllBytes(candidate);
            if (bytes.Length > 4 && bytes[0] == (byte)'B' && bytes[1] == (byte)'S' && bytes[2] == (byte)'J' && bytes[3] == (byte)'B')
            {
                return MetadataReaderProvider.FromPortablePdbImage([.. bytes]);
            }
        }

        return null;
    }

    /// <summary>The document holding most of the type's method bodies (nested types included).</summary>
    private static DocumentHandle? DocumentOf(MetadataReader metadata, MetadataReader pdb, TypeDefinitionHandle type)
    {
        var counts = new Dictionary<DocumentHandle, int>();
        void Count(TypeDefinitionHandle handle)
        {
            var definition = metadata.GetTypeDefinition(handle);
            foreach (var method in definition.GetMethods())
            {
                var info = pdb.GetMethodDebugInformation(method.ToDebugInformationHandle());
                if (!info.Document.IsNil)
                {
                    counts[info.Document] = counts.GetValueOrDefault(info.Document) + 1;
                }
                else if (!info.SequencePointsBlob.IsNil)
                {
                    foreach (var point in info.GetSequencePoints())
                    {
                        counts[point.Document] = counts.GetValueOrDefault(point.Document) + 1;
                        break;
                    }
                }
            }

            foreach (var nested in definition.GetNestedTypes())
            {
                Count(nested);
            }
        }

        Count(type);
        return counts.Count == 0 ? null : counts.MaxBy(c => c.Value).Key;
    }

    private static string? EmbeddedSource(MetadataReader pdb, DocumentHandle document)
    {
        foreach (var handle in pdb.GetCustomDebugInformation(document))
        {
            var info = pdb.GetCustomDebugInformation(handle);
            if (pdb.GetGuid(info.Kind) != s_embeddedSource)
            {
                continue;
            }

            var blob = pdb.GetBlobBytes(info.Value);
            var format = BitConverter.ToInt32(blob, 0);
            if (format == 0)
            {
                return Decode(blob.AsSpan(4));
            }

            using var input = new DeflateStream(new MemoryStream(blob, 4, blob.Length - 4), CompressionMode.Decompress);
            using var output = new MemoryStream(format);
            input.CopyTo(output);
            return Decode(output.ToArray());
        }

        return null;
    }

    private static string Decode(ReadOnlySpan<byte> bytes) =>
        bytes.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]) ? Encoding.UTF8.GetString(bytes[3..]) : Encoding.UTF8.GetString(bytes);

    /// <summary>The URL of a document from the PDB's Source Link map (<c>"C:\src\*": "https://raw…/*"</c>).</summary>
    internal static string? SourceLinkUrl(MetadataReader pdb, string documentPath)
    {
        foreach (var handle in pdb.GetCustomDebugInformation(EntityHandle.ModuleDefinition))
        {
            var info = pdb.GetCustomDebugInformation(handle);
            if (pdb.GetGuid(info.Kind) == s_sourceLink)
            {
                return MapSourceLink(Encoding.UTF8.GetString(pdb.GetBlobBytes(info.Value)), documentPath);
            }
        }

        return null;
    }

    /// <summary>Maps a document path through a Source Link JSON map (the longest matching prefix wins).</summary>
    internal static string? MapSourceLink(string json, string documentPath)
    {
        using var map = JsonDocument.Parse(json);
        if (!map.RootElement.TryGetProperty("documents", out var documents) || documents.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        string? best = null;
        var bestLength = -1;
        foreach (var entry in documents.EnumerateObject())
        {
            var pattern = entry.Name;
            var url = entry.Value.GetString();
            if (url is null)
            {
                continue;
            }

            if (pattern.EndsWith('*'))
            {
                var prefix = pattern[..^1];
                if (documentPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && prefix.Length > bestLength)
                {
                    var rest = documentPath[prefix.Length..].Replace('\\', '/');
                    best = url.Replace("*", Uri.EscapeDataString(rest).Replace("%2F", "/", StringComparison.Ordinal), StringComparison.Ordinal);
                    bestLength = prefix.Length;
                }
            }
            else if (string.Equals(pattern, documentPath, StringComparison.OrdinalIgnoreCase) && pattern.Length > bestLength)
            {
                best = url;
                bestLength = pattern.Length;
            }
        }

        return best;
    }

    private static string? Download(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            return null;
        }

        var cache = Path.Combine(CacheDirectory, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(url)))[..32] + ".txt");
        if (File.Exists(cache))
        {
            return File.ReadAllText(cache);
        }

        try
        {
            using var response = s_http.GetAsync(uri).GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var text = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            Directory.CreateDirectory(CacheDirectory);
            File.WriteAllText(cache, text);
            return text;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            return null;
        }
    }
}
