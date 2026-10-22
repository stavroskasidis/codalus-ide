using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.CodeAnalysis;

namespace Codalus.LanguageServices.Host;

/// <summary>
/// The XML documentation of a referenced assembly (a NuGet package's, a reference pack's), for Quick Info, completion
/// and signature help. It is looked for as the classic IDE does: <c>&lt;name&gt;.xml</c> in the folder of the UI culture
/// or of one of its parents next to the assembly (localized IntelliSense: <c>de/</c>, <c>pt-br/</c>, <c>zh-hans/</c>),
/// else next to the assembly. A file's provider is shared by every reference to it, as the references themselves are,
/// and reads the file once, when a symbol of the assembly is first described.
/// </summary>
internal static class DocumentationFiles
{
    private static readonly ConcurrentDictionary<string, (DateTime Stamp, DocumentationProvider Provider)> s_providers = new(Paths.Comparer);

    /// <summary>
    /// The documentation of the assembly at <paramref name="assemblyPath"/> in <paramref name="culture"/> (the UI
    /// culture's), or null when it has none.
    /// </summary>
    public static DocumentationProvider? For(string assemblyPath, CultureInfo culture)
    {
        if (Find(assemblyPath, culture) is not { } path)
        {
            return null;
        }

        DateTime stamp;
        try
        {
            stamp = File.GetLastWriteTimeUtc(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        // A file rewritten since (a library's build output referenced as a file) gets a new provider.
        return s_providers.AddOrUpdate(
            path,
            p => (stamp, XmlDocumentationProvider.CreateFromFile(p)),
            (p, existing) => existing.Stamp == stamp ? existing : (stamp, XmlDocumentationProvider.CreateFromFile(p))).Provider;
    }

    /// <summary>The documentation file of the assembly at <paramref name="assemblyPath"/> in <paramref name="culture"/>, or null.</summary>
    internal static string? Find(string assemblyPath, CultureInfo culture)
    {
        if (Path.GetDirectoryName(assemblyPath) is not { Length: > 0 } directory)
        {
            return null;
        }

        var name = Path.GetFileNameWithoutExtension(assemblyPath) + ".xml";
        for (var current = culture; current.Name.Length > 0; current = current.Parent)
        {
            // Localized IntelliSense folders are named in lowercase (pt-br, zh-hans); a case-sensitive file system needs both.
            foreach (var folder in new[] { current.Name, current.Name.ToLowerInvariant() }.Distinct(Paths.Comparer))
            {
                var localized = Path.Combine(directory, folder, name);
                if (File.Exists(localized))
                {
                    return localized;
                }
            }
        }

        var neutral = Path.Combine(directory, name);
        return File.Exists(neutral) ? neutral : null;
    }
}
