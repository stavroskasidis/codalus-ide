using System.Runtime.CompilerServices;
using Avalonia.Input;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Snippets;
using Codalus.Workbench.Snippets;
using Snippet = AvaloniaEdit.Snippets.Snippet;

namespace Codalus.Shell.Editor;

/// <summary>
/// Inserts TextMate/VS Code snippets with AvaloniaEdit's snippet engine (M12b): placeholders become fields that Tab
/// and Shift+Tab move between, repeated placeholders mirror the first, <c>$0</c> is where the caret ends (Enter or
/// Escape), and <c>$TM_SELECTED_TEXT</c> is the selection (Surround With). Fields are visited in the order of their
/// numbers (<c>$1</c>, <c>$2</c>…, then <c>$0</c>), not their place in the text, and a field with a choice list
/// (<c>${1|public,private|}</c>) offers its choices when it's reached.
/// </summary>
public static class SnippetEngine
{
    /// <summary>
    /// Replaces <paramref name="length"/> characters at <paramref name="offset"/> (a completed prefix) with the snippet.
    /// With <paramref name="keepSelection"/>, the editor's selection is the snippet's <c>$TM_SELECTED_TEXT</c>.
    /// </summary>
    public static void Insert(TextArea area, string body, int offset, int length, Func<string, string?> variables, bool keepSelection = false)
    {
        var document = area.Document;
        if (!keepSelection)
        {
            if (length > 0)
            {
                document.Remove(offset, length);
            }

            area.ClearSelection();
            area.Caret.Offset = offset;
        }

        var session = new SnippetSession(area);
        s_sessions.AddOrUpdate(area, session);
        Build(body, variables, session).Insert(area);
        session.Start();
    }

    private static readonly ConditionalWeakTable<TextArea, SnippetSession> s_sessions = [];

    /// <summary>
    /// The editor's Tab and Shift+Tab while a snippet is being filled: the next field by number (after the last, the
    /// snippet ends at <c>$0</c>). Returns whether it took the key. Called before AvaloniaEdit's own handler, which moves
    /// in document order.
    /// </summary>
    public static bool HandleKey(TextArea area, KeyEventArgs e) =>
        e.Key == Key.Tab && (e.KeyModifiers & ~KeyModifiers.Shift) == KeyModifiers.None
        && s_sessions.TryGetValue(area, out var session) && session.Move(backwards: e.KeyModifiers.HasFlag(KeyModifiers.Shift));

    /// <summary>The snippet session of an editor, or null (tests).</summary>
    public static SnippetSession? SessionOf(TextArea area) => s_sessions.TryGetValue(area, out var session) && session.IsActive ? session : null;

    /// <summary>The AvaloniaEdit snippet for a body (tests).</summary>
    public static Snippet Build(string body, Func<string, string?> variables) => Build(body, variables, session: null);

    private static Snippet Build(string body, Func<string, string?> variables, SnippetSession? session)
    {
        var snippet = new Snippet();
        if (session is not null)
        {
            snippet.Elements.Add(new SessionElement(session));
        }

        var fields = new Dictionary<int, SnippetReplaceableTextElement>();
        var hasCaret = false;
        foreach (var part in SnippetParser.Parse(body))
        {
            switch (part)
            {
                case SnippetText text:
                    snippet.Elements.Add(new SnippetTextElement { Text = text.Text });
                    break;
                case SnippetPlaceholder { Index: 0 }:
                    if (!hasCaret)
                    {
                        snippet.Elements.Add(new SnippetCaretElement());
                        hasCaret = true;
                    }

                    break;
                case SnippetPlaceholder placeholder when fields.TryGetValue(placeholder.Index, out var target):
                    snippet.Elements.Add(new SnippetBoundElement { TargetElement = target });
                    break;
                case SnippetPlaceholder placeholder:
                    var field = new IndexedField(placeholder.Index, placeholder.Choices, session) { Text = placeholder.Choices is [var first, ..] ? first : placeholder.Default };
                    fields[placeholder.Index] = field;
                    snippet.Elements.Add(field);
                    break;
                case SnippetVariable { Name: "TM_SELECTED_TEXT" or "SELECTION" }:
                    snippet.Elements.Add(new SnippetSelectionElement());
                    break;
                case SnippetVariable variable:
                    snippet.Elements.Add(new SnippetTextElement { Text = variables(variable.Name) ?? variable.Default });
                    break;
            }
        }

        return snippet;
    }

