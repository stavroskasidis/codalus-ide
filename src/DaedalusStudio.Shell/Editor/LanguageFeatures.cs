using System.Text.RegularExpressions;
using AvaloniaEdit.Document;
using AvaloniaEdit.Folding;
using DaedalusStudio.Workbench.Languages;

namespace DaedalusStudio.Shell.Editor;

public enum CommentAction
{
    Toggle,
    Comment,
    Uncomment,
}

/// <summary>
/// Editing features that come from a language's configuration (ADR 0016) rather than from a language server:
/// line and block comments, folding by indentation, markers and XML tags, and word completion.
/// </summary>
public static partial class LanguageFeatures
{
    /// <summary>
    /// Comments or uncomments lines <paramref name="first"/>..<paramref name="last"/> (1-based) with the line comment, or
    /// wraps them in the block comment when the language has no line comment. Toggle uncomments when every non-blank
    /// line is commented. Returns false when the language has no comments.
    /// </summary>
    public static bool CommentLines(TextDocument document, int first, int last, LanguageConfiguration? configuration, CommentAction action)
    {
        if (configuration?.LineComment is { Length: > 0 } token)
        {
            var lines = Enumerable.Range(first, last - first + 1).Select(document.GetLineByNumber).ToList();
            var content = lines.Where(l => !string.IsNullOrWhiteSpace(document.GetText(l))).ToList();
            if (content.Count == 0)
            {
                return true;
            }

            var allCommented = content.All(l => document.GetText(l).TrimStart().StartsWith(token, StringComparison.Ordinal));
            var uncomment = action == CommentAction.Uncomment || (action == CommentAction.Toggle && allCommented);
            using (document.RunUpdate())
            {
                if (uncomment)
                {
                    foreach (var line in content)
                    {
                        var text = document.GetText(line);
                        var at = text.IndexOf(token, StringComparison.Ordinal);
                        if (at >= 0 && string.IsNullOrWhiteSpace(text[..at]))
                        {
                            var length = token.Length + (at + token.Length < text.Length && text[at + token.Length] == ' ' ? 1 : 0);
                            document.Remove(line.Offset + at, length);
                        }
                    }
                }
                else
                {
                    // At the smallest indentation, so the comment markers line up.
                    var column = content.Min(l => Indentation(document.GetText(l)));
                    foreach (var line in content)
                    {
                        document.Insert(line.Offset + column, token + " ");
                    }
                }
            }

            return true;
        }

        if (configuration?.BlockComment is var (start, end))
        {
            var from = document.GetLineByNumber(first);
            var to = document.GetLineByNumber(last);
            var text = document.GetText(from.Offset, to.EndOffset - from.Offset);
            var trimmed = text.Trim();
            var isCommented = trimmed.StartsWith(start, StringComparison.Ordinal) && trimmed.EndsWith(end, StringComparison.Ordinal);
            using (document.RunUpdate())
            {
                if (isCommented && action != CommentAction.Comment)
                {
                    var open = from.Offset + text.IndexOf(start, StringComparison.Ordinal);
                    var close = from.Offset + text.LastIndexOf(end, StringComparison.Ordinal);
                    var trailing = close > 0 && document.GetCharAt(close - 1) == ' ' ? 1 : 0;
                    document.Remove(close - trailing, end.Length + trailing);
                    var leading = open + start.Length < document.TextLength && document.GetCharAt(open + start.Length) == ' ' ? 1 : 0;
                    document.Remove(open, start.Length + leading);
                }
                else if (!isCommented && action != CommentAction.Uncomment)
                {
                    document.Insert(to.EndOffset, " " + end);
                    document.Insert(from.Offset + Indentation(document.GetText(from)), start + " ");
                }
            }

            return true;
        }

        return false;
    }

    private static int Indentation(string line)
    {
        var i = 0;
        while (i < line.Length && line[i] is ' ' or '\t')
        {
            i++;
        }

        return i;
    }

    /// <summary>
    /// Foldings without a language server: XML elements for XML languages, otherwise marker regions
    /// (<c>#region</c>…<c>#endregion</c>) and blocks of deeper indentation (as VS Code does by default).
    /// </summary>
    public static List<NewFolding> Foldings(TextDocument document, string? languageId, LanguageConfiguration? configuration)
    {
        if (languageId is "xml" or "msbuild")
        {
            try
            {
                return [.. new XmlFoldingStrategy { ShowAttributesWhenFolded = true }.CreateNewFoldings(document, out _)];
            }
            catch (System.Xml.XmlException)
            {
                // Not well-formed: fall back to indentation.
            }
        }

        var foldings = new List<NewFolding>();
        var lineCount = document.LineCount;
        if (lineCount > 200_000)
        {
            return foldings;
        }

        // Marker regions.
        if (configuration is { FoldingStart: { } start, FoldingEnd: { } end })
        {
            var stack = new Stack<DocumentLine>();
            Regex startRegex, endRegex;
            try
            {
                startRegex = new Regex(start, RegexOptions.None, TimeSpan.FromMilliseconds(50));
                endRegex = new Regex(end, RegexOptions.None, TimeSpan.FromMilliseconds(50));
            }
            catch (ArgumentException)
            {
                startRegex = endRegex = NeverMatches();
            }

            for (var number = 1; number <= lineCount; number++)
            {
                var line = document.GetLineByNumber(number);
                var text = document.GetText(line);
                if (startRegex.IsMatch(text))
                {
                    stack.Push(line);
                }
                else if (endRegex.IsMatch(text) && stack.Count > 0)
                {
                    var open = stack.Pop();
                    foldings.Add(new NewFolding(open.EndOffset, line.EndOffset) { Name = document.GetText(open).Trim() });
                }
            }
        }

        // Indentation blocks: a line followed by deeper-indented lines folds them (blank lines don't end a block).
        var indents = new int[lineCount + 1];
        for (var number = 1; number <= lineCount; number++)
        {
            var text = document.GetText(document.GetLineByNumber(number));
            indents[number] = string.IsNullOrWhiteSpace(text) ? -1 : IndentWidth(text);
        }

        var open2 = new Stack<(int Line, int Indent)>();
        for (var number = 1; number <= lineCount + 1; number++)
        {
            var indent = number <= lineCount ? indents[number] : 0;
            if (indent < 0)
            {
                continue;
            }

            while (open2.Count > 0 && indent <= open2.Peek().Indent)
            {
                var (line, _) = open2.Pop();
                var last = number - 1;
                while (last > line && indents[last] < 0)
                {
                    last--; // trailing blank lines stay outside
                }

                // The line that ends the block (a closing brace, or the next statement) stays visible.
                if (last > line)
                {
                    foldings.Add(new NewFolding(document.GetLineByNumber(line).EndOffset, document.GetLineByNumber(last).EndOffset));
                }
            }

            if (number < lineCount && NextIndent(indents, number) > indent)
            {
                open2.Push((number, indent));
            }
        }

        foldings.Sort((a, b) => a.StartOffset.CompareTo(b.StartOffset));
        return foldings;
    }

