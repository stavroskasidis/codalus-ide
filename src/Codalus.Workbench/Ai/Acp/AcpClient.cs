using System.Text;
using System.Text.Json;
using Codalus.Rpc;

namespace Codalus.Workbench.Ai.Acp;

/// <summary>An external agent from <c>ai.agents</c>: the command that starts it (the user's CLI).</summary>
public sealed record AcpAgentConfig(string Command, IReadOnlyList<string> Args, IReadOnlyDictionary<string, string> Env)
{
    private static readonly IReadOnlyDictionary<string, string> s_none = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// <c>secretEnv</c>: the names of variables whose values are kept in the OS credential store (<see cref="AcpAgentSecrets"/>)
    /// and given to the agent when it starts; never in a file (ADR 0083).
    /// </summary>
    public IReadOnlyList<string> SecretEnv { get; init; } = [];

    /// <summary>
    /// <c>unsetEnv</c>: variables taken out of the environment the agent would inherit (Claude signed in with its own
    /// account starts without <c>ANTHROPIC_API_KEY</c>, <c>ANTHROPIC_AUTH_TOKEN</c> and <c>ANTHROPIC_BASE_URL</c>, which
    /// would outrank it).
    /// </summary>
    public IReadOnlyList<string> UnsetEnv { get; init; } = [];

    /// <summary><c>template</c>: the template it was connected from (<c>claude@0.94.0</c>), or null for an entry written by hand.</summary>
    public string? Template { get; init; }

    /// <summary><c>node</c>: <c>builtIn</c> when Codalus's own Node.js runs it (D-node, ADR 0083); null for the user's.</summary>
    public string? Node { get; init; }

    /// <summary>The <see cref="SecretEnv"/> values, read when the session starts; never in settings, the log or a message.</summary>
    public IReadOnlyDictionary<string, string> SecretValues { get; init; } = s_none;

    /// <summary>The folder of Codalus's Node.js when <see cref="Node"/> is <c>builtIn</c> (resolved when it starts), or null.</summary>
    public string? NodeFolder { get; init; }

    /// <summary>The same command, arguments and environment, and the same keys (the settings or a key changed when not).</summary>
    public bool SameAs(AcpAgentConfig other) =>
        Command == other.Command && Args.SequenceEqual(other.Args, StringComparer.Ordinal)
        && Env.Count == other.Env.Count && Env.All(e => other.Env.TryGetValue(e.Key, out var value) && value == e.Value)
        && SecretEnv.SequenceEqual(other.SecretEnv, StringComparer.Ordinal) && UnsetEnv.SequenceEqual(other.UnsetEnv, StringComparer.Ordinal)
        && Node == other.Node && NodeFolder == other.NodeFolder && SecretHash == other.SecretHash;

    /// <summary>A hash of the secret values, to tell a changed key without keeping two copies around.</summary>
    private string SecretHash =>
        SecretValues.Count == 0 ? "" : Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', SecretValues.OrderBy(v => v.Key, StringComparer.Ordinal).Select(v => v.Key + "=" + v.Value)))));

    /// <summary>The command line, never the secret values (a record would print them).</summary>
    public override string ToString() => string.Join(' ', new[] { Command }.Concat(Args));
}

/// <summary>An agent failed: it didn't start, answered with an error, or stopped.</summary>
public class AcpException(string message, int? code = null) : Exception(message)
{
    /// <summary>The JSON-RPC error code, when the agent answered with one.</summary>
    public int? Code { get; } = code;
}

/// <summary>The agent needs the user to sign in first (<c>auth_required</c>), with the ways it offers.</summary>
public sealed class AcpAuthRequiredException(string message, IReadOnlyList<AcpAuthMethod> methods) : AcpException(message, AcpMethods.AuthRequired)
{
    public IReadOnlyList<AcpAuthMethod> Methods { get; } = methods;
}

/// <summary>What the client does with the agent's requests and notifications (an <see cref="AcpAgentSession"/>).</summary>
public interface IAcpClientHandler
{
    Task OnUpdateAsync(AcpSessionNotification notification);

    Task<AcpRequestPermissionResponse> RequestPermissionAsync(AcpRequestPermissionRequest request, CancellationToken cancellationToken);

