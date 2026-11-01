namespace Codalus.LanguageServices.Client;

/// <summary>
/// Whether a Visual Basic file declares a class, structure, interface or enum, from the text alone: the types the C#
/// host's document symbols list for it (<c>VisualBasicStructure</c>), in a namespace, a module or another type. The
/// editor keeps the navigation bar's room from the first frame when they will come. A declaration is the first word
/// of a statement (after its attributes and modifiers), outside comments, <c>REM</c> lines, strings and preprocessor
/// lines; every <c>#If</c> branch is read.
/// </summary>
public static class VisualBasicTypeScanner
{
    private static readonly HashSet<string> s_modifiers = new(StringComparer.OrdinalIgnoreCase)
    {
        "Public", "Private", "Protected", "Friend", "Partial", "MustInherit", "NotInheritable", "Shadows", "Shared",
        "Overloads", "Overrides", "Overridable", "NotOverridable", "MustOverride", "ReadOnly", "WriteOnly", "Widening",
        "Narrowing", "Default", "WithEvents", "Static", "Async", "Iterator",
    };

    private static readonly HashSet<string> s_types = new(StringComparer.OrdinalIgnoreCase) { "Class", "Structure", "Interface", "Enum" };

    /// <summary>Whether the text declares a type; it stops at the first.</summary>
    public static bool DeclaresType(string text)
    {
        var i = 0;
        while (i < text.Length)
        {
            i = Statement(text, i, out var declares);
            if (declares)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Reads one statement from <paramref name="start"/>; returns where the next one starts.</summary>
    private static int Statement(string text, int start, out bool declares)
    {
        declares = false;
        var i = SkipBlanks(text, start);
        if (i < text.Length && text[i] == '#')
        {
            return EndOfLine(text, i); // #If, #Region, #Const…
        }

        // Its attributes and modifiers, then the declaration's keyword and a name.
        var first = true;
        while (i < text.Length)
        {
            i = SkipBlanks(text, i);
            if (i >= text.Length)
            {
                return i;
            }

            var c = text[i];
            if (c == '<' && first)
            {
                i = SkipAttributes(text, i);
                continue;
            }

            if (!IsWordStart(c))
            {
                break;
            }

            var end = WordEnd(text, i);
            var word = text.AsSpan(i, end - i);
            if (word.Equals("REM", StringComparison.OrdinalIgnoreCase))
            {
                return EndOfLine(text, i);
            }

            if (s_types.GetAlternateLookup<ReadOnlySpan<char>>().Contains(word))
            {
                var name = end;
                while (name < text.Length && text[name] is ' ' or '\t')
                {
                    name++;
                }

                declares = name < text.Length && IsWordStart(text[name]); // not "End Class", "Exit …": a name follows
                return end;
            }

            i = end;
            if (!s_modifiers.GetAlternateLookup<ReadOnlySpan<char>>().Contains(word))
            {
                break;
            }

            first = false;
        }

        return RestOfStatement(text, i);
    }

    /// <summary>To the end of the statement: its line (through <c> _</c> continuations) or a <c>:</c>, past strings and comments.</summary>
    private static int RestOfStatement(string text, int i)
    {
        while (i < text.Length)
        {
            var c = text[i];
            switch (c)
            {
                case '\n':
                    return i + 1;
                case '\'' or '‘' or '’':
                    return EndOfLine(text, i);
                case '"' or '“' or '”':
                    i = SkipString(text, i + 1);
                    continue;
                case ':' when i + 1 >= text.Length || text[i + 1] != '=':
                    return i + 1;
                case '_' when (i + 1 >= text.Length || text[i + 1] is '\r' or '\n' or ' ' or '\t' or '\'') && i > 0 && text[i - 1] is ' ' or '\t':
                    // A line continuation: the statement goes on after the line's end (past a comment after it).
                    i = EndOfLine(text, i);
                    continue;
            }

            if (IsWordStart(c))
            {
                var end = WordEnd(text, i);
                if (end - i == 3 && text.AsSpan(i, 3).Equals("REM", StringComparison.OrdinalIgnoreCase) && (i == 0 || !IsWordPart(text[i - 1])))
                {
                    return EndOfLine(text, i);
                }

                i = end;
                continue;
            }

            i++;
        }

        return i;
    }

    /// <summary>Past a string's closing quote (<c>""</c> is a quote inside it); interpolations' holes are read as its text.</summary>
    private static int SkipString(string text, int i)
    {
        while (i < text.Length)
        {
            if (text[i] is '"' or '“' or '”')
            {
                if (i + 1 < text.Length && text[i + 1] is '"' or '“' or '”')
                {
                    i += 2;
                    continue;
                }

                return i + 1;
            }

            i++;
        }

        return i;
    }

    /// <summary>Past an attribute list (<c>&lt;Serializable&gt;</c>, nested generic brackets and strings included).</summary>
    private static int SkipAttributes(string text, int i)
    {
        var depth = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (c == '"')
            {
                i = SkipString(text, i + 1);
                continue;
            }

            if (c == '<')
            {
                depth++;
            }
            else if (c == '>' && --depth == 0)
            {
                return i + 1; // the declaration may follow on the next line (implicit continuation)
            }

            i++;
        }

        return i;
    }

    private static int SkipBlanks(string text, int i)
    {
        while (i < text.Length && text[i] is ' ' or '\t' or '\r' or '\n')
        {
            i++;
        }

        return i;
    }

    private static int EndOfLine(string text, int i)
    {
        var end = text.IndexOf('\n', i);
        return end < 0 ? text.Length : end + 1;
    }

    private static int WordEnd(string text, int i)
    {
        if (text[i] == '[')
        {
            var close = text.IndexOf(']', i);
            return close < 0 ? text.Length : close + 1; // an escaped name: [Class]
        }

        while (i < text.Length && IsWordPart(text[i]))
        {
            i++;
        }

        return i;
    }

    private static bool IsWordStart(char c) => char.IsLetter(c) || c is '_' or '[';

    private static bool IsWordPart(char c) => char.IsLetterOrDigit(c) || c == '_';
}
