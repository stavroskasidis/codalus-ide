using System.Globalization;
using System.Text;
using System.Text.Json;
using Codalus.Rpc;
using Codalus.Workbench.Ai.Agent;

namespace Codalus.Workbench.Ai.Acp;

/// <summary>
/// One chat session with an external agent (M48, ADR 0079): its process and ACP session, the turns it answers, and the
/// IDE's side of what it asks for. Every request passes the same checks as the built-in agent's tools (ADR 0063): paths
/// inside the workspace only (links included), never a file the AI boundary excludes, text redacted, every write through
/// the refactoring preview, every permission and command through the approval dialog with Reject the default. Each
/// request and tool call is a line of the AI Output channel (the method and its path or command, never content).
/// </summary>
public sealed class AcpAgentSession : IAcpClientHandler, IAsyncDisposable
{
    /// <summary>How long a cancelled turn waits for the agent to confirm before it ends anyway.</summary>
    public static TimeSpan CancelGrace { get; set; } = TimeSpan.FromSeconds(10);

    private const int MaxOutputShown = 8_000;

    private readonly AiService _ai;
    private readonly IAcpHost _host;
    private readonly AcpTerminals _terminals = new();
    private readonly Lock _gate = new();
    private readonly HashSet<(string Path, string Text)> _reviewed = [];
    private readonly HashSet<(string ToolCall, string Path)> _reloaded = [];
    private AcpClient? _client;
    private IDisposable? _mcpLifetime;
    private string? _sessionId;
    private AiChatTurn? _turn;
    private Action? _turnChanged;
    private CancellationTokenSource _turnCancelled = new();
    private IReadOnlyList<AcpAvailableCommand> _commands = [];
    private IReadOnlyList<AcpSessionMode> _modes = [];
    private string? _modeId;
    private int _disposed;

