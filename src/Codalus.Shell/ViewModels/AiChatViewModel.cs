using Codalus.Shell.Infrastructure;
using Codalus.Workbench.Ai;
using Codalus.Workbench.Ai.Acp;
using Codalus.Workbench.Localization;

namespace Codalus.Shell.ViewModels;

/// <summary>Where a question's selection was, so Apply can replace it even after edits elsewhere.</summary>
/// <param name="Path">The file.</param>
/// <param name="Offset">Its start when the question was asked.</param>
/// <param name="Text">Its text.</param>
public sealed record AiSelectionAnchor(string Path, int Offset, string Text);

/// <summary>Which context goes with the next question (the chips above the input).</summary>
public sealed record AiContextChoice(bool Selection, bool CurrentFile, bool OpenFiles, bool Errors, bool BuildOutput, bool Solution);

/// <summary>An external agent's slash command, offered in the input (M48).</summary>
/// <param name="Name">The command, without its slash.</param>
/// <param name="Text">How the list shows it: <c>/name — description</c>.</param>
public sealed record AiCommandSuggestion(string Name, string Text);

/// <summary>What the chat window needs from the shell: context, applying code, and the provider prompts.</summary>
public interface IAiChatHost
{
    AiService Ai { get; }

    /// <summary>The context items of <paramref name="choice"/> (before the boundary), and where the selection is.</summary>
    (IReadOnlyList<AiContextItem> Items, AiSelectionAnchor? Anchor) GatherAiContext(AiContextChoice choice);

    /// <summary>Applies a code block of <paramref name="turn"/>: over its selection, the named or active file, or at the caret, after a preview.</summary>
    Task ApplyAiCodeAsync(AiChatTurn turn, AiCodeBlock block, bool insertOnly);

    Task CopyTextAsync(string text);

    Task ChooseAiProviderAsync();

    /// <summary>Turns AI on in the user's settings (and back on for this workspace).</summary>
    Task TurnOnAiAsync();

    /// <summary>Asks in agent mode (M28c): checks trust and the model, then runs the agent's loop.</summary>
    Task RunAgentAsync(string question, IReadOnlyList<AiContextItem> items, AiSelectionAnchor? anchor, CancellationToken cancellationToken);

    Task ConfigureAiProviderAsync();

    void ShowAiLog();

    /// <summary>Shows the AI Chat window.</summary>
    void ShowAiChatWindow();

    void OpenLink(string target);

    /// <summary>Opens a file a step of an agent worked on (relative to the workspace), at a line (1-based) when given.</summary>
    Task OpenAiLocationAsync(string path, int? line);

    /// <summary>The editor's services, for code blocks in the editor's colors (M34h).</summary>
    Editor.IEditorServices EditorServices { get; }

    /// <summary>The editor's font, for code blocks.</summary>
    EditorFont EditorFont { get; }

    /// <summary>The external agents (M48, ADR 0079) and the session that runs.</summary>
    AcpAgents ExternalAgents { get; }

    /// <summary>The IDE's side of an external agent's requests.</summary>
    IAcpHost AcpHost { get; }

    /// <summary>Connect to an agent (D43): the Connect dialog for <paramref name="templateId"/>, or More agents… without one; the agent written, or null.</summary>
    Task<string?> ConnectExternalAgentAsync(string? templateId);

    /// <summary>Manage Agents…: the configured agents, each with Check Connection and Disconnect.</summary>
    Task ManageExternalAgentsAsync();

    /// <summary>Why agents can't be connected here (a remote window), or null.</summary>
    string? WhyAgentsCantConnect { get; }
}

/// <summary>An agent AI Chat offers to connect in one click (D43): its template's id and name.</summary>
public sealed record AiConnectAgent(string Id, string Name)
{
    public string AccessibleName => Localizer.Format("msg.acp.connectTo", "Connect to {0}…", Name);
}

/// <summary>A picker of an external agent's choice (M51): its name, the current value's name, the values, and the current one's place.</summary>
public sealed record AiAgentChoiceItem(string Id, string Name, string CurrentName, IReadOnlyList<string> ValueNames, int SelectedIndex)
{
    public string AccessibleName => Name + ": " + CurrentName;
}

/// <summary>An action of the agent menu after its choices (Connect to Claude…, More agents…, Manage Agents…).</summary>
public sealed record AiAgentMenuAction(string Label, string Icon, Func<Task> Run);

