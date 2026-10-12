using System.Text.Json.Serialization;

namespace DaedalusStudio.Remote.Protocol;

/// <summary>
/// The remote development server's control channel (M21, ADR 0049 and 0050; docs/remote-protocol.md): JSON-RPC on
/// channel 0 of the multiplexed stdio stream. The client opens every other channel by id before naming it in a
/// request (<c>process/spawn</c>, <c>pty/open</c>, <c>tunnel/open</c>).
/// </summary>
public static class RemoteMethods
{
    /// <summary>Bumped on every incompatible change; the client and the server must agree exactly.</summary>
    public const int ProtocolVersion = 1;

    /// <summary>
    /// The line the server prints on stdout before the first frame: <c>DAEDALUS-SERVER &lt;version&gt; &lt;protocol&gt;</c>.
    /// Anything a login shell prints before it (a banner, a profile's echo) is skipped.
    /// </summary>
    public const string Preamble = "DAEDALUS-SERVER";

    public const string Hello = "server/hello";
    public const string Shutdown = "shutdown";
    public const string Exit = "exit";

    public const string Spawn = "process/spawn";
    public const string Kill = "process/kill";
    public const string Stderr = "process/stderr";
    public const string Exited = "process/exited";

    public const string PtyOpen = "pty/open";
    public const string PtyResize = "pty/resize";

    public const string Stat = "fs/stat";
    public const string ReadFile = "fs/readFile";
    public const string WriteFile = "fs/writeFile";
    public const string ReadDirectory = "fs/readDirectory";
    public const string CreateDirectory = "fs/createDirectory";
    public const string Delete = "fs/delete";
    public const string Rename = "fs/rename";
    public const string Watch = "fs/watch";
    public const string Unwatch = "fs/unwatch";
    public const string Changed = "fs/changed";

    public const string ListPorts = "ports/list";
    public const string WatchPorts = "ports/watch";
    public const string PortsChanged = "ports/changed";

    public const string OpenTunnel = "tunnel/open";

    /// <summary>The server's error code for a path that doesn't exist (a client maps it to FileNotFoundException).</summary>
    public const int FileNotFound = -32010;
}

public sealed record HelloParams(string ClientVersion, int ProtocolVersion);

/// <param name="Dotnet">The <c>dotnet</c> the server starts hosts with, or null when there is none.</param>
/// <param name="Home">The user's home folder on the server's machine.</param>
public sealed record HelloResult(
    string ServerVersion,
    int ProtocolVersion,
    string Os,
    string Architecture,
    int ProcessId,
    string Home,
    string User,
    string? Dotnet,
    string? DotnetVersion,
    string Shell,
    string ServerFolder);

/// <param name="FileName">A path, a program on the PATH, or <c>dotnet</c> (the server's own resolution).</param>
public sealed record SpawnParams(uint Channel, string FileName, IReadOnlyList<string> Arguments, string? WorkingDirectory = null, IReadOnlyDictionary<string, string>? Environment = null);

public sealed record SpawnResult(int ProcessId);

public sealed record ChannelParams(uint Channel);

public sealed record StderrParams(uint Channel, string Line);

public sealed record ExitedParams(uint Channel, int? ExitCode);

/// <param name="FileName">Null: the user's login shell.</param>
public sealed record PtyOpenParams(uint Channel, string? FileName, IReadOnlyList<string> Arguments, string? WorkingDirectory, IReadOnlyDictionary<string, string>? Environment, int Columns, int Rows);

public sealed record PtyResizeParams(uint Channel, int Columns, int Rows);

public sealed record PathParams(string Path);

/// <param name="Modified">Unix milliseconds.</param>
public sealed record FileStat(bool Exists, bool IsDirectory, long Size, long Modified);

public sealed record ReadFileResult(byte[] Content);

public sealed record WriteFileParams(string Path, byte[] Content);

public sealed record DirectoryEntry(string Name, bool IsDirectory, long Size, long Modified);

public sealed record ReadDirectoryResult(IReadOnlyList<DirectoryEntry> Entries);

public sealed record DeleteParams(string Path, bool Recursive);

public sealed record RenameParams(string Source, string Target, bool Overwrite);

public sealed record WatchParams(int Id, string Path, bool Recursive);

public sealed record UnwatchParams(int Id);

/// <param name="Kind"><c>created</c>, <c>changed</c> or <c>deleted</c>.</param>
public sealed record FileChange(string Path, string Kind);

public sealed record FileChangesParams(int Id, IReadOnlyList<FileChange> Changes);

/// <param name="Address">The address it listens on (<c>0.0.0.0</c>, <c>127.0.0.1</c>, <c>::</c>…).</param>
public sealed record ListeningPort(int Port, string Address, int? ProcessId = null, string? ProcessName = null);

public sealed record PortsResult(IReadOnlyList<ListeningPort> Ports);

public sealed record WatchPortsParams(bool Enabled, int IntervalMilliseconds = 1500);

/// <param name="Host">Where the server connects (a loopback address normally).</param>
public sealed record TunnelParams(uint Channel, string Host, int Port);

public sealed record EmptyResult;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(HelloParams))]
[JsonSerializable(typeof(HelloResult))]
[JsonSerializable(typeof(SpawnParams))]
[JsonSerializable(typeof(SpawnResult))]
[JsonSerializable(typeof(ChannelParams))]
[JsonSerializable(typeof(StderrParams))]
[JsonSerializable(typeof(ExitedParams))]
[JsonSerializable(typeof(PtyOpenParams))]
[JsonSerializable(typeof(PtyResizeParams))]
[JsonSerializable(typeof(PathParams))]
[JsonSerializable(typeof(FileStat))]
[JsonSerializable(typeof(ReadFileResult))]
[JsonSerializable(typeof(WriteFileParams))]
[JsonSerializable(typeof(ReadDirectoryResult))]
[JsonSerializable(typeof(DeleteParams))]
[JsonSerializable(typeof(RenameParams))]
[JsonSerializable(typeof(WatchParams))]
[JsonSerializable(typeof(UnwatchParams))]
[JsonSerializable(typeof(FileChangesParams))]
[JsonSerializable(typeof(PortsResult))]
[JsonSerializable(typeof(WatchPortsParams))]
[JsonSerializable(typeof(TunnelParams))]
[JsonSerializable(typeof(EmptyResult))]
public sealed partial class RemoteJsonContext : JsonSerializerContext;
