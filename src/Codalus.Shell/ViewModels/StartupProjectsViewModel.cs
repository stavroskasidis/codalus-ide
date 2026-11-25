using System.Collections.ObjectModel;
using System.Windows.Input;
using Codalus.Projects.Protocol;
using Codalus.Rpc;
using Codalus.Shell.Infrastructure;
using Codalus.Workbench.Build;
using Codalus.Workbench.Localization;
using Codalus.Workbench.Projects;

namespace Codalus.Shell.ViewModels;

/// <summary>A project row of the Startup Projects page: its action and launch profile.</summary>
public sealed class StartupProjectRowViewModel(string path, string name, StartupAction action, bool executable) : ObservableObject
{
    /// <summary>The actions, in <see cref="StartupAction"/>'s order.</summary>
    public static IReadOnlyList<string> Actions { get; } = ["None", "Start", "Start without debugging"];

    private int _actionIndex = (int)action;
    private string? _profile;
    private string? _wanted;

    public string Path { get; } = path;

    public string Name { get; } = name;

    /// <summary>Whether it builds a program (others can be started, but usually aren't).</summary>
    public bool IsExecutable { get; } = executable;

    public int ActionIndex
    {
        get => _actionIndex;
        set => SetField(ref _actionIndex, Math.Clamp(value, 0, Actions.Count - 1));
    }

    public StartupAction Action
    {
        get => (StartupAction)_actionIndex;
        set => ActionIndex = (int)value;
    }

    /// <summary>The project's launch profiles (empty without a launch settings file).</summary>
    public ObservableCollection<string> Profiles { get; } = [];

    public bool HasProfiles => Profiles.Count > 0;

    public string? Profile
    {
        get => _profile;
        set => SetField(ref _profile, value);
    }

    /// <summary>
    /// The launch profile the set's entry names (ADR 0109): null starts the first runnable one, and is kept null when
    /// that's still the choice, so a launch file's entry without a <c>DebugTarget</c> stays without one.
    /// </summary>
    public string? EntryProfile => _wanted is null && _profile == Profiles.FirstOrDefault() ? null : _profile;

    /// <summary>Shows a set's entry for the project: its action and launch profile.</summary>
    internal void Assign(StartupAction action, string? profile)
    {
        Action = action;
        _wanted = profile;
        Profile = Choose();
    }

    internal void SetProfiles(IReadOnlyList<string> profiles)
    {
        Profiles.Clear();
        foreach (var profile in profiles)
        {
            Profiles.Add(profile);
        }

        Profile = Choose();
        OnPropertyChanged(nameof(HasProfiles));
    }

    /// <summary>The entry's profile, kept when the launch settings don't list it (it's written back as it was); else the first.</summary>
    private string? Choose()
    {
        if (_wanted is { } wanted && !Profiles.Contains(wanted) && Profiles.Count > 0)
        {
            Profiles.Add(wanted);
        }

        return _wanted ?? Profiles.FirstOrDefault();
    }
}

/// <summary>A set the Startup Projects page edits: the unnamed set, a named set of the launch files, or a new one not saved yet.</summary>
public sealed class LaunchSetChoiceViewModel(string name, LaunchSet? saved, bool unnamed) : ObservableObject
{
    private string _name = name;

    /// <summary>The set's name (the unnamed set's is the toolbar's label).</summary>
    public string Name
    {
        get => _name;
        set => SetField(ref _name, value);
    }

    /// <summary>The set as the launch files hold it (null: the unnamed set, or a new one).</summary>
    public LaunchSet? Saved { get; set; } = saved;

    /// <summary>The unnamed set, saved with the solution's session (M14c).</summary>
    public bool IsUnnamed { get; } = unnamed;

    /// <summary>A new set the user's entries go into until Apply saves it.</summary>
    internal IReadOnlyList<StartupEntry>? Draft { get; set; }

    public override string ToString() => Name;
}

