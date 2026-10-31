using Codalus.LanguageServices.Client;
using Codalus.LanguageServices.Protocol;
using Codalus.Shell.Editor;
using Codalus.Workbench.Languages;

namespace Codalus.Shell;

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

        // Projected off the UI thread from an immutable snapshot at the document's version (M57, the pattern of ADR 0078):
        // the UI thread made the whole text a string, projected it and compared it at each request in markup.
        var snapshot = document.Text.CreateSnapshot();
        var version = document.Version;
        var previous = _razorHtml.TryGetValue(document.Path, out var known) ? known.Text : null;
        var (html, unchanged) = await Task.Run(() =>
        {
            var razor = snapshot.Text;
            var projected = RazorHtmlProjection.Project(razor);
            return RazorHtmlProjection.IsMarkup(razor, projected, offset)
                ? (projected, string.Equals(previous, projected, StringComparison.Ordinal))
                : ((string?)null, false);
        }).ConfigureAwait(true);
        if (html is null || await Languages.ClientAsync("html").ConfigureAwait(true) is not { } client)
        {
            return null;
        }

        var uri = DocumentUris.FromPath(document.Path + RazorHtmlProjection.VirtualSuffix);
        if (!_razorHtml.TryGetValue(document.Path, out var state) || state.Client != client)
        {
            // New, or the server restarted: queued with the Razor file's version, and encoded on the writer loop.
            _razorHtml[document.Path] = (client, version, html);
            await client.DidOpenAsync(uri, "html", () => (html, version), CancellationToken.None).ConfigureAwait(true);
        }
        else if (version > state.Version)
        {
            // Compared off the UI thread with the text known then; another request may have sent a newer one meanwhile.
            if (unchanged && ReferenceEquals(state.Text, previous))
            {
                _razorHtml[document.Path] = (client, version, state.Text); // so an older projection coming late is dropped
            }
            else
            {
                _razorHtml[document.Path] = (client, version, html);
                await client.DidChangeTextAsync(uri, version, () => (html, version), CancellationToken.None).ConfigureAwait(true);
            }
        }

        // Otherwise a projection of a newer version went first: this one is dropped, and the server has the newer HTML.
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
