using System.Text;
using System.Text.RegularExpressions;
using DaedalusStudio.LanguageServices.Protocol;

namespace DaedalusStudio.Data.Host.Sql;

/// <summary>
/// Completion and hover for SQL (ADR 0038): the dialect's keywords and functions, and the connection's schema objects
/// and columns, with the statement's table aliases resolved. Works on text only, so it is the same for every dialect.
/// </summary>
internal static partial class SqlCompletion
{
    private static readonly string[] s_common =
    [
        "SELECT", "FROM", "WHERE", "AND", "OR", "NOT", "NULL", "IS", "IN", "EXISTS", "BETWEEN", "LIKE", "AS", "ON", "JOIN", "INNER", "LEFT", "RIGHT",
        "FULL", "OUTER", "CROSS", "GROUP BY", "ORDER BY", "HAVING", "DISTINCT", "UNION", "UNION ALL", "EXCEPT", "INTERSECT", "INSERT INTO", "VALUES",
        "UPDATE", "SET", "DELETE FROM", "CREATE TABLE", "CREATE VIEW", "CREATE INDEX", "CREATE UNIQUE INDEX", "ALTER TABLE", "DROP TABLE", "DROP VIEW",
        "DROP INDEX", "ADD", "COLUMN", "PRIMARY KEY", "FOREIGN KEY", "REFERENCES", "UNIQUE", "CHECK", "DEFAULT", "CONSTRAINT", "CASE", "WHEN", "THEN",
        "ELSE", "END", "ASC", "DESC", "WITH", "BEGIN", "COMMIT", "ROLLBACK", "TRANSACTION", "CAST", "COALESCE", "COUNT", "SUM", "AVG", "MIN", "MAX",
        "TRUE", "FALSE", "ALL", "ANY", "OVER", "PARTITION BY", "ROW_NUMBER", "RANK", "DENSE_RANK",
    ];

    private static readonly Dictionary<string, string[]> s_dialects = new(StringComparer.Ordinal)
    {
        ["sqlserver"] =
        [
            "TOP", "GO", "DECLARE", "PRINT", "EXEC", "EXECUTE", "PROCEDURE", "FUNCTION", "RETURN", "RETURNS", "IF", "WHILE", "TRY", "CATCH", "THROW",
            "OUTPUT", "MERGE", "USING", "MATCHED", "IDENTITY", "NVARCHAR", "VARCHAR", "INT", "BIGINT", "BIT", "DATETIME2", "DATETIMEOFFSET", "DECIMAL",
            "UNIQUEIDENTIFIER", "VARBINARY", "GETDATE", "GETUTCDATE", "SYSUTCDATETIME", "NEWID", "ISNULL", "IIF", "LEN", "CONVERT", "TRY_CONVERT",
            "OFFSET", "FETCH NEXT", "ROWS ONLY", "NOLOCK", "SET NOCOUNT ON", "USE", "SCHEMA_NAME", "OBJECT_ID", "STRING_AGG",
        ],
        ["postgresql"] =
        [
            "LIMIT", "OFFSET", "RETURNING", "ILIKE", "SERIAL", "BIGSERIAL", "TEXT", "INTEGER", "BOOLEAN", "TIMESTAMPTZ", "JSONB", "UUID", "NOW",
            "CURRENT_TIMESTAMP", "GENERATED ALWAYS AS IDENTITY", "ON CONFLICT", "DO NOTHING", "DO UPDATE", "EXPLAIN", "ANALYZE", "STRING_AGG",
            "ARRAY_AGG", "LATERAL", "DISTINCT ON", "SCHEMA", "VACUUM",
        ],
        ["mysql"] =
        [
            "LIMIT", "OFFSET", "AUTO_INCREMENT", "ENGINE", "INT", "BIGINT", "VARCHAR", "TEXT", "DATETIME", "TINYINT", "JSON", "NOW", "IFNULL",
            "GROUP_CONCAT", "ON DUPLICATE KEY UPDATE", "REPLACE INTO", "SHOW TABLES", "DESCRIBE", "EXPLAIN", "USE", "UNSIGNED",
        ],
        ["sqlite"] =
        [
            "LIMIT", "OFFSET", "AUTOINCREMENT", "INTEGER", "TEXT", "REAL", "BLOB", "NUMERIC", "WITHOUT ROWID", "PRAGMA", "ATTACH", "DETACH",
            "INSERT OR REPLACE", "INSERT OR IGNORE", "RETURNING", "GLOB", "IFNULL", "datetime", "strftime", "EXPLAIN QUERY PLAN", "VACUUM",
        ],
    };