    private static int NextIndent(int[] indents, int line)
    {
        for (var i = line + 1; i < indents.Length; i++)
        {
            if (indents[i] >= 0)
            {
                return indents[i];
            }
        }

        return -1;
    }

    private static int IndentWidth(string line)
    {
        var width = 0;
        foreach (var c in line)
        {
            if (c == ' ')
            {
                width++;
            }
            else if (c == '\t')
            {
                width += 4;
            }
            else
            {
                break;
            }
        }

        return width;
    }

    /// <summary>
    /// Words of the text that start with <paramref name="prefix"/> (case-insensitive), most frequent first: the
    /// completion of languages without a language server.
    /// </summary>
    public static List<string> WordCompletions(string text, string prefix, int max = 200) =>
        WordIndex.Build(new StringTextSource(text), 0, CancellationToken.None).Complete(prefix, string.Empty, max);

    [GeneratedRegex("(?!)")]
    private static partial Regex NeverMatches();
}

/// <summary>
/// The words of a document and how often each occurs, for word completion (ADR 0016). Built off the UI thread from a
/// snapshot, read in chunks so that large files never become one string; the editor rebuilds it while the user types
/// and adds the words of the current line, which may be newer than the index.
/// </summary>
public sealed class WordIndex
{
    private const int MaxWordLength = 128;
    private readonly Dictionary<string, int> _counts;

    private WordIndex(int version, Dictionary<string, int> counts)
    {
        Version = version;
        _counts = counts;
    }

    /// <summary>The document version the index was built from.</summary>
    public int Version { get; }

    public static WordIndex Build(ITextSource snapshot, int version, CancellationToken cancellationToken)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        Scan(snapshot.CreateReader(), counts, cancellationToken);
        return new WordIndex(version, counts);
    }

    /// <summary>Words starting with <paramref name="prefix"/> (longer than it), most frequent first; <paramref name="line"/> adds its words.</summary>
    public List<string> Complete(string prefix, string line, int max = 200)
    {
        if (prefix.Length == 0)
        {
            return [];
        }

        var lineWords = new Dictionary<string, int>(StringComparer.Ordinal);
        Scan(new StringReader(line), lineWords, CancellationToken.None);
        return [.. _counts.Concat(lineWords)
            .Where(c => c.Key.Length > prefix.Length && c.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .GroupBy(c => c.Key, StringComparer.Ordinal)
            .Select(g => (Word: g.Key, Count: g.Sum(c => c.Value)))
            .OrderByDescending(c => c.Count).ThenBy(c => c.Word, StringComparer.OrdinalIgnoreCase)
            .Take(max).Select(c => c.Word)];
    }

    // A word is a letter or '_' followed by letters, digits, '_' and '-'.
    private static void Scan(TextReader reader, Dictionary<string, int> counts, CancellationToken cancellationToken)
    {
        var buffer = new char[16 * 1024];
        var word = new char[MaxWordLength];
        var length = 0;
        var tooLong = false;
        int read;
        while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var i = 0; i < read; i++)
            {
                var c = buffer[i];
                var inWord = length > 0 || tooLong
                    ? char.IsLetterOrDigit(c) || c is '_' or '-'
                    : char.IsLetter(c) || c == '_';
                if (inWord)
                {
                    if (length < MaxWordLength)
                    {
                        word[length++] = c;
                    }
                    else
                    {
                        tooLong = true;
                    }
                }
                else
                {
                    Add(counts, word, ref length, ref tooLong);
                }
            }
        }

        Add(counts, word, ref length, ref tooLong);
    }

    private static void Add(Dictionary<string, int> counts, char[] word, ref int length, ref bool tooLong)
    {
        if (length > 0 && !tooLong)
        {
            var lookup = counts.GetAlternateLookup<ReadOnlySpan<char>>();
            var span = word.AsSpan(0, length);
            lookup[span] = lookup.TryGetValue(span, out var count) ? count + 1 : 1;
        }

        length = 0;
        tooLong = false;
    }
}