    public AcpAgentSession(string name, AcpAgentConfig config, AiService ai, IAcpHost host, string workspaceRoot)
    {
        Name = name;
        Config = config;
        _ai = ai;
        _host = host;
        WorkspaceRoot = Path.GetFullPath(workspaceRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        _terminals.Changed += OnTerminalChanged;
    }

    /// <summary>The agent's name in <c>ai.agents</c>.</summary>
    public string Name { get; }

    public AcpAgentConfig Config { get; }

    /// <summary>The session's <c>cwd</c>: the workspace's root, the only folder it works in.</summary>
    public string WorkspaceRoot { get; }

    /// <summary>The agent's answer to <c>initialize</c> (its name, capabilities, ways to sign in).</summary>
    public AcpInitializeResponse? Info => _client?.Info;

    /// <summary>The <c>mcpServers</c> sent with <c>session/new</c>.</summary>
    public IReadOnlyList<AcpMcpServerStdio> McpServers { get; private set; } = [];

    public string? SessionId => _sessionId;

    /// <summary>The agent's process runs and its session exists.</summary>
    public bool IsAlive => _client is { IsRunning: true } && _sessionId is not null && Volatile.Read(ref _disposed) == 0;

    /// <summary>How long a command may run (<c>ai.agent.commandTimeoutSeconds</c>).</summary>
    public TimeSpan CommandTimeout
    {
        get => _terminals.Timeout;
        set => _terminals.Timeout = value;
    }

    /// <summary>The agent's slash commands, offered in the input.</summary>
    public IReadOnlyList<AcpAvailableCommand> Commands
    {
        get
        {
            lock (_gate)
            {
                return _commands;
            }
        }
    }

    /// <summary>The agent's modes (empty when it has none) and the current one.</summary>
    public IReadOnlyList<AcpSessionMode> Modes
    {
        get
        {
            lock (_gate)
            {
                return _modes;
            }
        }
    }

    public string? CurrentModeId => Volatile.Read(ref _modeId);

    /// <summary>The commands, the modes or whether it is alive changed (any thread).</summary>
    public event EventHandler? StateChanged;

    // ------------------------------------------------------------------ lifetime

    /// <summary>Starts the agent's process, initializes, and creates the session (signing in first when the agent asks).</summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _ai.LogAgent(Name, $"starting {Config.Command} in {WorkspaceRoot}");
        _client = await AcpClient.StartAsync(Name, Config, WorkspaceRoot, this, cancellationToken).ConfigureAwait(false);
        _client.Exited += OnExited;
        var info = _client.Info!;
        _ai.LogAgent(Name, string.Create(CultureInfo.InvariantCulture, $"initialize → {info.AgentInfo?.Title ?? info.AgentInfo?.Name ?? Name} {info.AgentInfo?.Version}, protocol {info.ProtocolVersion}, {info.AuthMethods?.Count ?? 0} sign-in method(s)"));

        if (_host.StartMcpServer(Name) is { } mcp)
        {
            _mcpLifetime = mcp.Lifetime;
            McpServers = [mcp.Server];
        }

        var request = new AcpNewSessionRequest(WorkspaceRoot, McpServers);
        AcpNewSessionResponse created;
        try
        {
            created = await _client.NewSessionAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (AcpAuthRequiredException ex)
        {
            var methods = ex.Methods.Where(m => !m.IsTerminal).ToList();
            _ai.LogAgent(Name, "session/new → sign-in needed");
            if (methods.Count == 0)
            {
                throw new AcpException($"{Name} needs you to sign in, in a way Codalus IDE can't run here. Sign in with the agent's own command in a terminal, then try again.");
            }

            if (await _host.ChooseSignInAsync(Name, methods, cancellationToken).ConfigureAwait(false) is not { } method)
            {
                throw new AcpException($"{Name} needs you to sign in first.");
            }

            _ai.LogAgent(Name, "authenticate " + method.Id);
            await _client.AuthenticateAsync(method.Id, cancellationToken).ConfigureAwait(false);
            created = await _client.NewSessionAsync(request, cancellationToken).ConfigureAwait(false);
        }

        _sessionId = created.SessionId;
        lock (_gate)
        {
            _modes = created.Modes?.AvailableModes ?? [];
        }

        _modeId = created.Modes?.CurrentModeId;
        _ai.LogAgent(Name, string.Create(CultureInfo.InvariantCulture, $"session/new → {_sessionId} (cwd {WorkspaceRoot}, {McpServers.Count} MCP server(s))"));
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnExited(object? sender, EventArgs e)
    {
        _ai.LogAgent(Name, "the agent's process ended");
        _terminals.KillAll();
        try
        {
            _turnCancelled.Cancel(); // pending approvals close
        }
        catch (ObjectDisposedException)
        {
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _turnCancelled.CancelAsync().ConfigureAwait(false);
        if (_client is { } client)
        {
            client.Exited -= OnExited;
            await client.DisposeAsync().ConfigureAwait(false);
            _ai.LogAgent(Name, "stopped");
        }

        await _terminals.DisposeAsync().ConfigureAwait(false);
        _mcpLifetime?.Dispose();
        _turnCancelled.Dispose();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    // ------------------------------------------------------------------ turns

    /// <summary>
    /// Sends <paramref name="text"/> and streams the turn into <paramref name="turn"/> until the agent answers with its stop
    /// reason. Cancelling sends <c>session/cancel</c>, answers pending permissions with <c>cancelled</c>, kills the turn's
    /// commands, and waits a little for the agent to confirm. A crash throws <see cref="AcpException"/>.
    /// </summary>
    public async Task<string> PromptAsync(string text, AiChatTurn turn, Action changed, CancellationToken cancellationToken)
    {
        if (!IsAlive || _client is not { } client)
        {
            throw new AcpException(_client?.StoppedMessage() ?? $"The agent '{Name}' isn't running.");
        }

        CancellationTokenSource turnCancelled;
        lock (_gate)
        {
            _turn = turn;
            _turnChanged = changed;
            _reviewed.Clear();
            _reloaded.Clear();
            if (_turnCancelled.IsCancellationRequested)
            {
                _turnCancelled.Dispose();
                _turnCancelled = new CancellationTokenSource();
            }

            turnCancelled = _turnCancelled;
        }

        using var giveUp = new CancellationTokenSource();
        using var registration = cancellationToken.Register(() => _ = CancelTurnAsync(turnCancelled, giveUp));
        try
        {
            var response = await client.PromptAsync(new AcpPromptRequest(_sessionId!, [AcpContentBlock.FromText(text)]), giveUp.Token).ConfigureAwait(false);
            _ai.LogAgent(Name, "session/prompt → " + response.StopReason);
            if (cancellationToken.IsCancellationRequested)
            {
                // The agent can answer (its permission cancelled) before CancelTurnAsync gets to the steps.
                FinishSteps(turn, "cancelled");
            }

            return response.StopReason;
        }
        catch (OperationCanceledException) when (giveUp.IsCancellationRequested)
        {
            _ai.LogAgent(Name, "session/prompt → the agent didn't confirm the cancel");
            FinishSteps(turn, "cancelled");
            return "cancelled";
        }
        catch (AcpException) when (cancellationToken.IsCancellationRequested && client.IsRunning)
        {
            // An agent that answers a cancelled turn with an error rather than its stop reason: still a cancel.
            FinishSteps(turn, "cancelled");
            return "cancelled";
        }
        catch (AcpException ex)
        {
            _ai.LogAgent(Name, "session/prompt failed: " + FirstLine(ex.Message));
            FinishSteps(turn, "failed");
            throw;
        }
        finally
        {
            lock (_gate)
            {
                _turn = null;
                _turnChanged = null;
            }
        }
    }

    private async Task CancelTurnAsync(CancellationTokenSource turnCancelled, CancellationTokenSource giveUp)
    {
        if (_client is not { } client || _sessionId is not { } sessionId)
        {
            return;
        }

        _ai.LogAgent(Name, "session/cancel");
        try
        {
            await turnCancelled.CancelAsync().ConfigureAwait(false); // pending permissions answer "cancelled", approvals close
        }
        catch (ObjectDisposedException)
        {
        }

        await client.CancelAsync(sessionId).ConfigureAwait(false);
        _terminals.KillAll();
        if (CurrentTurn() is { } turn)
        {
            FinishSteps(turn, "cancelled");
            Changed();
        }

        try
        {
            giveUp.CancelAfter(CancelGrace);
        }
        catch (ObjectDisposedException)
        {
            // The turn ended meanwhile.
        }
    }

    /// <summary>Marks the steps that haven't finished as <paramref name="status"/>.</summary>
    private static void FinishSteps(AiChatTurn turn, string status) =>
        turn.UpdateSteps(
            s => s.Id is not null && s.Status is not ("completed" or "failed" or "cancelled"),
            s => s with { Status = status, Outcome = status, IsError = status == "failed" });

    public async Task SetModeAsync(string modeId, CancellationToken cancellationToken)
    {
        if (_client is not { } client || _sessionId is not { } sessionId)
        {
            return;
        }

        _ai.LogAgent(Name, "session/set_mode " + modeId);
        await client.SetModeAsync(sessionId, modeId, cancellationToken).ConfigureAwait(false);
        Volatile.Write(ref _modeId, modeId);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private AiChatTurn? CurrentTurn()
    {
        lock (_gate)
        {
            return _turn;
        }
    }

    private void Changed()
    {
        Action? changed;
        lock (_gate)
        {
            changed = _turnChanged;
        }

        changed?.Invoke();
    }

    private CancellationToken TurnToken
    {
        get
        {
            lock (_gate)
            {
                return _turnCancelled.Token;
            }
        }
    }

    // ------------------------------------------------------------------ session/update

    public Task OnUpdateAsync(AcpSessionNotification notification)
    {
        if (notification.Update.ValueKind != JsonValueKind.Object || !notification.Update.TryGetProperty("sessionUpdate", out var kind))
        {
            return Task.CompletedTask;
        }

        var update = notification.Update;
        var json = AcpJsonContext.Default;
        var turn = CurrentTurn();
        switch (kind.GetString())
        {
            case "agent_message_chunk" when turn is not null:
                turn.Append(Text(update.Deserialize(json.AcpContentChunk)?.Content));
                break;
            case "agent_thought_chunk" when turn is not null:
                turn.AppendThought(Text(update.Deserialize(json.AcpContentChunk)?.Content));
                break;
            case "tool_call" when turn is not null:
                OnToolCall(turn, update.Deserialize(json.AcpToolCall)!);
                break;
            case "tool_call_update" when turn is not null:
                OnToolCall(turn, update.Deserialize(json.AcpToolCall)!);
                break;
            case "plan" when turn is not null:
                turn.SetPlan([.. (update.Deserialize(json.AcpPlan)?.Entries ?? []).Select(e => new AiPlanEntry(e.Content, e.Status ?? "pending"))]);
                break;
            case "notice" when turn is not null:
                var notice = update.Deserialize(json.AcpNotice);
                if (notice?.Title is { Length: > 0 } title)
                {
                    turn.SetNotice(null, new AiTurnNotice(notice.Severity ?? "info", title));
                }

                break;
            case "compaction_update" when turn is not null:
                var compaction = update.Deserialize(json.AcpCompactionUpdate)!;
                turn.SetNotice("compaction:" + compaction.CompactionId, compaction.Status switch
                {
                    "completed" => new AiTurnNotice("info", "The agent compacted the conversation's context."),
                    "failed" => new AiTurnNotice("warning", "The agent's context compaction failed" + (compaction.Error is { Length: > 0 } error ? ": " + error : ".")),
                    "cancelled" => new AiTurnNotice("info", "The agent's context compaction was cancelled."),
                    _ => new AiTurnNotice("info", "The agent is compacting the conversation's context…"),
                });
                _ai.LogAgent(Name, "compaction " + (compaction.Status ?? "in_progress"));
                break;
            case "available_commands_update":
                lock (_gate)
                {
                    _commands = update.Deserialize(json.AcpAvailableCommandsUpdate)?.AvailableCommands ?? [];
                }

                StateChanged?.Invoke(this, EventArgs.Empty);
                return Task.CompletedTask;
            case "current_mode_update":
                Volatile.Write(ref _modeId, update.Deserialize(json.AcpCurrentModeUpdate)?.CurrentModeId);
                StateChanged?.Invoke(this, EventArgs.Empty);
                return Task.CompletedTask;
            default:
                // user_message_chunk (a replay), usage, session info and config options: nothing to show.
                return Task.CompletedTask;
        }

        Changed();
        return Task.CompletedTask;
    }

    /// <summary>A content block as chat text: text as it is, a link or resource by its name, other media as a placeholder.</summary>
    private static string Text(AcpContentBlock? block) => block?.Type switch
    {
        null => "",
        "text" => block.Text ?? "",
        "resource_link" => $"[{block.Title ?? block.Name ?? block.Uri}]({block.Uri})",
        "resource" => block.Resource?.Text ?? $"[{block.Resource?.Uri}]",
        _ => $"[{block.Type}]",
    };

    private void OnToolCall(AiChatTurn turn, AcpToolCall call)
    {
        var locations = call.Locations?.Select(l => new AiAgentLocation(l.Path, l.Line is { } line ? Math.Max(1, line) : null)).ToList();
        var diffs = call.Content?.Where(c => c.Type == "diff" && c.Path is { Length: > 0 }).Select(c => new AiStepDiff(c.Path!, c.OldText, c.NewText ?? "", IsReviewed(c.Path!, c.NewText ?? ""))).ToList();
        var terminal = call.Content?.FirstOrDefault(c => c.Type == "terminal")?.TerminalId;
        var text = call.Content is { } content ? string.Join("\n", content.Where(c => c.Type == "content").Select(c => Text(c.Content))) : null;
        var existing = turn.FindStep(call.ToolCallId);
        if (existing is null)
        {
            var title = call.Title ?? call.Kind ?? "tool call";
            var step = new AiAgentStep(call.Kind ?? "other", title, null)
            {
                Id = call.ToolCallId,
                Kind = call.Kind ?? "other",
                Status = call.Status ?? "pending",
                Locations = locations ?? [],
                Diffs = diffs ?? [],
                Output = text is { Length: > 0 } ? Cut(text) : null,
                TerminalId = terminal,
            };
            turn.AddStep(Fill(step));
            _ai.LogAgent(Name, $"tool_call {step.Kind} \"{Short(title)}\"{Where(step)}");
        }
        else
        {
            var before = existing.Status;
            turn.UpdateStep(call.ToolCallId, step => Fill(step with
            {
                Tool = call.Kind ?? step.Tool,
                Kind = call.Kind ?? step.Kind,
                Target = call.Title ?? step.Target,
                Status = call.Status ?? step.Status,
                Locations = locations ?? step.Locations,
                Diffs = diffs ?? step.Diffs,
                Output = text is { Length: > 0 } ? Cut(text) : step.Output,
                TerminalId = terminal ?? step.TerminalId,
            }));
            if (call.Status is { } status && status != before && status is "completed" or "failed")
            {
                var updated = turn.FindStep(call.ToolCallId)!;
                _ai.LogAgent(Name, $"tool_call {updated.Kind} \"{Short(updated.Target)}\"{Where(updated)} → {status}");
            }
        }

        // A change the agent made itself (not through fs/write_text_file): the open editor reloads it from the disk.
        if (turn.FindStep(call.ToolCallId) is { Status: "completed" } done)
        {
            foreach (var diff in done.Diffs.Where(d => !d.Reviewed))
            {
                string full;
                try
                {
                    full = WorkspacePaths.Resolve(WorkspaceRoot, diff.Path);
                }
                catch (AgentToolException)
                {
                    continue;
                }

                bool first;
                lock (_gate)
                {
                    first = _reloaded.Add((call.ToolCallId, full));
                }

                if (first)
                {
                    _ai.LogAgent(Name, $"changed {Relative(full)} itself (not reviewed in the preview)");
                    _ = _host.ReloadFromDiskAsync(full);
                }
            }
        }

        AiAgentStep Fill(AiAgentStep step)
        {
            var outcome = step.Status switch
            {
                "completed" => "done",
                "failed" => "failed",
                "cancelled" => "cancelled",
                _ => null,
            };
            step = step with { Outcome = outcome, IsError = step.Status == "failed" };
            return step.TerminalId is { } id && _terminals.Find(id) is { } t ? step with { Output = Shown(t) } : step;
        }
    }

    private bool IsReviewed(string path, string text)
    {
        try
        {
            var full = WorkspacePaths.Resolve(WorkspaceRoot, path);
            lock (_gate)
            {
                return _reviewed.Contains((full, text));
            }
        }
        catch (AgentToolException)
        {
            return false;
        }
    }

    private string Where(AiAgentStep step) =>
        step.Locations.Count > 0 ? " (" + string.Join(", ", step.Locations.Take(3).Select(l => RelativeOrAsIs(l.Path))) + ")" : "";

    private string RelativeOrAsIs(string path)
    {
        try
        {
            return Relative(WorkspacePaths.Resolve(WorkspaceRoot, path));
        }
        catch (AgentToolException)
        {
            return path;
        }
    }

    private static string Short(string text) => FirstLine(text) is var line && line.Length > 120 ? line[..120] + "…" : line;

    private static string FirstLine(string text) => text.Split('\n', 2)[0].Trim();

    private static string Cut(string text) => text.Length > MaxOutputShown ? "…" + text[^MaxOutputShown..] : text;

    private static string Shown(AcpTerminal terminal) => Cut(SecretRedactor.Redact(terminal.Output.Text).Text);

    private void OnTerminalChanged(object? sender, AcpTerminal terminal)
    {
        if (CurrentTurn() is not { } turn)
        {
            return;
        }

        var shown = Shown(terminal);
        turn.UpdateSteps(s => s.TerminalId == terminal.Id, s => s with { Output = shown });
        Changed();
    }

    // ------------------------------------------------------------------ the agent's requests

    private string Relative(string full) => Path.GetRelativePath(WorkspaceRoot, full).Replace('\\', '/');

    /// <summary>A path inside the workspace that the AI boundary doesn't exclude; anything else is refused with why.</summary>
    private async Task<string> AllowedPathAsync(string method, string path, CancellationToken cancellationToken)
    {
        string full;
        try
        {
            full = WorkspacePaths.Resolve(WorkspaceRoot, path);
        }
        catch (AgentToolException ex)
        {
            _ai.LogAgent(Name, $"{method} {path} → refused: {ex.Message}");
            throw new JsonRpcHandlerException(JsonRpcConnection.InvalidParams, ex.Message + " Codalus IDE refused it.");
        }

        if (await _ai.Exclusions.ExclusionReasonAsync(full, WorkspaceRoot, cancellationToken).ConfigureAwait(false) is { } reason)
        {
            _ai.LogAgent(Name, $"{method} {Relative(full)} → refused: excluded ({reason})");
            throw new JsonRpcHandlerException(JsonRpcConnection.InvalidParams, $"'{Relative(full)}' is excluded from AI ({reason}). Codalus IDE refused it.");
        }

        return full;
    }

    public async Task<AcpReadTextFileResponse> ReadTextFileAsync(AcpReadTextFileRequest request, CancellationToken cancellationToken)
    {
        var full = await AllowedPathAsync(AcpMethods.ReadTextFile, request.Path, cancellationToken).ConfigureAwait(false);
        if (await _host.ReadTextAsync(full, cancellationToken).ConfigureAwait(false) is not { } text)
        {
            _ai.LogAgent(Name, $"{AcpMethods.ReadTextFile} {Relative(full)} → doesn't exist");
            throw new JsonRpcHandlerException(AcpMethods.ResourceNotFound, $"'{Relative(full)}' doesn't exist.");
        }

        var range = "";
        if (request.Line is not null || request.Limit is not null)
        {
            var lines = text.Split('\n');
            var first = Math.Clamp((request.Line ?? 1) - 1, 0, lines.Length);
            var count = Math.Clamp(request.Limit ?? lines.Length, 0, lines.Length - first);
            text = string.Join('\n', lines, first, count);
            range = string.Create(CultureInfo.InvariantCulture, $" (lines {first + 1}–{first + count})");
        }

        var (redacted, redactions) = SecretRedactor.Redact(text);
        _ai.LogAgent(Name, string.Create(CultureInfo.InvariantCulture, $"{AcpMethods.ReadTextFile} {Relative(full)}{range}{(redactions > 0 ? $", {redactions} secret(s) redacted" : "")}"));
        return new AcpReadTextFileResponse(redacted);
    }

    public async Task WriteTextFileAsync(AcpWriteTextFileRequest request, CancellationToken cancellationToken)
    {
        var full = await AllowedPathAsync(AcpMethods.WriteTextFile, request.Path, cancellationToken).ConfigureAwait(false);
        var current = await _host.ReadTextAsync(full, cancellationToken).ConfigureAwait(false);
        if (current == request.Content)
        {
            _ai.LogAgent(Name, $"{AcpMethods.WriteTextFile} {Relative(full)} → unchanged");
            return;
        }

        _ai.LogAgent(Name, $"{AcpMethods.WriteTextFile} {Relative(full)} → preview");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, TurnToken);
        var applied = await _host.ProposeWriteAsync(full, current, request.Content, Name, linked.Token).ConfigureAwait(false);
        _ai.LogAgent(Name, $"{AcpMethods.WriteTextFile} {Relative(full)} → {(applied ? "applied" : "rejected")}");
        if (!applied)
        {
            throw new JsonRpcHandlerException(JsonRpcConnection.InternalError, $"The user rejected the change to {Relative(full)}. Don't make it again; ask what they want instead.");
        }

        lock (_gate)
        {
            _reviewed.Add((full, request.Content));
        }

        if (CurrentTurn() is { } turn)
        {
            turn.UpdateSteps(s => s.Diffs.Any(d => !d.Reviewed && IsReviewed(d.Path, d.NewText)), s => s with { Diffs = [.. s.Diffs.Select(d => d with { Reviewed = d.Reviewed || IsReviewed(d.Path, d.NewText) })] });
            Changed();
        }
    }

    public async Task<AcpRequestPermissionResponse> RequestPermissionAsync(AcpRequestPermissionRequest request, CancellationToken cancellationToken)
    {
        var call = request.ToolCall ?? new AcpToolCall();
        var options = request.Options ?? [];

        // Allow always isn't offered (ADR 0063: no always-allow yet).
        var offered = options.Where(o => o.Kind is "allow_once" or "reject_once" or "reject_always").ToList();
        var title = call.Title ?? CurrentTurn()?.FindStep(call.ToolCallId)?.Target ?? call.Kind ?? "a tool call";
        var diffs = call.Content?.Where(c => c.Type == "diff" && c.Path is { Length: > 0 }).Select(c => new AiStepDiff(c.Path!, c.OldText, c.NewText ?? "", Reviewed: false)).ToList() ?? [];
        var raw = call.RawInput is { ValueKind: not (JsonValueKind.Undefined or JsonValueKind.Null) } input ? Pretty(input) : null;
        _ai.LogAgent(Name, $"{AcpMethods.SessionRequestPermission} {call.Kind ?? "other"} \"{Short(title)}\"");

        var turnToken = TurnToken;
        if (turnToken.IsCancellationRequested)
        {
            _ai.LogAgent(Name, $"{AcpMethods.SessionRequestPermission} → cancelled");
            return new AcpRequestPermissionResponse(AcpPermissionOutcome.Cancelled);
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, turnToken);
        string? chosen;
        try
        {
            chosen = await _host.RequestPermissionAsync(new AcpPermissionPrompt(Name, title, call.Kind, raw, diffs, offered), linked.Token).WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (turnToken.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            _ai.LogAgent(Name, $"{AcpMethods.SessionRequestPermission} → cancelled");
            return new AcpRequestPermissionResponse(AcpPermissionOutcome.Cancelled);
        }

        var option = offered.FirstOrDefault(o => o.OptionId == chosen) ?? offered.FirstOrDefault(o => o.Kind == "reject_once") ?? offered.FirstOrDefault(o => o.Kind == "reject_always");
        _ai.LogAgent(Name, $"{AcpMethods.SessionRequestPermission} → {option?.Kind ?? "cancelled"}");
        return new AcpRequestPermissionResponse(option is null ? AcpPermissionOutcome.Cancelled : AcpPermissionOutcome.Selected(option.OptionId));
    }

    private static string Pretty(JsonElement element)
    {
        var text = JsonSerializer.Serialize(element, AcpPrettyJson.Default.JsonElement);
        return text.Length > 4000 ? text[..4000] + "\n…" : text;
    }

    public async Task<AcpCreateTerminalResponse> CreateTerminalAsync(AcpCreateTerminalRequest request, CancellationToken cancellationToken)
    {
        string folder;
        try
        {
            folder = string.IsNullOrWhiteSpace(request.Cwd) ? WorkspaceRoot : WorkspacePaths.Resolve(WorkspaceRoot, request.Cwd);
        }
        catch (AgentToolException ex)
        {
            _ai.LogAgent(Name, $"{AcpMethods.TerminalCreate} {request.Command} in {request.Cwd} → refused: {ex.Message}");
            throw new JsonRpcHandlerException(JsonRpcConnection.InvalidParams, ex.Message + " Codalus IDE refused it.");
        }

        if (!Directory.Exists(folder))
        {
            throw new JsonRpcHandlerException(JsonRpcConnection.InvalidParams, $"'{Relative(folder)}' isn't a folder.");
        }

        var env = request.Env is { Count: > 0 } variables ? variables.ToDictionary(v => v.Name, v => v.Value, StringComparer.Ordinal) : null;
        var (display, launch) = AcpTerminals.CommandLine(request.Command, request.Args ?? [], folder, env);
        var where = folder == WorkspaceRoot ? "the workspace" : Relative(folder);
        _ai.LogAgent(Name, $"{AcpMethods.TerminalCreate} {Short(display)} in {where}");

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, TurnToken);
        bool allowed;
        try
        {
            allowed = await _host.ApproveCommandAsync(Name, display, folder, linked.Token).WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            allowed = false; // the turn was cancelled
        }

        if (!allowed)
        {
            _ai.LogAgent(Name, $"{AcpMethods.TerminalCreate} → not allowed");
            throw new JsonRpcHandlerException(JsonRpcConnection.InternalError, "The user didn't allow the command. Don't run it again; ask what they want instead.");
        }

        var terminal = await _terminals.StartAsync(display, launch, request.OutputByteLimit, cancellationToken).ConfigureAwait(false);
        _ai.LogAgent(Name, $"{AcpMethods.TerminalCreate} → {terminal.Id}");
        return new AcpCreateTerminalResponse(terminal.Id);
    }

    public Task<AcpTerminalOutputResponse> TerminalOutputAsync(AcpTerminalRequest request, CancellationToken cancellationToken)
    {
        var terminal = Terminal(request.TerminalId);
        var (text, truncated) = terminal.Output;
        return Task.FromResult(new AcpTerminalOutputResponse(SecretRedactor.Redact(text).Text, truncated, terminal.ExitStatus));
    }

    public async Task<AcpTerminalExitStatus> WaitForTerminalExitAsync(AcpTerminalRequest request, CancellationToken cancellationToken)
    {
        var status = await Terminal(request.TerminalId).Exited.WaitAsync(cancellationToken).ConfigureAwait(false);
        _ai.LogAgent(Name, string.Create(CultureInfo.InvariantCulture, $"{AcpMethods.TerminalWaitForExit} {request.TerminalId} → {(status.ExitCode is { } code ? "exit code " + code.ToString(CultureInfo.InvariantCulture) : status.Signal)}"));
        return status;
    }

    public Task KillTerminalAsync(AcpTerminalRequest request, CancellationToken cancellationToken)
    {
        _ai.LogAgent(Name, $"{AcpMethods.TerminalKill} {request.TerminalId}");
        Terminal(request.TerminalId).Kill();
        return Task.CompletedTask;
    }

    public async Task ReleaseTerminalAsync(AcpTerminalRequest request, CancellationToken cancellationToken)
    {
        if (_terminals.Find(request.TerminalId) is { } terminal && CurrentTurn() is { } turn)
        {
            // The step keeps showing what the command wrote.
            var shown = Shown(terminal);
            turn.UpdateSteps(s => s.TerminalId == terminal.Id, s => s with { Output = shown, TerminalId = null });
        }

        _ai.LogAgent(Name, $"{AcpMethods.TerminalRelease} {request.TerminalId}");
        await _terminals.ReleaseAsync(request.TerminalId).ConfigureAwait(false);
    }

    private AcpTerminal Terminal(string id) =>
        _terminals.Find(id) ?? throw new JsonRpcHandlerException(AcpMethods.ResourceNotFound, $"There is no terminal '{id}' (it was released, or never created).");
}

/// <summary>Indented JSON for showing a tool call's input in the approval dialog.</summary>
[System.Text.Json.Serialization.JsonSourceGenerationOptions(WriteIndented = true)]
[System.Text.Json.Serialization.JsonSerializable(typeof(JsonElement))]
internal sealed partial class AcpPrettyJson : System.Text.Json.Serialization.JsonSerializerContext;
