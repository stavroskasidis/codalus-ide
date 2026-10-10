using System.Collections.ObjectModel;
using System.Windows.Input;
using DaedalusStudio.Shell.Infrastructure;

namespace DaedalusStudio.Shell.ViewModels;

/// <summary>A project in the toolbar's startup project list.</summary>
public sealed record StartupProjectChoice(string Name, string Path)
{
    public override string ToString() => Name;
}

/// <summary>
/// The standard toolbar under the menu (M29): navigation, open and save, undo and redo, the solution configuration and
/// platform, the startup project and Start; while debugging, the debug commands. The shell keeps it up to date
/// (<see cref="SetSolution"/>, <see cref="SetStartup"/>, <see cref="SetDebugState"/>); choices made here go back through
/// the callbacks.
/// </summary>
public sealed class ToolbarViewModel : ObservableObject
{
    private readonly Func<string, ICommand> _command;
    private readonly Action<string, string> _selectConfiguration;
    private readonly Action<string> _selectStartup;
    private bool _updating;
    private string? _configuration;
    private string? _platform;
    private StartupProjectChoice? _startup;
    private string _startLabel = "Start";
    private bool _hasSolution;
    private string? _solutionName;
    private bool _isDebugging;
    private bool _isStopped;
    private bool _isRunning;
    private IReadOnlyList<(string Configuration, string Platform)> _pairs = [];

    public ToolbarViewModel(Func<string, ICommand> command, Action<string, string> selectConfiguration, Action<string> selectStartup, Action undo, Action redo)
    {
        _command = command;
        _selectConfiguration = selectConfiguration;
        _selectStartup = selectStartup;
        UndoCommand = new RelayCommand(undo);
        RedoCommand = new RelayCommand(redo);
    }

    public ICommand BackCommand => _command("workbench.navigateBack");

    public ICommand ForwardCommand => _command("workbench.navigateForward");

    public ICommand OpenCommand => _command("workbench.openSolution");

    public ICommand SaveCommand => _command("file.save");

    public ICommand SaveAllCommand => _command("file.saveAll");

    public ICommand UndoCommand { get; }

    public ICommand RedoCommand { get; }

    /// <summary>Start Debugging, or Continue when stopped at a breakpoint (F5).</summary>
    public ICommand StartCommand => _command(_isStopped ? "debug.continue" : "debug.start");

    public ICommand StartWithoutDebuggingCommand => _command("run.startWithoutDebugging");

    public ICommand BreakAllCommand => _command("debug.pause");

    public ICommand StopCommand => _command(_isDebugging ? "debug.stop" : "run.stop");

    public ICommand RestartCommand => _command("debug.restart");

    public ICommand StepOverCommand => _command("debug.stepOver");

    public ICommand StepIntoCommand => _command("debug.stepInto");

    public ICommand StepOutCommand => _command("debug.stepOut");

    public ICommand SearchCommand => _command("workbench.action.featureSearch");

    public ObservableCollection<string> Configurations { get; } = [];

    public ObservableCollection<string> Platforms { get; } = [];

    public ObservableCollection<StartupProjectChoice> StartupProjects { get; } = [];

    public bool HasSolution
    {
        get => _hasSolution;
        private set => SetField(ref _hasSolution, value);
    }

    /// <summary>The open solution's name, shown in the title bar.</summary>
    public string? SolutionName
    {
        get => _solutionName;
        private set => SetField(ref _solutionName, value);
    }

    public string? Configuration
    {
        get => _configuration;
        set
        {
            if (SetField(ref _configuration, value) && !_updating && value is not null)
            {
                Select(value, _platform);
            }
        }
    }

    public string? Platform
    {
        get => _platform;
        set
        {
            if (SetField(ref _platform, value) && !_updating && value is not null && _configuration is not null)
            {
                Select(_configuration, value);
            }
        }
    }

    public StartupProjectChoice? StartupProject
    {
        get => _startup;
        set
        {
            if (SetField(ref _startup, value) && !_updating && value is not null)
            {
                _selectStartup(value.Path);
            }
        }
    }

    /// <summary>The Start button's text: the startup project and its launch profile, or "Start".</summary>
    public string StartLabel
    {
        get => _startLabel;
        private set => SetField(ref _startLabel, value);
    }

    public bool IsDebugging
    {
        get => _isDebugging;
        private set => SetField(ref _isDebugging, value);
    }

    /// <summary>Stopped at a breakpoint or a step: Continue and the step commands apply.</summary>
    public bool IsStopped
    {
        get => _isStopped;
        private set => SetField(ref _isStopped, value);
    }

    /// <summary>Running under the debugger: Break All applies.</summary>
    public bool CanBreak => _isDebugging && !_isStopped;

    /// <summary>A program runs without the debugger: Stop applies.</summary>
    public bool IsRunning
    {
        get => _isRunning;
        private set => SetField(ref _isRunning, value);
    }

    public bool CanStop => _isDebugging || _isRunning;

    public bool CanStart => _hasSolution && !_isDebugging;

    /// <summary>The solution's configurations ("Debug", "Release") and platforms, and the active pair; null: none open.</summary>
    public void SetSolution(IReadOnlyList<(string Configuration, string Platform)>? pairs, (string Configuration, string Platform)? active, string? name = null)
    {
        SolutionName = name;
        _updating = true;
        try
        {
            _pairs = pairs ?? [];
            Replace(Configurations, _pairs.Select(p => p.Configuration).Distinct(StringComparer.Ordinal));
            Replace(Platforms, _pairs.Where(p => active is null || p.Configuration == active.Value.Configuration).Select(p => p.Platform).Distinct(StringComparer.Ordinal));
            Configuration = active?.Configuration;
            Platform = active?.Platform;
            HasSolution = pairs is not null;
            OnPropertyChanged(nameof(CanStart));
        }
        finally
        {
            _updating = false;
        }
    }

    /// <summary>The executable projects, the startup project and its launch profile.</summary>
    public void SetStartup(IReadOnlyList<StartupProjectChoice> projects, string? startup, string? profile)
    {
        _updating = true;
        try
        {
            Replace(StartupProjects, projects);
            StartupProject = StartupProjects.FirstOrDefault(p => DaedalusStudio.Workbench.Projects.SolutionService.PathComparer.Equals(p.Path, startup));
            StartLabel = StartupProject is null ? "Start" : profile is null || profile == StartupProject.Name ? StartupProject.Name : $"{StartupProject.Name}: {profile}";
        }
        finally
        {
            _updating = false;
        }
    }

    public void SetDebugState(bool debugging, bool stopped, bool running)
    {
        IsDebugging = debugging;
        IsStopped = stopped;
        IsRunning = running;
        OnPropertyChanged(nameof(CanBreak));
        OnPropertyChanged(nameof(CanStop));
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(StartCommand));
        OnPropertyChanged(nameof(StopCommand));
    }

    private void Select(string configuration, string? platform)
    {
        var pair = _pairs.FirstOrDefault(p => p.Configuration == configuration && p.Platform == platform);
        if (pair == default)
        {
            pair = _pairs.FirstOrDefault(p => p.Configuration == configuration);
        }

        if (pair != default)
        {
            _selectConfiguration(pair.Configuration, pair.Platform);
        }
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        var list = items.ToList();
        if (target.SequenceEqual(list))
        {
            return;
        }

        target.Clear();
        foreach (var item in list)
        {
            target.Add(item);
        }
    }
}
