using System.Collections.Concurrent;
using Codalus.LanguageServices.Protocol;

namespace Codalus.Markup.Host;

/// <summary>An open document: its text, language and version, with line starts for position conversions.</summary>
internal sealed class TextDocument(string uri, string languageId, string text, int version)
{
    private int[]? _lineStarts;

    public string Uri { get; } = uri;

    public string LanguageId { get; } = languageId;

    public string Text { get; private set; } = text;

    public int Version { get; private set; } = version;

    /// <summary>The file path (for schema associations by name), or null for non-file URIs.</summary>
    public string? Path => DocumentUris.ToPath(Uri);

    public int[] LineStarts => _lineStarts ??= ComputeLineStarts(Text);

    public void Apply(int version, TextDocumentContentChangeEvent[] changes)
    {
        foreach (var change in changes)
        {
            if (change.Range is not { } range)
            {
                Text = change.Text;
            }
            else
            {
                var start = OffsetAt(range.Start);
                var end = Math.Max(start, OffsetAt(range.End));
                Text = string.Concat(Text.AsSpan(0, start), change.Text, Text.AsSpan(end));
            }

            _lineStarts = null;
        }

        Version = version;
    }

    public int OffsetAt(Position position)
    {
        var starts = LineStarts;
        if (position.Line >= starts.Length)
        {
            return Text.Length;
        }

        var lineStart = starts[Math.Max(0, position.Line)];
        var lineEnd = position.Line + 1 < starts.Length ? starts[position.Line + 1] : Text.Length;
        return Math.Min(lineStart + Math.Max(0, position.Character), lineEnd);
    }

    public Position PositionAt(int offset)
    {
        offset = Math.Clamp(offset, 0, Text.Length);
        var starts = LineStarts;
        var line = Array.BinarySearch(starts, offset);
        if (line < 0)
        {
            line = ~line - 1;
        }

        return new Position(line, offset - starts[line]);
    }

    public LanguageServices.Protocol.Range RangeOf(int start, int end) => new(PositionAt(start), PositionAt(end));

    private static int[] ComputeLineStarts(string text)
    {
        var starts = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                starts.Add(i + 1);
            }
        }

        return [.. starts];
    }
}

internal sealed class TextDocuments
{
    private readonly ConcurrentDictionary<string, TextDocument> _documents = new(StringComparer.Ordinal);

    public TextDocument Open(TextDocumentItem item) => _documents[item.Uri] = new TextDocument(item.Uri, item.LanguageId, item.Text, item.Version);

    public TextDocument? Change(string uri, int version, TextDocumentContentChangeEvent[] changes)
    {
        if (!_documents.TryGetValue(uri, out var document))
        {
            return null;
        }

        lock (document)
        {
            document.Apply(version, changes);
        }

        return document;
    }

    public void Close(string uri) => _documents.TryRemove(uri, out _);

    public TextDocument? Get(string uri) => _documents.GetValueOrDefault(uri);
}
