using System.Collections.Concurrent;
using System.Data.Common;
using System.Text.Json;
using System.Text.Json.Serialization;
using DaedalusStudio.Data.Protocol;
using DaedalusStudio.Rpc;
using DaedalusStudio.Workbench.Secrets;

namespace DaedalusStudio.Workbench.Data;

/// <summary>Locates the data host (ADR 0037).</summary>
public static class DataHostLaunch
{
    public const string EnvironmentVariable = "DAEDALUS_DATAHOST";

    /// <summary>Resolution order: <c>DAEDALUS_DATAHOST</c> (an apphost or a .dll), then <c>datahost/</c> next to the shell.</summary>
    public static ProcessLaunchInfo? Resolve(params string[] arguments)
    {
        var candidates = new List<string>();
        if (Environment.GetEnvironmentVariable(EnvironmentVariable) is { Length: > 0 } fromEnv)
        {
            candidates.Add(fromEnv);
        }

        var exe = OperatingSystem.IsWindows() ? "daedalus-datahost.exe" : "daedalus-datahost";
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "datahost", exe));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "datahost", "daedalus-datahost.dll"));
        return candidates.FirstOrDefault(File.Exists) is { } path ? ProcessLaunchInfo.FromPath(path, arguments) : null;
    }

    /// <summary>The SQL language server: the same executable with <c>--lsp</c> (ADR 0038).</summary>
    public static ProcessLaunchInfo? ResolveLanguageServer() => Resolve("--lsp");
}

/// <summary>
/// A database connection. The user's own are kept in the state folder without their password (<see cref="SavePassword"/>:
/// it is in the OS credential store); the solution's (<see cref="Source"/> set: an appsettings file, user secrets or an
/// Aspire AppHost) are read from where they are each time and never stored.
/// </summary>
public sealed record DataConnection(string Id, string Name, string Engine, string ConnectionString, bool SavePassword = false, string? Source = null, string? Project = null)
{
    [JsonIgnore]
    public bool IsDiscovered => Source is not null;

    [JsonIgnore]
    public string Label => Source is null ? Name : $"{Name} ({Source})";

    [JsonIgnore]
    public string Description => DatabaseEngines.DisplayName(Engine) + " · " + ConnectionDiscovery.Mask(ConnectionString);
}

/// <summary>A query run from a query editor, for the history.</summary>
public sealed record QueryHistoryEntry(DateTimeOffset At, string Connection, string Text, double ElapsedMilliseconds, bool Succeeded, int ResultSets);

/// <summary>The connection needs a password that isn't saved; ask the user and call <see cref="DataService.RememberPassword"/>.</summary>
public sealed class DataPasswordRequiredException(DataConnection connection) : Exception($"Enter the password for {connection.Label}.")
{
    public DataConnection Connection { get; } = connection;
}

/// <summary>
/// The database explorer's model (ADR 0037): the connections, and the data host, started on the first request and
/// supervised (after a crash it restarts and the open connections are registered again). Schemas are cached per
/// connection and database until refreshed. UI-agnostic; events arrive on any thread.
/// </summary>
public sealed class DataService : IAsyncDisposable
{
    public const string SecretsOwner = "daedalus.data";
    public const int MaxHistory = 200;

    private static readonly SupervisionPolicy s_policy = new() { MaxRestarts = 5 };
    private static DataJsonContext Json => DataJsonContext.Default;

    private readonly Func<ProcessLaunchInfo?> _launch;
    private readonly string _connectionsFile;
    private readonly string _historyFile;
    private readonly SecretStorage _secrets;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, string> _sessionPasswords = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (string Engine, string ConnectionString)> _connected = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<(string Id, string? Database), SchemaResult> _schemas = new();
    private readonly List<DataConnection> _connections;
    private List<DataConnection> _discovered = [];
    private readonly List<QueryHistoryEntry> _history;
    private SupervisedProcess? _process;