/// <summary>A turn as AI Chat shows it (M34h): the turn, who answered (the model then) and when it was asked.</summary>
public sealed class AiChatTurnItem(AiChatTurn turn, string? model, DateTime asked)
{
    public AiChatTurn Turn { get; } = turn;

    /// <summary>The model that answered, as the provider names it.</summary>
    public string? Model { get; } = model;

    public DateTime Asked { get; } = asked;

    /// <summary>Who answers: the assistant, or the external agent by its name (M48).</summary>
    public string Author { get; init; } = Localizer.Get("msg.ai.assistant", "Assistant");

    /// <summary>The line beside the author: the model and the time (<c>gpt-4o · 14:32</c>).</summary>
    public string Byline => string.Join(" · ", new[] { Model, Asked.ToString("t", System.Globalization.CultureInfo.CurrentCulture) }.Where(s => !string.IsNullOrEmpty(s)));
}

/// <summary>
/// The AI Chat window (M28a, ADR 0062): a conversation with streamed answers, the context chips that say what goes with
/// the next question, and the state of AI (off, no provider, ready) with what to do about it.
/// </summary>
public sealed class AiChatViewModel : ObservableObject, IDisposable
{
    public const string ToolWindowId = "aiChat";

    private readonly IAiChatHost _host;
    private CancellationTokenSource? _cts;
    private bool _sending;
    private string _input = string.Empty;
    private bool _selection = true;
    private bool _currentFile = true;
    private bool _openFiles;
    private bool _errors = true;
    private bool _buildOutput;
    private bool _solution = true;
    private string _contextPreview = string.Empty;
    private bool _agent;
    private readonly HashSet<AiChatTurn> _known = [];
    private int _agentIndex;
    private bool _picked;
    private List<string> _agentNames;
    private IReadOnlyList<AiCommandSuggestion> _suggestions = [];

    public AiChatViewModel(IAiChatHost host)
    {
        _host = host;
        _agentNames = [.. host.ExternalAgents.Configured.Keys.Order(StringComparer.OrdinalIgnoreCase)];
        _agentIndex = SavedAgentIndex(_agentNames);
        host.ExternalAgents.Changed += (_, _) => Ui.Post(RefreshAgentState);
        Chat = new AiChat(host.Ai);
        Chat.TurnChanged += (_, turn) => Ui.Post(() =>
        {
            // A late change of a turn that New Chat cleared away doesn't bring it back.
            if (!_known.Contains(turn) && Chat.Turns.Contains(turn))
            {
                _known.Add(turn);
                // An external agent's turn names the agent; its model is its own business (ADR 0079).
                Turns.Add(turn.ExternalAgent is { } agent
                    ? new AiChatTurnItem(turn, null, DateTime.Now) { Author = agent }
                    : new AiChatTurnItem(turn, Ai.ModelId ?? Ai.Provider?.Label, DateTime.Now));
                OnTurnsChanged();
            }

            TurnChanged?.Invoke(this, turn);
            OnAnsweringChanged();
        });
        Chat.Cleared += (_, _) => Ui.Post(() =>
        {
            _known.Clear();
            Turns.Clear();
            OnTurnsChanged();
            Cleared?.Invoke(this, EventArgs.Empty);
        });
        host.Ai.Changed += (_, _) => Ui.Post(Refresh);
        host.Ai.BusyChanged += (_, _) => Ui.Post(() =>
        {
            OnPropertyChanged(nameof(IsBusy));
            OnPropertyChanged(nameof(CanSend));
        });
    }

    public AiChat Chat { get; }

    public AiService Ai => _host.Ai;

    /// <summary>The editor's services and font, for the answers' code blocks.</summary>
    public Editor.IEditorServices EditorServices => _host.EditorServices;

    public EditorFont EditorFont => _host.EditorFont;

    /// <summary>A turn was added or its answer changed (UI thread).</summary>
    public event EventHandler<AiChatTurn>? TurnChanged;

    /// <summary>The conversation was cleared (UI thread).</summary>
    public event EventHandler? Cleared;

    /// <summary>The conversation, in order (UI thread; the view virtualizes it, M34h).</summary>
    public System.Collections.ObjectModel.ObservableCollection<AiChatTurnItem> Turns { get; } = [];

    public bool HasTurns => Turns.Count > 0;

