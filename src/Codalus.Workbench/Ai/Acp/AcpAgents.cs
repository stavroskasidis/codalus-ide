using System.Text.Json;
using Codalus.Workbench.Settings;

namespace Codalus.Workbench.Ai.Acp;

/// <summary>A permission an agent asks for: its tool call, as the user sees it, and the options the dialog offers.</summary>
/// <param name="Agent">The agent's name.</param>
/// <param name="Title">The tool call's title.</param>
/// <param name="Kind">Its kind (read, edit, execute…), when given.</param>
/// <param name="RawInput">Its input as the agent sent it (JSON), when given.</param>
/// <param name="Diffs">The changes it would make, when it says.</param>
/// <param name="Options">The options offered: allow once, reject once and reject always (never allow always, ADR 0063).</param>
public sealed record AcpPermissionPrompt(string Agent, string Title, string? Kind, string? RawInput, IReadOnlyList<AiStepDiff> Diffs, IReadOnlyList<AcpPermissionOption> Options);

/// <summary>What an external agent's session needs from the shell: files as the editor has them, the preview, approvals.</summary>
public interface IAcpHost
{
    /// <summary>A file's text: the editor's when it is open (unsaved changes included), else the disk's; null when it doesn't exist.</summary>
    Task<string?> ReadTextAsync(string path, CancellationToken cancellationToken);

    /// <summary>
    /// Shows a whole-file change in the refactoring preview (a new file when <paramref name="oldText"/> is null) and, on
    /// Apply, applies it to the editor's buffer (opening the file; unsaved, one undo step). True when it was applied.
    /// </summary>
    Task<bool> ProposeWriteAsync(string path, string? oldText, string newText, string agent, CancellationToken cancellationToken);

    /// <summary>Asks the user with the approval dialog; the chosen option's id, or null for Reject (the default).</summary>
    Task<string?> RequestPermissionAsync(AcpPermissionPrompt prompt, CancellationToken cancellationToken);

    /// <summary>Asks the user to allow a command, with its exact command line and folder (Reject is the default).</summary>
    Task<bool> ApproveCommandAsync(string agent, string commandLine, string folder, CancellationToken cancellationToken);

    /// <summary>Shows the agent's ways to sign in; the chosen one, or null.</summary>
    Task<AcpAuthMethod?> ChooseSignInAsync(string agent, IReadOnlyList<AcpAuthMethod> methods, CancellationToken cancellationToken);

    /// <summary>The agent changed a file itself: an open editor reloads it from the disk (asking when it has unsaved changes).</summary>
    Task ReloadFromDiskAsync(string path);

    /// <summary>
    /// <c>codalus mcp</c> for this session (the running executable's absolute path and the user-data folder), with a pipe
    /// to this IDE open until the returned lifetime is disposed; null when the IDE can't offer it.
    /// </summary>
    (AcpMcpServerStdio Server, IDisposable Lifetime)? StartMcpServer(string agent);
}

/// <summary>
/// The external agents the user configured (<c>ai.agents</c>, user settings only: a workspace can't add a process, as
/// for <c>ai.mcp.servers</c>; M48, ADR 0079), whether one may start now, and the one session that runs: one agent
/// process per chat session, stopped on New Chat, when another agent is picked, when the workspace closes, and on exit.
/// </summary>
public sealed class AcpAgents : IAsyncDisposable
{
    public const string AgentsSetting = "ai.agents";

    /// <summary>The label every external agent's chat shows (ADR 0079).</summary>
    public const string Label = "External agent: uses its own provider and data policy. Codalus checks what it asks the IDE for.";

    private readonly SettingsService _settings;
    private readonly AiService _ai;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private AcpAgentSession? _current;

    public AcpAgents(SettingsService settings, AiService ai)
    {
        _settings = settings;
        _ai = ai;
    }

    /// <summary>The configured agents, read from the user's settings only.</summary>
    public IReadOnlyDictionary<string, AcpAgentConfig> Configured => Parse(_settings.GetLayerValue(AgentsSetting, SettingScope.User));

    /// <summary>The session that runs, if any.</summary>
    public AcpAgentSession? Current => Volatile.Read(ref _current);

