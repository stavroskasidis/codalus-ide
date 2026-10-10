using DaedalusStudio.LanguageServices.Client;
using DaedalusStudio.LanguageServices.Protocol;
using DaedalusStudio.Shell.Editor;
using DaedalusStudio.Workbench.Languages;

namespace DaedalusStudio.Shell;

/// <summary>
/// HTML and CSS inside Razor files (M16d, ADR 0027). The HTML server gets the HTML of an open Razor file (its C# and
/// Razor syntax blanked, <see cref="RazorHtmlProjection"/>) as a document of its own next to it,
/// <c>Counter.razor__virtual.html</c>, and the editor asks it at markup positions, together with the C# host: HTML
/// elements and attributes, and CSS in <c>&lt;style&gt;</c> blocks. The positions are the same in both documents.
/// </summary>
public sealed partial class ShellHost
{
    private readonly Dictionary<string, (LanguageHostClient Client, int Version, string Text)> _razorHtml = new(SolutionPaths.Comparer);

    async Task<EmbeddedRequest?> IEditorServices.EmbeddedAsync(CodeDocument document, int offset)
    {
        if (document.LanguageId != RazorLanguageId || document.IsReadOnly || !Path.IsPathRooted(document.Path))
        {
            return null;
        }

        var razor = document.Text.Text;
        var html = RazorHtmlProjection.Project(razor);
        if (!RazorHtmlProjection.IsMarkup(razor, html, offset) || await Languages.ClientAsync("html").ConfigureAwait(true) is not { } client)
        {
            return null;
        }

        var uri = DocumentUris.FromPath(document.Path + RazorHtmlProjection.VirtualSuffix);
        if (!_razorHtml.TryGetValue(document.Path, out var state) || state.Client != client)
        {
            // New, or the server restarted.
            await client.DidOpenAsync(new OpenDocumentState(uri, "html", 1, html), CancellationToken.None).ConfigureAwait(true);
            _razorHtml[document.Path] = (client, 1, html);
        }
        else if (!string.Equals(state.Text, html, StringComparison.Ordinal))
        {
            var version = state.Version + 1;
            await client.DidChangeAsync(uri, version, [new TextDocumentContentChangeEvent(null, html)], CancellationToken.None).ConfigureAwait(true);
            _razorHtml[document.Path] = (client, version, html);
        }

        return new EmbeddedRequest(client, uri);
    }

    /// <summary>A Razor file closed: so does its HTML.</summary>
    private void CloseRazorHtml(string path)
    {
        if (_razorHtml.Remove(path, out var state))
        {
            _ = state.Client.DidCloseAsync(DocumentUris.FromPath(path + RazorHtmlProjection.VirtualSuffix), CancellationToken.None);
        }
    }
}