    /// <summary>The standard variables for a document (<c>TM_FILENAME</c>, <c>CURRENT_YEAR</c>, <c>UUID</c>…); null when unknown.</summary>
    public static Func<string, string?> Variables(string? path, TextArea area, string? lineComment, (string Open, string Close)? blockComment, string? clipboard = null) => name =>
    {
        var document = area.Document;
        var line = document.GetLineByOffset(area.Caret.Offset);
        var now = DateTime.Now;
        return name switch
        {
            "TM_FILENAME" => path is null ? null : Path.GetFileName(path),
            "TM_FILENAME_BASE" => path is null ? null : Path.GetFileNameWithoutExtension(path),
            "TM_DIRECTORY" => path is null ? null : Path.GetDirectoryName(path),
            "TM_DIRECTORY_NAME" => path is null ? null : Path.GetFileName(Path.GetDirectoryName(path)),
            "TM_FILEPATH" => path,
            "TM_LINE_INDEX" => (line.LineNumber - 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
            "TM_LINE_NUMBER" => line.LineNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "TM_CURRENT_LINE" => document.GetText(line),
            "TM_CURRENT_WORD" => CurrentWord(area),
            "CLIPBOARD" => clipboard,
            "CURRENT_YEAR" => now.ToString("yyyy", System.Globalization.CultureInfo.InvariantCulture),
            "CURRENT_YEAR_SHORT" => now.ToString("yy", System.Globalization.CultureInfo.InvariantCulture),
            "CURRENT_MONTH" => now.ToString("MM", System.Globalization.CultureInfo.InvariantCulture),
            "CURRENT_MONTH_NAME" => now.ToString("MMMM", System.Globalization.CultureInfo.CurrentCulture),
            "CURRENT_DATE" => now.ToString("dd", System.Globalization.CultureInfo.InvariantCulture),
            "CURRENT_DAY_NAME" => now.ToString("dddd", System.Globalization.CultureInfo.CurrentCulture),
            "CURRENT_HOUR" => now.ToString("HH", System.Globalization.CultureInfo.InvariantCulture),
            "CURRENT_MINUTE" => now.ToString("mm", System.Globalization.CultureInfo.InvariantCulture),
            "CURRENT_SECOND" => now.ToString("ss", System.Globalization.CultureInfo.InvariantCulture),
            "UUID" => Guid.NewGuid().ToString(),
            "LINE_COMMENT" => lineComment,
            "BLOCK_COMMENT_START" => blockComment?.Open,
            "BLOCK_COMMENT_END" => blockComment?.Close,
            _ => null,
        };
    };

    private static string CurrentWord(TextArea area)
    {
        var document = area.Document;
        var start = area.Caret.Offset;
        var end = start;
        while (start > 0 && (char.IsLetterOrDigit(document.GetCharAt(start - 1)) || document.GetCharAt(start - 1) == '_'))
        {
            start--;
        }

        while (end < document.TextLength && (char.IsLetterOrDigit(document.GetCharAt(end)) || document.GetCharAt(end) == '_'))
        {
            end++;
        }

        return document.GetText(start, end - start);
    }

    /// <summary>Captures the insertion's context for the session (the snippet's first element).</summary>
    private sealed class SessionElement(SnippetSession session) : SnippetElement
    {
        public override void Insert(InsertionContext context) => session.Attach(context);
    }

    /// <summary>A field with its number and choices; it tells the session its place once inserted.</summary>
    private sealed class IndexedField(int index, IReadOnlyList<string>? choices, SnippetSession? session) : SnippetReplaceableTextElement
    {
        public override void Insert(InsertionContext context)
        {
            base.Insert(context);
            if (session is not null && context.GetActiveElement(this) is { } active)
            {
                session.Add(index, choices, active);
            }
        }
    }
}

/// <summary>
/// A snippet being filled: its fields in the order of their numbers. It ends when AvaloniaEdit's insertion ends (Enter,
/// Escape, a click elsewhere) or Tab leaves the last field.
/// </summary>
public sealed class SnippetSession(TextArea area)
{
    private readonly List<(int Index, IReadOnlyList<string>? Choices, IActiveElement Element)> _fields = [];
    private InsertionContext? _context;
    private CompletionWindow? _choices;

