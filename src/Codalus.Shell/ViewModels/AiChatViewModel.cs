using Codalus.Shell.Infrastructure;
using Codalus.Workbench.Ai;
using Codalus.Workbench.Localization;

namespace Codalus.Shell.ViewModels;

/// <summary>Where a question's selection was, so Apply can replace it even after edits elsewhere.</summary>
/// <param name="Path">The file.</param>
/// <param name="Offset">Its start when the question was asked.</param>
/// <param name="Text">Its text.</param>
public sealed record AiSelectionAnchor(string Path, int Offset, string Text);

/// <summary>Which context goes with the next question (the chips above the input).</summary>
public sealed record AiContextChoice(bool Selection, bool CurrentFile, bool OpenFiles, bool Errors, bool BuildOutput, bool Solution);

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
}

/// <summary>A turn as AI Chat shows it (M34h): the turn, who answered (the model then) and when it was asked.</summary>
public sealed class AiChatTurnItem(AiChatTurn turn, string? model, DateTime asked)
{
    public AiChatTurn Turn { get; } = turn;

    /// <summary>The model that answered, as the provider names it.</summary>
    public string? Model { get; } = model;

    public DateTime Asked { get; } = asked;

    /// <summary>Who answers: the assistant (an external agent's name, later).</summary>
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

    public AiChatViewModel(IAiChatHost host)
    {
        _host = host;
        Chat = new AiChat(host.Ai);
        Chat.TurnChanged += (_, turn) => Ui.Post(() =>
        {
            // A late change of a turn that New Chat cleared away doesn't bring it back.
            if (!_known.Contains(turn) && Chat.Turns.Contains(turn))
            {
                _known.Add(turn);
                Turns.Add(new AiChatTurnItem(turn, Ai.ModelId ?? Ai.Provider?.Label, DateTime.Now));
                OnTurnsChanged();
            }

            TurnChanged?.Invoke(this, turn);
            OnPropertyChanged(nameof(IsAnswering));
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

    /// <summary>The last turn is being answered: the composer offers Stop instead of Send.</summary>
    public bool IsAnswering => Turns.Count > 0 && Turns[^1].Turn.IsStreaming;

    public bool IsOff => Ai.State == AiState.Off;

    public bool IsOn => !IsOff;

    public bool NeedsProvider => Ai.State == AiState.NoProvider;

    /// <summary>The empty state while AI is off: what it is, and Turn On.</summary>
    public bool ShowOffState => IsOff && !HasTurns;

    /// <summary>The empty state while no provider is chosen: Choose Provider….</summary>
    public bool ShowProviderState => NeedsProvider && !HasTurns;

    /// <summary>The empty state with AI ready: what to ask, and what goes with it.</summary>
    public bool ShowReadyState => IsOn && !NeedsProvider && !HasTurns;

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
            }
        }
    }

    public bool CanSend => !IsBusy && !IsOff && Input.Trim().Length > 0;

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

    public void Refresh()
    {
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
        OnPropertyChanged(nameof(IsAnswering));
        OnStatesChanged();
    }

    private void OnStatesChanged()
    {
        OnPropertyChanged(nameof(ShowOffState));
        OnPropertyChanged(nameof(ShowProviderState));
        OnPropertyChanged(nameof(ShowReadyState));
        OnPropertyChanged(nameof(ShowOffBar));
    }

    /// <summary>Sends the input with the chosen context.</summary>
    public async Task SendAsync()
    {
        var question = Input.Trim();
        if (question.Length == 0 || IsBusy || IsOff)
        {
            return;
        }

        if (NeedsProvider)
        {
            await _host.ChooseAiProviderAsync().ConfigureAwait(true);
            if (NeedsProvider)
            {
                return;
            }
        }

        var (items, anchor) = _host.GatherAiContext(Choice);
        Input = string.Empty;
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        if (IsAgent)
        {
            await _host.RunAgentAsync(question, items, anchor, _cts.Token).ConfigureAwait(true);
        }
        else
        {
            await Chat.SendAsync(question, items, _cts.Token, anchor).ConfigureAwait(true);
        }
    }

    /// <summary>Asks <paramref name="question"/> with <paramref name="items"/> chosen by an action (not the chips), in this chat.</summary>
    public async Task AskAsync(string question, IReadOnlyList<AiContextItem> items, AiSelectionAnchor? anchor = null)
    {
        _host.ShowAiChatWindow();
        if (IsBusy || IsOff)
        {
            return;
        }

        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        await Chat.SendAsync(question, items, _cts.Token, anchor).ConfigureAwait(true);
    }

    public void Stop() => _cts?.Cancel();

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
    }

    public void NewChat()
    {
        Stop();
        Chat.Clear();
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
