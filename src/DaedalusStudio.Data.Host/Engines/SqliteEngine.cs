using System.Data.Common;
using DaedalusStudio.Data.Protocol;
using Microsoft.Data.Sqlite;

namespace DaedalusStudio.Data.Host.Engines;

/// <summary>SQLite through Microsoft.Data.Sqlite: <c>sqlite_schema</c> and the <c>pragma_*</c> table functions.</summary>
internal sealed class SqliteEngine : DbEngine
{
    public override string Id => DataEngines.Sqlite;

    /// <summary>Attached databases are chosen by their schema name; there is no other database to switch to.</summary>
    public override DbConnection Create(string connectionString, string? database) => new SqliteConnection(connectionString);

    /// <summary>Microsoft.Data.Sqlite runs a statement to the end: <c>sqlite3_interrupt</c> stops it.</summary>
    public override IDisposable? OnCancel(DbConnection connection, CancellationToken cancellationToken) =>
        connection is SqliteConnection { Handle: { } handle } ? cancellationToken.Register(() => SQLitePCL.raw.sqlite3_interrupt(handle)) : null;

    /// <summary>
    /// SQLite stores what it is given (type affinity): values follow the declared type, so a number stays a number and
    /// a typo in an INTEGER column is refused rather than stored as text.
    /// </summary>
    public override Type ValueType(DbColumn column) => ValueType(column.DataTypeName);

    private static Type ValueType(string? declaredType)
    {
        var declared = (declaredType ?? string.Empty).ToUpperInvariant();
        return declared switch
        {
            _ when declared.Contains("INT", StringComparison.Ordinal) => typeof(long),
            _ when declared.Contains("REAL", StringComparison.Ordinal) || declared.Contains("FLOA", StringComparison.Ordinal) || declared.Contains("DOUB", StringComparison.Ordinal) => typeof(double),
            _ when declared.Contains("NUM", StringComparison.Ordinal) || declared.Contains("DEC", StringComparison.Ordinal) => typeof(decimal),
            _ when declared.Contains("BLOB", StringComparison.Ordinal) => typeof(byte[]),
            _ when declared.Contains("BOOL", StringComparison.Ordinal) => typeof(bool),
            _ => typeof(string),
        };
    }

    /// <summary>From <c>pragma_table_info</c>: Microsoft.Data.Sqlite's key information reads the whole table.</summary>
    public override async Task<(ResultColumn[] Columns, string[] Keys, Dictionary<string, Type> Types)> ShapeAsync(DbConnection connection, DbTableRef table, CancellationToken cancellationToken)
    {
        var rows = await RowsAsync(connection, "SELECT name, type, \"notnull\", pk FROM pragma_table_info(@t) ORDER BY cid", cancellationToken, ("@t", table.Name)).ConfigureAwait(false);
        var types = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            types[Text(row[0])] = ValueType(Text(row[1]));
        }

