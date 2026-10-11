using System.Data.Common;
using System.Text;
using DaedalusStudio.Data.Protocol;

namespace DaedalusStudio.Data.Host.Engines;

/// <summary>
/// What differs between the engines (ADR 0037): opening a connection on a database, the catalog queries, identifier
/// quoting, paging, and plans. Everything else is ADO.NET.
/// </summary>
internal abstract class DbEngine
{
    public abstract string Id { get; }

    public static DbEngine For(string engine) => engine switch
    {
        DataEngines.Sqlite => new SqliteEngine(),
        DataEngines.SqlServer => new SqlServerEngine(),
        DataEngines.PostgreSql => new PostgresEngine(),
        DataEngines.MySql => new MySqlEngine(),
        _ => throw new ArgumentException($"Unknown database engine '{engine}'."),
    };

    /// <summary>A connection (not open) to <paramref name="database"/>, or to the connection string's own database.</summary>
    public abstract DbConnection Create(string connectionString, string? database);

    public async Task<DbConnection> OpenAsync(string connectionString, string? database, CancellationToken cancellationToken)
    {
        var connection = Create(connectionString, database);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public abstract string Quote(string identifier);

    public string Qualified(DbTableRef table) => table.Schema is { Length: > 0 } schema ? Quote(schema) + "." + Quote(table.Name) : Quote(table.Name);

    /// <summary>A page query: <c>SELECT *</c> with the filter, the order and the page.</summary>
    public virtual string Page(DbTableRef table, string? filter, IReadOnlyList<string> order, bool descending, int offset, int count)
    {
        var sql = new StringBuilder("SELECT * FROM ").Append(Qualified(table));
        if (filter is { Length: > 0 })
        {
            sql.Append(" WHERE ").Append(filter);
        }

        if (order.Count > 0)
        {
            sql.Append(" ORDER BY ").Append(string.Join(", ", order.Select(c => Quote(c) + (descending ? " DESC" : string.Empty))));
        }

        return sql.Append(" LIMIT ").Append(count).Append(" OFFSET ").Append(offset).ToString();
    }

    /// <summary>The parameter placeholder for a name (<c>@p0</c>).</summary>
    public virtual string Parameter(string name) => "@" + name;

    public abstract Task<DatabasesResult> DatabasesAsync(DbConnection connection, CancellationToken cancellationToken);

    public abstract Task<SchemaResult> SchemaAsync(DbConnection connection, CancellationToken cancellationToken);

    /// <summary>SQL Server splits batches on <c>GO</c>; the others run the text as one command.</summary>
    public virtual IReadOnlyList<string> Batches(string text) => [text];

    /// <summary>
    /// Interrupts the connection's running statement when the token is cancelled, for providers whose commands don't
    /// watch the token themselves (SQLite).
    /// </summary>
    public virtual IDisposable? OnCancel(DbConnection connection, CancellationToken cancellationToken) => null;

    /// <summary>The table's columns, key columns and .NET types, from an empty <c>SELECT</c> with key information.</summary>
    public virtual async Task<(ResultColumn[] Columns, string[] Keys, Dictionary<string, Type> Types)> ShapeAsync(DbConnection connection, DbTableRef table, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = this is SqlServerEngine ? "SELECT TOP (0) * FROM " + Qualified(table) : "SELECT * FROM " + Qualified(table) + " LIMIT 0";
        await using var reader = await command.ExecuteReaderAsync(System.Data.CommandBehavior.KeyInfo | System.Data.CommandBehavior.SchemaOnly, cancellationToken).ConfigureAwait(false);
        var columns = new List<ResultColumn>();
        var keys = new List<string>();
        var types = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);
        var schema = await reader.GetColumnSchemaAsync(cancellationToken).ConfigureAwait(false);
        foreach (var column in schema)
        {
            columns.Add(new ResultColumn(column.ColumnName, column.DataTypeName ?? column.DataType?.Name ?? "?", column.AllowDBNull));
            types[column.ColumnName] = ValueType(column);
            if (column.IsKey == true)
            {
                keys.Add(column.ColumnName);
            }
        }

        return ([.. columns], [.. keys], types);
    }

    /// <summary>The .NET type a column's values are converted to before they are sent (edits and imports).</summary>
    public virtual Type ValueType(System.Data.Common.DbColumn column) => column.DataType ?? typeof(string);

    /// <summary>Reports the engine's messages (PRINT, notices) while a command runs.</summary>
    public virtual IDisposable? Listen(DbConnection connection, Action<QueryMessage> message) => null;

    /// <summary>
    /// The plan of <paramref name="text"/>: estimated (nothing runs) or actual (the statements run). <c>Results</c> are the
    /// result sets of an actual run when the engine returns them with the plan (SQL Server), else null.
    /// <paramref name="read"/> runs a command and reads its result sets the way <c>data/execute</c> does.
    /// </summary>
    public abstract Task<(PlanInfo Plan, List<ResultSetInfo>? Results)> PlanAsync(DbConnection connection, string text, bool actual, Func<DbCommand, Task<List<ResultSetInfo>>> read, CancellationToken cancellationToken);

