using System.Collections.ObjectModel;
using System.Windows.Input;
using DaedalusStudio.Shell.Infrastructure;
using DaedalusStudio.Workbench.ExternalTools;

namespace DaedalusStudio.Shell.ViewModels;

/// <summary>An external tool being edited.</summary>
public sealed class ExternalToolRowViewModel(ExternalTool tool, Action changed) : ObservableObject
{
    private ExternalTool _tool = tool;

    public ExternalTool Tool => _tool;

    public string Title
    {
        get => _tool.Title;
        set => Update(_tool with { Title = value ?? string.Empty });
    }

    public string Command
    {
        get => _tool.Command;
        set => Update(_tool with { Command = value ?? string.Empty });
    }

    public string Arguments
    {
        get => _tool.Arguments;
        set => Update(_tool with { Arguments = value ?? string.Empty });
    }

    public string InitialDirectory
    {
        get => _tool.InitialDirectory;
        set => Update(_tool with { InitialDirectory = value ?? string.Empty });
    }

    public bool UseOutputWindow
    {
        get => _tool.UseOutputWindow;
        set => Update(_tool with { UseOutputWindow = value });
    }

    public bool PromptForArguments
    {
        get => _tool.PromptForArguments;
        set => Update(_tool with { PromptForArguments = value });
    }

    public string CommandId => _tool.CommandId;

    private void Update(ExternalTool updated, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (updated == _tool)
        {
            return;
        }

        _tool = updated;
        OnPropertyChanged(name);
        changed();
    }
}

/// <summary>
/// External Tools (ADR 0033): the user's tools, in order, with their command, arguments and initial directory (macros
/// allowed), whether output goes to the Output window and whether to ask for the arguments. Saving writes
/// <c>externalTools.json</c> and updates the Tools menu and the commands.
/// </summary>
public sealed class ExternalToolsViewModel : ObservableObject
{
    private readonly Func<IReadOnlyList<ExternalTool>> _load;
    private readonly Func<IReadOnlyList<ExternalTool>, Task> _save;
    private readonly Func<ExternalTool, Task> _run;
    private ExternalToolRowViewModel? _selected;
    private bool _isModified;
    private string? _error;

    public ExternalToolsViewModel(Func<IReadOnlyList<ExternalTool>> load, Func<IReadOnlyList<ExternalTool>, Task> save, Func<ExternalTool, Task> run, IReadOnlyList<ToolMacro>? macros = null)
    {
        Macros = macros ?? ExternalToolStore.Macros;
        _load = load;
        _save = save;
        _run = run;
        AddCommand = new RelayCommand(Add);
        RemoveCommand = new RelayCommand(Remove, () => _selected is not null);
        MoveUpCommand = new RelayCommand(() => Move(-1), () => _selected is not null && Tools.IndexOf(_selected) > 0);
        MoveDownCommand = new RelayCommand(() => Move(1), () => _selected is not null && Tools.IndexOf(_selected) < Tools.Count - 1);
        SaveCommand = new RelayCommand(() => _ = SaveAsync(), () => _isModified);
        RevertCommand = new RelayCommand(Load, () => _isModified);
        RunCommand = new RelayCommand(() => _ = RunSelectedAsync(), () => _selected is not null);
        Load();
    }

    public ObservableCollection<ExternalToolRowViewModel> Tools { get; } = [];

    public IReadOnlyList<ToolMacro> Macros { get; }

    public ICommand AddCommand { get; }

    public ICommand RemoveCommand { get; }

    public ICommand MoveUpCommand { get; }

    public ICommand MoveDownCommand { get; }

    public ICommand SaveCommand { get; }

    public ICommand RevertCommand { get; }

    public ICommand RunCommand { get; }

    public ExternalToolRowViewModel? Selected
    {
        get => _selected;
        set
        {
            if (SetField(ref _selected, value))
            {
                OnPropertyChanged(nameof(HasSelection));
                Raise();
            }
        }
    }

    public bool HasSelection => _selected is not null;

    public bool IsModified
    {
        get => _isModified;
        private set
        {
            if (SetField(ref _isModified, value))
            {
                Raise();
            }
        }
    }

    public string? Error
    {
        get => _error;
        private set
        {
            if (SetField(ref _error, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => _error is not null;

    private void Load()
    {
        Tools.Clear();
        foreach (var tool in _load())
        {
            Tools.Add(new ExternalToolRowViewModel(tool, MarkModified));
        }

        Selected = Tools.FirstOrDefault();
        IsModified = false;
        Error = null;
    }

    public void Add()
    {
        var tool = new ExternalTool(ExternalTool.NewId("New Tool", Tools.Select(t => t.Tool.Id)), "New Tool", string.Empty, "$(ItemPath)", "$(ItemDir)");
        var row = new ExternalToolRowViewModel(tool, MarkModified);
        Tools.Add(row);
        Selected = row;
        MarkModified();
    }

    private void Remove()
    {
        if (_selected is not { } row)
        {
            return;
        }

        var index = Tools.IndexOf(row);
        Tools.Remove(row);
        Selected = Tools.Count == 0 ? null : Tools[Math.Min(index, Tools.Count - 1)];
        MarkModified();
    }

    private void Move(int direction)
    {
        if (_selected is not { } row)
        {
            return;
        }

        var index = Tools.IndexOf(row);
        if (index + direction >= 0 && index + direction < Tools.Count)
        {
            Tools.Move(index, index + direction);
            Selected = row;
            MarkModified();
        }
    }

    /// <summary>Saves the tools (those without a title or command are refused).</summary>
    public async Task SaveAsync()
    {
        if (Tools.FirstOrDefault(t => string.IsNullOrWhiteSpace(t.Title) || string.IsNullOrWhiteSpace(t.Command)) is { } incomplete)
        {
            Selected = incomplete;
            Error = "Every tool needs a title and a command.";
            return;
        }

        try
        {
            await _save([.. Tools.Select(t => t.Tool)]).ConfigureAwait(true);
            IsModified = false;
            Error = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Error = ex.Message;
        }
    }

    private async Task RunSelectedAsync()
    {
        if (_selected is { } row)
        {
            await _run(row.Tool).ConfigureAwait(true);
        }
    }

    private void MarkModified() => IsModified = true;

    private void Raise()
    {
        foreach (var command in new[] { RemoveCommand, MoveUpCommand, MoveDownCommand, SaveCommand, RevertCommand, RunCommand })
        {
            ((RelayCommand)command).RaiseCanExecuteChanged();
        }
    }
}
