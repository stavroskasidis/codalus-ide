using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Text.Json;
using DaedalusStudio.Data.Host.Engines;
using DaedalusStudio.Data.Protocol;
using DaedalusStudio.Rpc;

namespace DaedalusStudio.Data.Host;

/// <summary>
/// The data host's request handlers (ADR 0037, docs/data-protocol.md). Connections are registered by id; each request
/// opens a pooled connection, except query sessions, which keep theirs. Cancelling a request cancels its command.
/// </summary>
internal sealed class DataServer : IAsyncDisposable
{
    private readonly JsonRpcConnection _connection;
    private readonly TaskCompletionSource _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentDictionary<string, (DbEngine Engine, string ConnectionString)> _connections = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<(string Id, string Session, string? Database), Session> _sessions = new();

    public DataServer(JsonRpcConnection connection) => _connection = connection;

    public Task ExitRequested => _exit.Task;

    public void Register()
    {
        var json = DataJsonContext.Default;
        _connection.OnRequest(DataMethods.Initialize, json.DataInitializeParams, json.DataInitializeResult, (_, _) => Task.FromResult(new DataInitializeResult(
            "daedalus-datahost",
            typeof(DataServer).Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
            DataMethods.ProtocolVersion,
            [DataEngines.SqlServer, DataEngines.PostgreSql, DataEngines.MySql, DataEngines.Sqlite])));
        _connection.OnRequest(DataMethods.Connect, json.ConnectParams, json.ConnectResult, (p, ct) => Guard(() => ConnectAsync(p, ct)));
        _connection.OnRequest(DataMethods.Disconnect, json.ConnectionIdParams, json.DataEmpty, (p, _) => Guard(() => DisconnectAsync(p.Id)));
        _connection.OnRequest(DataMethods.Test, json.TestParams, json.TestResult, (p, ct) => Task.Run(() => TestAsync(p, ct), ct));
        _connection.OnRequest(DataMethods.Databases, json.ConnectionIdParams, json.DatabasesResult, (p, ct) => Guard(() => WithConnectionAsync(p.Id, null, (e, c) => e.DatabasesAsync(c, ct), ct)));
        _connection.OnRequest(DataMethods.Schema, json.SchemaParams, json.SchemaResult, (p, ct) => Guard(() => WithConnectionAsync(p.Id, p.Database, (e, c) => e.SchemaAsync(c, ct), ct)));
        _connection.OnRequest(DataMethods.Read, json.ReadParams, json.ReadResult, (p, ct) => Guard(() => ReadAsync(p, ct)));
        _connection.OnRequest(DataMethods.Count, json.CountParams, json.CountResult, (p, ct) => Guard(() => CountAsync(p, ct)));
        _connection.OnRequest(DataMethods.Commit, json.CommitParams, json.CommitResult, (p, ct) => Guard(() => CommitAsync(p, ct)));
        _connection.OnRequest(DataMethods.Execute, json.ExecuteParams, json.ExecuteResult, (p, ct) => Guard(() => ExecuteAsync(p, ct)));
        _connection.OnRequest(DataMethods.CloseSession, json.SessionParams, json.DataEmpty, (p, _) => Guard(() => CloseSessionAsync(p.Id, p.Session)));
        _connection.OnRequest(DataMethods.Export, json.ExportParams, json.TransferResult, (p, ct) => Guard(() => Transfer.ExportAsync(this, p, ct)));
        _connection.OnRequest(DataMethods.Import, json.ImportParams, json.TransferResult, (p, ct) => Guard(() => Transfer.ImportAsync(this, p, ct)));
        _connection.OnRequest(DataMethods.Shutdown, (_, _) => Task.FromResult<JsonElement?>(null));
        _connection.OnNotification(DataMethods.Exit, json.DataEmpty, _ => _exit.TrySetResult());
    }

    /// <summary>
    /// Runs a handler on the thread pool (providers such as SQLite run synchronously, and the connection starts handlers
    /// in arrival order: a long query must not hold the others or its own cancellation), and maps database errors to
    /// <see cref="DataErrorCodes.DatabaseError"/> with the database's message.
    /// </summary>
    private static async Task<T> Guard<T>(Func<Task<T>> action)
    {
        try
        {
            return await Task.Run(action).ConfigureAwait(false);
        }
        catch (DbException ex)
        {
            throw new JsonRpcHandlerException(DataErrorCodes.DatabaseError, ex.Message);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            throw new JsonRpcHandlerException(DataErrorCodes.DatabaseError, ex.Message);
        }
        catch (NotSupportedException ex)
        {
            throw new JsonRpcHandlerException(DataErrorCodes.NotSupported, ex.Message);
        }
    }

    // ------------------------------------------------------------------ connections