    public DataService(Func<ProcessLaunchInfo?> launch, string stateFolder, SecretStorage secrets)
    {
        _launch = launch;
        _secrets = secrets;
        _connectionsFile = Path.Combine(stateFolder, "data-connections.json");
        _historyFile = Path.Combine(stateFolder, "query-history.json");
        _connections = Load(_connectionsFile, DataStateJsonContext.Default.ListDataConnection) ?? [];
        _history = Load(_historyFile, DataStateJsonContext.Default.ListQueryHistoryEntry) ?? [];
    }

    /// <summary>The connections or their state changed (added, removed, connected, a schema loaded).</summary>
    public event EventHandler? Changed;

    /// <summary>The host restarted after a crash (true) or gave up (false).</summary>
    public event EventHandler<bool>? HostCrashed;

    public event EventHandler<string>? Log;

    public bool IsAvailable => _launch() is not null;

    public int? HostProcessId => _process?.ProcessId;

    /// <summary>The user's connections, then the solution's.</summary>
    public IReadOnlyList<DataConnection> Connections => [.. _connections, .. _discovered];

    public IReadOnlyList<DataConnection> UserConnections => [.. _connections];

    public IReadOnlyList<DataConnection> DiscoveredConnections => [.. _discovered];

    public IReadOnlyList<QueryHistoryEntry> History
    {
        get
        {
            lock (_history)
            {
                return [.. _history];
            }
        }
    }

    public DataConnection? Find(string id) => Connections.FirstOrDefault(c => c.Id == id);

    public bool IsConnected(string id) => _connected.ContainsKey(id);

    public SchemaResult? CachedSchema(string id, string? database = null) => _schemas.GetValueOrDefault((id, database));

    // ------------------------------------------------------------------ the connection list

