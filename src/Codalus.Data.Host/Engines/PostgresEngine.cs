using System.Data.Common;
using Codalus.Data.Protocol;
using Npgsql;

namespace Codalus.Data.Host.Engines;

/// <summary>PostgreSQL through Npgsql. Another database is another connection (PostgreSQL can't switch).</summary>
internal sealed class PostgresEngine : DbEngine
{

    public override string Id => DataEngines.PostgreSql;

    public override DbConnection Create(string connectionString, string? database)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        if (database is { Length: > 0 })
        {
            builder.Database = database;
        }

        return new NpgsqlConnection(builder.ConnectionString);
    }

    public override string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    public override IDisposable? Listen(DbConnection connection, Action<QueryMessage> message)
    {
        var npgsql = (NpgsqlConnection)connection;
        void Handler(object sender, NpgsqlNoticeEventArgs e) =>
            message(new QueryMessage(e.Notice.Severity is "WARNING" ? "warning" : "info", e.Notice.MessageText));
        npgsql.Notice += Handler;
        return new Unsubscribe(() => npgsql.Notice -= Handler);
    }

    /// <summary>User schemas only: no catalogs, TOAST or temporary schemas.</summary>
    private static string Where(string column) => $"{column} NOT IN ('pg_catalog', 'information_schema') AND {column} NOT LIKE 'pg_toast%' AND {column} NOT LIKE 'pg_temp%'";

    public override async Task<DatabasesResult> DatabasesAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        var rows = await RowsAsync(connection, "SELECT datname FROM pg_database WHERE NOT datistemplate AND datallowconn ORDER BY datname", cancellationToken).ConfigureAwait(false);
        return new DatabasesResult([.. rows.Select(r => Text(r[0]))], connection.Database);
    }

    public override async Task<SchemaResult> SchemaAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        var objects = await RowsAsync(connection, $"""
            SELECT table_schema, table_name, CASE table_type WHEN 'VIEW' THEN 'view' ELSE 'table' END FROM information_schema.tables WHERE {Where("table_schema")}
            UNION ALL
            SELECT routine_schema, routine_name, CASE routine_type WHEN 'PROCEDURE' THEN 'procedure' ELSE 'function' END FROM information_schema.routines WHERE {Where("routine_schema")}
            """, cancellationToken).ConfigureAwait(false);
        var columns = (await RowsAsync(connection, $"""
            SELECT table_schema, table_name, column_name, data_type, CASE is_nullable WHEN 'YES' THEN 1 ELSE 0 END, column_default,
                   CASE WHEN is_identity = 'YES' OR column_default LIKE 'nextval(%' THEN 1 ELSE 0 END,
                   character_maximum_length, numeric_precision, numeric_scale
            FROM information_schema.columns WHERE {Where("table_schema")} ORDER BY table_schema, table_name, ordinal_position
            """, cancellationToken).ConfigureAwait(false))
            .Select(c => new[] { c[0], c[1], c[2], TypeName(c[3], c[7], c[8], c[9]), c[4], c[5], c[6] }).ToList();
        var constraints = await RowsAsync(connection, $"""
            SELECT tc.table_schema, tc.table_name, tc.constraint_name, tc.constraint_type, kcu.column_name, kcu.ordinal_position,
                   ruk.table_schema, ruk.table_name, ruk.column_name
            FROM information_schema.table_constraints tc
            JOIN information_schema.key_column_usage kcu ON kcu.constraint_schema = tc.constraint_schema AND kcu.constraint_name = tc.constraint_name AND kcu.table_name = tc.table_name
            LEFT JOIN information_schema.referential_constraints rc ON rc.constraint_schema = tc.constraint_schema AND rc.constraint_name = tc.constraint_name
            LEFT JOIN information_schema.key_column_usage ruk ON ruk.constraint_schema = rc.unique_constraint_schema AND ruk.constraint_name = rc.unique_constraint_name AND ruk.ordinal_position = kcu.position_in_unique_constraint
            WHERE tc.constraint_type IN ('PRIMARY KEY', 'FOREIGN KEY', 'UNIQUE') AND {Where("tc.table_schema")}
            """, cancellationToken).ConfigureAwait(false);
        var indexes = await RowsAsync(connection, $"""
            SELECT n.nspname, t.relname, i.relname, ix.indisunique, a.attname, k.ord
            FROM pg_index ix
            JOIN pg_class i ON i.oid = ix.indexrelid
            JOIN pg_class t ON t.oid = ix.indrelid
            JOIN pg_namespace n ON n.oid = t.relnamespace
            CROSS JOIN LATERAL unnest(ix.indkey) WITH ORDINALITY AS k(attnum, ord)
            JOIN pg_attribute a ON a.attrelid = t.oid AND a.attnum = k.attnum
            WHERE {Where("n.nspname")}
            """, cancellationToken).ConfigureAwait(false);
        var schema = Text(await ScalarAsync(connection, "SELECT current_schema()", cancellationToken).ConfigureAwait(false));
        return new SchemaResult(connection.Database, schema, Assemble(objects, columns, constraints, indexes));
    }

    public override async Task<(PlanInfo Plan, List<ResultSetInfo>? Results)> PlanAsync(DbConnection connection, string text, bool actual, Func<DbCommand, Task<List<ResultSetInfo>>> read, CancellationToken cancellationToken)
    {
        var json = Text(await ScalarAsync(connection, (actual ? "EXPLAIN (ANALYZE, FORMAT JSON) " : "EXPLAIN (FORMAT JSON) ") + text.Trim().TrimEnd(';'), cancellationToken).ConfigureAwait(false));
        return (new PlanInfo("json", json, Plans.Postgres(json), actual), null);
    }

    private sealed class Unsubscribe(Action action) : IDisposable
    {
        public void Dispose() => action();
    }
}
