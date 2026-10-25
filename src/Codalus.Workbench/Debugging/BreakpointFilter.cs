using System.Globalization;

namespace Codalus.Workbench.Debugging;

/// <summary>Where a breakpoint was hit, for its filter.</summary>
public sealed record BreakpointFilterContext(int ThreadId, string? ThreadName, int? ProcessId, string? ProcessName);

/// <summary>
/// A breakpoint filter (M14b), as in the classic IDE: <c>ThreadId = 5</c>, <c>ThreadName = "Worker"</c>,
/// <c>ProcessId = 1234</c> or <c>ProcessName = "app"</c>, with <c>=</c>/<c>==</c>/<c>!=</c>, combined with
/// <c>&amp;</c>/<c>&amp;&amp;</c>, <c>|</c>/<c>||</c>, <c>!</c> and parentheses. netcoredbg has no filters, so the debugger
/// stops and continues when the filter doesn't match.
/// </summary>
public sealed class BreakpointFilter
{
    private static readonly string[] Names = ["ThreadId", "ThreadName", "ProcessId", "ProcessName", "MachineName"];
    private readonly Func<BreakpointFilterContext, bool> _test;

    private BreakpointFilter(Func<BreakpointFilterContext, bool> test) => _test = test;

    public bool Matches(BreakpointFilterContext context) => _test(context);

    /// <summary>The filter, or the reason <paramref name="text"/> isn't one.</summary>
    public static BreakpointFilter Parse(string text) => TryParse(text, out var filter, out var error) ? filter : throw new FormatException(error);

    public static bool TryParse(string text, out BreakpointFilter filter, out string? error)
    {
        var parser = new Parser(text);
        try
        {
            var test = parser.Or();
            parser.SkipSpace();
            if (!parser.AtEnd)
            {
                throw new FormatException($"Unexpected '{parser.Rest}'.");
            }

            filter = new BreakpointFilter(test);
            error = null;
            return true;
        }
        catch (FormatException ex)
        {
            filter = new BreakpointFilter(_ => true);
            error = ex.Message;
            return false;
        }
    }

    private sealed class Parser(string text)
    {
        private int _at;

        public bool AtEnd => _at >= text.Length;

        public string Rest => text[_at..];

        public void SkipSpace()
        {
            while (_at < text.Length && char.IsWhiteSpace(text[_at]))
            {
                _at++;
            }
        }

        private bool Take(string token)
        {
            SkipSpace();
            if (string.CompareOrdinal(text, _at, token, 0, token.Length) != 0)
            {
                return false;
            }

            _at += token.Length;
            return true;
        }

        public Func<BreakpointFilterContext, bool> Or()
        {
            var left = And();
            while (Take("||") || Take("|"))
            {
                var (a, b) = (left, And());
                left = c => a(c) || b(c);
            }

            return left;
        }

        private Func<BreakpointFilterContext, bool> And()
        {
            var left = Unary();
            while (Take("&&") || Take("&"))
            {
                var (a, b) = (left, Unary());
                left = c => a(c) && b(c);
            }

            return left;
        }

        private Func<BreakpointFilterContext, bool> Unary()
        {
            if (Take("!") && !Peek('='))
            {
                var inner = Unary();
                return c => !inner(c);
            }

            if (Take("("))
            {
                var inner = Or();
                if (!Take(")"))
                {
                    throw new FormatException("A ')' is missing.");
                }

                return inner;
            }

            return Comparison();
        }

        private bool Peek(char c) => _at < text.Length && text[_at] == c;

        private Func<BreakpointFilterContext, bool> Comparison()
        {
            SkipSpace();
            var start = _at;
            while (_at < text.Length && char.IsLetter(text[_at]))
            {
                _at++;
            }

            var name = Names.FirstOrDefault(n => string.Equals(n, text[start.._at], StringComparison.OrdinalIgnoreCase))
                ?? throw new FormatException($"Use ThreadId, ThreadName, ProcessId, ProcessName or MachineName (not '{text[start.._at]}').");
            var negate = Take("!=") ? true : Take("==") || Take("=") ? false : throw new FormatException($"'=' or '!=' is missing after {name}.");
            var value = Value();
            Func<BreakpointFilterContext, bool> test = name switch
            {
                "ThreadId" => c => c.ThreadId.ToString(CultureInfo.InvariantCulture) == value,
                "ThreadName" => c => string.Equals(c.ThreadName, value, StringComparison.Ordinal),
                "ProcessId" => c => c.ProcessId?.ToString(CultureInfo.InvariantCulture) == value,
                "ProcessName" => c => c.ProcessName is { } process && (string.Equals(process, value, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(process + ".exe", value, StringComparison.OrdinalIgnoreCase)),
                _ => _ => string.Equals(Environment.MachineName, value, StringComparison.OrdinalIgnoreCase),
            };
            return negate ? c => !test(c) : test;
        }

        private string Value()
        {
            SkipSpace();
            if (Peek('"'))
            {
                var end = text.IndexOf('"', _at + 1);
                if (end < 0)
                {
                    throw new FormatException("A closing '\"' is missing.");
                }

                var quoted = text[(_at + 1)..end];
                _at = end + 1;
                return quoted;
            }

            var start = _at;
            while (_at < text.Length && (char.IsLetterOrDigit(text[_at]) || text[_at] is '.' or '_' or '-'))
            {
                _at++;
            }

            return start == _at ? throw new FormatException("A value is missing.") : text[start.._at];
        }
    }
}
