namespace Daedalus.LanguageServices.Protocol;

/// <summary>
/// File paths ↔ LSP document URIs. Files use <c>file://</c> URIs; source-generated documents use the
/// <c>daedalus-generated</c> scheme (their text comes from <c>daedalus/sourceGeneratedText</c>).
/// </summary>
public static class DocumentUris
{
    public const string GeneratedScheme = "daedalus-generated";

    public static string FromPath(string path) => new Uri(Path.GetFullPath(path)).AbsoluteUri;

    /// <summary>The file path of a <c>file://</c> URI, or null for other schemes.</summary>
    public static string? ToPath(string uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && parsed.IsFile ? parsed.LocalPath : null;

    public static bool IsGenerated(string uri) => uri.StartsWith(GeneratedScheme + ":", StringComparison.Ordinal);

    /// <summary>The URI of a source-generated document, from the (virtual) file path Roslyn gives it.</summary>
    public static string FromGeneratedPath(string generatedPath) => GeneratedScheme + ":///" + Uri.EscapeDataString(generatedPath);

    /// <summary>The virtual file path of a <c>daedalus-generated</c> URI, or null for other schemes.</summary>
    public static string? GeneratedPath(string uri) =>
        IsGenerated(uri) ? Uri.UnescapeDataString(uri[(GeneratedScheme.Length + 4)..]) : null;
}
