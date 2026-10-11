using System.Text.Json;
using System.Text.Json.Serialization;

namespace DaedalusStudio.Data.Protocol;

/// <summary>
/// The data host protocol (ADR 0037, docs/data-protocol.md): JSON-RPC over stdio between the shell and
/// <c>daedalus-datahost</c>. Connections are registered by id with their full connection string, which the host keeps in
/// memory only. Values travel as invariant strings (null is SQL NULL); binary values as <c>0x</c> hex.
/// </summary>
public static class DataMethods
{
    public const string ProtocolVersion = "1";

    public const string Initialize = "initialize";
    public const string Shutdown = "shutdown";
    public const string Exit = "exit";

    public const string Connect = "data/connect";
    public const string Disconnect = "data/disconnect";
    public const string Test = "data/test";
    public const string Databases = "data/databases";
    public const string Schema = "data/schema";
    public const string Read = "data/read";
    public const string Count = "data/count";
    public const string Commit = "data/commit";
    public const string Execute = "data/execute";
    public const string CloseSession = "data/closeSession";
    public const string Export = "data/export";
    public const string Import = "data/import";
}

/// <summary>The engines (D15).</summary>
public static class DataEngines
{
    public const string SqlServer = "sqlserver";
    public const string PostgreSql = "postgresql";
    public const string Sqlite = "sqlite";
    public const string MySql = "mysql";
}

/// <summary>Kinds of schema objects.</summary>
public static class DbObjectKinds
{
    public const string Table = "table";
    public const string View = "view";
    public const string Procedure = "procedure";
    public const string Function = "function";
}

/// <summary>Plan requests of <see cref="ExecuteParams"/>.</summary>
public static class PlanKinds
{
    public const string None = "none";

    /// <summary>The plan without running the statements.</summary>
    public const string Estimated = "estimated";

    /// <summary>Runs the statements and returns the plan with actual counts.</summary>
    public const string Actual = "actual";
}

/// <summary>Errors the host answers with.</summary>
public static class DataErrorCodes
{
    public const int UnknownConnection = -33200;

    /// <summary>The database answered with an error; the message is the database's.</summary>
    public const int DatabaseError = -33201;

    /// <summary>The request isn't possible here (a table without a key isn't editable, a plan the engine doesn't have).</summary>
    public const int NotSupported = -33202;

    /// <summary>A commit's row was changed or deleted by someone else: nothing was committed.</summary>
    public const int Conflict = -33203;
}

public sealed record DataInitializeParams(int ProcessId, string ProtocolVersion);

public sealed record DataInitializeResult(string Name, string Version, string ProtocolVersion, string[] Engines);

public sealed record ConnectParams(string Id, string Engine, string ConnectionString);

/// <summary>The server's version and the database the connection opens.</summary>
public sealed record ConnectResult(string ServerVersion, string? Database, string? DefaultSchema);

public sealed record ConnectionIdParams(string Id);

public sealed record TestParams(string Engine, string ConnectionString);

public sealed record TestResult(bool Succeeded, string? ServerVersion, string? Error);

public sealed record DatabasesResult(string[] Databases, string? Current);

/// <summary>One database's objects (null: the connection's own database).</summary>
public sealed record SchemaParams(string Id, string? Database = null);

public sealed record SchemaResult(string? Database, string? DefaultSchema, DbObjectInfo[] Objects);

/// <summary>A table, view, procedure or function. Routines have no columns.</summary>
public sealed record DbObjectInfo(string? Schema, string Name, string Kind, DbColumnInfo[] Columns, DbKeyInfo[] Keys, DbIndexInfo[] Indexes)
{
    public string QualifiedName => Schema is { Length: > 0 } schema ? schema + "." + Name : Name;
}

/// <summary><see cref="Type"/> is the database's type name with its size (<c>nvarchar(200)</c>).</summary>
public sealed record DbColumnInfo(string Name, string Type, bool Nullable, bool IsKey, bool IsIdentity, string? Default);

/// <summary><see cref="Kind"/>: <c>primary</c>, <c>foreign</c> or <c>unique</c>; foreign keys name what they reference.</summary>
public sealed record DbKeyInfo(string Name, string Kind, string[] Columns, string? ReferencedSchema = null, string? ReferencedTable = null, string[]? ReferencedColumns = null);

public sealed record DbIndexInfo(string Name, string[] Columns, bool IsUnique);

public sealed record DbTableRef(string? Schema, string Name);

/// <summary>
/// A page of a table: <see cref="Count"/> rows from <see cref="Offset"/>, ordered by <see cref="OrderBy"/> (a column) or
/// the primary key. <see cref="Filter"/> is a WHERE condition the user typed.
/// </summary>
public sealed record ReadParams(string Id, string? Database, DbTableRef Table, int Offset, int Count, string? OrderBy = null, bool Descending = false, string? Filter = null);

