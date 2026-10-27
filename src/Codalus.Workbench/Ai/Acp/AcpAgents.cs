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

/// <summary>A command an agent asks to run, as the approval shows it.</summary>
/// <param name="Agent">The agent's name.</param>
/// <param name="CommandLine">The exact command line.</param>
/// <param name="Folder">Where it runs.</param>
/// <param name="Environment">The variables the agent sets for it, as <c>NAME=value</c> (values redacted); the ones that change what runs are refused before.</param>
/// <param name="Resolved">What starts on Windows when it isn't the command as written (a script through node or cmd.exe), or null.</param>
public sealed record AcpCommandPrompt(string Agent, string CommandLine, string Folder, IReadOnlyList<string> Environment, string? Resolved);

/// <summary>An agent's terminal sign-in: who, how the user sees the command, and how it starts.</summary>
/// <param name="Agent">The agent's name.</param>
/// <param name="Method">The method's name, as the agent gives it ("Claude Subscription").</param>
/// <param name="CommandLine">The exact command line, as configured, with the method's arguments.</param>
/// <param name="Variables">The names of the variables it sets (never their values).</param>
/// <param name="Launch">How it starts: the agent's own command, its environment, the method's arguments and variables.</param>
/// <param name="Hint">A line under the command (Claude in an SSH session: "or run <c>claude</c> and type <c>/login</c>"), or null.</param>
public sealed record AcpTerminalSignIn(string Agent, string Method, string CommandLine, IReadOnlyList<string> Variables, Codalus.Rpc.ProcessLaunchInfo Launch, string? Hint);

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

    /// <summary>Asks the user to allow a command, with its exact command line, folder and environment (Reject is the default).</summary>
    Task<bool> ApproveCommandAsync(AcpCommandPrompt prompt, CancellationToken cancellationToken);

    /// <summary>Shows the agent's ways to sign in; the chosen one, or null.</summary>
    Task<AcpAuthMethod?> ChooseSignInAsync(string agent, IReadOnlyList<AcpAuthMethod> methods, CancellationToken cancellationToken);

    /// <summary>The agent changed a file itself: an open editor reloads it from the disk (asking when it has unsaved changes).</summary>
    Task ReloadFromDiskAsync(string path);

    /// <summary>
    /// Runs an agent's terminal sign-in (ADR 0083) in the IDE's terminal after showing its exact command line and the
    /// names of the variables it sets, and waits for it to end: its exit code, or null when the user declined or it was
    /// refused (a remote window, whose terminal runs elsewhere). Cancelling kills it.
    /// </summary>
    Task<int?> RunTerminalSignInAsync(AcpTerminalSignIn signIn, CancellationToken cancellationToken);

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

    /// <summary>Who answers in AI Chat, remembered across a restart (M51): an agent of <c>ai.agents</c>, or empty for the built-in assistant.</summary>
    public const string ChatAgentSetting = "ai.chat.agent";

    /// <summary>The member of an agent's entry that keeps the user's picks of its choices (M51).</summary>
    public const string ChoicesMember = "choices";

    /// <summary>The label every external agent's chat shows (ADR 0079).</summary>
    public const string Label = "External agent: uses its own provider and data policy. Codalus checks what it asks the IDE for.";

    private readonly SettingsService _settings;
    private readonly AiService _ai;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, (string Names, IReadOnlyDictionary<string, string> Values)> _secretCache = new(StringComparer.Ordinal);
    private readonly Lock _cacheGate = new();
    private AcpAgentSession? _current;
    private AcpAgentSecrets? _secrets;

    public AcpAgents(SettingsService settings, AiService ai)
    {
        _settings = settings;
        _ai = ai;

        // AI turned off (by the user, or for this workspace): a running agent stops, and its requests aren't served.
        _ai.Changed += (_, _) => _ = EnforceAsync();
    }

    /// <summary>
    /// Agents' keys (<c>secretEnv</c>) in the OS credential store; null when the shell has none (an entry with
    /// <c>secretEnv</c> then doesn't start). A key stored or deleted refreshes the values read for the sessions.
    /// </summary>
    public AcpAgentSecrets? Secrets
    {
        get => _secrets;
        set
        {
            if (_secrets is { } old)
            {
                old.Changed -= OnSecretChanged;
            }

            _secrets = value;
            if (value is not null)
            {
                value.Changed += OnSecretChanged;
            }
        }
    }

    /// <summary>The folder of Codalus's own Node.js (D-node) for entries with <c>"node": "builtIn"</c>, or null when it isn't installed.</summary>
    public Func<string?> BuiltInNodeFolder { get; set; } = () => null;

    private void OnSecretChanged(object? sender, string agent)
    {
        lock (_cacheGate)
        {
            _secretCache.Remove(agent);
        }
    }

    /// <summary>The window is a remote one (WSL, SSH, a container): an agent would run on this machine, not there (ADR 0079).</summary>
    public Func<bool> IsRemoteWindow { get; set; } = () => false;

    /// <summary>
    /// The folder agents' processes start in: neutral, so a cloned repository's <c>.npmrc</c> or <c>node_modules</c> can't
    /// steer what <c>npx</c> runs (the shell sets an empty folder of its own; the user's home otherwise).
    /// </summary>
    public string ProcessDirectory { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>
    /// Stops the running agent when it may no longer run (AI off, the folder untrusted or closed, a remote window): called
    /// when AI's state changes and when the workspace or its trust does. Its requests are refused meanwhile (the session's gate).
    /// </summary>
    public async Task EnforceAsync()
    {
        if (Current is not null && WhyNot() is not null)
        {
            await StopAsync().ConfigureAwait(false);
        }
    }

    /// <summary>The agent AI Chat answers with (<see cref="ChatAgentSetting"/>, the user's settings), or null for the built-in assistant.</summary>
    public string? ChatAgent => _settings.GetLayerValue(ChatAgentSetting, SettingScope.User) is { ValueKind: JsonValueKind.String } value && value.GetString() is { Length: > 0 } name ? name : null;

    /// <summary>Remembers the agent AI Chat answers with (null: the built-in assistant) in the user's settings.</summary>
    public Task SaveChatAgentAsync(string? name, CancellationToken cancellationToken) =>
        ChatAgent == name ? Task.CompletedTask : _settings.SetAsync(ChatAgentSetting, name ?? string.Empty, SettingScope.User, cancellationToken);

    /// <summary>The configured agents, read from the user's settings only.</summary>
    public IReadOnlyDictionary<string, AcpAgentConfig> Configured => Parse(_settings.GetLayerValue(AgentsSetting, SettingScope.User));

    /// <summary>Why entries of the user's <c>ai.agents</c> were skipped (a bad <c>secretEnv</c> name…), one line each.</summary>
    public IReadOnlyList<string> Problems
    {
        get
        {
            var problems = new List<string>();
            Parse(_settings.GetLayerValue(AgentsSetting, SettingScope.User), problems);
            return problems;
        }
    }

    /// <summary>The folder the connection test runs agents in: empty, the IDE's own, no workspace (ADR 0083).</summary>
    public string ConnectTestDirectory => Path.Combine(ProcessDirectory, "connect-test");

    /// <summary>The session that runs, if any.</summary>
    public AcpAgentSession? Current => Volatile.Read(ref _current);

    /// <summary>A session started or stopped, or its commands or modes changed (any thread).</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// Parses <c>{ "name": { "command": "…", "args": ["…"], "env": { "K": "V" }, "secretEnv": ["K"], "unsetEnv": ["K"],
    /// "template": "…", "node": "builtIn" } }</c>; entries without a command are skipped, and so are entries whose
    /// <c>secretEnv</c> or <c>unsetEnv</c> has a name that isn't a variable's, is one Codalus refuses an agent to set
    /// (<see cref="AcpTerminals.RefusedVariable"/>), or is also in <c>env</c> (why goes to <paramref name="problems"/>).
    /// </summary>
    public static IReadOnlyDictionary<string, AcpAgentConfig> Parse(JsonElement? value, List<string>? problems = null)
    {
        var agents = new Dictionary<string, AcpAgentConfig>(StringComparer.Ordinal);
        foreach (var (name, server) in Mcp.McpServers.Parse(value))
        {
            var entry = value!.Value.GetProperty(name);
            var secretEnv = Names(entry, "secretEnv");
            var unsetEnv = Names(entry, "unsetEnv");
            if (secretEnv.Concat(unsetEnv).FirstOrDefault(n => !IsVariableName(n)) is { } bad)
            {
                problems?.Add($"ai.agents.{name}: '{bad}' isn't a variable name, so the entry is skipped.");
                continue;
            }

            if (AcpTerminals.RefusedVariable(secretEnv) is { } refused)
            {
                problems?.Add($"ai.agents.{name}: secretEnv can't set {refused} (it changes what runs), so the entry is skipped.");
                continue;
            }

            if (secretEnv.FirstOrDefault(n => server.Env.ContainsKey(n)) is { } both)
            {
                problems?.Add($"ai.agents.{name}: {both} is in both env and secretEnv, so the entry is skipped.");
                continue;
            }

            agents[name] = new AcpAgentConfig(server.Command, server.Args, server.Env)
            {
                SecretEnv = secretEnv,
                UnsetEnv = unsetEnv,
                Template = entry.TryGetProperty("template", out var template) && template.ValueKind == JsonValueKind.String ? template.GetString() : null,
                Node = entry.TryGetProperty("node", out var node) && node.ValueKind == JsonValueKind.String ? node.GetString() : null,
                Choices = entry.TryGetProperty(ChoicesMember, out var choices) && choices.ValueKind == JsonValueKind.Object
                    ? choices.EnumerateObject().Where(c => c.Value.ValueKind == JsonValueKind.String).ToDictionary(c => c.Name, c => c.Value.GetString()!, StringComparer.Ordinal)
                    : new Dictionary<string, string>(StringComparer.Ordinal),
            };
        }

        return agents;

        static IReadOnlyList<string> Names(JsonElement entry, string property) =>
            entry.TryGetProperty(property, out var list) && list.ValueKind == JsonValueKind.Array
                ? [.. list.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? e.GetString()! : "")]
                : [];
    }

    /// <summary>A variable's name: a letter or underscore, then letters, digits and underscores.</summary>
    public static bool IsVariableName(string name) =>
        name.Length > 0 && (char.IsAsciiLetter(name[0]) || name[0] == '_') && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');

    /// <summary>Why an external agent can't start now, or null when it can: AI on, a workspace, and a trusted one (ADR 0079).</summary>
    public string? WhyNot()
    {
        if (IsRemoteWindow())
        {
            return "External agents don't run in a remote window: the agent and its commands would run on this machine, not where the folder is.";
        }

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
    public async Task<AcpAgentSession> GetOrStartAsync(string name, IAcpHost host, CancellationToken cancellationToken, bool signIn = true)
    {
        if (WhyNot() is { } reason)
        {
            throw new AcpException(reason);
        }

        if (!Configured.TryGetValue(name, out var config))
        {
            throw new AcpException(Problems.FirstOrDefault(p => p.StartsWith($"ai.agents.{name}:", StringComparison.Ordinal)) ?? $"There is no agent '{name}' in your settings (ai.agents).");
        }

        config = await ResolveAsync(name, config, cancellationToken).ConfigureAwait(false);
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

            var session = new AcpAgentSession(name, config, _ai, host, root) { CommandTimeout = CommandTimeout, Gate = () => WhyNot() ?? (string.Equals(_ai.WorkspaceRoot, root, StringComparison.Ordinal) ? null : "The folder changed."), ProcessDirectory = ProcessDirectory };
            session.StateChanged += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
            try
            {
                await session.StartAsync(cancellationToken, signIn).ConfigureAwait(false);
                await session.ApplyChoicesAsync(config.Choices, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await session.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            // The folder may have changed, or AI or trust turned off, while it started: it doesn't stay for the old one.
            var now = WhyNot();
            if (now is not null || !string.Equals(_ai.WorkspaceRoot, root, StringComparison.Ordinal))
            {
                await session.DisposeAsync().ConfigureAwait(false);
                throw new AcpException(now ?? "The folder changed while the agent started; ask again.");
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

    /// <summary>
    /// Keeps the user's pick of a choice in the agent's entry (<c>ai.agents.&lt;name&gt;.choices.&lt;id&gt;</c>, the user's settings,
    /// the entry's other members kept), so the next session starts with it (M51). Nothing when the entry is gone.
    /// </summary>
    public async Task SaveChoiceAsync(string name, string id, string value, CancellationToken cancellationToken)
    {
        if (_settings.GetLayerValue(AgentsSetting, SettingScope.User) is not { ValueKind: JsonValueKind.Object } all
            || !all.TryGetProperty(name, out var entry) || entry.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var updated = System.Text.Json.Nodes.JsonNode.Parse(entry.GetRawText())!.AsObject();
        if (updated[ChoicesMember] is not System.Text.Json.Nodes.JsonObject choices)
        {
            choices = [];
            updated[ChoicesMember] = choices;
        }

        if (choices[id]?.GetValueKind() == JsonValueKind.String && choices[id]!.GetValue<string>() == value)
        {
            return;
        }

        choices[id] = value;
        using var document = JsonDocument.Parse(updated.ToJsonString());
        await _settings.SetMemberAsync(AgentsSetting, name, document.RootElement.Clone(), SettingScope.User, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// <paramref name="config"/> ready to start: its <c>secretEnv</c> values (read once and kept for later sessions until a
    /// key is stored or deleted, so a turn doesn't ask the OS store, which can prompt or be slow) and Codalus's Node.js
    /// folder for <c>"node": "builtIn"</c>. Throws <see cref="AcpException"/> with what to do when one is missing.
    /// </summary>
    public async Task<AcpAgentConfig> ResolveAsync(string name, AcpAgentConfig config, CancellationToken cancellationToken)
    {
        if (config.Node is { } node)
        {
            if (node != "builtIn")
            {
                throw new AcpException($"ai.agents.{name}: \"node\" can only be \"builtIn\" (Codalus's own Node.js), or left out for yours.");
            }

            config = config with { NodeFolder = BuiltInNodeFolder() ?? throw new AcpException($"{name} runs on Codalus's own Node.js, which isn't installed: connect it again (AI: Connect to an Agent…).") };
        }

        if (config.SecretEnv.Count == 0)
        {
            return config;
        }

        if (Secrets is not { } secrets)
        {
            throw new AcpException($"{name}'s keys ({string.Join(", ", config.SecretEnv)}) are kept in the credential store, which this window can't use.");
        }

        var names = string.Join('\n', config.SecretEnv);
        lock (_cacheGate)
        {
            if (_secretCache.TryGetValue(name, out var cached) && cached.Names == names)
            {
                return config with { SecretValues = cached.Values };
            }
        }

        var (values, missing) = await secrets.ResolveAsync(name, config.SecretEnv, cancellationToken).ConfigureAwait(false);
        if (missing.Count > 0)
        {
            throw new AcpException($"{name}'s {string.Join(", ", missing)} isn't in {secrets.StoreName}: connect it again with its key (AI: Connect to an Agent…), or remove it from secretEnv in its entry.");
        }

        lock (_cacheGate)
        {
            _secretCache[name] = (names, values);
        }

        return config with { SecretValues = values };
    }

    /// <summary>Stops the running session when it is <paramref name="name"/>'s (Connect, Check and Disconnect of that agent).</summary>
    public async Task StopAgentAsync(string name)
    {
        if (Current is { } session && session.Name == name)
        {
            await StopAsync().ConfigureAwait(false);
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
