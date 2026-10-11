using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DaedalusStudio.Workbench.Aspire;

/// <summary>A <c>launch_configurations</c> entry of a run session request: <c>type</c> is <c>project</c>.</summary>
public sealed record IdeLaunchConfiguration(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("project_path")] string? ProjectPath = null,
    [property: JsonPropertyName("mode")] string? Mode = null,
    [property: JsonPropertyName("launch_profile")] string? LaunchProfile = null,
    [property: JsonPropertyName("disable_launch_profile")] bool DisableLaunchProfile = false)
{
    /// <summary>Whether the AppHost asks for the debugger (<c>Debug</c>; <c>NoDebug</c> runs it).</summary>
    public bool Debug => !string.Equals(Mode, "NoDebug", StringComparison.OrdinalIgnoreCase);
}

public sealed record IdeEnvironmentVariable([property: JsonPropertyName("name")] string Name, [property: JsonPropertyName("value")] string? Value);

/// <summary><c>PUT /run_session</c>'s body: what to start, its environment and its arguments.</summary>
public sealed record IdeRunSessionRequest(
    [property: JsonPropertyName("launch_configurations")] IdeLaunchConfiguration[] LaunchConfigurations,
    [property: JsonPropertyName("env")] IdeEnvironmentVariable[]? Environment = null,
    [property: JsonPropertyName("args")] string[]? Arguments = null);

public sealed record IdeInfo([property: JsonPropertyName("protocols_supported")] string[] ProtocolsSupported);

/// <summary>A notification on <c>/run_session/notify</c>: <c>processRestarted</c>, <c>sessionTerminated</c> or <c>serviceLogs</c>.</summary>
public sealed record IdeNotification(
    [property: JsonPropertyName("notification_type")] string Type,
    [property: JsonPropertyName("session_id")] string SessionId,
    [property: JsonPropertyName("pid")] int? ProcessId = null,
    [property: JsonPropertyName("exit_code")] int? ExitCode = null,
    [property: JsonPropertyName("is_std_err")] bool? IsStdErr = null,
    [property: JsonPropertyName("log_message")] string? LogMessage = null);

public sealed record IdeErrorDetail([property: JsonPropertyName("code")] string Code, [property: JsonPropertyName("message")] string Message);

public sealed record IdeError([property: JsonPropertyName("error")] IdeErrorDetail Error);

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(IdeRunSessionRequest))]
[JsonSerializable(typeof(IdeInfo))]
[JsonSerializable(typeof(IdeNotification))]
[JsonSerializable(typeof(IdeError))]
internal sealed partial class IdeExecutionJsonContext : JsonSerializerContext;

/// <summary>A program the IDE started for a run session.</summary>
public interface IIdeRunSession
{
    /// <summary>Completes with the program's process id once it runs.</summary>
    Task<int> Started { get; }

    /// <summary>Completes with its exit code (null when unknown) when it ends.</summary>
    Task<int?> Exited { get; }

    /// <summary>Ends it (a <c>DELETE</c>).</summary>
    void Terminate();
}

/// <summary>A request the server hands to the IDE: start <see cref="Configuration"/> with this environment and these arguments.</summary>
public sealed record IdeRunRequest(string SessionId, IdeLaunchConfiguration Configuration, IReadOnlyDictionary<string, string> Environment, IReadOnlyList<string> Arguments);

/// <summary>
/// The IDE side of Aspire's IDE execution protocol (M17b, ADR 0043): the AppHost's orchestrator (DCP, which runs with
/// the user's app and which we never ship) asks the IDE to start each project resource (<c>PUT /run_session</c>), to
/// stop one (<c>DELETE /run_session/{id}</c>), and listens on a WebSocket (<c>/run_session/notify</c>) for the process
/// ids, exit codes and output. The AppHost finds the server through <c>DEBUG_SESSION_PORT</c> and authenticates with
/// <c>DEBUG_SESSION_TOKEN</c>. Plain HTTP on the loopback interface; the token keeps other local processes out.
/// </summary>
public sealed class IdeExecutionServer : IAsyncDisposable
{
    /// <summary>The protocol versions served (the request and notification shapes of the first one, which later ones extend).</summary>
    public static readonly string[] ProtocolVersions = ["2024-03-03"];

    private readonly HttpListener _listener = new();
    private readonly Func<IdeRunRequest, CancellationToken, Task<IIdeRunSession>> _start;
    private readonly ConcurrentDictionary<string, IIdeRunSession> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<WebSocket, SemaphoreSlim> _sockets = new();
    private readonly CancellationTokenSource _lifetime = new();
    private int _nextSession;
    private Task _loop = Task.CompletedTask;