/// <summary>
/// <see cref="KeyColumns"/> identify rows for edits; a table without a primary key is <see cref="ReadOnly"/>, with the
/// reason.
/// </summary>
public sealed record ReadResult(ResultColumn[] Columns, string?[][] Rows, string[] KeyColumns, bool ReadOnly, string? ReadOnlyReason, double ElapsedMilliseconds);

/// <summary>A result column: its name, the database's type name and whether it accepts NULL (when known).</summary>
public sealed record ResultColumn(string Name, string Type, bool? Nullable = null);

public sealed record CountParams(string Id, string? Database, DbTableRef Table, string? Filter = null);

public sealed record CountResult(long Count);

/// <summary>
/// An edit: <c>insert</c> (<see cref="Values"/>), <c>update</c> (<see cref="Key"/> and the changed <see cref="Values"/>)
/// or <c>delete</c> (<see cref="Key"/>). Values are strings converted to the columns' types; null is NULL.
/// </summary>
public sealed record RowChange(string Kind, Dictionary<string, string?>? Key = null, Dictionary<string, string?>? Values = null);

public static class RowChangeKinds
{
    public const string Insert = "insert";
    public const string Update = "update";
    public const string Delete = "delete";
}

/// <summary>Applies every change in one transaction; an update or delete that matches no row rolls everything back.</summary>
public sealed record CommitParams(string Id, string? Database, DbTableRef Table, RowChange[] Changes);

public sealed record CommitResult(int Affected);

/// <summary>
/// Runs a batch (SQL Server: separated by <c>GO</c> lines). <see cref="Session"/> keeps a connection for a query editor
/// (temporary tables and <c>SET</c> options last between runs), until <c>data/closeSession</c>.
/// </summary>
public sealed record ExecuteParams(string Id, string Text, string? Database = null, string? Session = null, string Plan = PlanKinds.None, int MaxRows = 10_000);

public sealed record ExecuteResult(ResultSetInfo[] ResultSets, QueryMessage[] Messages, double ElapsedMilliseconds, PlanInfo? Plan);

/// <summary><see cref="Truncated"/>: more rows than the request's limit; <see cref="RecordsAffected"/> is -1 for queries.</summary>
public sealed record ResultSetInfo(ResultColumn[] Columns, string?[][] Rows, bool Truncated, int RecordsAffected);

/// <summary><see cref="Kind"/>: <c>info</c>, <c>warning</c> or <c>error</c>.</summary>
public sealed record QueryMessage(string Kind, string Text, int? Line = null);

/// <summary>An execution plan: the engine's own (<see cref="Format"/> <c>xml</c>, <c>json</c> or <c>text</c>) and its tree.</summary>
public sealed record PlanInfo(string Format, string Raw, PlanNode[] Roots, bool Actual);

/// <summary>
/// A plan operator: <see cref="CostPercent"/> is its own share of the statement's estimated cost (0–100) when the engine
/// gives costs.
/// </summary>
public sealed record PlanNode(string Operation, string? Detail, double? EstimatedRows, double? ActualRows, double? EstimatedCost, double? CostPercent, PlanNode[] Children);

public sealed record SessionParams(string Id, string Session);

/// <summary>Writes a table's rows, or a query's, to <see cref="Path"/> as <c>csv</c> or <c>json</c>.</summary>
public sealed record ExportParams(string Id, string? Database, string Format, string Path, DbTableRef? Table = null, string? Query = null);

/// <summary>Inserts the rows of a CSV file (with a header row of column names) or a JSON array of objects, in one transaction.</summary>
public sealed record ImportParams(string Id, string? Database, DbTableRef Table, string Format, string Path);

public sealed record TransferResult(long Rows);

public sealed record DataEmpty;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(DataInitializeParams))]
[JsonSerializable(typeof(DataInitializeResult))]
[JsonSerializable(typeof(ConnectParams))]
[JsonSerializable(typeof(ConnectResult))]
[JsonSerializable(typeof(ConnectionIdParams))]
[JsonSerializable(typeof(TestParams))]
[JsonSerializable(typeof(TestResult))]
[JsonSerializable(typeof(DatabasesResult))]
[JsonSerializable(typeof(SchemaParams))]
[JsonSerializable(typeof(SchemaResult))]
[JsonSerializable(typeof(ReadParams))]
[JsonSerializable(typeof(ReadResult))]
[JsonSerializable(typeof(CountParams))]
[JsonSerializable(typeof(CountResult))]
[JsonSerializable(typeof(CommitParams))]
[JsonSerializable(typeof(CommitResult))]
[JsonSerializable(typeof(ExecuteParams))]
[JsonSerializable(typeof(ExecuteResult))]
[JsonSerializable(typeof(SessionParams))]
[JsonSerializable(typeof(ExportParams))]
[JsonSerializable(typeof(ImportParams))]
[JsonSerializable(typeof(TransferResult))]
[JsonSerializable(typeof(DataEmpty))]
[JsonSerializable(typeof(JsonElement))]
public sealed partial class DataJsonContext : JsonSerializerContext;
