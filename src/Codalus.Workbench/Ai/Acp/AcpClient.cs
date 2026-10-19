using System.Text;
using System.Text.Json;
using Codalus.Rpc;

namespace Codalus.Workbench.Ai.Acp;

/// <summary>An external agent from <c>ai.agents</c>: the command that starts it (the user's CLI).</summary>
public sealed record AcpAgentConfig(string Command, IReadOnlyList<string> Args, IReadOnlyDictionary<string, string> Env)
{
    /// <summary>The same command, arguments and environment (the settings changed when not).</summary>
    public bool SameAs(AcpAgentConfig other) =>
        Command == other.Command && Args.SequenceEqual(other.Args, StringComparer.Ordinal)
        && Env.Count == other.Env.Count && Env.All(e => other.Env.TryGetValue(e.Key, out var value) && value == e.Value);
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
    private static readonly TimeSpan s_startTimeout = TimeSpan.FromSeconds(60);

    private readonly SupervisedProcess _process;
    private readonly StringBuilder _stderr = new();
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

    /// <summary>The end of what the agent wrote to its standard error (its log), for the message when it stops.</summary>
    public string ErrorOutput
    {
        get
        {
            lock (_stderr)
            {
                return _stderr.ToString().Trim();
            }
        }
    }

    /// <summary>The agent's process ended (it crashed, or exited on its own).</summary>
    public event EventHandler? Exited;

    /// <summary>The capabilities Codalus advertises: files, terminals, compaction and notices; no terminal sign-in, no elicitation.</summary>
    public static AcpClientCapabilities Capabilities { get; } = new(
        new AcpFileSystemCapabilities(ReadTextFile: true, WriteTextFile: true),
        Terminal: true,
        new AcpClientSessionCapabilities(AcpEmpty.Instance, AcpEmpty.Instance),
        new AcpAuthCapabilities(Terminal: false));

    /// <summary>
    /// How an agent's command starts: as it is configured, except on Windows, where a script on the PATH (npx is
    /// <c>npx.cmd</c>) starts through <see cref="WindowsCommand"/>.
    /// </summary>
    public static ProcessLaunchInfo Launch(AcpAgentConfig config, string workingDirectory, Func<string, string?>? environment = null)
    {
        environment ??= Environment.GetEnvironmentVariable;
        var env = config.Env.Count > 0 ? new Dictionary<string, string>(config.Env, StringComparer.Ordinal) : null;
        var launch = OperatingSystem.IsWindows() ? WindowsCommand.Launch(config.Command, config.Args, environment("PATH")) : new ProcessLaunchInfo(config.Command, config.Args);
        return launch with { Environment = env, WorkingDirectory = workingDirectory };
    }

    /// <summary>Starts the agent in <paramref name="workingDirectory"/> and initializes the connection (protocol version 1).</summary>
    public static async Task<AcpClient> StartAsync(string name, AcpAgentConfig config, string workingDirectory, IAcpClientHandler handler, CancellationToken cancellationToken)
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
        var client = new AcpClient(name, process);
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
            timeout.CancelAfter(s_startTimeout);
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
            throw new AcpException($"The agent '{name}' didn't answer initialize within {s_startTimeout.TotalSeconds:0} seconds.");
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

    public Task AuthenticateAsync(string methodId, CancellationToken cancellationToken) =>
        InvokeAsync(AcpMethods.Authenticate, new AcpAuthenticateRequest(methodId), AcpJsonContext.Default.AcpAuthenticateRequest, AcpJsonContext.Default.AcpEmpty, cancellationToken);

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
        var response = await InvokeAsync(AcpMethods.SessionPrompt, request, AcpJsonContext.Default.AcpPromptRequest, AcpJsonContext.Default.AcpPromptResponse, cancellationToken).ConfigureAwait(false);
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
