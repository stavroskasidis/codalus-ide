using System.Globalization;
using System.Text;
using System.Text.Json;
using Codalus.Rpc;

namespace Codalus.Workbench.Ai.Acp;

/// <summary>What a text session serves (ADR 0097): suggestions while typing, or the features the user asks for.</summary>
public enum AcpTextPurpose
{
    /// <summary>Inline completions and next-edit suggestions: the model for suggestions (picked, or guessed small).</summary>
    Suggestions,

    /// <summary>Commit messages, pull request descriptions and Fix with AI: the chat's model.</summary>
    Actions,
}

/// <summary>
/// A session of its own on AI Chat's agent connection for the IDE's AI features (ADR 0097): text in, text out. It gets no
/// <c>codalus mcp</c> pipe and refuses every file, terminal and permission request (each a line of the AI Output channel),
/// so what the agent gets from the IDE is only the prompt, prepared through <see cref="AiService"/>'s boundary. Its turns
/// are never shown in AI Chat; one prompt runs at a time.
/// </summary>
public sealed class AcpTextSession : IAcpClientHandler, IAsyncDisposable
{
    /// <summary>How long a cancelled prompt waits for the agent's <c>cancelled</c> before the next one may go.</summary>
    public static TimeSpan CancelGrace { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>The words that mark a small, fast model in the agents' lists, in order of preference.</summary>
    public static readonly IReadOnlyList<string> FastModelWords = ["haiku", "mini", "flash", "nano", "lite"];

    private const string RefusalMessage = "Codalus IDE asked this session for text only (an AI feature), so it refuses what the agent asks for.";

    private readonly AcpClient _client;
    private readonly AiService _ai;
    private readonly SemaphoreSlim _one = new(1, 1);
    private readonly Lock _gate = new();
    private IDisposable? _route;
    private StringBuilder? _reply;
    private int _refused;
    private int _disposed;

    private AcpTextSession(string agent, AcpClient client, AiService ai, AcpTextPurpose purpose)
    {
        Agent = agent;
        _client = client;
        _ai = ai;
        Purpose = purpose;
    }

    /// <summary>The agent's name in <c>ai.agents</c>.</summary>
    public string Agent { get; }

    public AcpTextPurpose Purpose { get; }

    /// <summary>The connection it runs on (AI Chat's): a new one (after a terminal sign-in) means a new text session.</summary>
    public AcpClient Client => _client;

    public string SessionId { get; private set; } = "";

    /// <summary>The model it uses (the value sent to the agent), or null when the agent offers no model choice.</summary>
    public string? Model { get; private set; }

    /// <summary>The agent's models, as its model choice lists them (empty when it offers none).</summary>
    public IReadOnlyList<AcpChoiceValue> Models { get; private set; } = [];

    public bool IsAlive => _client.IsRunning && Volatile.Read(ref _disposed) == 0;

    /// <summary>
    /// <c>session/new</c> in <paramref name="workspaceRoot"/> with no MCP servers on <paramref name="client"/>, then its model:
    /// <paramref name="model"/> when the agent offers it, else for suggestions the small one <see cref="GuessFastModel"/>
    /// finds, else <paramref name="chatModel"/> (the chat's) when offered, else the agent's default.
    /// </summary>
    public static async Task<AcpTextSession> CreateAsync(string agent, AcpClient client, AiService ai, string workspaceRoot, AcpTextPurpose purpose, string? model, string? chatModel, CancellationToken cancellationToken)
    {
        var session = new AcpTextSession(agent, client, ai, purpose);
        var created = await client.NewSessionAsync(new AcpNewSessionRequest(workspaceRoot, []), cancellationToken).ConfigureAwait(false);
        session.SessionId = created.SessionId;
        session._route = client.Route(created.SessionId, session);
        try
        {
            var choice = AcpSessionChoices.From(created.ConfigOptions, null, created.Models).FirstOrDefault(c => c.Category == AcpSessionChoices.ModelCategory);
            session.Models = choice?.Values ?? [];
            var (value, why) = ChooseModel(choice, purpose, model, chatModel);
            session.Model = value ?? choice?.CurrentValue;
            if (choice is not null && value is not null && value != choice.CurrentValue)
            {
                await session.SetModelAsync(choice, value, cancellationToken).ConfigureAwait(false);
            }

            ai.LogAgent(agent, $"session/new → {created.SessionId} ({Describe(purpose)}, no tools, model {session.Model ?? "the agent's default"}{why})");
            return session;
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>The model a text session uses, and why (for the log); null keeps the agent's default.</summary>
    public static (string? Value, string Why) ChooseModel(AcpSessionChoice? choice, AcpTextPurpose purpose, string? picked, string? chatModel)
    {
        if (choice is null)
        {
            return (null, "");
        }

        bool Offered(string? value) => value is { Length: > 0 } && choice.Values.Any(v => v.Value == value);
        if (purpose == AcpTextPurpose.Suggestions)
        {
            if (Offered(picked))
            {
                return (picked, ", picked for suggestions");
            }

            if (GuessFastModel(choice.Values) is { } guessed)
            {
                return (guessed, ", guessed small by its name");
            }
        }

        return Offered(chatModel) ? (chatModel, ", the chat's") : (null, "");
    }

    /// <summary>
    /// The model a small one's name suggests (<see cref="FastModelWords"/>, matched as words of its value or name, in that
    /// order of preference), or null. Of several, the one with the fewest such words (flash before flash-lite, the smaller
    /// one's quality drops more), then the last listed (agents list older models first).
    /// </summary>
    public static string? GuessFastModel(IReadOnlyList<AcpChoiceValue> models)
    {
        foreach (var word in FastModelWords)
        {
            var found = models.Select((m, i) => (Model: m, Index: i, Words: Words(m)))
                .Where(m => m.Words.Contains(word))
                .OrderBy(m => m.Words.Count(FastModelWords.Contains))
                .ThenByDescending(m => m.Index)
                .FirstOrDefault();
            if (found.Model is { } model)
            {
                return model.Value;
            }
        }

        return null;

        static HashSet<string> Words(AcpChoiceValue model) =>
            [.. (model.Value + " " + model.Name).Split([' ', '-', '_', '.', '/', ':', '(', ')', '[', ']'], StringSplitOptions.RemoveEmptyEntries).Select(w => w.ToLowerInvariant())];
    }

    private async Task SetModelAsync(AcpSessionChoice choice, string value, CancellationToken cancellationToken)
    {
        try
        {
            if (choice.Kind == AcpChoiceKind.Model)
            {
                await _client.SetModelAsync(SessionId, value, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await _client.SetConfigOptionAsync(SessionId, choice.Id, value, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (AcpException ex)
        {
            // The agent's default answers instead.
            _ai.LogAgent(Agent, $"the model {value} was refused for {Describe(Purpose)}: {ex.Message}");
            Model = choice.CurrentValue;
        }
    }

    private static string Describe(AcpTextPurpose purpose) => purpose == AcpTextPurpose.Suggestions ? "suggestions" : "AI actions";

    /// <summary>
    /// Sends <paramref name="prompt"/> and returns the agent's message once it ends its turn (thoughts and tool calls left
    /// out). One prompt at a time: the next waits. Cancelling sends <c>session/cancel</c> and waits up to
    /// <see cref="CancelGrace"/> for the agent to confirm. A crash or a refusal throws <see cref="AcpException"/>.
    /// </summary>
    public async Task<string> AskAsync(string prompt, CancellationToken cancellationToken)
    {
        await _one.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!IsAlive)
            {
                throw new AcpException(_client.StoppedMessage());
            }

            var reply = new StringBuilder();
            lock (_gate)
            {
                _reply = reply;
            }

            Volatile.Write(ref _refused, 0);
            var stopReason = "end_turn";

            var answer = _client.PromptAsync(new AcpPromptRequest(SessionId, [AcpContentBlock.FromText(prompt)]), CancellationToken.None);
            try
            {
                var response = await answer.WaitAsync(cancellationToken).ConfigureAwait(false);
                stopReason = response.StopReason;
                if (response.StopReason == "refusal")
                {
                    throw new AcpException($"{Agent} refused to answer.");
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await _client.CancelAsync(SessionId).ConfigureAwait(false);
                try
                {
                    await answer.WaitAsync(CancelGrace, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is TimeoutException or AcpException or JsonRpcConnectionLostException)
                {
                    // Gone or slow: the next prompt goes anyway.
                }

                throw;
            }

            lock (_gate)
            {
                _reply = null;
            }

            // An agent that ended its turn without text (it reached for a tool the session refuses, and gave up, or it
            // stopped) is a failure the user sees, not an empty commit message or fix that looks like nothing happened.
            var refused = Volatile.Read(ref _refused);
            _ai.LogAgent(Agent, string.Create(CultureInfo.InvariantCulture, $"session/prompt → {stopReason}, {reply.Length:N0} characters{(refused > 0 ? $", {refused} request(s) refused" : "")}"));
            if (reply.ToString().Trim().Length == 0)
            {
                throw new AcpException(refused > 0
                    ? $"{Agent} ended its turn without an answer: it asked to use its own tools ({refused} time(s)), which Codalus IDE refuses for AI features, where everything it needs is in the request. Ask again, or use AI Chat."
                    : $"{Agent} ended its turn without an answer ({stopReason}).");
            }

            return reply.ToString();
        }
        finally
        {
            lock (_gate)
            {
                _reply = null;
            }

            _one.Release();
        }
    }

    public Task OnUpdateAsync(AcpSessionNotification notification)
    {
        if (notification.Update.ValueKind == JsonValueKind.Object
            && notification.Update.TryGetProperty("sessionUpdate", out var kind) && kind.GetString() == "agent_message_chunk"
            && notification.Update.Deserialize(AcpJsonContext.Default.AcpContentChunk)?.Content is { Type: "text", Text: { } text })
        {
            lock (_gate)
            {
                _reply?.Append(text);
            }
        }

        return Task.CompletedTask;
    }

    private JsonRpcHandlerException Refuse(string method)
    {
        Interlocked.Increment(ref _refused);
        _ai.LogAgent(Agent, string.Create(CultureInfo.InvariantCulture, $"{method} → refused ({Describe(Purpose)} ask for text only)"));
        return new JsonRpcHandlerException(JsonRpcConnection.InvalidParams, RefusalMessage);
    }

    /// <summary>
    /// A refusal, with the agent's own reject option: <c>cancelled</c> is only the answer to a turn the client cancelled
    /// (ACP), and Claude's adapter, given it for its turn's tool, aborted the tool use, so the turn ended without text and
    /// a commit message came back empty (ADR 0097). Without a reject option, <c>cancelled</c> is all that's left.
    /// </summary>
    public Task<AcpRequestPermissionResponse> RequestPermissionAsync(AcpRequestPermissionRequest request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _refused);
        var options = request.Options ?? [];
        var reject = options.FirstOrDefault(o => o.Kind == "reject_once") ?? options.FirstOrDefault(o => o.Kind == "reject_always");
        _ai.LogAgent(Agent, $"{AcpMethods.SessionRequestPermission} {request.ToolCall?.Kind ?? "tool"} → {(reject is null ? "cancelled" : "rejected")} ({Describe(Purpose)} ask for text only)");
        return Task.FromResult(new AcpRequestPermissionResponse(reject is null ? AcpPermissionOutcome.Cancelled : AcpPermissionOutcome.Selected(reject.OptionId)));
    }

    public Task<AcpReadTextFileResponse> ReadTextFileAsync(AcpReadTextFileRequest request, CancellationToken cancellationToken) =>
        Task.FromException<AcpReadTextFileResponse>(Refuse(AcpMethods.ReadTextFile));

    public Task WriteTextFileAsync(AcpWriteTextFileRequest request, CancellationToken cancellationToken) =>
        Task.FromException(Refuse(AcpMethods.WriteTextFile));

    public Task<AcpCreateTerminalResponse> CreateTerminalAsync(AcpCreateTerminalRequest request, CancellationToken cancellationToken) =>
        Task.FromException<AcpCreateTerminalResponse>(Refuse(AcpMethods.TerminalCreate));

    public Task<AcpTerminalOutputResponse> TerminalOutputAsync(AcpTerminalRequest request, CancellationToken cancellationToken) =>
        Task.FromException<AcpTerminalOutputResponse>(Refuse(AcpMethods.TerminalOutput));

    public Task<AcpTerminalExitStatus> WaitForTerminalExitAsync(AcpTerminalRequest request, CancellationToken cancellationToken) =>
        Task.FromException<AcpTerminalExitStatus>(Refuse(AcpMethods.TerminalWaitForExit));

    public Task KillTerminalAsync(AcpTerminalRequest request, CancellationToken cancellationToken) =>
        Task.FromException(Refuse(AcpMethods.TerminalKill));

    public Task ReleaseTerminalAsync(AcpTerminalRequest request, CancellationToken cancellationToken) =>
        Task.FromException(Refuse(AcpMethods.TerminalRelease));

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            // TODO(issue): close the session on the agent's side with session/close once it is stable in ACP; until then it
            // is forgotten here and ends with the agent's process.
            _route?.Dispose();
        }

        return ValueTask.CompletedTask;
    }
}