    /// <summary>
    /// A question is being answered, from Send until its turn ends (an agent's turn too, while a tool runs between its
    /// requests): the composer offers Stop instead of Send, and nothing else is sent meanwhile.
    /// </summary>
    public bool IsAnswering => _sending || (Turns.Count > 0 && Turns[^1].Turn.IsStreaming);

    public bool IsOff => Ai.State == AiState.Off;

    public bool IsOn => !IsOff;

    public bool NeedsProvider => Ai.State == AiState.NoProvider;

    /// <summary>The empty state while AI is off: what it is, and Turn On.</summary>
    public bool ShowOffState => IsOff && !HasTurns;

    /// <summary>The empty state while no provider is chosen: Choose Provider… (an external agent brings its own, M48).</summary>
    public bool ShowProviderState => NeedsProvider && !IsExternal && !HasTurns;

    /// <summary>The empty state with AI ready: what to ask, and what goes with it (the built-in assistant's).</summary>
    public bool ShowReadyState => IsOn && !NeedsProvider && !IsExternal && !HasTurns;

    /// <summary>An external agent's empty state (D43): it answers with its own provider; Codalus asks before changes and commands.</summary>
    public bool ShowExternalReadyState => IsOn && IsExternal && !HasTurns;

    public string ExternalReadyMessage => Localizer.Format("msg.acp.askAgent", "Ask {0} about your solution. It answers with its own provider; Codalus asks you before it changes a file or runs a command.", SelectedAgentDisplayName);

    /// <summary>The chosen agent as its template names it (Claude), else its key.</summary>
    private string SelectedAgentDisplayName => SelectedAgent is { } agent ? AcpAgentTemplates.FromTag(_host.ExternalAgents.Configured.GetValueOrDefault(agent)?.Template)?.Name ?? agent : string.Empty;

    /// <summary>AI was turned off with a conversation on screen: the conversation stays, Turn On replaces the composer.</summary>
    public bool ShowOffBar => IsOff && HasTurns;

    public bool IsBusy => Ai.IsBusy;

    public string OffMessage => Ai.IsOffForWorkspace
        ? Localizer.Get("msg.ai.offForWorkspace", "AI assistance is turned off for this workspace by its settings (.codalus/settings.json).")
        : Localizer.Get("msg.ai.off", "AI assistance is off. Turn it on, then choose a provider: nothing is sent anywhere until you do. Files that .gitignore or a .codalusignore excludes, and secrets, are never sent.");

    /// <summary>The provider and model, or what's missing.</summary>
    public string ProviderLabel => Ai.Provider is { } provider
        ? Ai.ModelId is { } model ? $"{provider.Label} · {model}" : provider.Label
        : Ai.ProviderId is { } id ? id : Localizer.Get("msg.ai.chooseProvider", "Choose Provider…");

    public string Input
    {
        get => _input;
        set
        {
            if (SetField(ref _input, value))
            {
                OnPropertyChanged(nameof(CanSend));
                UpdateSuggestions();
            }
        }
    }

    /// <summary>A question can go now: AI is on and idle, no turn is being answered, there is a question, and a chosen external agent may run.</summary>
    public bool CanSend => !IsBusy && !IsAnswering && !IsOff && Input.Trim().Length > 0 && !IsAgentBlocked;

    /// <summary>Agent mode (M28c): the model may read, search, build, propose edits and ask to run commands.</summary>
    public bool IsAgent
    {
        get => _agent;
        set => SetField(ref _agent, value);
    }

    public bool IncludeSelection { get => _selection; set => SetChip(ref _selection, value); }

    public bool IncludeCurrentFile { get => _currentFile; set => SetChip(ref _currentFile, value); }

    public bool IncludeOpenFiles { get => _openFiles; set => SetChip(ref _openFiles, value); }

    public bool IncludeErrors { get => _errors; set => SetChip(ref _errors, value); }

    public bool IncludeBuildOutput { get => _buildOutput; set => SetChip(ref _buildOutput, value); }

    public bool IncludeSolution { get => _solution; set => SetChip(ref _solution, value); }

    /// <summary>What would go with the next question now (names only), shown under the chips.</summary>
    public string ContextPreview
    {
        get => _contextPreview;
        private set => SetField(ref _contextPreview, value);
    }

    public AiContextChoice Choice => new(_selection, _currentFile, _openFiles, _errors, _buildOutput, _solution);

    private void SetChip(ref bool field, bool value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (SetField(ref field, value, name))
        {
            UpdateContextPreview();
        }
    }