    protected static DbCommand Command(DbConnection connection, string text)
    {
        var command = connection.CreateCommand();
        command.CommandText = text;
        command.CommandTimeout = 0;
        return command;
    }

    protected static async Task ExecuteAsync(DbConnection connection, string text, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, text);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ catalog helpers

    protected static async Task<List<object?[]>> RowsAsync(DbConnection connection, string sql, CancellationToken cancellationToken, params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }

        var rows = new List<object?[]>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var row = new object?[reader.FieldCount];
            for (var i = 0; i < row.Length; i++)
            {
                row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }

            rows.Add(row);
        }

        return rows;
    }

    protected static async Task<object?> ScalarAsync(DbConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is DBNull ? null : value;
    }

    protected static string Text(object? value) => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;

    protected static bool Truthy(object? value) => value switch
    {
        bool b => b,
        null => false,
        string s => s is "1" or "YES" or "yes" or "true" or "True" or "t",
        _ => Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture) != 0,
    };

    /// <summary>
    /// Builds objects from the <c>INFORMATION_SCHEMA</c>-shaped rows the server engines share: objects
    /// (schema, name, kind), columns (schema, table, name, type, nullable, default, identity), constraint columns
    /// (schema, table, name, kind, column, position, referenced schema, table, column), and index columns (schema, table,
    /// name, unique, column, position).
    /// </summary>
    protected static DbObjectInfo[] Assemble(
        IEnumerable<object?[]> objects,
        IEnumerable<object?[]> columns,
        IEnumerable<object?[]> constraints,
        IEnumerable<object?[]> indexes)
    {
        static (string, string) Key(object? schema, object? name) => (Text(schema), Text(name));
        var columnsByObject = columns.GroupBy(c => Key(c[0], c[1])).ToDictionary(g => g.Key, g => g.ToList());
        var keysByObject = constraints.GroupBy(c => Key(c[0], c[1])).ToDictionary(
            g => g.Key,
            g => g.GroupBy(c => (Name: Text(c[2]), Kind: Text(c[3])))
                .Select(k =>
                {
                    var ordered = k.OrderBy(c => Convert.ToInt32(c[5] ?? 0, System.Globalization.CultureInfo.InvariantCulture)).ToList();
                    var kind = k.Key.Kind switch
                    {
                        "PRIMARY KEY" => "primary",
                        "FOREIGN KEY" => "foreign",
                        _ => "unique",
                    };
                    return kind == "foreign"
                        ? new DbKeyInfo(k.Key.Name, kind, [.. ordered.Select(c => Text(c[4]))], NullIfEmpty(Text(ordered[0][6])), NullIfEmpty(Text(ordered[0][7])), [.. ordered.Select(c => Text(c[8]))])
                        : new DbKeyInfo(k.Key.Name, kind, [.. ordered.Select(c => Text(c[4]))]);
                })
                .ToArray());
        var indexesByObject = indexes.GroupBy(i => Key(i[0], i[1])).ToDictionary(
            g => g.Key,
            g => g.GroupBy(i => Text(i[2])).Select(i => new DbIndexInfo(i.Key, [.. i.OrderBy(c => Convert.ToInt32(c[5] ?? 0, System.Globalization.CultureInfo.InvariantCulture)).Select(c => Text(c[4]))], Truthy(i.First()[3]))).ToArray());

        return [.. objects.Select(o =>
        {
            var key = Key(o[0], o[1]);
            var keys = keysByObject.GetValueOrDefault(key) ?? [];
            var primary = keys.FirstOrDefault(k => k.Kind == "primary")?.Columns ?? [];
            var cols = (columnsByObject.GetValueOrDefault(key) ?? [])
                .Select(c => new DbColumnInfo(Text(c[2]), Text(c[3]), Truthy(c[4]), primary.Contains(Text(c[2]), StringComparer.Ordinal), Truthy(c[6]), c[5] is null ? null : Text(c[5])))
                .ToArray();
            return new DbObjectInfo(NullIfEmpty(Text(o[0])), Text(o[1]), Text(o[2]), cols, keys, indexesByObject.GetValueOrDefault(key) ?? []);
        }).OrderBy(o => o.Kind is DbObjectKinds.Table or DbObjectKinds.View ? 0 : 1).ThenBy(o => o.Schema, StringComparer.OrdinalIgnoreCase).ThenBy(o => o.Name, StringComparer.OrdinalIgnoreCase)];
    }

    protected static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;

    /// <summary>A type name with its size: <c>varchar(50)</c>, <c>decimal(18,2)</c>.</summary>
    protected static string TypeName(object? type, object? length, object? precision, object? scale)
    {
        var name = Text(type);
        if (length is not null && Convert.ToInt64(length, System.Globalization.CultureInfo.InvariantCulture) is var size and (> 0 or -1))
        {
            return $"{name}({(size == -1 ? "max" : size.ToString(System.Globalization.CultureInfo.InvariantCulture))})";
        }

        if (name is "decimal" or "numeric" && precision is not null)
        {
            return $"{name}({Text(precision)},{Text(scale ?? 0)})";
        }

        return name;
    }
}
