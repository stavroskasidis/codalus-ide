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

/// <summary>
/// Which context goes with a question: the parts the IDE gathers, and files by their path (relative to the workspace).
/// AI Chat sends only what the question @-mentions (ADR 0092); actions such as Explain choose their own.
/// </summary>
public sealed record AiContextChoice(bool Selection, bool CurrentFile, bool OpenFiles, bool Errors, bool BuildOutput, bool Solution)
{
    /// <summary>Files the question mentions, relative to the workspace.</summary>
    public IReadOnlyList<string> Files { get; init; } = [];

    /// <summary>Nothing but the question.</summary>
    public static AiContextChoice None { get; } = new(false, false, false, false, false, false);

    public bool IsEmpty => !Selection && !CurrentFile && !OpenFiles && !Errors && !BuildOutput && !Solution && Files.Count == 0;
}

/// <summary>
/// A suggestion in the input (M48, ADR 0092): an external agent's slash command, or an @-mention (a part of the IDE's
/// state or a file).
/// </summary>
/// <param name="Name">The command without its slash, or the mention without its @.</param>
/// <param name="Text">How the list shows it: <c>/name — description</c>, <c>@errors — the Error List</c>.</param>
public sealed record AiCommandSuggestion(string Name, string Text)
{
    /// <summary>An @-mention rather than a slash command.</summary>
    public bool IsMention { get; init; }
}

/// <summary>A question waiting for the running turn to end (ADR 0092).</summary>
public sealed record AiQueuedQuestion(string Text, IReadOnlyList<AiImageAttachment> Images)
{
    public string AccessibleName => Localizer.Format("msg.ai.queuedQuestion", "Queued: {0}", Text);
}

/// <summary>What the chat window needs from the shell: context, applying code, and the provider prompts.</summary>
public interface IAiChatHost
{
    AiService Ai { get; }

    /// <summary>The context items of <paramref name="choice"/> (before the boundary), and where the selection is.</summary>
    (IReadOnlyList<AiContextItem> Items, AiSelectionAnchor? Anchor) GatherAiContext(AiContextChoice choice);

    Task ChooseAiProviderAsync();

    /// <summary>Turns AI on in the user's settings (and back on for this workspace).</summary>
    Task TurnOnAiAsync();

    /// <summary>
    /// Asks the built-in assistant, always as an agent (ADR 0092): with the IDE's tools where they can run (a trusted
    /// folder, a model that takes tools), otherwise without them, the turn saying why.
    /// </summary>
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

    /// <summary>A short message for the user (the status bar).</summary>
    void ShowAiMessage(string message);

    /// <summary>The solution's files, relative to the workspace, for @-mentions.</summary>
    IReadOnlyList<string> MentionableFiles();

    /// <summary>How AI Chat shows an agent's work: <c>compact</c> or <c>detailed</c> (<c>ai.chat.density</c>).</summary>
    string ChatDensity { get; }

    /// <summary>Writes <c>ai.chat.density</c> to the user's settings.</summary>
    Task SetChatDensityAsync(string density);

    /// <summary>Suggestions while typing are on (<c>ai.completions.enabled</c> or <c>ai.nextEdit.enabled</c>): AI Chat offers an agent's model for them (ADR 0097).</summary>
    bool SuggestionsEnabled => false;

    /// <summary>Compares a file a turn changed with what it was before the turn.</summary>
    Task OpenAiChangeDiffAsync(AiChangedFile change);

    /// <summary>Puts a file a turn changed back as it was before the turn (after asking); true when it did.</summary>
    Task<bool> RevertAiChangeAsync(AiChangedFile change);
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
    private bool _stopped;
    private IReadOnlyList<string>? _mentionableFiles;
    private readonly HashSet<AiChatTurn> _known = [];
    private int _agentIndex;
    private bool _picked;
    private bool _starting;
    private string? _startedFor;
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