/// <summary>
/// Configure Startup Projects (M14c, ADR 0109), like the solution property page of other IDEs: one startup project, or
/// a set of several that Start launches together (debugged in one session) with each one's action, launch profile and
/// order. The unnamed set is saved with the solution's session; named sets are the classic IDE's multi-project launch
/// profiles, in the solution's launch files (shared ones in the file people commit, the others in its <c>.user</c> file).
/// </summary>
public sealed class StartupProjectsViewModel : ObservableObject, ILoadable
{
    private readonly RunService _runs;
    private readonly SolutionService _solutions;
    private readonly Action _saved;
    private bool _multiple;
    private SolutionProjectInfo? _single;
    private StartupProjectRowViewModel? _selected;
    private LaunchSetChoiceViewModel? _set;
    private string _setName = string.Empty;
    private bool _shared;
    private string? _status;

    public StartupProjectsViewModel(RunService runs, SolutionService solutions, Action saved)
    {
        _runs = runs;
        _solutions = solutions;
        _saved = saved;
        ApplyCommand = new RelayCommand(Apply);
        MoveUpCommand = new RelayCommand(() => Move(-1));
        MoveDownCommand = new RelayCommand(() => Move(1));
        NewSetCommand = new RelayCommand(NewSet);
        DeleteSetCommand = new RelayCommand(() => _ = DeleteSetAsync());
    }

    public IReadOnlyList<SolutionProjectInfo> Projects => _solutions.Solution?.Projects ?? [];

    public ObservableCollection<StartupProjectRowViewModel> Rows { get; } = [];

    /// <summary>The sets: the unnamed one first, then the launch files' named sets and any new one.</summary>
    public ObservableCollection<LaunchSetChoiceViewModel> Sets { get; } = [];

    public ICommand ApplyCommand { get; }

    public ICommand MoveUpCommand { get; }

    public ICommand MoveDownCommand { get; }

    /// <summary>A new named set, from the entries shown (saved by Apply).</summary>
    public ICommand NewSetCommand { get; }

    /// <summary>Removes the chosen named set from its launch file.</summary>
    public ICommand DeleteSetCommand { get; }

    /// <summary>Whether the solution has launch files (a folder or a project opened alone has none).</summary>
    public bool CanNameSets => _solutions.Solution is { IsFolder: false, Format: SolutionFormats.Sln or SolutionFormats.Slnx };

    public bool IsMultiple
    {
        get => _multiple;
        set
        {
            if (SetField(ref _multiple, value))
            {
                OnPropertyChanged(nameof(IsSingle));
                OnPropertyChanged(nameof(IsNamedSetEditable));
            }
        }
    }

    public bool IsSingle
    {
        get => !_multiple;
        set => IsMultiple = !value;
    }

    public SolutionProjectInfo? SingleProject
    {
        get => _single;
        set => SetField(ref _single, value);
    }

    /// <summary>The set the rows show; choosing another shows its entries (unapplied changes to the rows are dropped).</summary>
    public LaunchSetChoiceViewModel? SelectedSet
    {
        get => _set;
        set
        {
            var previous = _set;
            if (!SetField(ref _set, value))
            {
                return;
            }

            // A new set keeps what it was made from until Apply: leaving it keeps its entries for coming back.
            if (previous is { Saved: null, IsUnnamed: false })
            {
                previous.Draft = CurrentEntries();
            }

            if (value is not null)
            {
                SetName = value.IsUnnamed ? string.Empty : value.Name;
                IsShared = value.Saved?.Shared ?? false;
                ShowEntries(EntriesOf(value));
            }

            OnPropertyChanged(nameof(IsNamedSet));
            OnPropertyChanged(nameof(IsNamedSetEditable));
        }
    }

    /// <summary>Whether the chosen set is a named one (its name and sharing can be edited, and it can be deleted).</summary>
    public bool IsNamedSet => _set is { IsUnnamed: false };

    public bool IsNamedSetEditable => _multiple && IsNamedSet;

    /// <summary>The chosen named set's name, as Apply saves it (a rename when it differs).</summary>
    public string SetName
    {
        get => _setName;
        set => SetField(ref _setName, value ?? string.Empty);
    }

