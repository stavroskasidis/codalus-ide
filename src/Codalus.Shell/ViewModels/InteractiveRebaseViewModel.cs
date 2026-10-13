using System.Collections.ObjectModel;
using System.Windows.Input;
using Codalus.Shell.Docking;
using Codalus.Shell.Infrastructure;
using Codalus.Workbench.Git;

namespace Codalus.Shell.ViewModels;

/// <summary>A commit in the interactive rebase list: what to do with it, and a new message for reword.</summary>
public sealed class RebaseStepRow(RebaseStep step) : ObservableObject
{
    private string _action = step.Action;
    private string _message = step.Subject;

    public string Hash { get; } = step.Hash;

    public string ShortHash => Hash.Length > 8 ? Hash[..8] : Hash;

    public string Subject { get; } = step.Subject;

    public static IReadOnlyList<string> Actions { get; } = ["pick", "reword", "squash", "fixup", "drop"];

    public string Action
    {
        get => _action;
        set
        {
            if (SetField(ref _action, value ?? "pick"))
            {
                OnPropertyChanged(nameof(IsReword));
                OnPropertyChanged(nameof(AccessibleName));
            }
        }
    }

    public bool IsReword => _action == "reword";

    public string Message
    {
        get => _message;
        set => SetField(ref _message, value ?? string.Empty);
    }

    public string AccessibleName => $"{_action} {ShortHash} {Subject}";

    public RebaseStep ToStep() => new(_action, Hash, Subject) { Message = _action == "reword" ? _message : null };
}

/// <summary>
/// The interactive rebase editor (M13): the commits after the chosen base, oldest first. Reorder them, and pick, reword,
/// squash, fixup or drop each; Start runs the rebase without ever opening a text editor.
/// </summary>
public sealed class InteractiveRebaseViewModel : ObservableObject
{
    private readonly Func<IReadOnlyList<RebaseStep>, Task> _start;
    private RebaseStepRow? _selected;
    private string? _error;

    public InteractiveRebaseViewModel(string onto, string ontoTitle, IReadOnlyList<RebaseStep> steps, Func<IReadOnlyList<RebaseStep>, Task> start)
    {
        Onto = onto;
        OntoTitle = ontoTitle;
        _start = start;
        foreach (var step in steps)
        {
            Steps.Add(new RebaseStepRow(step));
        }

        _selected = Steps.FirstOrDefault();
        MoveUpCommand = new RelayCommand(() => Move(-1));
        MoveDownCommand = new RelayCommand(() => Move(1));
        StartCommand = new RelayCommand(() => _ = StartAsync());
    }

    public string Onto { get; }

    public string OntoTitle { get; }

    public string Heading => $"Rebase {Steps.Count} commit(s) onto {OntoTitle}. The list is oldest first.";

    public ObservableCollection<RebaseStepRow> Steps { get; } = [];

    public RebaseStepRow? Selected
    {
        get => _selected;
        set => SetField(ref _selected, value);
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

    public ICommand MoveUpCommand { get; }

    public ICommand MoveDownCommand { get; }

    public ICommand StartCommand { get; }

    public event EventHandler? Finished;

    public void Move(int direction)
    {
        if (_selected is not { } row)
        {
            return;
        }

        var index = Steps.IndexOf(row);
        var target = index + direction;
        if (target >= 0 && target < Steps.Count)
        {
            Steps.Move(index, target);
            Selected = row;
        }
    }

    public async Task StartAsync()
    {
        var steps = Steps.Select(s => s.ToStep()).ToList();
        var first = steps.FirstOrDefault(s => s.Action != "drop");
        if (first is { Action: "squash" or "fixup" })
        {
            Error = "The first commit kept can't be squashed or fixed up: there's nothing before it to combine with.";
            return;
        }

        Error = null;
        try
        {
            await _start(steps).ConfigureAwait(true);
            Finished?.Invoke(this, EventArgs.Empty);
        }
        catch (GitException ex)
        {
            Error = ex.Message;
            Finished?.Invoke(this, EventArgs.Empty); // stopped on a conflict: Git Changes shows the rebase in progress
        }
    }
}

/// <summary>An interactive rebase tab.</summary>
public sealed class InteractiveRebaseDocument : ShellDocument
{
    public const string DocumentId = "git-interactive-rebase";

    public InteractiveRebaseDocument(InteractiveRebaseViewModel viewModel)
    {
        ViewModel = viewModel;
        Id = DocumentId;
        Title = "Interactive Rebase";
    }

    public override string Kind => "interactiveRebase";

    public InteractiveRebaseViewModel ViewModel { get; }
}