    /// <summary>The solution's connections (appsettings, user secrets, Aspire), replacing the previous solution's.</summary>
    public void SetDiscovered(IEnumerable<DataConnection> connections)
    {
        _discovered = [.. connections];
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Adds or replaces a user connection. The password is removed from the stored connection string; when
    /// <see cref="DataConnection.SavePassword"/> it goes to the OS credential store, else it is kept for the session.
    /// </summary>
    public async Task SaveAsync(DataConnection connection, string? password, CancellationToken cancellationToken)
    {
        var (stripped, embedded) = SplitPassword(connection.ConnectionString);
        password ??= embedded;
        var stored = connection with { ConnectionString = stripped };
        lock (_connections)
        {
            _connections.RemoveAll(c => c.Id == stored.Id);
            _connections.Add(stored);
        }

        if (password is { Length: > 0 })
        {
            _sessionPasswords[stored.Id] = password;
            if (stored.SavePassword)
            {
                await _secrets.StoreAsync(SecretsOwner, "connection/" + stored.Id, password, cancellationToken).ConfigureAwait(false);
            }
        }

        if (!stored.SavePassword)
        {
            await _secrets.DeleteAsync(SecretsOwner, "connection/" + stored.Id, cancellationToken).ConfigureAwait(false);
        }

        SaveConnections();
        await DisconnectAsync(stored.Id).ConfigureAwait(false);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task RemoveAsync(string id, CancellationToken cancellationToken)
    {
        lock (_connections)
        {
            _connections.RemoveAll(c => c.Id == id);
        }

        _sessionPasswords.TryRemove(id, out _);
        await _secrets.DeleteAsync(SecretsOwner, "connection/" + id, cancellationToken).ConfigureAwait(false);
        SaveConnections();
        await DisconnectAsync(id).ConfigureAwait(false);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>A password the user typed, kept for the session only.</summary>
    public void RememberPassword(string id, string password) => _sessionPasswords[id] = password;

    /// <summary>
    /// The connection string with its password: the solution's as they are; the user's with the saved or remembered
    /// password. Throws <see cref="DataPasswordRequiredException"/> when one is needed and unknown.
    /// </summary>
    public async Task<string> ConnectionStringAsync(DataConnection connection, CancellationToken cancellationToken)
    {
        if (connection.IsDiscovered || !NeedsPassword(connection))
        {
            return connection.ConnectionString;
        }

        var password = _sessionPasswords.GetValueOrDefault(connection.Id)
                       ?? (connection.SavePassword ? await _secrets.GetAsync(SecretsOwner, "connection/" + connection.Id, cancellationToken).ConfigureAwait(false) : null);
        if (password is null)
        {
            throw new DataPasswordRequiredException(connection);
        }

        _sessionPasswords[connection.Id] = password;
        return WithPassword(connection.ConnectionString, password);
    }

    /// <summary>
    /// Whether the connection authenticates with a password: a user name without integrated security (SQLite files
    /// have none, unless they set one).
    /// </summary>
    public static bool NeedsPassword(DataConnection connection)
    {
        var keys = DatabaseEngines.Parse(connection.ConnectionString);
        if (connection.Engine == DataEngines.Sqlite)
        {
            return false;
        }

        var integrated = keys.TryGetValue("integrated security", out var i) && i is "true" or "sspi" or "yes" || keys.TryGetValue("trusted_connection", out var t) && t is "true" or "yes";
        var user = keys.ContainsKey("user id") || keys.ContainsKey("uid") || keys.ContainsKey("username") || keys.ContainsKey("user");
        return user && !integrated;
    }

    /// <summary>The connection string without its password, and the password.</summary>
    public static (string ConnectionString, string? Password) SplitPassword(string connectionString)
    {
        try
        {
            var builder = new DbConnectionStringBuilder { ConnectionString = connectionString };
            string? password = null;
            foreach (var key in new[] { "password", "pwd" })
            {
                if (builder.TryGetValue(key, out var value))
                {
                    password = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
                    builder.Remove(key);
                }
            }

            return (builder.ConnectionString, password);
        }
        catch (ArgumentException)
        {
            return (connectionString, null);
        }
    }

    public static string WithPassword(string connectionString, string password)
    {
        var builder = new DbConnectionStringBuilder { ConnectionString = connectionString };
        builder["Password"] = password;
        return builder.ConnectionString;
    }

    private void SaveConnections()
    {
        lock (_connections)
        {
            Store(_connectionsFile, [.. _connections], DataStateJsonContext.Default.ListDataConnection);
        }
    }

    // ------------------------------------------------------------------ connecting

    /// <summary>Opens the connection in the host (testing it); the server's version and the database.</summary>
    public async Task<ConnectResult> ConnectAsync(DataConnection connection, CancellationToken cancellationToken)
    {
        var connectionString = await ConnectionStringAsync(connection, cancellationToken).ConfigureAwait(false);
        var host = await ConnectionToHostAsync(cancellationToken).ConfigureAwait(false);
        var result = await host.InvokeAsync(DataMethods.Connect, new ConnectParams(connection.Id, connection.Engine, connectionString), Json.ConnectParams, Json.ConnectResult, cancellationToken).ConfigureAwait(false);
        _connected[connection.Id] = (connection.Engine, connectionString);
        Changed?.Invoke(this, EventArgs.Empty);
        return result;
    }

    public async Task DisconnectAsync(string id)
    {
        foreach (var key in _schemas.Keys.Where(k => k.Id == id).ToList())
        {
            _schemas.TryRemove(key, out _);
        }

        if (_connected.TryRemove(id, out _) && _process is { IsRunning: true } process)
        {
            try
            {
                await process.Connection.InvokeAsync(DataMethods.Disconnect, new ConnectionIdParams(id), Json.ConnectionIdParams, Json.DataEmpty, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is JsonRpcConnectionLostException or JsonRpcRemoteException or ObjectDisposedException)
            {
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task<TestResult> TestAsync(string engine, string connectionString, CancellationToken cancellationToken)
    {
        var host = await ConnectionToHostAsync(cancellationToken).ConfigureAwait(false);
        return await host.InvokeAsync(DataMethods.Test, new TestParams(engine, connectionString), Json.TestParams, Json.TestResult, cancellationToken).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ requests

    public Task<DatabasesResult> DatabasesAsync(string id, CancellationToken cancellationToken) =>
        RequestAsync(id, DataMethods.Databases, new ConnectionIdParams(id), Json.ConnectionIdParams, Json.DatabasesResult, cancellationToken);

    /// <summary>A database's objects, cached until <paramref name="refresh"/>.</summary>
    public async Task<SchemaResult> SchemaAsync(string id, string? database, bool refresh, CancellationToken cancellationToken)
    {
        if (!refresh && _schemas.TryGetValue((id, database), out var cached))
        {
            return cached;
        }

        var schema = await RequestAsync(id, DataMethods.Schema, new SchemaParams(id, database), Json.SchemaParams, Json.SchemaResult, cancellationToken).ConfigureAwait(false);
        _schemas[(id, database)] = schema;
        Changed?.Invoke(this, EventArgs.Empty);
        return schema;
    }

    public Task<ReadResult> ReadAsync(ReadParams parameters, CancellationToken cancellationToken) =>
        RequestAsync(parameters.Id, DataMethods.Read, parameters, Json.ReadParams, Json.ReadResult, cancellationToken);

    public Task<CountResult> CountAsync(CountParams parameters, CancellationToken cancellationToken) =>
        RequestAsync(parameters.Id, DataMethods.Count, parameters, Json.CountParams, Json.CountResult, cancellationToken);

    public Task<CommitResult> CommitAsync(CommitParams parameters, CancellationToken cancellationToken) =>
        RequestAsync(parameters.Id, DataMethods.Commit, parameters, Json.CommitParams, Json.CommitResult, cancellationToken);

    public Task<TransferResult> ExportAsync(ExportParams parameters, CancellationToken cancellationToken) =>
        RequestAsync(parameters.Id, DataMethods.Export, parameters, Json.ExportParams, Json.TransferResult, cancellationToken);

    public Task<TransferResult> ImportAsync(ImportParams parameters, CancellationToken cancellationToken) =>
        RequestAsync(parameters.Id, DataMethods.Import, parameters, Json.ImportParams, Json.TransferResult, cancellationToken);

    /// <summary>Runs a query; cancelling the token cancels it in the database. The run goes to the history.</summary>
    public async Task<ExecuteResult> ExecuteAsync(ExecuteParams parameters, CancellationToken cancellationToken)
    {
        var label = Find(parameters.Id)?.Label ?? parameters.Id;
        try
        {
            var result = await RequestAsync(parameters.Id, DataMethods.Execute, parameters, Json.ExecuteParams, Json.ExecuteResult, cancellationToken).ConfigureAwait(false);
            AddHistory(new QueryHistoryEntry(DateTimeOffset.Now, label, parameters.Text, result.ElapsedMilliseconds, !result.Messages.Any(m => m.Kind == "error"), result.ResultSets.Length));
            return result;
        }
        catch (Exception ex) when (ex is JsonRpcRemoteException or OperationCanceledException)
        {
            AddHistory(new QueryHistoryEntry(DateTimeOffset.Now, label, parameters.Text, 0, false, 0));
            throw;
        }
    }

    public async Task CloseSessionAsync(string id, string session)
    {
        if (_process is { IsRunning: true } process && _connected.ContainsKey(id))
        {
            try
            {
                await process.Connection.InvokeAsync(DataMethods.CloseSession, new SessionParams(id, session), Json.SessionParams, Json.DataEmpty, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is JsonRpcConnectionLostException or JsonRpcRemoteException or ObjectDisposedException)
            {
            }
        }
    }

    private void AddHistory(QueryHistoryEntry entry)
    {
        lock (_history)
        {
            _history.Insert(0, entry);
            if (_history.Count > MaxHistory)
            {
                _history.RemoveRange(MaxHistory, _history.Count - MaxHistory);
            }

            Store(_historyFile, [.. _history], DataStateJsonContext.Default.ListQueryHistoryEntry);
        }
    }

    public void ClearHistory()
    {
        lock (_history)
        {
            _history.Clear();
            Store(_historyFile, [], DataStateJsonContext.Default.ListQueryHistoryEntry);
        }
    }

    /// <summary>Sends a request for a connection, connecting it first when it isn't (or when the host restarted).</summary>
    private async Task<TResult> RequestAsync<TParams, TResult>(string id, string method, TParams parameters, System.Text.Json.Serialization.Metadata.JsonTypeInfo<TParams> parametersInfo, System.Text.Json.Serialization.Metadata.JsonTypeInfo<TResult> resultInfo, CancellationToken cancellationToken)
    {
        if (!_connected.ContainsKey(id))
        {
            var connection = Find(id) ?? throw new InvalidOperationException("The connection no longer exists.");
            await ConnectAsync(connection, cancellationToken).ConfigureAwait(false);
        }

        var host = await ConnectionToHostAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await host.InvokeAsync(method, parameters, parametersInfo, resultInfo, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonRpcRemoteException ex) when (ex.Code == DataErrorCodes.UnknownConnection && Find(id) is { } connection)
        {
            await ConnectAsync(connection, cancellationToken).ConfigureAwait(false);
            return await host.InvokeAsync(method, parameters, parametersInfo, resultInfo, cancellationToken).ConfigureAwait(false);
        }
    }

    // ------------------------------------------------------------------ the host

    private async Task<JsonRpcConnection> ConnectionToHostAsync(CancellationToken cancellationToken)
    {
        if (_process is { IsRunning: true } running)
        {
            return running.Connection;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_process is null)
            {
                var launch = _launch() ?? throw new InvalidOperationException("The data host (daedalus-datahost) was not found.");
                var process = new SupervisedProcess("data host", launch, s_policy) { Initialize = InitializeAsync };
                process.Crashed += (_, e) =>
                {
                    Log?.Invoke(this, $"The data host exited (code {e.ExitCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?"}); {(e.WillRestart ? "restarting" : "giving up")}.");
                    HostCrashed?.Invoke(this, e.WillRestart);
                };
                process.Log += (_, line) => Log?.Invoke(this, line);
                _process = process;
            }

            await _process.StartAsync(cancellationToken).ConfigureAwait(false);
            return _process.Connection;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>After every (re)start: the handshake, then the open connections again (their sessions are gone).</summary>
    private async Task InitializeAsync(JsonRpcConnection connection, bool isRestart, CancellationToken cancellationToken)
    {
        await connection.InvokeAsync(DataMethods.Initialize, new DataInitializeParams(Environment.ProcessId, DataMethods.ProtocolVersion), Json.DataInitializeParams, Json.DataInitializeResult, cancellationToken).ConfigureAwait(false);
        if (!isRestart)
        {
            return;
        }

        foreach (var (id, (engine, connectionString)) in _connected.ToArray())
        {
            try
            {
                await connection.InvokeAsync(DataMethods.Connect, new ConnectParams(id, engine, connectionString), Json.ConnectParams, Json.ConnectResult, cancellationToken).ConfigureAwait(false);
            }
            catch (JsonRpcRemoteException)
            {
                _connected.TryRemove(id, out _);
            }
        }
    }

    // ------------------------------------------------------------------ files

    private static T? Load<T>(string path, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info)
        where T : class
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize(File.ReadAllText(path), info) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void Store<T>(string path, T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(value, info));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _process, null) is { } process)
        {
            await process.DisposeAsync().ConfigureAwait(false);
        }

        _gate.Dispose();
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, WriteIndented = true)]
[JsonSerializable(typeof(List<DataConnection>))]
[JsonSerializable(typeof(List<QueryHistoryEntry>))]
internal sealed partial class DataStateJsonContext : JsonSerializerContext;
