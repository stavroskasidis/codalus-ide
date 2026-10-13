using System.Collections.ObjectModel;
using System.Windows.Input;
using Codalus.Projects.Protocol;
using Codalus.Rpc;
using Codalus.Shell.Infrastructure;
using Codalus.Workbench.Build;
using Codalus.Workbench.Projects;

namespace Codalus.Shell.ViewModels;

/// <summary>A project row of the Startup Projects page: its action and launch profile.</summary>
public sealed class StartupProjectRowViewModel(string path, string name, StartupAction action, bool executable) : ObservableObject
{
    /// <summary>The actions, in <see cref="StartupAction"/>'s order.</summary>
    public static IReadOnlyList<string> Actions { get; } = ["None", "Start", "Start without debugging"];

    private int _actionIndex = (int)action;
    private string? _profile;

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

    internal void SetProfiles(IReadOnlyList<string> profiles, string? chosen)
    {
        Profiles.Clear();
        foreach (var profile in profiles)
        {
            Profiles.Add(profile);
        }

        Profile = chosen;
        OnPropertyChanged(nameof(HasProfiles));
    }
}

/// <summary>
/// Configure Startup Projects (M14c), like the solution property page of other IDEs: one startup project, or several
/// that Start launches together (debugged in one session) with each one's action, launch profile and order. Saved with
/// the solution's session.
/// </summary>
public sealed class StartupProjectsViewModel : ObservableObject, ILoadable
{
    private readonly RunService _runs;
    private readonly SolutionService _solutions;
    private readonly Action _saved;
    private bool _multiple;
    private SolutionProjectInfo? _single;
    private StartupProjectRowViewModel? _selected;
    private string? _status;

    public StartupProjectsViewModel(RunService runs, SolutionService solutions, Action saved)
    {
        _runs = runs;
        _solutions = solutions;
        _saved = saved;
        ApplyCommand = new RelayCommand(Apply);
        MoveUpCommand = new RelayCommand(() => Move(-1));
        MoveDownCommand = new RelayCommand(() => Move(1));
    }

    public IReadOnlyList<SolutionProjectInfo> Projects => _solutions.Solution?.Projects ?? [];

    public ObservableCollection<StartupProjectRowViewModel> Rows { get; } = [];

    public ICommand ApplyCommand { get; }

    public ICommand MoveUpCommand { get; }

    public ICommand MoveDownCommand { get; }

    public bool IsMultiple
    {
        get => _multiple;
        set
        {
            if (SetField(ref _multiple, value))
            {
                OnPropertyChanged(nameof(IsSingle));
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

        // The saved order first, then the other projects.
        var saved = _runs.StartupEntries;
        var ordered = saved.Select(e => Projects.FirstOrDefault(p => SolutionService.PathComparer.Equals(p.Path, e.Project))).OfType<SolutionProjectInfo>()
            .Concat(Projects.Where(p => !saved.Any(e => SolutionService.PathComparer.Equals(e.Project, p.Path))))
            .ToList();
        Rows.Clear();
        foreach (var project in ordered)
        {
            var action = saved.FirstOrDefault(e => SolutionService.PathComparer.Equals(e.Project, project.Path))?.Action
                         ?? (!_runs.MultipleStartup && SolutionService.PathComparer.Equals(project.Path, _runs.StartupProject) ? StartupAction.Start : StartupAction.None);
            Rows.Add(new StartupProjectRowViewModel(project.Path, project.Name, action, _runs.IsExecutable(project.Path)));
        }

        Selected = Rows.FirstOrDefault();
        foreach (var row in Rows.Where(r => r.IsExecutable).ToList())
        {
            try
            {
                var (_, profiles, _) = await _runs.GetProfilesAsync(row.Path, CancellationToken.None).ConfigureAwait(true);
                row.SetProfiles([.. profiles.Where(p => p.IsRunnable).Select(p => p.Name)], _runs.ResolveProfile(row.Path, profiles)?.Name);
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

    /// <summary>Saves the choice: single or multiple startup projects, the actions in order, and the launch profiles.</summary>
    public void Apply()
    {
        _runs.SetStartupProjects(_multiple, [.. Rows.Select(r => new StartupEntry(r.Path, r.Action))], _single?.Path);
        foreach (var row in Rows.Where(r => r.Profile is not null))
        {
            _runs.SetProfile(row.Path, row.Profile);
        }

        Status = IsMultiple
            ? $"Start launches {Rows.Count(r => r.Action != StartupAction.None)} project(s)."
            : $"Start launches {_single?.Name ?? "the first executable project"}.";
        _saved();
    }
}