    /// <summary>A session started or stopped, or its commands or modes changed (any thread).</summary>
    public event EventHandler? Changed;

    /// <summary>Parses <c>{ "name": { "command": "…", "args": ["…"], "env": { "K": "V" } } }</c>; entries without a command are skipped.</summary>
    public static IReadOnlyDictionary<string, AcpAgentConfig> Parse(JsonElement? value) =>
        Mcp.McpServers.Parse(value).ToDictionary(e => e.Key, e => new AcpAgentConfig(e.Value.Command, e.Value.Args, e.Value.Env), StringComparer.Ordinal);

    /// <summary>Why an external agent can't start now, or null when it can: AI on, a workspace, and a trusted one (ADR 0079).</summary>
    public string? WhyNot()
    {
        if (!_ai.IsEnabled)
        {
            return _ai.IsOffForWorkspace
                ? "AI assistance is turned off for this workspace, so external agents can't run here."
                : "Turn on AI assistance first (Tools → Options → AI): external agents are off with it.";
        }

        if (_ai.WorkspaceRoot is null)
        {
            return "External agents work in a solution or folder: open one first.";
        }

        return _ai.IsWorkspaceTrusted ? null : "External agents work only in trusted folders: they read files and run commands you allow. Trust this folder first.";
    }

    /// <summary>
    /// The session of <paramref name="name"/>: the running one when it is that agent's, alive and in this workspace; else
    /// the running one is stopped and a new one starts. Throws <see cref="AcpException"/> with why it can't.
    /// </summary>
    public async Task<AcpAgentSession> GetOrStartAsync(string name, IAcpHost host, CancellationToken cancellationToken)
    {
        if (WhyNot() is { } reason)
        {
            throw new AcpException(reason);
        }

        if (!Configured.TryGetValue(name, out var config))
        {
            throw new AcpException($"There is no agent '{name}' in your settings (ai.agents).");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var root = _ai.WorkspaceRoot!;
            if (_current is { } current)
            {
                if (current.Name == name && current.IsAlive && current.Config.SameAs(config) && string.Equals(current.WorkspaceRoot, root, StringComparison.Ordinal))
                {
                    current.CommandTimeout = CommandTimeout;
                    return current;
                }

                _current = null;
                await current.DisposeAsync().ConfigureAwait(false);
            }

            var session = new AcpAgentSession(name, config, _ai, host, root) { CommandTimeout = CommandTimeout };
            session.StateChanged += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
            try
            {
                await session.StartAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await session.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            _current = session;
            Changed?.Invoke(this, EventArgs.Empty);
            return session;
        }
        finally
        {
            _gate.Release();
        }
    }

    private TimeSpan CommandTimeout => TimeSpan.FromSeconds(Math.Clamp(_settings.GetInt32("ai.agent.commandTimeoutSeconds", 120), 5, 3600));

    /// <summary>Stops the running agent (New Chat, another agent picked, the workspace closed, exit).</summary>
    public async Task StopAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_current is { } current)
            {
                _current = null;
                await current.DisposeAsync().ConfigureAwait(false);
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// The <c>codalus mcp</c> entry of <c>session/new</c>: the running executable by its absolute path (<c>dotnet</c> with
    /// the shell's assembly when it runs that way), the user-data folder when the IDE has its own, and the session's token,
    /// which names the pipe the IDE opens for this session only.
    /// </summary>
    public static AcpMcpServerStdio CodalusMcpServer(string processPath, string? entryAssembly, string? userDataDir, string sessionToken)
    {
        var args = new List<string>();
        if (Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase) && entryAssembly is { Length: > 0 })
        {
            args.Add(Path.GetFullPath(entryAssembly));
        }

        args.Add("mcp");
        if (userDataDir is { Length: > 0 })
        {
            args.Add("--user-data-dir");
            args.Add(Path.GetFullPath(userDataDir));
        }

        args.Add("--agent-session");
        args.Add(sessionToken);
        return new AcpMcpServerStdio("codalus", Path.GetFullPath(processPath), args, []);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _gate.Dispose();
    }
}