    public IdeExecutionServer(Func<IdeRunRequest, CancellationToken, Task<IIdeRunSession>> start, int? port = null)
    {
        _start = start;
        Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        Port = port ?? Containers.ContainerLaunch.FreePort();
        _listener.Prefixes.Add(string.Create(CultureInfo.InvariantCulture, $"http://localhost:{Port}/"));
    }

    public int Port { get; }

    public string Token { get; }

    /// <summary>What happened (requests, errors), for the Aspire output.</summary>
    public event Action<string>? Log;

    /// <summary>The variables that point an AppHost at this server.</summary>
    public IReadOnlyDictionary<string, string> AppHostEnvironment(bool debug) => new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["DEBUG_SESSION_PORT"] = "localhost:" + Port.ToString(CultureInfo.InvariantCulture),
        ["DEBUG_SESSION_TOKEN"] = Token,
        ["DEBUG_SESSION_RUN_MODE"] = debug ? "Debug" : "NoDebug",
        ["DEBUG_SESSION_INFO"] = """{"protocols_supported":["2024-03-03"],"supported_launch_configurations":["project"]}""",
    };

    public void Start()
    {
        _listener.Start();
        _loop = Task.Run(() => AcceptAsync(_lifetime.Token));
    }

    private async Task AcceptAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or OperationCanceledException or InvalidOperationException)
            {
                return;
            }

            _ = Task.Run(() => HandleAsync(context, cancellationToken), CancellationToken.None);
        }
    }

    private async Task HandleAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        var request = context.Request;
        var response = context.Response;
        try
        {
            var path = request.Url?.AbsolutePath.TrimEnd('/') ?? string.Empty;
            if (!Authorized(request))
            {
                Log?.Invoke($"Refused {request.HttpMethod} {path}: no or a wrong token.");
                await RespondAsync(response, HttpStatusCode.Unauthorized, null).ConfigureAwait(false);
                return;
            }

            switch (request.HttpMethod, path)
            {
                case ("GET", "/info"):
                    await RespondAsync(response, HttpStatusCode.OK, JsonSerializer.Serialize(new IdeInfo(ProtocolVersions), IdeExecutionJsonContext.Default.IdeInfo)).ConfigureAwait(false);
                    break;
                case ("GET", "/run_session/notify") when request.IsWebSocketRequest:
                    await NotifyAsync(context, cancellationToken).ConfigureAwait(false);
                    break;
                case ("PUT", "/run_session"):
                    await CreateAsync(request, response, cancellationToken).ConfigureAwait(false);
                    break;
                case ("DELETE", _) when path.StartsWith("/run_session/", StringComparison.Ordinal):
                    var id = path["/run_session/".Length..];
                    if (_sessions.TryRemove(id, out var session))
                    {
                        Log?.Invoke($"Stopping run session {id}.");
                        session.Terminate();
                        await RespondAsync(response, HttpStatusCode.OK, null).ConfigureAwait(false);
                    }
                    else
                    {
                        await RespondAsync(response, HttpStatusCode.NoContent, null).ConfigureAwait(false);
                    }

                    break;
                default:
                    await RespondAsync(response, HttpStatusCode.NotFound, null).ConfigureAwait(false);
                    break;
            }
        }
        catch (Exception ex) when (ex is HttpListenerException or IOException or ObjectDisposedException or WebSocketException or InvalidOperationException)
        {
            Log?.Invoke($"{request.HttpMethod} {request.Url?.AbsolutePath} failed: {ex.Message}");
        }
    }

    private bool Authorized(HttpListenerRequest request) =>
        request.Headers["Authorization"] is { } header
        && header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
        && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(header[7..].Trim()), Encoding.UTF8.GetBytes(Token));

    private async Task CreateAsync(HttpListenerRequest request, HttpListenerResponse response, CancellationToken cancellationToken)
    {
        IdeRunSessionRequest? body;
        using (var reader = new StreamReader(request.InputStream, Encoding.UTF8))
        {
            var text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                body = JsonSerializer.Deserialize(text, IdeExecutionJsonContext.Default.IdeRunSessionRequest);
            }
            catch (JsonException ex)
            {
                await ErrorAsync(response, "BadRequest", ex.Message).ConfigureAwait(false);
                return;
            }
        }

        var configuration = body?.LaunchConfigurations?.FirstOrDefault();
        if (configuration is not { Type: "project", ProjectPath: { Length: > 0 } })
        {
            await ErrorAsync(response, "UnsupportedLaunchConfiguration", "Only project launch configurations are supported.").ConfigureAwait(false);
            return;
        }

        var id = Interlocked.Increment(ref _nextSession).ToString(CultureInfo.InvariantCulture);
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var variable in body!.Environment ?? [])
        {
            environment[variable.Name] = variable.Value ?? string.Empty;
        }

        Log?.Invoke($"Run session {id}: {(configuration.Debug ? "debug" : "run")} {configuration.ProjectPath}{(configuration.LaunchProfile is { } profile ? $" (profile '{profile}')" : string.Empty)}.");
        IIdeRunSession session;
        try
        {
            session = await _start(new IdeRunRequest(id, configuration, environment, body.Arguments ?? []), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception or OperationCanceledException)
        {
            Log?.Invoke($"Run session {id} couldn't start: {ex.Message}");
            await ErrorAsync(response, "ProjectLaunchFailed", ex.Message).ConfigureAwait(false);
            return;
        }

        _sessions[id] = session;
        response.Headers["Location"] = string.Create(CultureInfo.InvariantCulture, $"http://localhost:{Port}/run_session/{id}");
        await RespondAsync(response, HttpStatusCode.Created, null).ConfigureAwait(false);
        _ = session.Started.ContinueWith(t => t.IsCompletedSuccessfully ? SendAsync(new IdeNotification("processRestarted", id, t.Result)) : Task.CompletedTask, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
        _ = session.Exited.ContinueWith(
            async t =>
            {
                _sessions.TryRemove(id, out _);
                var pid = session.Started.IsCompletedSuccessfully ? session.Started.Result : (int?)null;
                await SendAsync(new IdeNotification("sessionTerminated", id, pid, t.IsCompletedSuccessfully ? t.Result ?? 0 : 0)).ConfigureAwait(false);
            },
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default).Unwrap();
    }

    /// <summary>A line of a session's output, for the dashboard's console logs.</summary>
    public Task LogAsync(string sessionId, string line, bool isError) => SendAsync(new IdeNotification("serviceLogs", sessionId, IsStdErr: isError, LogMessage: line));

    private async Task SendAsync(IdeNotification notification)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(notification, IdeExecutionJsonContext.Default.IdeNotification));
        if (_sockets.IsEmpty && notification.Type != "serviceLogs")
        {
            Log?.Invoke($"No notification socket: {notification.Type} for run session {notification.SessionId} wasn't sent.");
        }

        foreach (var (socket, gate) in _sockets)
        {
            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (socket.State == WebSocketState.Open)
                {
                    await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, _lifetime.Token).ConfigureAwait(false);
                    if (notification.Type != "serviceLogs")
                    {
                        Log?.Invoke($"Sent {notification.Type} for run session {notification.SessionId}{(notification.ProcessId is { } pid ? $" (process {pid})" : string.Empty)}.");
                    }
                }
                else
                {
                    Log?.Invoke($"The notification socket is {socket.State}: {notification.Type} for run session {notification.SessionId} wasn't sent.");
                }
            }
            catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or OperationCanceledException or IOException)
            {
                Log?.Invoke($"Could not send {notification.Type} for run session {notification.SessionId}: {ex.Message}");
                _sockets.TryRemove(socket, out _);
            }
            finally
            {
                gate.Release();
            }
        }
    }

    private async Task NotifyAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        var accepted = await context.AcceptWebSocketAsync(null).ConfigureAwait(false);
        var socket = accepted.WebSocket;
        _sockets[socket] = new SemaphoreSlim(1, 1);
        Log?.Invoke("The AppHost listens for notifications.");
        var buffer = new byte[1024];
        try
        {
            while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                var result = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
        }
        finally
        {
            _sockets.TryRemove(socket, out _);
            socket.Dispose();
        }
    }

    private static Task ErrorAsync(HttpListenerResponse response, string code, string message) =>
        RespondAsync(response, HttpStatusCode.BadRequest, JsonSerializer.Serialize(new IdeError(new IdeErrorDetail(code, message)), IdeExecutionJsonContext.Default.IdeError));

    private static async Task RespondAsync(HttpListenerResponse response, HttpStatusCode status, string? json)
    {
        response.StatusCode = (int)status;
        if (json is not null)
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            response.ContentType = "application/json";
            response.ContentLength64 = bytes.Length;
            await response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        }

        response.Close();
    }

    /// <summary>Stops every session the server started.</summary>
    public void StopAll()
    {
        foreach (var (id, session) in _sessions)
        {
            if (_sessions.TryRemove(id, out _))
            {
                session.Terminate();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        StopAll();
        await _lifetime.CancelAsync().ConfigureAwait(false);
        try
        {
            _listener.Stop();
            _listener.Close();
        }
        catch (ObjectDisposedException)
        {
        }

        await _loop.ConfigureAwait(false);
        foreach (var socket in _sockets.Keys)
        {
            socket.Dispose();
        }

        _lifetime.Dispose();
    }
}