    /// <summary>Reads the context the chips choose and lists it (the editor's selection or files changed).</summary>
    public void UpdateContextPreview()
    {
        if (IsOff)
        {
            ContextPreview = string.Empty;
            return;
        }

        var (items, _) = _host.GatherAiContext(Choice);
        ContextPreview = items.Count == 0
            ? Localizer.Get("msg.ai.noContext", "Only your question is sent.")
            : Localizer.Format("msg.ai.contextPreview", "Sent with your question: {0}", string.Join(", ", items.Select(i => i.Label).Distinct(StringComparer.Ordinal)));
    }

    // ------------------------------------------------------------------ external agents (M48, ADR 0079)

    /// <summary>The picker: Built-in, then each agent of <c>ai.agents</c>.</summary>
    public IReadOnlyList<string> AgentChoices => [Localizer.Get("msg.acp.builtIn", "Built-in"), .. _agentNames];

    /// <summary>The picker's choice: 0 is the built-in assistant. Picking another agent starts a new chat.</summary>
    public int SelectedAgentIndex
    {
        get => _agentIndex;
        set
        {
            // -1 is the picker clearing its selection while its items are replaced, not a choice.
            if (value < 0 || value > _agentNames.Count || value == _agentIndex)
            {
                return;
            }

            NewChat();
            _agentIndex = value;
            _picked = true;
            OnPropertyChanged();
            RefreshAgentState();

            // Remembered across a restart (M51): AI Chat opens with the agent chosen last.
            _ = _host.ExternalAgents.SaveChatAgentAsync(SelectedAgent, CancellationToken.None);
        }
    }

    /// <summary>The picker's place of the agent chosen last (<c>ai.chat.agent</c>), or 0 (Built-in) when it's gone.</summary>
    private int SavedAgentIndex(List<string> names) =>
        _host.ExternalAgents.ChatAgent is { } saved && names.IndexOf(saved) is var at and >= 0 ? at + 1 : 0;

    /// <summary>The external agent chosen, or null for the built-in assistant.</summary>
    public string? SelectedAgent => _agentIndex > 0 && _agentIndex <= _agentNames.Count ? _agentNames[_agentIndex - 1] : null;

    public bool IsExternal => SelectedAgent is not null;

    /// <summary>The picker's label: the chosen agent, or Built-in.</summary>
    public string SelectedAgentLabel => AgentChoices[Math.Clamp(_agentIndex, 0, _agentNames.Count)];

    /// <summary>The picker shows while AI is on: Built-in, the agents, then Connect, More agents… and Manage Agents… (D43).</summary>
    public bool HasAgents => IsOn;

    /// <summary>The provider, model and Agent switch are the built-in assistant's.</summary>
    public bool ShowBuiltInControls => IsOn && !IsExternal;

    /// <summary>Why the chosen external agent can't run here (AI off, no workspace, an untrusted one), or null.</summary>
    public string? AgentBlockedMessage => IsExternal ? _host.ExternalAgents.WhyNot() : null;

    public bool IsAgentBlocked => AgentBlockedMessage is not null;

    /// <summary>The running agent's session, when it is the chosen one.</summary>
    private AcpAgentSession? Session => _host.ExternalAgents.Current is { } session && session.Name == SelectedAgent ? session : null;

    /// <summary>
    /// The agent's choices, one picker each (M51): its model, mode, effort and other selects, as it offers them over ACP
    /// (empty when it offers none, or hasn't started). A pick is sent to the agent and kept for its next sessions.
    /// </summary>
    public IReadOnlyList<AiAgentChoiceItem> AgentChoicePickers => IsExternal && Session is { } session
        ? [.. session.Choices.Select(c => new AiAgentChoiceItem(c.Id, ChoiceName(c), c.CurrentName, [.. c.Values.Select(v => v.Name)], c.Values.ToList().FindIndex(v => v.Value == c.CurrentValue)))]
        : [];

    public bool HasAgentChoices => AgentChoicePickers.Count > 0;

    /// <summary>A picker's name: the known categories in the IDE's words, the others as the agent names them.</summary>
    private static string ChoiceName(AcpSessionChoice choice) => choice.Category switch
    {
        AcpSessionChoices.ModelCategory => Localizer.Get("msg.acp.choice.model", "Model"),
        AcpSessionChoices.ModeCategory => Localizer.Get("msg.acp.choice.mode", "Mode"),
        AcpSessionChoices.ThoughtLevelCategory => Localizer.Get("msg.acp.choice.effort", "Effort"),
        _ => choice.Name,
    };

