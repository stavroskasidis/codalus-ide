using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Microsoft.CodeAnalysis;

namespace Codalus.LanguageServices.Host.WinForms;

/// <summary>
/// A Toolbox item's own picture (#460): the resource its <c>[ToolboxBitmap]</c> names, read from its assembly's file
/// without loading it, as Windows Forms finds it. <c>[ToolboxBitmap(typeof(T), "name")]</c> is the resource
/// <c>T</c>'s namespace + <c>.name</c> in <c>T</c>'s assembly (DevExpress's <c>ToolboxIconsRootNS</c>), and
/// <c>[ToolboxBitmap(typeof(T))]</c> is <c>T</c>'s full name + <c>.bmp</c>; the attribute is inherited. A picture given by
/// a file's path, or larger than <see cref="MaxBytes"/>, isn't read. One per listing: each assembly's file is opened once.
/// </summary>
internal sealed class ToolboxBitmaps(Compilation compilation) : IDisposable
{
    /// <summary>A Toolbox picture is 16 by 16 (a 32-bit one is about 1 KB); anything much larger isn't one.</summary>
    public const int MaxBytes = 64 * 1024;

    private readonly Dictionary<IAssemblySymbol, Resources?> _assemblies = new(SymbolEqualityComparer.Default);

    /// <summary>The picture of <paramref name="type"/>'s nearest <c>[ToolboxBitmap]</c>, base64, or null.</summary>
    public string? Of(INamedTypeSymbol type)
    {
        for (INamedTypeSymbol? current = type; current is not null; current = current.BaseType)
        {
            if (current.GetAttributes().FirstOrDefault(a => a.AttributeClass?.Name == "ToolboxBitmapAttribute") is not { } attribute)
            {
                continue;
            }

            if (attribute.ConstructorArguments is [{ Value: INamedTypeSymbol owner }, ..] arguments)
            {
                var ns = owner.ContainingNamespace is { IsGlobalNamespace: false } containing ? containing.ToDisplayString() + "." : string.Empty;
                var name = arguments is [_, { Value: string resource }] ? ns + resource : ns + owner.MetadataName + ".bmp";
                return Resource(owner.ContainingAssembly, name) is { } bytes ? Convert.ToBase64String(bytes) : null;
            }

            return null; // a file's path: not read
        }

        return null;
    }

    private byte[]? Resource(IAssemblySymbol? assembly, string name)
    {
        if (assembly is null)
        {
            return null;
        }

        if (!_assemblies.TryGetValue(assembly, out var resources))
        {
            resources = compilation.GetMetadataReference(assembly) is PortableExecutableReference { FilePath: { } path } ? Resources.Open(path) : null;
            _assemblies[assembly] = resources;
        }

        return resources?.Read(name);
    }

    public void Dispose()
    {
        foreach (var resources in _assemblies.Values)
        {
            resources?.Dispose();
        }
    }

    /// <summary>An assembly's embedded resources, by name.</summary>
    private sealed class Resources : IDisposable
    {
        private readonly PEReader _pe;
        private readonly Dictionary<string, long> _offsets = new(StringComparer.Ordinal);

        private Resources(PEReader pe)
        {
            _pe = pe;
            var metadata = pe.GetMetadataReader();
            foreach (var handle in metadata.ManifestResources)
            {
                var resource = metadata.GetManifestResource(handle);
                if (resource.Implementation.IsNil)
                {
                    _offsets.TryAdd(metadata.GetString(resource.Name), resource.Offset);
                }
            }
        }

        public static Resources? Open(string path)
        {
            PEReader? pe = null;
            try
            {
                pe = new PEReader(File.OpenRead(path));
                return pe.HasMetadata ? new Resources(pe) : Close(pe);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException or InvalidOperationException)
            {
                return Close(pe);
            }

            static Resources? Close(PEReader? pe)
            {
                pe?.Dispose();
                return null;
            }
        }

        public byte[]? Read(string name)
        {
            if (!_offsets.TryGetValue(name, out var offset) || _pe.PEHeaders.CorHeader is not { ResourcesDirectory: { Size: > 0 } directory })
            {
                return null;
            }

            try
            {
                var block = _pe.GetSectionData(directory.RelativeVirtualAddress);
                if (offset < 0 || offset + 4 > block.Length)
                {
                    return null;
                }

                var reader = block.GetReader((int)offset, block.Length - (int)offset);
                var length = reader.ReadInt32();
                return length is > 0 and <= MaxBytes && length <= reader.RemainingBytes ? reader.ReadBytes(length) : null;
            }
            catch (Exception ex) when (ex is BadImageFormatException or InvalidOperationException)
            {
                return null;
            }
        }

        public void Dispose() => _pe.Dispose();
    }
}
