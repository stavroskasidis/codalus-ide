using System.Collections.ObjectModel;
using System.Windows.Input;
using DaedalusStudio.Platform;
using DaedalusStudio.Shell.Docking;
using DaedalusStudio.Shell.Infrastructure;
using DaedalusStudio.Workbench;
using DaedalusStudio.Workbench.Keybindings;

namespace DaedalusStudio.Shell.ViewModels;

public sealed class KeyboardShortcutsDocument : ShellDocument
{
    public const string DocumentId = "keyboardShortcuts";

    public KeyboardShortcutsDocument(KeyboardShortcutsViewModel viewModel)
    {
        Id = DocumentId;
        Title = "Keyboard Shortcuts";
        ViewModel = viewModel;
    }

    public override string Kind => "keyboardShortcuts";

    public KeyboardShortcutsViewModel ViewModel { get; }
}

public sealed class KeybindingRowViewModel(string commandId, string title, KeybindingRule? rule, string keyLabel, string source, bool userDefined, bool conflict)
{
    public string CommandId { get; } = commandId;

    public string Title { get; } = title;

    public KeybindingRule? Rule { get; } = rule;

    public string KeyLabel { get; } = keyLabel;

    public string When => Rule?.When.Text is { Length: > 0 } when && when != "true" ? when : string.Empty;

    public string Source { get; } = source;

    public bool IsUserDefined { get; } = userDefined;

    public bool HasConflict { get; } = conflict;

    public string AccessibleName => KeyLabel.Length > 0 ? $"{Title}, {KeyLabel}, {Source}" : $"{Title}, no key binding";
}

/// <summary>
/// The Keyboard Shortcuts editor: every command with its bindings, searchable by title, command id or keys,
/// with conflict markers. Changing a binding records one or two chords; the result is written to
/// keybindings.json (the user layer), never to the defaults.
/// </summary>
public sealed class KeyboardShortcutsViewModel : ObservableObject
{
    private const int MaxChords = 2;
    private readonly WorkbenchServices _workbench;
    private readonly List<KeyChord> _recorded = [];
    private string _search = string.Empty;
    private KeybindingRowViewModel? _selected;
    private KeybindingRowViewModel? _recordingFor;
    private string? _status;

    public KeyboardShortcutsViewModel(WorkbenchServices workbench, Action openJson)
    {
        _workbench = workbench;
        OpenJsonCommand = new RelayCommand(openJson);
        ChangeCommand = new RelayCommand(() => StartRecording(Selected), () => Selected is not null);
        RemoveCommand = new RelayCommand(() => _ = RemoveAsync(Selected), () => Selected is not null);
        ResetCommand = new RelayCommand(() => _ = ResetAsync(Selected), () => Selected?.IsUserDefined == true);
        workbench.Keybindings.Changed += (_, _) => Ui.Post(Rebuild);
        workbench.Commands.DescriptorsChanged += (_, _) => Ui.Post(Rebuild);
        Rebuild();
    }

    public ObservableCollection<KeybindingRowViewModel> Rows { get; } = [];

    public ICommand OpenJsonCommand { get; }

    public RelayCommand ChangeCommand { get; }

    public RelayCommand RemoveCommand { get; }

    public RelayCommand ResetCommand { get; }

    public string Search
    {
        get => _search;
        set
        {
            if (SetField(ref _search, value ?? string.Empty))
            {
                Rebuild();
            }
        }
    }