    private async Task<ConnectResult> ConnectAsync(ConnectParams parameters, CancellationToken cancellationToken)
    {
        var engine = DbEngine.For(parameters.Engine);
        await using var connection = await engine.OpenAsync(parameters.ConnectionString, null, cancellationToken).ConfigureAwait(false);
        await DisconnectAsync(parameters.Id).ConfigureAwait(false);
        _connections[parameters.Id] = (engine, parameters.ConnectionString);
        var schema = engine is SqlServerEngine ? "dbo" : engine is PostgresEngine ? "public" : null;
        return new ConnectResult(connection.ServerVersion, connection.Database is { Length: > 0 } database ? database : null, schema);
    }

    private async Task<DataEmpty> DisconnectAsync(string id)
    {
        var sqlite = _connections.TryRemove(id, out var removed) && removed.Engine is SqliteEngine;

        foreach (var key in _sessions.Keys.Where(k => k.Id == id).ToList())
        {
            if (_sessions.TryRemove(key, out var session))
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
        }

        if (sqlite)
        {
            // Pooled SQLite connections keep the file open: release it (the user may move or delete it).
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        }

        return new DataEmpty();
    }

    private static async Task<TestResult> TestAsync(TestParams parameters, CancellationToken cancellationToken)
    {
        try
        {
            var engine = DbEngine.For(parameters.Engine);
            await using var connection = await engine.OpenAsync(parameters.ConnectionString, null, cancellationToken).ConfigureAwait(false);
            return new TestResult(true, connection.ServerVersion, null);
        }
        catch (Exception ex) when (ex is DbException or ArgumentException or InvalidOperationException or IOException or System.Net.Sockets.SocketException or TimeoutException)
        {
            return new TestResult(false, null, ex.Message);
        }
    }

    public (DbEngine Engine, string ConnectionString) Registered(string id) =>
        _connections.TryGetValue(id, out var registered) ? registered : throw new JsonRpcHandlerException(DataErrorCodes.UnknownConnection, "The connection isn't open. Connect again.");

    public async Task<DbConnection> OpenAsync(string id, string? database, CancellationToken cancellationToken)
    {
        var (engine, connectionString) = Registered(id);
        return await engine.OpenAsync(connectionString, database, cancellationToken).ConfigureAwait(false);
    }

    private async Task<T> WithConnectionAsync<T>(string id, string? database, Func<DbEngine, DbConnection, Task<T>> action, CancellationToken cancellationToken)
    {
        var engine = Registered(id).Engine;
        await using var connection = await OpenAsync(id, database, cancellationToken).ConfigureAwait(false);
        return await action(engine, connection).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ table data

    public static Task<(ResultColumn[] Columns, string[] Keys, Dictionary<string, Type> Types)> ShapeAsync(DbEngine engine, DbConnection connection, DbTableRef table, CancellationToken cancellationToken) =>
        engine.ShapeAsync(connection, table, cancellationToken);

    private async Task<ReadResult> ReadAsync(ReadParams parameters, CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        var engine = Registered(parameters.Id).Engine;
        await using var connection = await OpenAsync(parameters.Id, parameters.Database, cancellationToken).ConfigureAwait(false);
        var (columns, keys, _) = await ShapeAsync(engine, connection, parameters.Table, cancellationToken).ConfigureAwait(false);
        var order = parameters.OrderBy is { Length: > 0 } orderBy ? [orderBy] : keys.Length > 0 ? keys : (IReadOnlyList<string>)[];
        await using var command = connection.CreateCommand();
        command.CommandText = engine.Page(parameters.Table, parameters.Filter, order, parameters.Descending, Math.Max(0, parameters.Offset), Math.Clamp(parameters.Count, 1, 100_000));
        var rows = new List<string?[]>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows.Add(Row(reader, columns.Length));
            }
        }