    /// <summary>Picks the value at <paramref name="index"/> of the choice <paramref name="id"/>: sent to the agent, then kept in its entry.</summary>
    public async Task ChooseAsync(string id, int index)
    {
        if (Session is not { } session || session.Choices.FirstOrDefault(c => c.Id == id) is not { } choice || index < 0 || index >= choice.Values.Count)
        {
            return;
        }

        var value = choice.Values[index].Value;
        if (value != choice.CurrentValue)
        {
            try
            {
                await session.SetChoiceAsync(id, value, CancellationToken.None).ConfigureAwait(true);
            }
            catch (AcpException)
            {
                // The agent refused: the picker shows its current value again, and nothing is kept.
                OnChoicesChanged();
                return;
            }
        }

        await _host.ExternalAgents.SaveChoiceAsync(session.Name, id, value, CancellationToken.None).ConfigureAwait(true);
        OnChoicesChanged();
    }

    private void OnChoicesChanged()
    {
        OnPropertyChanged(nameof(AgentChoicePickers));
        OnPropertyChanged(nameof(HasAgentChoices));
    }

    /// <summary>The agent's slash commands matching what's typed after <c>/</c>.</summary>
    public IReadOnlyList<AiCommandSuggestion> CommandSuggestions
    {
        get => _suggestions;
        private set
        {
            if (SetField(ref _suggestions, value))
            {
                OnPropertyChanged(nameof(HasCommandSuggestions));
            }
        }
    }

    public bool HasCommandSuggestions => _suggestions.Count > 0;