    /// <summary>Share: the set goes in the launch file people commit, else in the user's own <c>.user</c> file.</summary>
    public bool IsShared
    {
        get => _shared;
        set => SetField(ref _shared, value);
    }

    public StartupProjectRowViewModel? Selected
    {
        get => _selected;
        set => SetField(ref _selected, value);
    }

    /// <summary>"Applied", or why profiles couldn't be read.</summary>
    public string? Status
    {
        get => _status;
        private set => SetField(ref _status, value);
    }

    public async Task LoadAsync()
    {
        IsMultiple = _runs.MultipleStartup;
        SingleProject = Projects.FirstOrDefault(p => SolutionService.PathComparer.Equals(p.Path, _runs.StartupProject));
        OnPropertyChanged(nameof(Projects));
        OnPropertyChanged(nameof(CanNameSets));

        Rows.Clear();
        foreach (var project in Projects)
        {
            Rows.Add(new StartupProjectRowViewModel(project.Path, project.Name, StartupAction.None, _runs.IsExecutable(project.Path)));
        }

        // The sets, then the active one's entries: the saved order first, then the other projects.
        Sets.Clear();
        Sets.Add(new LaunchSetChoiceViewModel(ShellHost.MultipleStartupProjectsLabel, null, unnamed: true));
        foreach (var set in _runs.LaunchSets)
        {
            Sets.Add(new LaunchSetChoiceViewModel(set.Name, set, unnamed: false));
        }

        _set = null;
        SelectedSet = Sets.FirstOrDefault(s => s.Saved is { } saved && saved.Name == _runs.ActiveLaunchSet?.Name) ?? Sets[0];
        if (!_runs.MultipleStartup && _runs.StartupEntries.Count == 0 && _runs.StartupProject is { } single)
        {
            // Turning Multiple on starts from the single startup project.
            Rows.FirstOrDefault(r => SolutionService.PathComparer.Equals(r.Path, single))?.Assign(StartupAction.Start, _runs.ChosenProfile(single));
        }

        Selected = Rows.FirstOrDefault();
        foreach (var row in Rows.Where(r => r.IsExecutable).ToList())
        {
            try
            {
                var (_, profiles, _) = await _runs.GetProfilesAsync(row.Path, CancellationToken.None).ConfigureAwait(true);
                row.SetProfiles([.. profiles.Where(p => p.IsRunnable).Select(p => p.Name)]);
            }
            catch (Exception ex) when (ex is InvalidOperationException or JsonRpcRemoteException or JsonRpcConnectionLostException)
            {
                Status = $"{row.Name}'s launch profiles couldn't be read: {ex.Message}";
            }
        }
    }

    public void Move(int direction)
    {
        if (_selected is not { } row)
        {
            return;
        }

        var index = Rows.IndexOf(row);
        var target = index + direction;
        if (index >= 0 && target >= 0 && target < Rows.Count)
        {
            Rows.Move(index, target);
            Selected = row;
        }
    }

    /// <summary>A new named set, "Profile 1" (or the next free number), starting from the entries shown.</summary>
    public void NewSet()
    {
        var number = 1;
        while (Sets.Any(s => !s.IsUnnamed && s.Name == $"Profile {number}"))
        {
            number++;
        }

        var choice = new LaunchSetChoiceViewModel($"Profile {number}", null, unnamed: false) { Draft = CurrentEntries() };
        Sets.Add(choice);
        IsMultiple = true;
        SelectedSet = choice;
        Status = Localizer.Get("msg.startupProjects.nameTheNewProfileThenApply", "Name the new launch profile, choose its projects, then Apply.");
    }

    /// <summary>Removes the chosen named set (from its launch file, if it was saved); the unnamed set is shown.</summary>
    public async Task DeleteSetAsync()
    {
        if (_set is not { IsUnnamed: false } choice)
        {
            return;
        }

        if (choice.Saved is { } saved)
        {
            try
            {
                await Task.Run(() => _runs.DeleteLaunchSet(saved)).ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                Status = Localizer.Format("msg.startupProjects.couldNotDelete", "Could not delete {0}: {1}", choice.Name, ex.Message);
                return;
            }
        }

        SelectedSet = Sets[0];
        Sets.Remove(choice);
        Status = Localizer.Format("msg.startupProjects.deleted", "Deleted the launch profile {0}.", choice.Name);
        _saved();
    }

