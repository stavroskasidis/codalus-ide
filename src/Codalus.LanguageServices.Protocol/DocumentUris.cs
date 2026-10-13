namespace Codalus.LanguageServices.Protocol;

/// <summary>
/// File paths ↔ LSP document URIs. Files use <c>file://</c> URIs; source-generated documents use the
/// <c>codalus-generated</c> scheme (their text comes from <c>codalus/sourceGeneratedText</c>).
/// </summary>
public static class DocumentUris
{
    public const string GeneratedScheme = "codalus-generated";

    public const string MetadataScheme = "codalus-metadata";

    public static string FromPath(string path) => new Uri(Path.GetFullPath(path)).AbsoluteUri;

    /// <summary>
    /// The file path of a <c>file://</c> URI, or null for other schemes. On Windows, URIs that escape the drive's colon
    /// (<c>file:///c%3A/…</c>, as VS Code's servers write them) give the same path as <c>file:///C:/…</c>.
    /// </summary>
    public static string? ToPath(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed) || !parsed.IsFile)
        {
            return null;
        }

        var path = parsed.LocalPath;
        if (OperatingSystem.IsWindows() && path.Length >= 3 && path[0] is '/' or '\\' && char.IsAsciiLetter(path[1]) && path[2] == ':')
        {
            path = char.ToUpperInvariant(path[1]) + path[2..].Replace('/', '\\');
        }
        else if (OperatingSystem.IsWindows() && path.Length >= 2 && char.IsAsciiLetterLower(path[0]) && path[1] == ':')
        {
            path = char.ToUpperInvariant(path[0]) + path[1..];
        }

        return path;
    }

    public static bool IsMetadata(string uri) => uri.StartsWith(MetadataScheme + ":", StringComparison.Ordinal);

    /// <summary>The URI of a decompiled type: its assembly file and metadata name.</summary>
    public static string FromMetadata(string assemblyPath, string typeMetadataName) =>
        MetadataScheme + ":///" + Uri.EscapeDataString(assemblyPath) + "?type=" + Uri.EscapeDataString(typeMetadataName);

    /// <summary>The assembly file and type of a <c>codalus-metadata</c> URI, or null.</summary>
    public static (string AssemblyPath, string TypeMetadataName)? MetadataTarget(string uri)
    {
        var query = uri.IndexOf("?type=", StringComparison.Ordinal);
        if (!IsMetadata(uri) || query < 0)
        {
            return null;
        }

        return (Uri.UnescapeDataString(uri[(MetadataScheme.Length + 4)..query]), Uri.UnescapeDataString(uri[(query + 6)..]));
    }

    public static bool IsGenerated(string uri) => uri.StartsWith(GeneratedScheme + ":", StringComparison.Ordinal);

    /// <summary>The URI of a source-generated document, from the (virtual) file path Roslyn gives it.</summary>
    public static string FromGeneratedPath(string generatedPath) => GeneratedScheme + ":///" + Uri.EscapeDataString(generatedPath);

    /// <summary>The virtual file path of a <c>codalus-generated</c> URI, or null for other schemes.</summary>
    public static string? GeneratedPath(string uri) =>
        IsGenerated(uri) ? Uri.UnescapeDataString(uri[(GeneratedScheme.Length + 4)..]) : null;
}