    private void UpdateSuggestions()
    {
        var typed = Input;
        if (Session is not { } session || !typed.StartsWith('/') || typed.Any(char.IsWhiteSpace))
        {
            CommandSuggestions = [];
            return;
        }

        var prefix = typed[1..];
        CommandSuggestions = [.. session.Commands
            .Where(c => c.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(c => new AiCommandSuggestion(c.Name, "/" + c.Name + (c.Description is { Length: > 0 } d ? " — " + d : "") + (c.Input?.Hint is { Length: > 0 } hint ? " (" + hint + ")" : "")))];
    }

    /// <summary>Puts a suggested command in the input, ready for its argument.</summary>
    public void UseCommand(AiCommandSuggestion suggestion)
    {
        Input = "/" + suggestion.Name + " ";
        CommandSuggestions = [];
    }

    /// <summary>The one-click agents (D43), offered as buttons while AI is off or has no provider.</summary>
    public IReadOnlyList<AiConnectAgent> ConnectAgents { get; } = [.. AcpAgentTemplates.All.Where(t => t.Tier == AcpTemplateTier.OneClick).Select(t => new AiConnectAgent(t.Id, t.Name))];

    /// <summary>The off and no-provider states' row: "Or connect a coding agent that brings its own provider" (not when the workspace turned AI off).</summary>
    public bool ShowConnectRow => (ShowOffState || ShowProviderState) && !Ai.IsOffForWorkspace;

    /// <summary>Why agents can't be connected here (a remote window): the buttons are disabled with it.</summary>
    public string? ConnectBlockedReason => _host.WhyAgentsCantConnect;

    public bool CanConnectAgents => ConnectBlockedReason is null;

    public bool IsConnectBlocked => !CanConnectAgents;

    /// <summary>Connect to <paramref name="templateId"/>… (a button of the off state, or the agent menu).</summary>
    public async Task ConnectAsync(string templateId)
    {
        await _host.ConnectExternalAgentAsync(templateId).ConfigureAwait(true);
        RefreshAgentState();
    }

    /// <summary>More agents…: every template in the palette.</summary>
    public async Task MoreAgentsAsync()
    {
        await _host.ConnectExternalAgentAsync(null).ConfigureAwait(true);
        RefreshAgentState();
    }

    /// <summary>Manage Agents….</summary>
    public async Task ManageAgentsAsync()
    {
        await _host.ManageExternalAgentsAsync().ConfigureAwait(true);
        RefreshAgentState();
    }

    /// <summary>
    /// The agent menu's actions after its choices (D43), in groups a separator parts: Connect to each one-click agent not
    /// connected yet, then More agents… and Manage Agents….
    /// </summary>
    public IReadOnlyList<IReadOnlyList<AiAgentMenuAction>> AgentMenuActions =>
    [
        [.. ConnectAgents.Where(a => !_agentNames.Contains(a.Id)).Select(a => new AiAgentMenuAction(a.AccessibleName, "plug", () => ConnectAsync(a.Id)))],
        [
            new(Localizer.Get("msg.acp.moreAgents", "More agents…"), "list", MoreAgentsAsync),
            new(Localizer.Get("msg.acp.manageAgents", "Manage Agents…"), "gear", ManageAgentsAsync),
        ],
    ];

    /// <summary>Picks <paramref name="name"/> (Start a Chat after Connect): a new chat with it.</summary>
    public void SelectAgent(string name)
    {
        RefreshAgentState();
        if (_agentNames.IndexOf(name) is var at and >= 0)
        {
            SelectedAgentIndex = at + 1;
        }
    }

    /// <summary>The settings or the running session changed: the picker, the label, the modes and the commands follow.</summary>
    private void RefreshAgentState()
    {
        var names = _host.ExternalAgents.Configured.Keys.Order(StringComparer.OrdinalIgnoreCase).ToList();
        if (!names.SequenceEqual(_agentNames, StringComparer.Ordinal))
        {
            // Until the user picks, the agent chosen last is restored when its entry arrives (the settings load after the window).
            var selected = SelectedAgent ?? (_picked ? null : _host.ExternalAgents.ChatAgent);
            var at = selected is null ? -1 : names.IndexOf(selected);
            if (selected is not null && at < 0)
            {
                // The chosen agent left the settings: back to the built-in assistant.
                NewChat();
            }

            _agentNames = names;
            _agentIndex = at < 0 ? 0 : at + 1;
            OnPropertyChanged(nameof(AgentChoices));
            OnPropertyChanged(nameof(SelectedAgentIndex));
        }

        OnPropertyChanged(nameof(SelectedAgent));
        OnPropertyChanged(nameof(SelectedAgentLabel));
        OnPropertyChanged(nameof(IsExternal));
        OnPropertyChanged(nameof(HasAgents));
        OnPropertyChanged(nameof(ShowBuiltInControls));
        OnPropertyChanged(nameof(AgentBlockedMessage));
        OnPropertyChanged(nameof(IsAgentBlocked));
        OnChoicesChanged();
        OnPropertyChanged(nameof(CanSend));
        OnStatesChanged();
        UpdateSuggestions();
    }

    public void Refresh()
    {
        RefreshAgentState();
        if (IsOff && IsAnswering)
        {
            // AI was turned off (the setting, or for the workspace) while a turn ran: it stops, as Stop does, and the
            // turn says so. The composer and its Stop are gone while AI is off.
            Stop();
        }

        OnPropertyChanged(nameof(IsOff));
        OnPropertyChanged(nameof(IsOn));
        OnPropertyChanged(nameof(NeedsProvider));
        OnPropertyChanged(nameof(OffMessage));
        OnPropertyChanged(nameof(ProviderLabel));
        OnPropertyChanged(nameof(CanSend));
        OnStatesChanged();
        UpdateContextPreview();
    }

    private void OnTurnsChanged()
    {
        OnPropertyChanged(nameof(HasTurns));
        OnAnsweringChanged();
        OnStatesChanged();
    }

    private void OnAnsweringChanged()
    {
        OnPropertyChanged(nameof(IsAnswering));
        OnPropertyChanged(nameof(CanSend));
    }

    private void OnStatesChanged()
    {
        OnPropertyChanged(nameof(ShowOffState));
        OnPropertyChanged(nameof(ShowProviderState));
        OnPropertyChanged(nameof(ShowReadyState));
        OnPropertyChanged(nameof(ShowOffBar));
        OnPropertyChanged(nameof(ShowConnectRow));
        OnPropertyChanged(nameof(ConnectBlockedReason));
        OnPropertyChanged(nameof(CanConnectAgents));
        OnPropertyChanged(nameof(IsConnectBlocked));
        OnPropertyChanged(nameof(ShowExternalReadyState));
        OnPropertyChanged(nameof(ExternalReadyMessage));
    }

    /// <summary>
    /// Sends the input with the chosen context. Nothing is sent while a turn is answered (Enter included): AI isn't busy
    /// between an agent's steps, and a second question would take Stop away from the running turn.
    /// </summary>
    public async Task SendAsync()
    {
        var question = Input.Trim();
        if (question.Length == 0 || IsBusy || IsAnswering || IsOff)
        {
            return;
        }

        if (IsExternal)
        {
            if (IsAgentBlocked)
            {
                return;
            }
        }
        else if (NeedsProvider)
        {
            await _host.ChooseAiProviderAsync().ConfigureAwait(true);
            if (NeedsProvider || IsAnswering)
            {
                return;
            }
        }

        var (items, anchor) = _host.GatherAiContext(Choice);
        var agent = IsAgent;
        Input = string.Empty;
        var external = SelectedAgent;
        await AnswerAsync(token => external is { } selected
            ? Chat.SendExternalAsync(question, items, _host.ExternalAgents, selected, _host.AcpHost, token, anchor)
            : agent
                ? _host.RunAgentAsync(question, items, anchor, token)
                : Chat.SendAsync(question, items, token, anchor)).ConfigureAwait(true);
    }

    /// <summary>Asks <paramref name="question"/> with <paramref name="items"/> chosen by an action (not the chips), in this chat.</summary>
    public async Task AskAsync(string question, IReadOnlyList<AiContextItem> items, AiSelectionAnchor? anchor = null)
    {
        _host.ShowAiChatWindow();
        if (IsBusy || IsAnswering || IsOff)
        {
            return;
        }

        await AnswerAsync(token => Chat.SendAsync(question, items, token, anchor)).ConfigureAwait(true);
    }

    /// <summary>
    /// Runs one question with its own cancellation, which Stop reaches until its turn ends. The question counts as being
    /// answered from now (before its turn exists: the context is prepared and agent mode checks first), so Stop shows at
    /// once and may stop it before its turn starts.
    /// </summary>
    private async Task AnswerAsync(Func<CancellationToken, Task> answer)
    {
        _cts?.Dispose();
        var cts = _cts = new CancellationTokenSource();
        _sending = true;
        OnAnsweringChanged();
        try
        {
            await answer(cts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // Stopped before its turn started (a started turn ends as stopped instead).
        }
        finally
        {
            _sending = false;
            OnAnsweringChanged();
        }
    }

    public void Stop() => _cts?.Cancel();

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
    }

    /// <summary>Starts over: the conversation is cleared, and an external agent's process stops (its session and pipe with it).</summary>
    public void NewChat()
    {
        Stop();
        Chat.Clear();
        _ = _host.ExternalAgents.StopAsync();
    }

    public async Task TurnOnAsync()
    {
        await _host.TurnOnAiAsync().ConfigureAwait(true);
        Refresh();
        if (NeedsProvider)
        {
            await _host.ChooseAiProviderAsync().ConfigureAwait(true);
        }
    }

    public Task ChooseProviderAsync() => _host.ChooseAiProviderAsync();

    public Task ConfigureAsync() => _host.ConfigureAiProviderAsync();

    public void ShowLog() => _host.ShowAiLog();

    public Task CopyAsync(string text) => _host.CopyTextAsync(text);

    public Task ApplyAsync(AiChatTurn turn, AiCodeBlock block) => _host.ApplyAiCodeAsync(turn, block, insertOnly: false);

    public Task InsertAsync(AiChatTurn turn, AiCodeBlock block) => _host.ApplyAiCodeAsync(turn, block, insertOnly: true);

    public void OpenLink(string target) => _host.OpenLink(target);

    /// <summary>Opens a file a step worked on.</summary>
    public void OpenLocation(AiStepLocation location) => _ = _host.OpenAiLocationAsync(location.Path, location.Line);

    /// <summary>What went with a turn, and what didn't, in a line.</summary>
    public static string ContextSummary(AiChatTurn turn)
    {
        var parts = new List<string>();
        if (turn.Context.Included.Count > 0)
        {
            parts.Add(Localizer.Format("msg.ai.sent", "Sent: {0}", string.Join(", ", turn.Context.Included.Select(i => i.Label).Distinct(StringComparer.Ordinal))));
        }

        if (turn.Context.Excluded.Count > 0)
        {
            parts.Add(Localizer.Format("msg.ai.notSent", "Not sent: {0}", string.Join(", ", turn.Context.Excluded.Select(e => $"{e.Item.Label} ({e.Reason})"))));
        }

        if (turn.Context.Redactions > 0)
        {
            parts.Add(Localizer.Format("msg.ai.redacted", "{0} secret(s) redacted", turn.Context.Redactions));
        }

        return string.Join(" · ", parts);
    }
}