        return (
            [.. rows.Select(r => new ResultColumn(Text(r[0]), Text(r[1]) is { Length: > 0 } type ? type : "ANY", !Truthy(r[2]) && Convert.ToInt64(r[3], System.Globalization.CultureInfo.InvariantCulture) == 0))],
            [.. rows.Where(r => Convert.ToInt64(r[3], System.Globalization.CultureInfo.InvariantCulture) > 0).OrderBy(r => Convert.ToInt64(r[3], System.Globalization.CultureInfo.InvariantCulture)).Select(r => Text(r[0]))],
            types);
    }

    public override string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    public override async Task<DatabasesResult> DatabasesAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        var rows = await RowsAsync(connection, "SELECT name FROM pragma_database_list ORDER BY seq", cancellationToken).ConfigureAwait(false);
        return new DatabasesResult([.. rows.Select(r => Text(r[0]))], "main");
    }

    public override async Task<SchemaResult> SchemaAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        var objects = new List<DbObjectInfo>();
        var tables = await RowsAsync(connection, "SELECT type, name FROM sqlite_schema WHERE type IN ('table', 'view') AND name NOT LIKE 'sqlite_%' ORDER BY name", cancellationToken).ConfigureAwait(false);
        foreach (var table in tables)
        {
            var kind = Text(table[0]) == "view" ? DbObjectKinds.View : DbObjectKinds.Table;
            var name = Text(table[1]);
            var columns = await RowsAsync(connection, "SELECT name, type, \"notnull\", dflt_value, pk FROM pragma_table_info(@t) ORDER BY cid", cancellationToken, ("@t", name)).ConfigureAwait(false);
            var keyColumns = columns.Where(c => Convert.ToInt64(c[4], System.Globalization.CultureInfo.InvariantCulture) > 0)
                .OrderBy(c => Convert.ToInt64(c[4], System.Globalization.CultureInfo.InvariantCulture)).Select(c => Text(c[0])).ToArray();
            var keys = new List<DbKeyInfo>();
            if (keyColumns.Length > 0)
            {
                keys.Add(new DbKeyInfo("PK_" + name, "primary", keyColumns));
            }

            var foreign = await RowsAsync(connection, "SELECT id, \"table\", \"from\", \"to\" FROM pragma_foreign_key_list(@t) ORDER BY id, seq", cancellationToken, ("@t", name)).ConfigureAwait(false);
            foreach (var group in foreign.GroupBy(f => Convert.ToInt64(f[0], System.Globalization.CultureInfo.InvariantCulture)))
            {
                var referenced = Text(group.First()[1]);
                keys.Add(new DbKeyInfo($"FK_{name}_{referenced}_{group.Key}", "foreign", [.. group.Select(f => Text(f[2]))], null, referenced, [.. group.Select(f => f[3] is null ? string.Empty : Text(f[3]))]));
            }

            var indexes = new List<DbIndexInfo>();
            foreach (var index in await RowsAsync(connection, "SELECT name, \"unique\", origin FROM pragma_index_list(@t)", cancellationToken, ("@t", name)).ConfigureAwait(false))
            {
                var indexColumns = await RowsAsync(connection, "SELECT name FROM pragma_index_info(@i) ORDER BY seqno", cancellationToken, ("@i", Text(index[0]))).ConfigureAwait(false);
                if (Text(index[2]) == "u")
                {
                    keys.Add(new DbKeyInfo(Text(index[0]), "unique", [.. indexColumns.Select(c => Text(c[0]))]));
                }

                indexes.Add(new DbIndexInfo(Text(index[0]), [.. indexColumns.Select(c => Text(c[0]))], Truthy(index[1])));
            }

            var single = keyColumns.Length == 1 ? keyColumns[0] : null;
            objects.Add(new DbObjectInfo(null, name, kind, [.. columns.Select(c => new DbColumnInfo(
                Text(c[0]),
                Text(c[1]) is { Length: > 0 } type ? type : "ANY",
                !Truthy(c[2]) && !keyColumns.Contains(Text(c[0])),
                keyColumns.Contains(Text(c[0])),
                single == Text(c[0]) && Text(c[1]).Equals("INTEGER", StringComparison.OrdinalIgnoreCase),
                c[3] is null ? null : Text(c[3])))], [.. keys], [.. indexes]));
        }

        return new SchemaResult("main", null, [.. objects]);
    }

    public override async Task<(PlanInfo Plan, List<ResultSetInfo>? Results)> PlanAsync(DbConnection connection, string text, bool actual, Func<DbCommand, Task<List<ResultSetInfo>>> read, CancellationToken cancellationToken)
    {
        if (actual)
        {
            throw new NotSupportedException("SQLite has no actual execution plan; use the estimated plan (EXPLAIN QUERY PLAN).");
        }

        var rows = await RowsAsync(connection, "EXPLAIN QUERY PLAN " + text.Trim().TrimEnd(';'), cancellationToken).ConfigureAwait(false);
        var tree = rows.Select(r => (Convert.ToInt64(r[0], System.Globalization.CultureInfo.InvariantCulture), Convert.ToInt64(r[1], System.Globalization.CultureInfo.InvariantCulture), Text(r[3]))).ToList();
        var raw = string.Join('\n', tree.Select(r => $"{r.Item1}|{r.Item2}|{r.Item3}"));
        return (new PlanInfo("text", raw, Plans.Sqlite(tree), false), null);
    }
}