    public bool IsActive => _context is not null;

    /// <summary>The fields' numbers in the order Tab visits them.</summary>
    public IReadOnlyList<int> Order => [.. _fields.OrderBy(f => f.Index).Select(f => f.Index)];

    /// <summary>The number of the field the caret is in, or null.</summary>
    public int? Current => CurrentField()?.Index;

    internal void Attach(InsertionContext context)
    {
        _context = context;
        context.Deactivated += (_, _) =>
        {
            _context = null;
            _choices?.Close();
        };
    }

    internal void Add(int index, IReadOnlyList<string>? choices, IActiveElement element) => _fields.Add((index, choices, element));

    /// <summary>After the insertion: the first field by number (AvaloniaEdit selected the first in the text).</summary>
    internal void Start()
    {
        if (IsActive && _fields.Count > 0)
        {
            Select(_fields.OrderBy(f => f.Index).First());
        }
    }

    /// <summary>Tab or Shift+Tab. Returns false when no snippet is being filled here.</summary>
    internal bool Move(bool backwards)
    {
        if (_context is not { } context)
        {
            return false;
        }

        var ordered = _fields.Where(f => f.Element.Segment is not null).OrderBy(f => f.Index).ToList();
        var current = CurrentField();
        var position = current is { } c ? ordered.FindIndex(f => f.Index == c.Index) : -1;
        var next = backwards ? Math.Max(0, position - 1) : position + 1;
        if (ordered.Count == 0 || next >= ordered.Count)
        {
            // Past the last field: the snippet is done, and the caret goes to $0.
            context.Deactivate(new SnippetEventArgs(DeactivateReason.ReturnPressed));
            return true;
        }

        Select(ordered[next]);
        return true;
    }

    private (int Index, IReadOnlyList<string>? Choices, IActiveElement Element)? CurrentField()
    {
        var caret = area.Caret.Offset;
        foreach (var field in _fields.OrderBy(f => f.Index))
        {
            if (field.Element.Segment is { } segment && caret >= segment.Offset && caret <= segment.EndOffset)
            {
                return field;
            }
        }

        return null;
    }

    private void Select((int Index, IReadOnlyList<string>? Choices, IActiveElement Element) field)
    {
        _choices?.Close();
        if (field.Element.Segment is not { } segment)
        {
            return;
        }

        area.Selection = Selection.Create(area, segment);
        area.Caret.Offset = segment.EndOffset;
        if (field.Choices is { Count: > 1 } choices)
        {
            ShowChoices(field.Element, choices);
        }
    }

    /// <summary>The field's choices in a list; the one picked replaces the field's text.</summary>
    private void ShowChoices(IActiveElement field, IReadOnlyList<string> choices)
    {
        // Every choice is listed (the list would filter by the field's text), and the one picked replaces the field.
        var caret = area.Caret.Offset;
        var window = new CompletionWindow(area) { StartOffset = caret, EndOffset = caret, CloseAutomatically = true };
        foreach (var choice in choices)
        {
            window.CompletionList.CompletionData.Add(new Choice(choice, field));
        }

        var current = field.Segment is { } segment ? area.Document.GetText(segment) : null;
        window.CompletionList.SelectedItem = window.CompletionList.CompletionData.FirstOrDefault(d => d.Text == current);

        window.Closed += (_, _) =>
        {
            if (_choices == window)
            {
                _choices = null;
            }
        };
        _choices = window;
        window.Show();
    }

    /// <summary>The open choice list's entries (tests), or empty.</summary>
    public IReadOnlyList<string> Choices => _choices is { } window ? [.. window.CompletionList.CompletionData.Select(d => d.Text)] : [];

    private sealed class Choice(string text, IActiveElement field) : ICompletionData
    {
        public Avalonia.Media.IImage? Image => null;

        public string Text => text;

        public object Content => text;

        public object Description => text;

        public double Priority => 0;

        public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs)
        {
            if (field.Segment is { } segment)
            {
                textArea.Document.Replace(segment, text);
                textArea.Selection = Selection.Create(textArea, segment.Offset, segment.Offset + text.Length);
            }
        }
    }
}
