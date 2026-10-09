namespace Daedalus.LanguageServices.Protocol;

/// <summary>
/// File paths ↔ LSP document URIs. Files use <c>file://</c> URIs; source-generated documents use the
/// <c>daedalus-generated</c> scheme (their text comes from <c>daedalus/sourceGeneratedText</c>).
/// </summary>
public static class DocumentUris
{
    public const string GeneratedScheme = "daedalus-generated";

    public const string MetadataScheme = "daedalus-metadata";

    public static string FromPath(string path) => new Uri(Path.GetFullPath(path)).AbsoluteUri;

    /// <summary>The file path of a <c>file://</c> URI, or null for other schemes.</summary>
    public static string? ToPath(string uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && parsed.IsFile ? parsed.LocalPath : null;

    public static bool IsMetadata(string uri) => uri.StartsWith(MetadataScheme + ":", StringComparison.Ordinal);

    /// <summary>The URI of a decompiled type: its assembly file and metadata name.</summary>
    public static string FromMetadata(string assemblyPath, string typeMetadataName) =>
        MetadataScheme + ":///" + Uri.EscapeDataString(assemblyPath) + "?type=" + Uri.EscapeDataString(typeMetadataName);

    /// <summary>The assembly file and type of a <c>daedalus-metadata</c> URI, or null.</summary>
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

    /// <summary>The virtual file path of a <c>daedalus-generated</c> URI, or null for other schemes.</summary>
    public static string? GeneratedPath(string uri) =>
        IsGenerated(uri) ? Uri.UnescapeDataString(uri[(GeneratedScheme.Length + 4)..]) : null;
}