    /// <summary>The words before which an identifier names a table (so the next word may be its alias).</summary>
    [GeneratedRegex(@"\b(?:FROM|JOIN|UPDATE|INTO|TABLE)\s+((?:[\[""`]?[\w$#@]+[\]""`]?\s*\.\s*)?[\[""`]?[\w$#@]+[\]""`]?)(?:\s+(?:AS\s+)?([A-Za-z_][\w$#@]*))?", RegexOptions.IgnoreCase)]
    private static partial Regex TableReference();

    private static readonly HashSet<string> s_notAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        "WHERE", "ON", "JOIN", "INNER", "LEFT", "RIGHT", "FULL", "OUTER", "CROSS", "GROUP", "ORDER", "HAVING", "SET", "VALUES", "SELECT", "UNION",
        "LIMIT", "OFFSET", "WITH", "USING", "AS", "OUTPUT", "RETURNING", "DEFAULT", "WHEN", "THEN", "ELSE", "END", "AND", "OR", "GO", "FOR",
    };

    public static CompletionList Complete(string text, int offset, SqlContextParams? context)
    {
        var (start, qualifier) = Word(text, offset);
        var range = new LanguageServices.Protocol.Range(PositionOf(text, start), PositionOf(text, offset));
        var items = new List<CompletionItem>();
        void Add(string label, int kind, string? detail, string sort, MarkupContent? documentation = null, string? insert = null) =>
            items.Add(new CompletionItem(label) { Kind = kind, Detail = detail, SortText = sort + label, Documentation = documentation, TextEdit = new TextEdit(range, insert ?? label) });

        var objects = context?.Objects ?? [];
        if (qualifier is not null)
        {
            // schema. → its objects; alias. or table. → columns.
            var schemaObjects = objects.Where(o => o.Schema is { } s && s.Equals(qualifier, StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var o in schemaObjects)
            {
                Add(o.Name, Kind(o.Kind), o.Kind, "1", Documentation(o));
            }

            if (schemaObjects.Count == 0 && Resolve(Statement(text, offset), qualifier, objects) is { } table)
            {
                foreach (var column in table.Columns)
                {
                    Add(column.Name, CompletionItemKind.Field, column.Type + (column.IsKey ? " key" : string.Empty), "0");
                }
            }

            return new CompletionList(false, [.. items]);
        }

        // Columns of the statement's tables first, then objects, then keywords.
        var statement = Statement(text, offset);
        foreach (var (reference, _) in References(statement))
        {
            if (Find(reference, objects) is { } table)
            {
                foreach (var column in table.Columns)
                {
                    Add(column.Name, CompletionItemKind.Field, $"{column.Type} ({table.Name})", "0");
                }
            }
        }

        foreach (var o in objects)
        {
            var qualified = o.Schema is { Length: > 0 } schema && !schema.Equals(context?.DefaultSchema, StringComparison.OrdinalIgnoreCase);
            Add(qualified ? o.Schema + "." + o.Name : o.Name, Kind(o.Kind), o.Kind, "1", Documentation(o));
        }

        foreach (var schema in objects.Select(o => o.Schema).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            Add(schema, CompletionItemKind.Module, "schema", "2");
        }

        foreach (var keyword in Keywords(context?.Dialect))
        {
            Add(keyword, CompletionItemKind.Keyword, null, "3");
        }

        return new CompletionList(false, [.. items.DistinctBy(i => (i.Label, i.Kind))]);
    }

    public static Hover? Hover(string text, int offset, SqlContextParams? context)
    {
        var start = offset;
        while (start > 0 && IsWordChar(text[start - 1]))
        {
            start--;
        }

        var end = offset;
        while (end < text.Length && IsWordChar(text[end]))
        {
            end++;
        }

        if (end == start || context is null)
        {
            return null;
        }

        var word = text[start..end];
        var qualifier = start > 1 && text[start - 1] == '.' ? WordBefore(text, start - 1) : null;
        var target = qualifier is null ? word : qualifier + "." + word;
        if (Find(target, context.Objects) is { } table)
        {
            return new Hover(Documentation(table)!, new LanguageServices.Protocol.Range(PositionOf(text, start), PositionOf(text, end)));
        }

        // A column: of the qualifier's table, or of a table of the statement.
        var statement = Statement(text, offset);
        var tables = qualifier is not null && Resolve(statement, qualifier, context.Objects) is { } resolved
            ? [resolved]
            : References(statement).Select(r => Find(r.Table, context.Objects)).OfType<SqlSchemaObject>().ToList();
        foreach (var candidate in tables)
        {
            if (candidate.Columns.FirstOrDefault(c => c.Name.Equals(word, StringComparison.OrdinalIgnoreCase)) is { } column)
            {
                return new Hover(MarkupContent.Markdown($"**{candidate.Name}.{column.Name}** `{column.Type}`{(column.IsKey ? " · key" : string.Empty)}{(column.Nullable ? " · nullable" : string.Empty)}"),
                    new LanguageServices.Protocol.Range(PositionOf(text, start), PositionOf(text, end)));
            }
        }

        return null;
    }

    public static IEnumerable<string> Keywords(string? dialect) => s_common.Concat(dialect is not null && s_dialects.TryGetValue(dialect, out var extra) ? extra : []);

    private static int Kind(string kind) => kind switch
    {
        "view" => CompletionItemKind.Interface,
        "procedure" or "function" => CompletionItemKind.Function,
        _ => CompletionItemKind.Class,
    };

    private static MarkupContent? Documentation(SqlSchemaObject o)
    {
        if (o.Columns.Length == 0)
        {
            return MarkupContent.Markdown($"**{(o.Schema is null ? o.Name : o.Schema + "." + o.Name)}** ({o.Kind})");
        }

        var text = new StringBuilder($"**{(o.Schema is null ? o.Name : o.Schema + "." + o.Name)}** ({o.Kind})\n\n| Column | Type |\n|---|---|\n");
        foreach (var column in o.Columns.Take(40))
        {
            text.Append("| ").Append(column.Name).Append(column.IsKey ? " (key)" : string.Empty).Append(" | `").Append(column.Type).Append(column.Nullable ? " null" : string.Empty).Append("` |\n");
        }

        return MarkupContent.Markdown(text.ToString());
    }

    /// <summary>The start of the word being completed, and the qualifier before a dot (<c>alias.</c>), unquoted.</summary>
    private static (int Start, string? Qualifier) Word(string text, int offset)
    {
        var start = offset;
        while (start > 0 && IsWordChar(text[start - 1]))
        {
            start--;
        }

        return start > 0 && text[start - 1] == '.' ? (start, WordBefore(text, start - 1)) : (start, null);
    }

    private static string? WordBefore(string text, int dot)
    {
        var end = dot;
        if (end > 0 && text[end - 1] is ']' or '"' or '`')
        {
            end--;
        }

        var start = end;
        while (start > 0 && IsWordChar(text[start - 1]))
        {
            start--;
        }

        return start < end ? text[start..end] : null;
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '$' or '#' or '@';

    /// <summary>The statement around the offset: between semicolons or <c>GO</c> lines.</summary>
    private static string Statement(string text, int offset)
    {
        var start = text.LastIndexOf(';', Math.Max(0, Math.Min(offset, text.Length) - 1));
        var end = text.IndexOf(';', Math.Min(offset, text.Length));
        return text[(start + 1)..(end < 0 ? text.Length : end)];
    }

    private static IEnumerable<(string Table, string? Alias)> References(string statement) =>
        TableReference().Matches(statement).Select(m => (Unquote(m.Groups[1].Value), m.Groups[2].Success && !s_notAliases.Contains(m.Groups[2].Value) ? m.Groups[2].Value : null));

    private static SqlSchemaObject? Resolve(string statement, string qualifier, SqlSchemaObject[] objects)
    {
        foreach (var (table, alias) in References(statement))
        {
            if (alias is not null && alias.Equals(qualifier, StringComparison.OrdinalIgnoreCase))
            {
                return Find(table, objects);
            }
        }

        return Find(qualifier, objects);
    }

    private static SqlSchemaObject? Find(string name, SqlSchemaObject[] objects)
    {
        var parts = name.Split('.');
        var table = parts[^1];
        var schema = parts.Length > 1 ? parts[^2] : null;
        return objects.FirstOrDefault(o => o.Name.Equals(table, StringComparison.OrdinalIgnoreCase) && (schema is null || string.Equals(o.Schema, schema, StringComparison.OrdinalIgnoreCase)));
    }

    private static string Unquote(string name) => string.Join('.', name.Split('.').Select(p => p.Trim().Trim('[', ']', '"', '`')));

    public static Position PositionOf(string text, int offset)
    {
        var line = 0;
        var lineStart = 0;
        for (var i = 0; i < offset && i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                line++;
                lineStart = i + 1;
            }
        }

        return new Position(line, offset - lineStart);
    }
}