        var view = await IsViewAsync(engine, connection, parameters.Table, cancellationToken).ConfigureAwait(false);
        var readOnly = view || keys.Length == 0;
        return new ReadResult(columns, [.. rows], keys, readOnly, view ? "Views are read-only here." : keys.Length == 0 ? "The table has no primary key, so its rows can't be identified for edits." : null, watch.Elapsed.TotalMilliseconds);
    }

    private static async Task<bool> IsViewAsync(DbEngine engine, DbConnection connection, DbTableRef table, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        if (engine is SqliteEngine)
        {
            command.CommandText = "SELECT type FROM sqlite_schema WHERE name = @n";
        }
        else
        {
            command.CommandText = engine is MySqlEngine
                ? "SELECT TABLE_TYPE FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @n"
                : "SELECT TABLE_TYPE FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = @n" + (table.Schema is { Length: > 0 } ? " AND TABLE_SCHEMA = @s" : string.Empty);
            if (table.Schema is { Length: > 0 } schema && engine is not MySqlEngine)
            {
                var s = command.CreateParameter();
                s.ParameterName = "@s";
                s.Value = schema;
                command.Parameters.Add(s);
            }
        }

        var n = command.CreateParameter();
        n.ParameterName = "@n";
        n.Value = table.Name;
        command.Parameters.Add(n);
        var type = Convert.ToString(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
        return type is "view" or "VIEW";
    }

    public static string?[] Row(DbDataReader reader, int count)
    {
        var row = new string?[count];
        for (var i = 0; i < count; i++)
        {
            row[i] = reader.IsDBNull(i) ? null : Values.Format(reader.GetValue(i));
        }

        return row;
    }

    private async Task<CountResult> CountAsync(CountParams parameters, CancellationToken cancellationToken)
    {
        var engine = Registered(parameters.Id).Engine;
        await using var connection = await OpenAsync(parameters.Id, parameters.Database, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM " + engine.Qualified(parameters.Table) + (parameters.Filter is { Length: > 0 } filter ? " WHERE " + filter : string.Empty);
        return new CountResult(Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>Applies the changes in one transaction; an update or delete that matches no row undoes them all.</summary>
    private async Task<CommitResult> CommitAsync(CommitParams parameters, CancellationToken cancellationToken)
    {
        var engine = Registered(parameters.Id).Engine;
        await using var connection = await OpenAsync(parameters.Id, parameters.Database, cancellationToken).ConfigureAwait(false);
        var (_, keys, types) = await ShapeAsync(engine, connection, parameters.Table, cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var affected = 0;
        foreach (var change in parameters.Changes)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            var index = 0;
            string Add(string column, string? value)
            {
                var parameter = command.CreateParameter();
                parameter.ParameterName = engine.Parameter("p" + index++.ToString(System.Globalization.CultureInfo.InvariantCulture));
                parameter.Value = Values.Parse(value, types.GetValueOrDefault(column) ?? typeof(string));
                command.Parameters.Add(parameter);
                return parameter.ParameterName;
            }

            string Where()
            {
                if (keys.Length == 0 || change.Key is null)
                {
                    throw new NotSupportedException("The table has no primary key, so its rows can't be identified for edits.");
                }

                return string.Join(" AND ", keys.Select(k => change.Key.TryGetValue(k, out var value) && value is not null
                    ? engine.Quote(k) + " = " + Add(k, value)
                    : engine.Quote(k) + " IS NULL"));
            }

            switch (change.Kind)
            {
                case RowChangeKinds.Insert:
                    var values = change.Values ?? [];
                    command.CommandText = values.Count == 0
                        ? $"INSERT INTO {engine.Qualified(parameters.Table)} DEFAULT VALUES"
                        : $"INSERT INTO {engine.Qualified(parameters.Table)} ({string.Join(", ", values.Keys.Select(engine.Quote))}) VALUES ({string.Join(", ", values.Select(v => Add(v.Key, v.Value)))})";
                    break;
                case RowChangeKinds.Update when change.Values is { Count: > 0 } updated:
                    var set = string.Join(", ", updated.Select(v => engine.Quote(v.Key) + " = " + Add(v.Key, v.Value)));
                    command.CommandText = $"UPDATE {engine.Qualified(parameters.Table)} SET {set} WHERE {Where()}";
                    break;
                case RowChangeKinds.Update:
                    continue;
                case RowChangeKinds.Delete:
                    command.CommandText = $"DELETE FROM {engine.Qualified(parameters.Table)} WHERE {Where()}";
                    break;
                default:
                    throw new ArgumentException($"Unknown change '{change.Kind}'.");
            }

            var count = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (change.Kind != RowChangeKinds.Insert && count == 0)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                throw new JsonRpcHandlerException(DataErrorCodes.Conflict, "A row was changed or deleted by someone else since it was read. Nothing was saved: refresh and edit again.");
            }

            affected += count;
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new CommitResult(affected);
    }

    // ------------------------------------------------------------------ queries

    private async Task<ExecuteResult> ExecuteAsync(ExecuteParams parameters, CancellationToken cancellationToken)
    {
        var engine = Registered(parameters.Id).Engine;
        var messages = new List<QueryMessage>();
        var results = new List<ResultSetInfo>();
        PlanInfo? plan = null;
        var session = parameters.Session is { Length: > 0 } name
            ? await SessionAsync(parameters.Id, name, parameters.Database, cancellationToken).ConfigureAwait(false)
            : new Session(await OpenAsync(parameters.Id, parameters.Database, cancellationToken).ConfigureAwait(false), owned: true);
        await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var watch = Stopwatch.StartNew();
        try
        {
            using var listener = engine.Listen(session.Connection, m =>
            {
                lock (messages)
                {
                    messages.Add(m);
                }
            });
            using var interrupt = engine.OnCancel(session.Connection, cancellationToken);
            Task<List<ResultSetInfo>> Read(DbCommand command) => ReadAllAsync(command, parameters.MaxRows, cancellationToken);
            if (parameters.Plan is PlanKinds.Estimated or PlanKinds.Actual)
            {
                var (found, sets) = await engine.PlanAsync(session.Connection, parameters.Text, parameters.Plan == PlanKinds.Actual, Read, cancellationToken).ConfigureAwait(false);
                plan = found;
                results.AddRange(sets ?? []);
            }
            else
            {
                foreach (var batch in engine.Batches(parameters.Text))
                {
                    await using var command = session.Connection.CreateCommand();
                    command.CommandText = batch;
                    command.CommandTimeout = 0;
                    try
                    {
                        results.AddRange(await Read(command).ConfigureAwait(false));
                    }
                    catch (DbException ex) when (!cancellationToken.IsCancellationRequested)
                    {
                        lock (messages)
                        {
                            messages.Add(new QueryMessage("error", ex.Message, ex is Microsoft.Data.SqlClient.SqlException sql && sql.LineNumber > 0 ? sql.LineNumber : null));
                        }

                        if (engine is not SqlServerEngine)
                        {
                            break;
                        }
                    }
                }
            }
        }
        finally
        {
            session.Gate.Release();
            if (session.Owned)
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
        }

        var elapsed = watch.Elapsed.TotalMilliseconds;
        foreach (var set in results.Where(r => r.Columns.Length == 0 && r.RecordsAffected >= 0))
        {
            messages.Add(new QueryMessage("info", set.RecordsAffected == 1 ? "1 row affected." : $"{set.RecordsAffected} rows affected."));
        }

        QueryMessage[] all;
        lock (messages)
        {
            all = [.. messages];
        }

        return new ExecuteResult([.. results.Where(r => r.Columns.Length > 0)], all, elapsed, plan);
    }

    /// <summary>Every result set of a command, each cut at <paramref name="maxRows"/> rows.</summary>
    public static async Task<List<ResultSetInfo>> ReadAllAsync(DbCommand command, int maxRows, CancellationToken cancellationToken)
    {
        var sets = new List<ResultSetInfo>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        do
        {
            if (reader.FieldCount == 0)
            {
                continue;
            }

            var columns = new ResultColumn[reader.FieldCount];
            for (var i = 0; i < columns.Length; i++)
            {
                columns[i] = new ResultColumn(reader.GetName(i), reader.GetDataTypeName(i));
            }

            var rows = new List<string?[]>();
            var truncated = false;
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (rows.Count >= maxRows)
                {
                    truncated = true;
                    break;
                }

                rows.Add(Row(reader, columns.Length));
            }

            sets.Add(new ResultSetInfo(columns, [.. rows], truncated, -1));
        }
        while (await reader.NextResultAsync(cancellationToken).ConfigureAwait(false));

        // Rows changed by the batch's statements (the providers sum them): one empty set carries the count.
        await reader.CloseAsync().ConfigureAwait(false);
        if (reader.RecordsAffected >= 0)
        {
            sets.Add(new ResultSetInfo([], [], false, reader.RecordsAffected));
        }

        return sets;
    }

    private async Task<Session> SessionAsync(string id, string name, string? database, CancellationToken cancellationToken)
    {
        var key = (id, name, database);
        if (_sessions.TryGetValue(key, out var existing) && existing.Connection.State == ConnectionState.Open)
        {
            return existing;
        }

        var session = new Session(await OpenAsync(id, database, cancellationToken).ConfigureAwait(false), owned: false);
        if (!_sessions.TryAdd(key, session))
        {
            await session.DisposeAsync().ConfigureAwait(false);
            return _sessions[key];
        }

        return session;
    }

    private async Task<DataEmpty> CloseSessionAsync(string id, string name)
    {
        foreach (var key in _sessions.Keys.Where(k => k.Id == id && k.Session == name).ToList())
        {
            if (_sessions.TryRemove(key, out var session))
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
        }

        return new DataEmpty();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var session in _sessions.Values)
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }

        _sessions.Clear();
    }

    /// <summary>A query editor's connection; one execution at a time.</summary>
    private sealed class Session(DbConnection connection, bool owned) : IAsyncDisposable
    {
        public DbConnection Connection { get; } = connection;

        public bool Owned { get; } = owned;

        public SemaphoreSlim Gate { get; } = new(1, 1);

        public async ValueTask DisposeAsync()
        {
            await Connection.DisposeAsync().ConfigureAwait(false);
            Gate.Dispose();
        }
    }
}