    /// <summary>Saves the choice: single or multiple startup projects, the chosen set's actions in order and launch profiles.</summary>
    public void Apply() => _ = ApplyAsync();

    /// <summary>
    /// Applies the page: a single startup project or the unnamed set at once; a named set saved to the launch files off
    /// the UI thread, then made the one Start starts.
    /// </summary>
    public async Task ApplyAsync()
    {
        var entries = CurrentEntries();
        var started = entries.Count(e => e.Action != StartupAction.None);
        if (!_multiple || _set is null or { IsUnnamed: true })
        {
            _runs.SetStartupProjects(_multiple, _multiple ? entries : _runs.StartupEntries, _single?.Path);
            Status = IsMultiple
                ? Localizer.Format("msg.startupProjects.startLaunchesProjects", "Start launches {0} project(s).", started)
                : Localizer.Format("msg.startupProjects.startLaunches", "Start launches {0}.", _single?.Name ?? Localizer.Get("msg.startupProjects.theFirstExecutableProject", "the first executable project"));
            _saved();
            return;
        }

        var choice = _set;
        var name = SetName.Trim();
        if (name.Length == 0)
        {
            Status = Localizer.Get("msg.startupProjects.aLaunchProfileNeedsAName", "A launch profile needs a name.");
            return;
        }

        if (Sets.Any(s => s != choice && !s.IsUnnamed && s.Name == name))
        {
            Status = Localizer.Format("msg.startupProjects.thereIsAlreadyALaunchProfile", "There is already a launch profile named {0}.", name);
            return;
        }

        var set = choice.Saved is { } saved ? saved.With(name, entries, _shared) : new LaunchSet(name, entries, _shared);
        var previous = choice.Saved?.Name;
        try
        {
            await Task.Run(() => _runs.SaveLaunchSet(set, previous)).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Text.Json.JsonException)
        {
            Status = Localizer.Format("msg.startupProjects.couldNotSave", "Could not save {0}: {1}", name, ex.Message);
            return;
        }

        _runs.SelectLaunchSet(name);
        choice.Name = name;
        choice.Saved = _runs.LaunchSets.FirstOrDefault(s => s.Name == name);
        choice.Draft = null;
        Status = Localizer.Format("msg.startupProjects.savedLaunchProfile", "Start launches {0}: {1} project(s), saved in {2}.", name, started, Path.GetFileName(choice.Saved?.File) ?? string.Empty);
        _saved();
    }

    /// <summary>The rows as a set's entries, in order: each one's action and launch profile.</summary>
    private List<StartupEntry> CurrentEntries() => [.. Rows.Select(r => new StartupEntry(r.Path, r.Action, r.EntryProfile))];

    private IReadOnlyList<StartupEntry> EntriesOf(LaunchSetChoiceViewModel choice) =>
        choice.IsUnnamed ? _runs.StartupEntries : choice.Saved?.Entries ?? choice.Draft ?? CurrentEntries();

    /// <summary>Shows a set's entries: their order first, then the other projects (None); each with its action and profile.</summary>
    private void ShowEntries(IReadOnlyList<StartupEntry> entries)
    {
        var ordered = entries.Select(e => Rows.FirstOrDefault(r => SolutionService.PathComparer.Equals(r.Path, e.Project))).OfType<StartupProjectRowViewModel>()
            .Concat(Rows.Where(r => !entries.Any(e => SolutionService.PathComparer.Equals(e.Project, r.Path))))
            .Distinct()
            .ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            var row = ordered[i];
            var entry = entries.FirstOrDefault(e => SolutionService.PathComparer.Equals(e.Project, row.Path));
            row.Assign(entry?.Action ?? StartupAction.None, entry?.Profile);
            var at = Rows.IndexOf(row);
            if (at != i)
            {
                Rows.Move(at, i);
            }
        }
    }
}
