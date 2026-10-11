using System.Data.Common;
using DaedalusStudio.Data.Protocol;
using MySqlConnector;

namespace DaedalusStudio.Data.Host.Engines;

/// <summary>MySQL and MariaDB through MySqlConnector. A database is a schema; objects have no schema of their own.</summary>
internal sealed class MySqlEngine : DbEngine
{
    public override string Id => DataEngines.MySql;

    public override DbConnection Create(string connectionString, string? database)
    {
        var builder = new MySqlConnectionStringBuilder(connectionString);
        if (database is { Length: > 0 })
        {
            builder.Database = database;
        }

        return new MySqlConnection(builder.ConnectionString);
    }

    public override string Quote(string identifier) => "`" + identifier.Replace("`", "``", StringComparison.Ordinal) + "`";

    public override IDisposable? Listen(DbConnection connection, Action<QueryMessage> message)
    {
        var mysql = (MySqlConnection)connection;
        void Handler(object sender, MySqlInfoMessageEventArgs e)
        {
            foreach (var error in e.Errors)
            {
                message(new QueryMessage(error.Level.Equals("Error", StringComparison.OrdinalIgnoreCase) ? "error" : error.Level.Equals("Warning", StringComparison.OrdinalIgnoreCase) ? "warning" : "info", error.Message));
            }
        }

        mysql.InfoMessage += Handler;
        return new Unsubscribe(() => mysql.InfoMessage -= Handler);
    }

    public override async Task<DatabasesResult> DatabasesAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        var rows = await RowsAsync(connection, "SELECT SCHEMA_NAME FROM information_schema.SCHEMATA WHERE SCHEMA_NAME NOT IN ('mysql', 'information_schema', 'performance_schema', 'sys') ORDER BY SCHEMA_NAME", cancellationToken).ConfigureAwait(false);
        return new DatabasesResult([.. rows.Select(r => Text(r[0]))], connection.Database);
    }

    public override async Task<SchemaResult> SchemaAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        // Objects of the current database; the schema column is left empty so names stay unqualified.
        var objects = await RowsAsync(connection, """
            SELECT '', TABLE_NAME, CASE TABLE_TYPE WHEN 'VIEW' THEN 'view' ELSE 'table' END FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE()
            UNION ALL
            SELECT '', ROUTINE_NAME, CASE ROUTINE_TYPE WHEN 'PROCEDURE' THEN 'procedure' ELSE 'function' END FROM information_schema.ROUTINES WHERE ROUTINE_SCHEMA = DATABASE()
            """, cancellationToken).ConfigureAwait(false);
        var columns = await RowsAsync(connection, """
            SELECT '', TABLE_NAME, COLUMN_NAME, COLUMN_TYPE, CASE IS_NULLABLE WHEN 'YES' THEN 1 ELSE 0 END, COLUMN_DEFAULT,
                   CASE WHEN EXTRA LIKE '%auto_increment%' THEN 1 ELSE 0 END
            FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE() ORDER BY TABLE_NAME, ORDINAL_POSITION
            """, cancellationToken).ConfigureAwait(false);
        var constraints = await RowsAsync(connection, """
            SELECT '', tc.TABLE_NAME, tc.CONSTRAINT_NAME, tc.CONSTRAINT_TYPE, kcu.COLUMN_NAME, kcu.ORDINAL_POSITION,
                   CASE WHEN kcu.REFERENCED_TABLE_SCHEMA = DATABASE() THEN '' ELSE kcu.REFERENCED_TABLE_SCHEMA END, kcu.REFERENCED_TABLE_NAME, kcu.REFERENCED_COLUMN_NAME
            FROM information_schema.TABLE_CONSTRAINTS tc
            JOIN information_schema.KEY_COLUMN_USAGE kcu ON kcu.CONSTRAINT_SCHEMA = tc.CONSTRAINT_SCHEMA AND kcu.CONSTRAINT_NAME = tc.CONSTRAINT_NAME AND kcu.TABLE_NAME = tc.TABLE_NAME
            WHERE tc.TABLE_SCHEMA = DATABASE() AND tc.CONSTRAINT_TYPE IN ('PRIMARY KEY', 'FOREIGN KEY', 'UNIQUE')
            """, cancellationToken).ConfigureAwait(false);
        var indexes = await RowsAsync(connection, """
            SELECT '', TABLE_NAME, INDEX_NAME, CASE NON_UNIQUE WHEN 0 THEN 1 ELSE 0 END, COLUMN_NAME, SEQ_IN_INDEX
            FROM information_schema.STATISTICS WHERE TABLE_SCHEMA = DATABASE()
            """, cancellationToken).ConfigureAwait(false);
        return new SchemaResult(connection.Database, null, Assemble(objects, columns, constraints, indexes));
    }

    public override async Task<(PlanInfo Plan, List<ResultSetInfo>? Results)> PlanAsync(DbConnection connection, string text, bool actual, Func<DbCommand, Task<List<ResultSetInfo>>> read, CancellationToken cancellationToken)
    {
        var statement = text.Trim().TrimEnd(';');
        var mariaDb = connection.ServerVersion.Contains("MariaDB", StringComparison.OrdinalIgnoreCase);
        if (actual && !mariaDb)
        {
            // MySQL 8.0.18+: EXPLAIN ANALYZE prints a tree.
            var tree = string.Join('\n', (await RowsAsync(connection, "EXPLAIN ANALYZE " + statement, cancellationToken).ConfigureAwait(false)).Select(r => Text(r[0])));
            return (new PlanInfo("text", tree, Plans.MySqlTree(tree), true), null);
        }

        var json = string.Join('\n', (await RowsAsync(connection, (actual ? "ANALYZE FORMAT=JSON " : "EXPLAIN FORMAT=JSON ") + statement, cancellationToken).ConfigureAwait(false)).Select(r => Text(r[0])));
        return (new PlanInfo("json", json, Plans.MySqlJson(json), actual), null);
    }

    private sealed class Unsubscribe(Action action) : IDisposable
    {
        public void Dispose() => action();
    }
}