    Task<AcpReadTextFileResponse> ReadTextFileAsync(AcpReadTextFileRequest request, CancellationToken cancellationToken);

    Task WriteTextFileAsync(AcpWriteTextFileRequest request, CancellationToken cancellationToken);

    Task<AcpCreateTerminalResponse> CreateTerminalAsync(AcpCreateTerminalRequest request, CancellationToken cancellationToken);

    Task<AcpTerminalOutputResponse> TerminalOutputAsync(AcpTerminalRequest request, CancellationToken cancellationToken);

    Task<AcpTerminalExitStatus> WaitForTerminalExitAsync(AcpTerminalRequest request, CancellationToken cancellationToken);

    Task KillTerminalAsync(AcpTerminalRequest request, CancellationToken cancellationToken);

    Task ReleaseTerminalAsync(AcpTerminalRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// The client side of one Agent Client Protocol connection (M48, ADR 0079): the agent's process, started by
/// <see cref="SupervisedProcess"/> and never restarted (a session lives in it), newline-delimited JSON-RPC on its stdio
/// through <see cref="JsonRpcConnection"/>, and the protocol's requests both ways. What the agent asks of the IDE goes to
/// an <see cref="IAcpClientHandler"/>, which applies the IDE's checks.
/// </summary>
public sealed class AcpClient : IAsyncDisposable
{
    /// <summary>How long an agent has to start and answer <c>initialize</c> unless the caller says otherwise.</summary>
    public static TimeSpan DefaultStartTimeout { get; } = TimeSpan.FromSeconds(60);

    private readonly SupervisedProcess _process;
    private readonly StringBuilder _stderr = new();
    private readonly TaskCompletionSource<AcpAuthStatus> _firstAuthStatus = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private AcpAuthStatus? _authStatus;
    private int _exited;

    private AcpClient(string name, SupervisedProcess process)
    {
        Name = name;
        _process = process;
    }

    public string Name { get; }

    /// <summary>The agent's answer to <c>initialize</c>.</summary>
    public AcpInitializeResponse? Info { get; private set; }

    /// <summary>The agent's process is running.</summary>
    public bool IsRunning => _process.IsRunning && Volatile.Read(ref _exited) == 0;

    public int? ProcessId => _process.ProcessId;

    /// <summary>Values never shown in a message: the agent's keys (its log could echo them).</summary>
    public IReadOnlyCollection<string> RedactedValues { get; set; } = [];

    /// <summary>The end of what the agent wrote to its standard error (its log), for the message when it stops; secrets redacted.</summary>
    public string ErrorOutput
    {
        get
        {
            string text;
            lock (_stderr)
            {
                text = _stderr.ToString().Trim();
            }

            return SecretRedactor.Redact(text, RedactedValues).Text;
        }
    }

    /// <summary>
    /// Who the agent says is signed in (<c>_auth/status_update</c>, Claude's adapter), or null while it hasn't said:
    /// <see cref="AcpAuthStatus.IsSignedOut"/> is a known signed-out state, null is "not reported".
    /// </summary>
    public AcpAuthStatus? AuthStatus => Volatile.Read(ref _authStatus);

    /// <summary>The agent pushes who is signed in (its <c>agentCapabilities._meta.authStatus</c> marker).</summary>
    public bool ReportsAuthStatus => Info?.AgentCapabilities?.Meta?.AuthStatus is not null;

    /// <summary>The agent can sign out (<c>agentCapabilities.auth.logout</c>).</summary>
    public bool CanLogout => Info?.AgentCapabilities?.Auth?.Logout is not null;

    /// <summary>The agent said who is signed in (any thread).</summary>
    public event EventHandler<AcpAuthStatus>? AuthStatusChanged;

    /// <summary>The agent's process ended (it crashed, or exited on its own).</summary>
    public event EventHandler? Exited;

    /// <summary>
    /// The capabilities Codalus advertises: files, terminals, compaction and notices, and terminal sign-in (the agent's own
    /// login command, run in Codalus's terminal; ADR 0083); no elicitation.
    /// </summary>
    public static AcpClientCapabilities Capabilities { get; } = new(
        new AcpFileSystemCapabilities(ReadTextFile: true, WriteTextFile: true),
        Terminal: true,
        new AcpClientSessionCapabilities(AcpEmpty.Instance, AcpEmpty.Instance),
        new AcpAuthCapabilities(Terminal: true));

    /// <summary>
    /// How an agent's command starts: as it is configured, except on Windows, where a script on the PATH (npx is
    /// <c>npx.cmd</c>) starts through <see cref="WindowsCommand"/>.
    /// </summary>
    /// <remarks>
    /// The <c>secretEnv</c> values join the environment, the <c>unsetEnv</c> names leave it, and with Codalus's Node.js
    /// (<see cref="AcpAgentConfig.NodeFolder"/>) its folder goes first on the PATH and <c>npx</c>, <c>npm</c> and
    /// <c>node</c> are its own.
    /// </remarks>
    public static ProcessLaunchInfo Launch(AcpAgentConfig config, string workingDirectory, Func<string, string?>? environment = null)
    {
        environment ??= Environment.GetEnvironmentVariable;
        var env = new Dictionary<string, string>(config.Env, StringComparer.Ordinal);
        foreach (var (name, value) in config.SecretValues)
        {
            env[name] = value;
        }

        var command = config.Command;
        var path = environment("PATH");
        if (config.NodeFolder is { Length: > 0 } node)
        {
            path = node + Path.PathSeparator + path;
            env["PATH"] = path;
            if (command is "npx" or "npm" or "node")
            {
                command = Path.Combine(node, command + (!OperatingSystem.IsWindows() ? "" : command == "node" ? ".exe" : ".cmd"));
            }
        }

        var launch = OperatingSystem.IsWindows() ? WindowsCommand.Launch(command, config.Args, path) : new ProcessLaunchInfo(command, config.Args);
        return launch with
        {
            Environment = env.Count > 0 ? env : null,
            UnsetEnvironment = config.UnsetEnv.Count > 0 ? config.UnsetEnv : null,
            WorkingDirectory = workingDirectory,
        };
    }

    /// <summary>Starts the agent in <paramref name="workingDirectory"/> and initializes the connection (protocol version 1).</summary>
    public static Task<AcpClient> StartAsync(string name, AcpAgentConfig config, string workingDirectory, IAcpClientHandler handler, CancellationToken cancellationToken) =>
        StartAsync(name, config, workingDirectory, handler, DefaultStartTimeout, cancellationToken);

    /// <summary>
    /// Starts the agent and initializes the connection within <paramref name="startTimeout"/> (the connection test gives
    /// its own, ADR 0083).
    /// </summary>
    public static async Task<AcpClient> StartAsync(string name, AcpAgentConfig config, string workingDirectory, IAcpClientHandler handler, TimeSpan startTimeout, CancellationToken cancellationToken)
    {
        var process = new SupervisedProcess("agent " + name, Launch(config, workingDirectory), new SupervisionPolicy
        {
            MaxRestarts = 0, // a session lives in the process: starting it again would lose it
            ShutdownRequest = null, // ACP has no shutdown: closing stdin ends the agent
            ExitNotification = null,
        })
        {
            Framing = JsonRpcFraming.NewlineDelimited,
        };
        var client = new AcpClient(name, process) { RedactedValues = [.. config.SecretValues.Values] };
        process.ConfigureConnection = connection => client.Register(connection, handler);
        process.Log += (_, line) =>
        {
            lock (client._stderr)
            {
                client._stderr.AppendLine(line);
                if (client._stderr.Length > 4000)
                {
                    client._stderr.Remove(0, client._stderr.Length - 4000);
                }
            }
        };
        process.Crashed += (_, _) => client.OnExited();

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(startTimeout);
            try
            {
                await process.StartAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
            {
                throw new AcpException($"The agent '{name}' didn't start ({config.Command}: {ex.Message}).");
            }

            var info = await client.InvokeAsync(
                AcpMethods.Initialize,
                new AcpInitializeRequest(AcpVersion.Protocol, Capabilities, new AcpImplementation("codalus", "Codalus IDE", typeof(AcpClient).Assembly.GetName().Version?.ToString() ?? "0")),
                AcpJsonContext.Default.AcpInitializeRequest,
                AcpJsonContext.Default.AcpInitializeResponse,
                timeout.Token).ConfigureAwait(false);
            if (info is null || info.ProtocolVersion != AcpVersion.Protocol)
            {
                throw new AcpException($"The agent '{name}' speaks ACP version {info?.ProtocolVersion.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?"}; Codalus IDE speaks version {AcpVersion.Protocol}.");
            }

            client.Info = info;
            return client;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw new AcpException($"The agent '{name}' didn't answer initialize within {startTimeout.TotalSeconds:0} seconds.");
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private void Register(JsonRpcConnection connection, IAcpClientHandler handler)
    {
        var json = AcpJsonContext.Default;
        connection.CancelNotification = JsonRpcCancelNotification.Acp;
        connection.OnNotification(AcpMethods.SessionUpdate, json.AcpSessionNotification, handler.OnUpdateAsync);
        connection.OnNotification(AcpMethods.AuthStatusUpdate, json.AcpAuthStatusUpdate, update =>
        {
            if (update.AuthStatus is { } status)
            {
                Volatile.Write(ref _authStatus, status);
                _firstAuthStatus.TrySetResult(status);
                AuthStatusChanged?.Invoke(this, status);
            }
        });
        connection.OnRequest(AcpMethods.SessionRequestPermission, json.AcpRequestPermissionRequest, json.AcpRequestPermissionResponse, handler.RequestPermissionAsync);
        connection.OnRequest(AcpMethods.ReadTextFile, json.AcpReadTextFileRequest, json.AcpReadTextFileResponse, handler.ReadTextFileAsync);
        connection.OnRequest(AcpMethods.WriteTextFile, json.AcpWriteTextFileRequest, json.AcpEmpty, async (request, ct) =>
        {
            await handler.WriteTextFileAsync(request, ct).ConfigureAwait(false);
            return AcpEmpty.Instance;
        });
        connection.OnRequest(AcpMethods.TerminalCreate, json.AcpCreateTerminalRequest, json.AcpCreateTerminalResponse, handler.CreateTerminalAsync);
        connection.OnRequest(AcpMethods.TerminalOutput, json.AcpTerminalRequest, json.AcpTerminalOutputResponse, handler.TerminalOutputAsync);
        connection.OnRequest(AcpMethods.TerminalWaitForExit, json.AcpTerminalRequest, json.AcpTerminalExitStatus, handler.WaitForTerminalExitAsync);
        connection.OnRequest(AcpMethods.TerminalKill, json.AcpTerminalRequest, json.AcpEmpty, async (request, ct) =>
        {
            await handler.KillTerminalAsync(request, ct).ConfigureAwait(false);
            return AcpEmpty.Instance;
        });
        connection.OnRequest(AcpMethods.TerminalRelease, json.AcpTerminalRequest, json.AcpEmpty, async (request, ct) =>
        {
            await handler.ReleaseTerminalAsync(request, ct).ConfigureAwait(false);
            return AcpEmpty.Instance;
        });
    }

    /// <summary><c>authenticate</c> with an agent method; <paramref name="apiKey"/> goes once in <c>_meta["api-key"]</c> (Codex).</summary>
    public Task AuthenticateAsync(string methodId, CancellationToken cancellationToken, JsonElement? apiKey = null) =>
        InvokeAsync(AcpMethods.Authenticate, new AcpAuthenticateRequest(methodId) { Meta = apiKey is { } key ? new AcpAuthenticateMeta(key) : null }, AcpJsonContext.Default.AcpAuthenticateRequest, AcpJsonContext.Default.AcpEmpty, cancellationToken);

    /// <summary><c>logout</c>: only when the agent advertised it (the protocol forbids it otherwise).</summary>
    public Task LogoutAsync(CancellationToken cancellationToken) =>
        CanLogout
            ? InvokeAsync(AcpMethods.Logout, AcpEmpty.Instance, AcpJsonContext.Default.AcpEmpty, AcpJsonContext.Default.AcpEmpty, cancellationToken)
            : throw new AcpException($"The agent '{Name}' can't sign out from Codalus IDE.");

    /// <summary>
    /// Who the agent says is signed in: the push it sent, or the first within <paramref name="timeout"/>; null when it
    /// said nothing (not reported, never "signed in").
    /// </summary>
    public async Task<AcpAuthStatus?> WaitForAuthStatusAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (AuthStatus is { } status)
        {
            return status;
        }

        try
        {
            return await _firstAuthStatus.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return null;
        }
    }

    /// <summary><c>session/new</c>; an <c>auth_required</c> answer throws <see cref="AcpAuthRequiredException"/>.</summary>
    public async Task<AcpNewSessionResponse> NewSessionAsync(AcpNewSessionRequest request, CancellationToken cancellationToken)
    {
        try
        {
            return await InvokeAsync(AcpMethods.SessionNew, request, AcpJsonContext.Default.AcpNewSessionRequest, AcpJsonContext.Default.AcpNewSessionResponse, cancellationToken).ConfigureAwait(false)
                ?? throw new AcpException($"The agent '{Name}' didn't create a session.");
        }
        catch (AcpException ex) when (ex.Code == AcpMethods.AuthRequired)
        {
            throw new AcpAuthRequiredException(ex.Message, [.. Info?.AuthMethods ?? []]);
        }
    }

    /// <summary>
    /// <c>session/prompt</c>: completes once the agent answered with its stop reason and every update it sent before the
    /// answer was handled (a response is read ahead of the notifications queued before it).
    /// </summary>
    public async Task<AcpPromptResponse> PromptAsync(AcpPromptRequest request, CancellationToken cancellationToken)
    {
        var connection = _process.Connection;
        AcpPromptResponse? response;
        try
        {
            response = await InvokeAsync(AcpMethods.SessionPrompt, request, AcpJsonContext.Default.AcpPromptRequest, AcpJsonContext.Default.AcpPromptResponse, cancellationToken).ConfigureAwait(false);
        }
        catch (AcpException ex) when (ex.Code == AcpMethods.AuthRequired)
        {
            // Claude's adapter creates a session signed out and asks for sign-in only here (ADR 0083).
            throw new AcpAuthRequiredException(ex.Message, [.. Info?.AuthMethods ?? []]);
        }

        await connection.WhenDispatchedAsync().ConfigureAwait(false);
        return response ?? new AcpPromptResponse();
    }

    private void OnExited()
    {
        if (Interlocked.Exchange(ref _exited, 1) == 0)
        {
            Exited?.Invoke(this, EventArgs.Empty);
        }
    }

    public Task SetModeAsync(string sessionId, string modeId, CancellationToken cancellationToken) =>
        InvokeAsync(AcpMethods.SessionSetMode, new AcpSetModeRequest(sessionId, modeId), AcpJsonContext.Default.AcpSetModeRequest, AcpJsonContext.Default.AcpEmpty, cancellationToken);

    /// <summary><c>session/cancel</c>: the agent stops the turn and answers its <c>session/prompt</c> with <c>cancelled</c>.</summary>
    public async Task CancelAsync(string sessionId)
    {
        try
        {
            await _process.Connection.NotifyAsync(AcpMethods.SessionCancel, new AcpCancelNotification(sessionId), AcpJsonContext.Default.AcpCancelNotification, CancellationToken.None).ConfigureAwait(false);
        }
        catch (JsonRpcConnectionLostException)
        {
        }
    }

    private async Task<TResult?> InvokeAsync<TParams, TResult>(string method, TParams parameters, System.Text.Json.Serialization.Metadata.JsonTypeInfo<TParams> paramsInfo, System.Text.Json.Serialization.Metadata.JsonTypeInfo<TResult> resultInfo, CancellationToken cancellationToken)
    {
        try
        {
            return await _process.Connection.InvokeAsync(method, parameters, paramsInfo, resultInfo, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonRpcRemoteException ex)
        {
            throw new AcpException($"{Name}: {ex.Message}", ex.Code);
        }
        catch (JsonRpcConnectionLostException)
        {
            OnExited();
            throw new AcpException(StoppedMessage());
        }
        catch (JsonException ex)
        {
            throw new AcpException($"The agent '{Name}' answered {method} with something Codalus IDE couldn't read: {ex.Message}");
        }
    }

    /// <summary>The agent stopped: what it last wrote to its log says why.</summary>
    public string StoppedMessage()
    {
        var log = ErrorOutput;
        var tail = log.Length > 600 ? "…" + log[^600..] : log;
        return tail.Length > 0 ? $"The agent '{Name}' stopped. Its last output:\n{tail}" : $"The agent '{Name}' stopped.";
    }

    public async ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _exited, 1);
        await _process.DisposeAsync().ConfigureAwait(false);
    }
}