    public KeybindingRowViewModel? Selected
    {
        get => _selected;
        set
        {
            if (SetField(ref _selected, value))
            {
                ChangeCommand.RaiseCanExecuteChanged();
                RemoveCommand.RaiseCanExecuteChanged();
                ResetCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>A short message after saving, or a problem.</summary>
    public string? Status
    {
        get => _status;
        private set => SetField(ref _status, value);
    }

    // ------------------------------------------------------------------ recording

    public bool IsRecording => _recordingFor is not null;

    public string RecordingTitle => _recordingFor is { } row ? $"Press the keys for “{row.Title}”, then Enter. Escape cancels." : string.Empty;

    public string RecordedLabel => _recorded.Count == 0 ? "…" : new KeySequence([.. _recorded]).ToLabel(_workbench.Platform);

    public string? RecordedConflicts
    {
        get
        {
            if (_recorded.Count == 0)
            {
                return null;
            }

            var keys = new KeySequence([.. _recorded]);
            var others = _workbench.Keybindings.EffectiveRules
                .Where(r => Equals(r.Keys, keys) && r.Command != _recordingFor?.CommandId)
                .Select(r => _workbench.Commands.Find(r.Command)?.DisplayTitle ?? r.Command)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            return others.Count == 0 ? null : $"Also bound to: {string.Join(", ", others)}";
        }
    }

    public event EventHandler? RecordingChanged;

    public void StartRecording(KeybindingRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        _recordingFor = row;
        _recorded.Clear();
        RaiseRecording();
    }

    /// <summary>Adds a chord; after <see cref="MaxChords"/> chords the next one starts over.</summary>
    public void Record(KeyChord chord)
    {
        if (_recorded.Count >= MaxChords)
        {
            _recorded.Clear();
        }

        _recorded.Add(chord);
        RaiseRecording();
    }

    public void CancelRecording()
    {
        _recordingFor = null;
        _recorded.Clear();
        RaiseRecording();
    }

    public async Task AcceptRecordingAsync()
    {
        if (_recordingFor is not { } row || _recorded.Count == 0)
        {
            CancelRecording();
            return;
        }

        var keys = new KeySequence([.. _recorded]);
        CancelRecording();
        await SaveAsync(_workbench.Keybindings.RebindForUser(row.CommandId, keys), $"“{row.Title}” is now {keys.ToLabel(_workbench.Platform)}.").ConfigureAwait(true);
    }

    private void RaiseRecording()
    {
        OnPropertyChanged(nameof(IsRecording));
        OnPropertyChanged(nameof(RecordingTitle));
        OnPropertyChanged(nameof(RecordedLabel));
        OnPropertyChanged(nameof(RecordedConflicts));
        RecordingChanged?.Invoke(this, EventArgs.Empty);
    }

    // ------------------------------------------------------------------ edits

    private async Task RemoveAsync(KeybindingRowViewModel? row)
    {
        if (row is not null)
        {
            await SaveAsync(_workbench.Keybindings.RebindForUser(row.CommandId, null), $"Removed the key binding of “{row.Title}”.").ConfigureAwait(true);
        }
    }

    private async Task ResetAsync(KeybindingRowViewModel? row)
    {
        if (row is not null)
        {
            await SaveAsync(_workbench.Keybindings.ResetForUser(row.CommandId), $"Reset “{row.Title}” to its default key binding.").ConfigureAwait(true);
        }
    }

    private async Task SaveAsync(string json, string message)
    {
        try
        {
            await _workbench.SaveUserKeybindingsAsync(json, CancellationToken.None).ConfigureAwait(true);
            Status = message;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = $"Could not write keybindings.json: {ex.Message}";
        }
    }

    // ------------------------------------------------------------------ rows

    private void Rebuild()
    {
        var platform = _workbench.Platform;
        var rules = _workbench.Keybindings.EffectiveRules;
        var userCommands = _workbench.Keybindings.UserRules.Select(r => r.Command.TrimStart('-')).ToHashSet(StringComparer.Ordinal);
        var conflicts = _workbench.Keybindings.FindConflicts().SelectMany(g => g).ToHashSet();
        var query = _search.Trim();
        var selectedId = _selected?.CommandId;
        var selectedKeys = _selected?.KeyLabel;

        var rows = new List<KeybindingRowViewModel>();
        foreach (var descriptor in _workbench.Commands.Descriptors)
        {
            var title = descriptor.DisplayTitle;
            var userDefined = userCommands.Contains(descriptor.Id);
            var commandRules = rules.Where(r => r.Command == descriptor.Id).ToList();
            if (commandRules.Count == 0)
            {
                rows.Add(new KeybindingRowViewModel(descriptor.Id, title, null, string.Empty, string.Empty, userDefined, conflict: false));
            }

            foreach (var rule in commandRules)
            {
                rows.Add(new KeybindingRowViewModel(
                    descriptor.Id, title, rule, rule.Keys!.ToLabel(platform), SourceLabel(rule), userDefined, conflicts.Contains(rule)));
            }
        }

        Rows.Clear();
        foreach (var row in rows.Where(r => Matches(r, query)).OrderBy(r => r.Title, StringComparer.CurrentCultureIgnoreCase).ThenBy(r => r.KeyLabel, StringComparer.Ordinal))
        {
            Rows.Add(row);
        }

        Selected = Rows.FirstOrDefault(r => r.CommandId == selectedId && r.KeyLabel == selectedKeys) ?? Rows.FirstOrDefault(r => r.CommandId == selectedId);
    }

    private string SourceLabel(KeybindingRule rule) => rule.Source switch
    {
        KeybindingSource.User => "User",
        KeybindingSource.Plugin => rule.SourceId is { } id ? $"Plugin ({id})" : "Plugin",
        _ => "Default",
    };

    private bool Matches(KeybindingRowViewModel row, string query) =>
        query.Length == 0
        || row.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase)
        || row.CommandId.Contains(query, StringComparison.OrdinalIgnoreCase)
        || row.KeyLabel.Contains(query, StringComparison.OrdinalIgnoreCase)
        || (row.Rule?.Keys?.ToString().Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
        || row.Source.Contains(query, StringComparison.OrdinalIgnoreCase);
}