    /// <summary>
    /// A question can go now: AI is on, there is a question, and a chosen external agent may run. While a turn is answered
    /// it waits in the queue (ADR 0092) and goes when the turn ends.
    /// </summary>
    public bool CanSend => !IsOff && Input.Trim().Length > 0 && !IsAgentBlocked && (!IsBusy || IsAnswering);

    /// <summary>Send queues the question: a turn is being answered.</summary>
    public bool WillQueue => IsAnswering;


    // ------------------------------------------------------------------ density (ADR 0092)

    /// <summary>Compact (the default): each turn's steps fold into one line, and its thinking into a short one.</summary>
    public bool IsCompact => _host.ChatDensity != "detailed";

    public string DensityToggleName => IsCompact
        ? Localizer.Get("msg.ai.showDetails", "Show every step (detailed)")
        : Localizer.Get("msg.ai.showCompact", "Fold the steps (compact)");

    /// <summary>The toolbar's toggle: compact ⇄ detailed, kept in the user's settings.</summary>
    public async Task ToggleDensityAsync()
    {
        await _host.SetChatDensityAsync(IsCompact ? "detailed" : "compact").ConfigureAwait(true);
        OnDensityChanged();
    }

    /// <summary>The setting changed: the toggle and the turns follow.</summary>
    public void OnDensityChanged()
    {
        OnPropertyChanged(nameof(IsCompact));
        OnPropertyChanged(nameof(DensityToggleName));
        DensityChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The density changed (UI thread): the turns render again.</summary>
    public event EventHandler? DensityChanged;

    // ------------------------------------------------------------------ the queue (ADR 0092)

    /// <summary>Questions sent while a turn ran, in order; the first goes when it ends (not after Stop).</summary>
    public System.Collections.ObjectModel.ObservableCollection<AiQueuedQuestion> Queued { get; } = [];

    public bool HasQueued => Queued.Count > 0;

    /// <summary>Takes a queued question out of the queue.</summary>
    public void RemoveQueued(AiQueuedQuestion question)
    {
        if (Queued.Remove(question))
        {
            OnPropertyChanged(nameof(HasQueued));
        }
    }

    /// <summary>Takes a queued question back into the input to change it (what's typed there goes in the queue first).</summary>
    public void EditQueued(AiQueuedQuestion question)
    {
        if (!Queued.Contains(question))
        {
            return;
        }

        var typed = Input.Trim();
        var at = Queued.IndexOf(question);
        Queued.RemoveAt(at);
        if (typed.Length > 0)
        {
            Queued.Insert(at, new AiQueuedQuestion(typed, [.. Images]));
        }

        Images.Clear();
        foreach (var image in question.Images)
        {
            Images.Add(image);
        }

        Input = question.Text;
        OnPropertyChanged(nameof(HasQueued));
        OnImagesChanged();
    }

    // ------------------------------------------------------------------ images (ADR 0092)

    /// <summary>Images pasted or dropped into the input, sent with the next question.</summary>
    public System.Collections.ObjectModel.ObservableCollection<AiImageAttachment> Images { get; } = [];

    public bool HasImages => Images.Count > 0;

    /// <summary>
    /// Whether the chosen agent takes images (an external agent whose <c>promptCapabilities.image</c> is true).
    /// TODO(issue): the built-in assistant, once the provider API carries image parts.
    /// </summary>
    public bool CanAttachImages => Session is { TakesImages: true };

    /// <summary>Why the images can't go, while there are some: the assistant or agent doesn't take them.</summary>
    public string? ImagesNotTakenMessage => HasImages && !CanAttachImages
        ? IsExternal
            ? Localizer.Format("msg.ai.agentTakesNoImages", "{0} doesn't take images: they won't be sent.", SelectedAgentDisplayName)
            : Localizer.Get("msg.ai.assistantTakesNoImages", "The built-in assistant doesn't take images: they won't be sent. An external agent that takes them (such as Claude) can.")
        : null;

    public bool HasImagesNotTakenMessage => ImagesNotTakenMessage is not null;

    /// <summary>Adds an image to the next question; false (and nothing added) when it is too big.</summary>
    public bool AddImage(AiImageAttachment image)
    {
        if (image.Data.Length / 4 * 3 > AiImageAttachment.MaxBytes)
        {
            return false;
        }

        Images.Add(image);
        OnImagesChanged();
        return true;
    }

    /// <summary>An image over <see cref="AiImageAttachment.MaxBytes"/> wasn't added: the chat says so.</summary>
    public void ShowImageTooBig(string name) =>
        _host.ShowAiMessage(Localizer.Format("msg.ai.imageTooBig", "{0} is larger than 5 MB, so it wasn't added.", name));

    public void RemoveImage(AiImageAttachment image)
    {
        if (Images.Remove(image))
        {
            OnImagesChanged();
        }
    }

    private void OnImagesChanged()
    {
        OnPropertyChanged(nameof(HasImages));
        OnPropertyChanged(nameof(CanAttachImages));
        OnPropertyChanged(nameof(ImagesNotTakenMessage));
        OnPropertyChanged(nameof(HasImagesNotTakenMessage));
    }

    // ------------------------------------------------------------------ the turn's changes (ADR 0092)

    public Task OpenChangeDiffAsync(AiChangedFile change) => _host.OpenAiChangeDiffAsync(change);

    public Task<bool> RevertChangeAsync(AiChangedFile change) => _host.RevertAiChangeAsync(change);

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
        ? [.. session.Choices.Select(c => new AiAgentChoiceItem(c.Id, ChoiceName(c), c.CurrentName, [.. c.Values.Select(v => v.Name)], c.Values.ToList().FindIndex(v => v.Value == c.CurrentValue))), .. SuggestionsPicker(session)]
        : [];

    /// <summary>The id of the picker of the model for suggestions while typing (ADR 0097), after the agent's own choices.</summary>
    // TODO(issue): offer it in the Options page's External Agents editor too, when that page is built (ADR 0083's phase 2).
    public const string SuggestionsChoiceId = "codalus.suggestionsModel";

    /// <summary>
    /// The model for suggestions while typing (ADR 0097), shown while they are on and the agent offers models: Automatic
    /// (a small model guessed by its name, else the chat's), then the agent's models; kept in its entry's <c>suggestions</c>.
    /// </summary>
    private IEnumerable<AiAgentChoiceItem> SuggestionsPicker(AcpAgentSession session)
    {
        if (!_host.SuggestionsEnabled || session.Choices.FirstOrDefault(c => c.Category == AcpSessionChoices.ModelCategory) is not { } models)
        {
            yield break;
        }

        var guessed = AcpTextSession.GuessFastModel(models.Values) ?? models.CurrentValue;
        var automatic = Localizer.Format("msg.acp.suggestionsAutomatic", "Automatic ({0})", models.Values.FirstOrDefault(v => v.Value == guessed)?.Name ?? guessed ?? "");
        var picked = _host.ExternalAgents.Configured.GetValueOrDefault(session.Name)?.SuggestionsModel;
        var index = picked is null ? -1 : models.Values.ToList().FindIndex(v => v.Value == picked);
        var current = index >= 0 ? models.Values[index].Name : automatic;
        yield return new AiAgentChoiceItem(
            SuggestionsChoiceId,
            Localizer.Get("msg.acp.choice.suggestions", "Model for suggestions"),
            Localizer.Format("msg.acp.suggestionsCurrent", "Suggestions: {0}", current),
            [automatic, .. models.Values.Select(v => v.Name)],
            index + 1);
    }

    public bool HasAgentChoices => AgentChoicePickers.Count > 0;

    /// <summary>A picker's name: the known categories in the IDE's words, the others as the agent names them.</summary>
    private static string ChoiceName(AcpSessionChoice choice) => choice.Category switch
    {
        AcpSessionChoices.ModelCategory => Localizer.Get("msg.acp.choice.model", "Model"),
        AcpSessionChoices.ModeCategory => Localizer.Get("msg.acp.choice.mode", "Mode"),
        AcpSessionChoices.ThoughtLevelCategory => Localizer.Get("msg.acp.choice.effort", "Effort"),
        _ => choice.Name,
    };

    /// <summary>
    /// Starts the chosen external agent when the chat shows it and it may run here, so its choices (model, mode, effort)
    /// show before the first question instead of with its answer (0.8.0's release candidate). It doesn't sign in: an agent
    /// that needs it then shows its choices after the first question, which asks. Nothing when it runs already.
    /// </summary>
    public void EnsureAgentStarted()
    {
        // Once per agent and folder: one that didn't start (not signed in, not installed) isn't tried again and again.
        var key = SelectedAgent is { } agent ? agent + "|" + Ai.WorkspaceRoot : null;
        if (key is null || IsAgentBlocked || Session is not null || _starting || key == _startedFor)
        {
            return;
        }

        _starting = true;
        _startedFor = key;
        _ = StartAgentAsync(SelectedAgent!);
    }

    private async Task StartAgentAsync(string name)
    {
        try
        {
            await _host.ExternalAgents.GetOrStartAsync(name, _host.AcpHost, CancellationToken.None, signIn: false).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is AcpException or OperationCanceledException or ObjectDisposedException or InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            // Not signed in, or it can't start (or the window closed): the first question says why, as before.
        }
        finally
        {
            _starting = false;
            Ui.Post(RefreshAgentState);
        }
    }

    /// <summary>Picks the value at <paramref name="index"/> of the choice <paramref name="id"/>: sent to the agent, then kept in its entry.</summary>
    public async Task ChooseAsync(string id, int index)
    {
        if (id == SuggestionsChoiceId)
        {
            await ChooseSuggestionsModelAsync(index).ConfigureAwait(true);
            return;
        }

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

    /// <summary>Keeps the model for suggestions (0: Automatic) in the agent's entry; the next suggestion uses it (ADR 0097).</summary>
    private async Task ChooseSuggestionsModelAsync(int index)
    {
        if (Session is not { } session || session.Choices.FirstOrDefault(c => c.Category == AcpSessionChoices.ModelCategory) is not { } models || index < 0 || index > models.Values.Count)
        {
            return;
        }

        await _host.ExternalAgents.SaveSuggestionsModelAsync(session.Name, index == 0 ? null : models.Values[index - 1].Value, CancellationToken.None).ConfigureAwait(true);
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

        // An @-mention being typed: the word at the end that starts with @ (ADR 0092).
        var start = typed.LastIndexOfAny([' ', '\n', '\t']) + 1;
        if (start < typed.Length && typed[start] == '@' && IsOn)
        {
            var prefix = typed[(start + 1)..];
            _mentionableFiles ??= _host.MentionableFiles();
            CommandSuggestions = [.. AiMentions.Suggest(prefix, _mentionableFiles)];
            return;
        }

        if (typed.Length == 0)
        {
            _mentionableFiles = null; // read again at the next @: files come and go
        }

        if (Session is not { } session || !typed.StartsWith('/') || typed.Any(char.IsWhiteSpace))
        {
            CommandSuggestions = [];
            return;
        }

        var command = typed[1..];
        CommandSuggestions = [.. session.Commands
            .Where(c => c.Name.StartsWith(command, StringComparison.OrdinalIgnoreCase))
            .Select(c => new AiCommandSuggestion(c.Name, "/" + c.Name + (c.Description is { Length: > 0 } d ? " — " + d : "") + (c.Input?.Hint is { Length: > 0 } hint ? " (" + hint + ")" : "")))];
    }

    /// <summary>Puts a suggested command in the input, ready for its argument, or completes the @-mention being typed.</summary>
    public void UseCommand(AiCommandSuggestion suggestion)
    {
        if (suggestion.IsMention)
        {
            var typed = Input;
            var start = typed.LastIndexOfAny([' ', '\n', '\t']) + 1;
            Input = typed[..start] + "@" + suggestion.Name + " ";
        }
        else
        {
            Input = "/" + suggestion.Name + " ";
        }

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
        OnImagesChanged();
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
        OnPropertyChanged(nameof(WillQueue));
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
    /// Sends the input with what it @-mentions (ADR 0092: nothing else goes with it). While a turn is answered (between an
    /// agent's steps too) the question waits in the queue, and goes when that turn ends; Stop pauses the queue.
    /// </summary>
    public async Task SendAsync()
    {
        var question = Input.Trim();
        if (question.Length == 0 || IsOff || (IsExternal && IsAgentBlocked))
        {
            return;
        }

        if (IsAnswering || IsBusy)
        {
            Queued.Add(new AiQueuedQuestion(question, [.. Images]));
            Input = string.Empty;
            Images.Clear();
            OnImagesChanged();
            OnPropertyChanged(nameof(HasQueued));
            return;
        }

        var images = CanAttachImages ? Images.ToList() : [];
        Input = string.Empty;
        Images.Clear();
        OnImagesChanged();
        await AskNowAsync(question, images).ConfigureAwait(true);
    }

    private async Task AskNowAsync(string question, IReadOnlyList<AiImageAttachment> images)
    {
        if (!IsExternal && NeedsProvider)
        {
            await _host.ChooseAiProviderAsync().ConfigureAwait(true);
            if (NeedsProvider || IsAnswering)
            {
                Input = question;
                return;
            }
        }

        var (items, anchor) = _host.GatherAiContext(AiMentions.Parse(question));
        var external = SelectedAgent;
        _stopped = false;
        await AnswerAsync(token => external is { } selected
            ? Chat.SendExternalAsync(question, items, _host.ExternalAgents, selected, _host.AcpHost, token, anchor, images)
            : _host.RunAgentAsync(question, items, anchor, token)).ConfigureAwait(true);

        // The next queued question goes once this turn has ended, unless Stop paused the queue or AI went off.
        if (!_stopped && !IsOff && !IsAnswering && Queued.Count > 0 && !(IsExternal && IsAgentBlocked))
        {
            var next = Queued[0];
            Queued.RemoveAt(0);
            OnPropertyChanged(nameof(HasQueued));
            await AskNowAsync(next.Text, CanAttachImages ? next.Images : []).ConfigureAwait(true);
        }
    }

    /// <summary>Asks <paramref name="question"/> with <paramref name="items"/> chosen by an action (not the chips), in this chat.</summary>
    public async Task AskAsync(string question, IReadOnlyList<AiContextItem> items, AiSelectionAnchor? anchor = null)
    {
        _host.ShowAiChatWindow();
        if (IsBusy || IsAnswering || IsOff)
        {
            return;
        }

        // An action's question goes to the agent too (ADR 0092): Generate Tests creates the file through the preview. With an
        // external agent picked, to that agent's chat (ADR 0097), as a typed question would.
        if (SelectedAgent is { } external)
        {
            if (IsAgentBlocked)
            {
                return; // the chat shows why
            }

            _stopped = false;
            await AnswerAsync(token => Chat.SendExternalAsync(question, items, _host.ExternalAgents, external, _host.AcpHost, token, anchor, [])).ConfigureAwait(true);
            return;
        }

        await AnswerAsync(token => _host.RunAgentAsync(question, items, anchor, token)).ConfigureAwait(true);
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

    /// <summary>Stops the running turn; queued questions stay, paused, until the next Send.</summary>
    public void Stop()
    {
        _stopped = true;
        _cts?.Cancel();
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
    }

    /// <summary>Starts over: the conversation is cleared, and an external agent's process stops (its session and pipe with it).</summary>
    public void NewChat()
    {
        Stop();
        Queued.Clear();
        OnPropertyChanged(nameof(HasQueued));
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
