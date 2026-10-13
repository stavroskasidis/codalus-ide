using Codalus.LanguageServices.Protocol;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Codalus.Data.Host.Sql;

/// <summary>T-SQL through ScriptDom (MIT, ADR 0038): parse errors and formatting.</summary>
internal static class TSql
{
    public static Diagnostic[] Diagnostics(string text)
    {
        var parser = new TSql170Parser(initialQuotedIdentifiers: true);
        parser.Parse(new StringReader(text), out var errors);
        return [.. errors.Select(e =>
        {
            var start = new Position(Math.Max(0, e.Line - 1), Math.Max(0, e.Column - 1));
            var end = new Position(start.Line, start.Character + Math.Max(1, TokenLength(text, e.Offset)));
            return new Diagnostic(new LanguageServices.Protocol.Range(start, end), DiagnosticSeverity.Error, e.Number.ToString(System.Globalization.CultureInfo.InvariantCulture), "T-SQL", e.Message);
        })];
    }

    /// <summary>
    /// The whole script formatted by ScriptDom's generator, or null when it doesn't parse or has comments (the generator
    /// drops comments, and formatting must never lose text).
    /// </summary>
    public static string? Format(string text, int indent)
    {
        if (text.Contains("--", StringComparison.Ordinal) || text.Contains("/*", StringComparison.Ordinal))
        {
            return null;
        }

        var parser = new TSql170Parser(initialQuotedIdentifiers: true);
        var fragment = parser.Parse(new StringReader(text), out var errors);
        if (errors.Count > 0)
        {
            return null;
        }

        var generator = new Sql170ScriptGenerator(new SqlScriptGeneratorOptions
        {
            KeywordCasing = KeywordCasing.Uppercase,
            IndentationSize = indent,
            IncludeSemicolons = true,
            NewLineBeforeFromClause = true,
            NewLineBeforeWhereClause = true,
            NewLineBeforeJoinClause = true,
            NewLineBeforeOrderByClause = true,
            NewLineBeforeGroupByClause = true,
            AlignClauseBodies = false,
        });
        generator.GenerateScript(fragment, out var script);
        return script;
    }

    private static int TokenLength(string text, int offset)
    {
        var end = offset;
        while (end < text.Length && !char.IsWhiteSpace(text[end]))
        {
            end++;
        }

        return Math.Min(end - offset, 40);
    }
}
