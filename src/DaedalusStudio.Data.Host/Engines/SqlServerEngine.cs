using System.Data.Common;
using System.Text;
using System.Text.RegularExpressions;
using DaedalusStudio.Data.Protocol;
using Microsoft.Data.SqlClient;

namespace DaedalusStudio.Data.Host.Engines;

/// <summary>SQL Server through Microsoft.Data.SqlClient (managed SNI, ADR 0037).</summary>
internal sealed partial class SqlServerEngine : DbEngine
{
    private const string ShowplanColumn = "Microsoft SQL Server 2005 XML Showplan";

    public override string Id => DataEngines.SqlServer;

    public override DbConnection Create(string connectionString, string? database)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        if (database is { Length: > 0 })
        {
            builder.InitialCatalog = database;
        }

        return new SqlConnection(builder.ConnectionString);
    }

    public override string Quote(string identifier) => "[" + identifier.Replace("]", "]]", StringComparison.Ordinal) + "]";

    public override string Page(DbTableRef table, string? filter, IReadOnlyList<string> order, bool descending, int offset, int count)
    {
        var sql = new StringBuilder("SELECT * FROM ").Append(Qualified(table));
        if (filter is { Length: > 0 })
        {
            sql.Append(" WHERE ").Append(filter);
        }

        sql.Append(" ORDER BY ").Append(order.Count > 0 ? string.Join(", ", order.Select(c => Quote(c) + (descending ? " DESC" : string.Empty))) : "(SELECT NULL)");
        return sql.Append(" OFFSET ").Append(offset).Append(" ROWS FETCH NEXT ").Append(count).Append(" ROWS ONLY").ToString();
    }

    /// <summary>Batches separated by lines that hold only <c>GO</c> (optionally with a count), as SSMS and sqlcmd do.</summary>
    public override IReadOnlyList<string> Batches(string text)
    {
        var batches = new List<string>();
        var current = new StringBuilder();
        foreach (var line in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var match = GoLine().Match(line);
            if (match.Success)
            {
                var times = match.Groups[1].Success ? int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : 1;
                for (var i = 0; i < times && current.ToString().Trim().Length > 0; i++)
                {
                    batches.Add(current.ToString());
                }

                current.Clear();
            }
            else
            {
                current.Append(line).Append('\n');
            }
        }

        if (current.ToString().Trim().Length > 0)
        {
            batches.Add(current.ToString());
        }

        return batches;
    }

    [GeneratedRegex(@"^\s*GO(?:\s+(\d+))?\s*(?:--.*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex GoLine();

    public override IDisposable? Listen(DbConnection connection, Action<QueryMessage> message)
    {
        var sql = (SqlConnection)connection;
        void Handler(object sender, SqlInfoMessageEventArgs e)
        {
            foreach (SqlError error in e.Errors)
            {
                message(new QueryMessage(error.Class > 10 ? "error" : "info", error.Message, error.LineNumber > 0 ? error.LineNumber : null));
            }
        }

        sql.InfoMessage += Handler;
        return new Unsubscribe(() => sql.InfoMessage -= Handler);
    }

    public override async Task<DatabasesResult> DatabasesAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        var rows = await RowsAsync(connection, "SELECT name FROM sys.databases WHERE HAS_DBACCESS(name) = 1 ORDER BY name", cancellationToken).ConfigureAwait(false);
        return new DatabasesResult([.. rows.Select(r => Text(r[0]))], Text(await ScalarAsync(connection, "SELECT DB_NAME()", cancellationToken).ConfigureAwait(false)));
    }

    public override async Task<SchemaResult> SchemaAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        var objects = await RowsAsync(connection, """
            SELECT TABLE_SCHEMA, TABLE_NAME, CASE TABLE_TYPE WHEN 'VIEW' THEN 'view' ELSE 'table' END FROM INFORMATION_SCHEMA.TABLES
            UNION ALL
            SELECT ROUTINE_SCHEMA, ROUTINE_NAME, CASE ROUTINE_TYPE WHEN 'PROCEDURE' THEN 'procedure' ELSE 'function' END FROM INFORMATION_SCHEMA.ROUTINES
            """, cancellationToken).ConfigureAwait(false);
        var columns = (await RowsAsync(connection, """
            SELECT c.TABLE_SCHEMA, c.TABLE_NAME, c.COLUMN_NAME, c.DATA_TYPE, CASE c.IS_NULLABLE WHEN 'YES' THEN 1 ELSE 0 END, c.COLUMN_DEFAULT,
                   COLUMNPROPERTY(OBJECT_ID(QUOTENAME(c.TABLE_SCHEMA) + '.' + QUOTENAME(c.TABLE_NAME)), c.COLUMN_NAME, 'IsIdentity'),
                   c.CHARACTER_MAXIMUM_LENGTH, c.NUMERIC_PRECISION, c.NUMERIC_SCALE
            FROM INFORMATION_SCHEMA.COLUMNS c ORDER BY c.TABLE_SCHEMA, c.TABLE_NAME, c.ORDINAL_POSITION
            """, cancellationToken).ConfigureAwait(false))
            .Select(c => new[] { c[0], c[1], c[2], TypeName(c[3], c[7], c[8], c[9]), c[4], c[5], c[6] }).ToList();
        var constraints = await RowsAsync(connection, """
            SELECT tc.TABLE_SCHEMA, tc.TABLE_NAME, tc.CONSTRAINT_NAME, tc.CONSTRAINT_TYPE, kcu.COLUMN_NAME, kcu.ORDINAL_POSITION,
                   ruk.TABLE_SCHEMA, ruk.TABLE_NAME, ruk.COLUMN_NAME
            FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
            JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE kcu ON kcu.CONSTRAINT_SCHEMA = tc.CONSTRAINT_SCHEMA AND kcu.CONSTRAINT_NAME = tc.CONSTRAINT_NAME
            LEFT JOIN INFORMATION_SCHEMA.REFERENTIAL_CONSTRAINTS rc ON rc.CONSTRAINT_SCHEMA = tc.CONSTRAINT_SCHEMA AND rc.CONSTRAINT_NAME = tc.CONSTRAINT_NAME
            LEFT JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE ruk ON ruk.CONSTRAINT_SCHEMA = rc.UNIQUE_CONSTRAINT_SCHEMA AND ruk.CONSTRAINT_NAME = rc.UNIQUE_CONSTRAINT_NAME AND ruk.ORDINAL_POSITION = kcu.ORDINAL_POSITION
            WHERE tc.CONSTRAINT_TYPE IN ('PRIMARY KEY', 'FOREIGN KEY', 'UNIQUE')
            """, cancellationToken).ConfigureAwait(false);
        var indexes = await RowsAsync(connection, """
            SELECT s.name, t.name, i.name, i.is_unique, c.name, ic.key_ordinal
            FROM sys.indexes i
            JOIN sys.objects t ON t.object_id = i.object_id AND t.type IN ('U', 'V')
            JOIN sys.schemas s ON s.schema_id = t.schema_id
            JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 0
            JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE i.type > 0 AND t.is_ms_shipped = 0
            """, cancellationToken).ConfigureAwait(false);
        var schema = Text(await ScalarAsync(connection, "SELECT SCHEMA_NAME()", cancellationToken).ConfigureAwait(false));
        return new SchemaResult(connection.Database, schema, Assemble(objects, columns, constraints, indexes));
    }

    public override async Task<(PlanInfo Plan, List<ResultSetInfo>? Results)> PlanAsync(DbConnection connection, string text, bool actual, Func<DbCommand, Task<List<ResultSetInfo>>> read, CancellationToken cancellationToken)
    {
        var xmls = new List<string>();
        List<ResultSetInfo>? results = null;
        await ExecuteAsync(connection, actual ? "SET STATISTICS XML ON" : "SET SHOWPLAN_XML ON", cancellationToken).ConfigureAwait(false);
        try
        {
            results = [];
            foreach (var batch in Batches(text))
            {
                await using var command = Command(connection, batch);
                foreach (var set in await read(command).ConfigureAwait(false))
                {
                    if (set.Columns is [{ Name: ShowplanColumn }])
                    {
                        xmls.AddRange(set.Rows.Select(r => r[0]).OfType<string>());
                    }
                    else
                    {
                        results.Add(set);
                    }
                }
            }
        }
        finally
        {
            await ExecuteAsync(connection, actual ? "SET STATISTICS XML OFF" : "SET SHOWPLAN_XML OFF", CancellationToken.None).ConfigureAwait(false);
        }

        return (new PlanInfo("xml", string.Join("\n", xmls), Plans.SqlServer(xmls), actual), actual ? results : null);
    }

    private sealed class Unsubscribe(Action action) : IDisposable
    {
        public void Dispose() => action();
    }
}
