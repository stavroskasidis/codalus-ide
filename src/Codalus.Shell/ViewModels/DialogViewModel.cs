using System.Collections.ObjectModel;
using System.Windows.Input;
using Codalus.Shell.Infrastructure;
using Codalus.Workbench.Localization;

namespace Codalus.Shell.ViewModels;

/// <summary>What a dialog asks: a message, optional text input with validation, and buttons.</summary>
public sealed record DialogRequest(string Title, string Message, IReadOnlyList<string> Buttons)
{
    /// <summary>The button chosen with Enter. Defaults to the first one.</summary>
    public string? DefaultButton { get; init; }

    /// <summary>Shows a text box with this initial text (null: no input).</summary>
    public string? Input { get; init; }

    /// <summary>Selects this range of <see cref="Input"/> initially (e.g. the name without extension).</summary>
    public (int Start, int Length)? InputSelection { get; init; }

    /// <summary>Returns an error for invalid input, which disables the default button.</summary>
    public Func<string, string?>? Validate { get; init; }

    public bool IsDanger { get; init; }

    /// <summary>Monospace lines shown in a scrollable box (previews of changes, colored by <see cref="DetailLine.Kind"/>).</summary>
    public IReadOnlyList<DetailLine>? Details { get; init; }

    /// <summary>A checklist (members to generate, parameters to keep…); null: none (M12b refactoring options).</summary>
    public IReadOnlyList<DialogChoice>? Choices { get; init; }

    /// <summary>The checklist's items can be moved up and down (Change Signature).</summary>
    public bool Reorderable { get; init; }

    /// <summary>The input is a secret: masked as it is typed (askpass's passwords and passphrases, M13b).</summary>
    public bool IsPassword { get; init; }

    /// <summary>The header's icon (an <see cref="Controls.Icon"/> kind, in the command's color on a tile; M34h); null: a dangerous one gets the warning icon, others none.</summary>
    public string? Icon { get; init; }

    /// <summary>A muted line under the title saying what the dialog acts on (the type and file; M34h), or null.</summary>
    public string? Context { get; init; }
}

/// <summary>An item of a dialog's checklist.</summary>
public sealed record DialogChoice(string Id, string Label, bool Checked, string? Detail = null)
{
    /// <summary>The member's icon (an <see cref="Controls.Icon"/> kind) before its label, or null.</summary>
    public string? Icon { get; init; }
}

/// <summary>A checklist row in the dialog.</summary>
public sealed class DialogChoiceViewModel(DialogChoice choice) : ObservableObject
{
    private bool _isChecked = choice.Checked;

    public string Id { get; } = choice.Id;

    public string Label { get; } = choice.Label;

    public string? Detail { get; } = choice.Detail;

    public bool HasDetail => Detail is { Length: > 0 };

    public string? Icon { get; } = choice.Icon;

    public bool HasIcon => Icon is { Length: > 0 };

    public bool IsChecked
    {
        get => _isChecked;
        set => SetField(ref _isChecked, value);
    }
}

/// <summary>A line of a dialog's details. <see cref="Kind"/> is <c>added</c>, <c>removed</c>, <c>header</c>, <c>note</c> or null.</summary>
public sealed record DetailLine(string Text, string? Kind = null)
{
    /// <summary>A note about the changes (Convert to SDK-Style's, M43a): wrapped text above them, not a line of code.</summary>
    public bool IsNote => Kind == "note";

    public bool IsAdded => Kind == "added";

    public bool IsRemoved => Kind == "removed";

    public bool IsHeader => Kind == "header";
}

/// <summary>The choice (null: cancelled), the entered text, and the checked checklist items' ids in their order.</summary>
public sealed record DialogResult(string? Button, string? Input)
{
    public IReadOnlyList<string> Checked { get; init; } = [];
}

public sealed class DialogButtonViewModel(string label, bool isDefault, ICommand command)
{
    public string Label { get; } = label;

    /// <summary>The default button (Enter): the dialog's one primary, drawn with the accent.</summary>
    public bool IsDefault { get; } = isDefault;

    public ICommand Command { get; } = command;
}

/// <summary>
/// One in-window modal dialog at a time (confirmations, names, the trust prompt). Requests made while one is open
/// wait for it to close. Its buttons show in the OS's order (M34h, docs/design/controls.md): the request lists them
/// primary first, as Windows and Linux show them; macOS shows them reversed, the primary last.
/// </summary>
public sealed class DialogViewModel : ObservableObject
{
    /// <summary>Whether this OS puts a dialog's primary button last (macOS) rather than first (Windows, Linux).</summary>
    public static bool PrimaryLastOnThisOs => OperatingSystem.IsMacOS();

    private Task _queue = Task.CompletedTask;
    private TaskCompletionSource<DialogResult>? _pending;
    private DialogRequest? _request;
    private string _input = string.Empty;
    private string? _error;

    public DialogRequest? Request
    {
        get => _request;
        private set
        {
            if (SetField(ref _request, value))
            {
                OnPropertyChanged(nameof(IsOpen));
                OnPropertyChanged(nameof(Title));
                OnPropertyChanged(nameof(Message));
                OnPropertyChanged(nameof(HasInput));
                OnPropertyChanged(nameof(PasswordChar));
                OnPropertyChanged(nameof(Details));
                OnPropertyChanged(nameof(HasDetails));
                OnPropertyChanged(nameof(Notes));
                OnPropertyChanged(nameof(HasNotes));
                OnPropertyChanged(nameof(Changes));
                OnPropertyChanged(nameof(HasChanges));
                OnPropertyChanged(nameof(Icon));
                OnPropertyChanged(nameof(HasIcon));
                OnPropertyChanged(nameof(IconTone));
                OnPropertyChanged(nameof(IsInfoTone));
                OnPropertyChanged(nameof(IsWarningTone));
                OnPropertyChanged(nameof(IsSuccessTone));
                OnPropertyChanged(nameof(IsErrorTone));
                OnPropertyChanged(nameof(Context));
                OnPropertyChanged(nameof(HasContext));
                OnPropertyChanged(nameof(ShowsMoveHint));
            }
        }
    }

    /// <summary>The buttons' order: the primary last (macOS) or first (Windows and Linux). Tests set it.</summary>
    public bool PrimaryLast { get; set; } = PrimaryLastOnThisOs;

    /// <summary>The header's icon kind: the request's, the warning icon for a dangerous one, or null.</summary>
    public string? Icon => _request?.Icon ?? (_request?.IsDanger == true ? "warning" : null);

    public bool HasIcon => Icon is not null;

    /// <summary>The icon tile's tint, from the icon's palette color: <c>warning</c> (yellow, orange, tan), <c>success</c>, <c>error</c> or <c>info</c>.</summary>
    public string IconTone => Icon is not { } icon ? "info" : Controls.CommandIcons.ColorOf(icon) switch
    {
        "yellow" or "orange" or "folder" => "warning",
        "green" => "success",
        "red" => "error",
        _ => "info",
    };

    public bool IsInfoTone => IconTone == "info";

    public bool IsWarningTone => IconTone == "warning";

    public bool IsSuccessTone => IconTone == "success";

    public bool IsErrorTone => IconTone == "error";

    public string? Context => _request?.Context;

    public bool HasContext => _request?.Context is { Length: > 0 };

    /// <summary>The footer's hint names Alt+Up and Alt+Down when the checklist can be reordered.</summary>
    public bool ShowsMoveHint => _request?.Reorderable == true && HasChoices;

    public bool IsOpen => _request is not null;

    public string Title => _request?.Title ?? string.Empty;

    public string Message => _request?.Message ?? string.Empty;

    public bool HasInput => _request?.Input is not null;

    /// <summary>The input's mask: a dot for passwords and passphrases (askpass, M13b), none otherwise.</summary>
    public char PasswordChar => _request?.IsPassword == true ? '●' : '\0';

    public IReadOnlyList<DetailLine> Details => _request?.Details ?? [];

    public bool HasDetails => _request?.Details is { Count: > 0 };

    /// <summary>The details' notes (<see cref="DetailLine.IsNote"/>): prose, wrapped above the changes (M34g on M43).</summary>
    public IReadOnlyList<DetailLine> Notes => [.. Details.Where(d => d.IsNote)];

    public bool HasNotes => Details.Any(d => d.IsNote);

    /// <summary>The details' other lines: the changes, in monospace, scrolling sideways.</summary>
    public IReadOnlyList<DetailLine> Changes => [.. Details.Where(d => !d.IsNote)];

    public bool HasChanges => Details.Any(d => !d.IsNote);

    public ObservableCollection<DialogButtonViewModel> Buttons { get; } = [];

    /// <summary>The checklist (refactoring options).</summary>
    public ObservableCollection<DialogChoiceViewModel> Choices { get; } = [];

    public bool HasChoices => Choices.Count > 0;

    /// <summary>
    /// The checklist's Select all: true when every item is checked, false when none, null (mixed) otherwise. Checking it
    /// checks all; unchecking it unchecks all, except from mixed, which checks all (as a click on a mixed box expects).
    /// </summary>
    public bool? AllChecked
    {
        get => Choices.Count == 0 || Choices.All(c => !c.IsChecked) ? false : Choices.All(c => c.IsChecked) ? true : null;
        set
        {
            var check = value == true || (value == false && AllChecked is null);
            foreach (var choice in Choices)
            {
                choice.IsChecked = check;
            }

            OnPropertyChanged();
        }
    }

    /// <summary>How many items are checked: "2 of 4".</summary>
    public string CheckedSummary => Localizer.Format("dialog.checkedOfTotal", "{0} of {1}", Choices.Count(c => c.IsChecked), Choices.Count);

    public bool Reorderable => _request?.Reorderable == true;

    private DialogChoiceViewModel? _selectedChoice;

    public DialogChoiceViewModel? SelectedChoice
    {
        get => _selectedChoice;
        set => SetField(ref _selectedChoice, value);
    }

    /// <summary>Moves the selected checklist item up (-1) or down (+1).</summary>
    public void MoveChoice(int direction)
    {
        if (_selectedChoice is not { } choice)
        {
            return;
        }

        var index = Choices.IndexOf(choice);
        var target = index + direction;
        if (index >= 0 && target >= 0 && target < Choices.Count)
        {
            Choices.Move(index, target);
            SelectedChoice = choice;
        }
    }

    public ICommand MoveUpCommand => new RelayCommand(() => MoveChoice(-1));

    public ICommand MoveDownCommand => new RelayCommand(() => MoveChoice(1));

    public string InputText
    {
        get => _input;
        set
        {
            if (SetField(ref _input, value ?? string.Empty))
            {
                Error = _request?.Validate?.Invoke(_input);
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

    public event EventHandler? Opened;

    public event EventHandler? Closed;

    /// <summary>Shows <paramref name="request"/> once earlier dialogs have closed (UI thread only).</summary>
    public Task<DialogResult> ShowAsync(DialogRequest request)
    {
        var previous = _queue;
        var shown = ShowAfterAsync(previous, request);
        _queue = shown;
        return shown;
    }

    private async Task<DialogResult> ShowAfterAsync(Task previous, DialogRequest request)
    {
        await previous.ConfigureAwait(true);
        _pending = new TaskCompletionSource<DialogResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _input = request.Input ?? string.Empty;
        OnPropertyChanged(nameof(InputText));
        Request = request;
        Error = request.Validate?.Invoke(_input);
        Buttons.Clear();
        foreach (var label in PrimaryLast ? request.Buttons.Reverse() : request.Buttons)
        {
            Buttons.Add(new DialogButtonViewModel(label, label == DefaultButton, new RelayCommand(() => Choose(label))));
        }

        Choices.Clear();
        foreach (var choice in request.Choices ?? [])
        {
            var row = new DialogChoiceViewModel(choice);
            row.PropertyChanged += OnChoiceChanged;
            Choices.Add(row);
        }

        SelectedChoice = Choices.FirstOrDefault();
        OnPropertyChanged(nameof(HasChoices));
        OnPropertyChanged(nameof(Reorderable));
        OnPropertyChanged(nameof(ShowsMoveHint));
        OnPropertyChanged(nameof(AllChecked));
        OnPropertyChanged(nameof(CheckedSummary));
        Opened?.Invoke(this, EventArgs.Empty);
        return await _pending.Task.ConfigureAwait(true);
    }

    private void OnChoiceChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DialogChoiceViewModel.IsChecked))
        {
            OnPropertyChanged(nameof(AllChecked));
            OnPropertyChanged(nameof(CheckedSummary));
        }
    }

    /// <summary>Space on the checklist: checks or unchecks the selected item.</summary>
    public void ToggleSelectedChoice()
    {
        if (_selectedChoice is { } choice)
        {
            choice.IsChecked = !choice.IsChecked;
        }
    }

    private string? DefaultButton => _request?.DefaultButton ?? (_request is { Buttons.Count: > 0 } r ? r.Buttons[0] : null);

    /// <summary>Enter: the default button, unless the input is invalid.</summary>
    public void Accept()
    {
        if (DefaultButton is { } button)
        {
            Choose(button);
        }
    }

    public void Cancel() => Close(new DialogResult(null, null));

    /// <summary>The header's close button: the same as Escape.</summary>
    public ICommand CancelCommand => new RelayCommand(Cancel);

    private void Choose(string button)
    {
        if (HasError && button == DefaultButton)
        {
            return;
        }

        Close(new DialogResult(button, HasInput ? _input : null) { Checked = [.. Choices.Where(c => c.IsChecked).Select(c => c.Id)] });
    }

    private void Close(DialogResult result)
    {
        if (_pending is not { } pending)
        {
            return;
        }

        _pending = null;
        Request = null;
        Buttons.Clear();
        foreach (var choice in Choices)
        {
            choice.PropertyChanged -= OnChoiceChanged;
        }

        Choices.Clear();
        OnPropertyChanged(nameof(HasChoices));
        Closed?.Invoke(this, EventArgs.Empty);
        pending.TrySetResult(result);
    }

    // ------------------------------------------------------------------ common dialogs

    public async Task<bool> ConfirmAsync(string title, string message, string action, bool danger = false) =>
        (await ShowAsync(new DialogRequest(title, message, [action, "Cancel"]) { IsDanger = danger }).ConfigureAwait(true)).Button == action;

    public async Task<string?> InputAsync(string title, string message, string initial, Func<string, string?>? validate = null, (int, int)? selection = null)
    {
        var result = await ShowAsync(new DialogRequest(title, message, ["OK", "Cancel"]) { Input = initial, Validate = validate, InputSelection = selection }).ConfigureAwait(true);
        return result.Button == "OK" ? result.Input : null;
    }
}

/// <summary>Dialogs with details (previews) are wider; the others grow with their content up to 720 px (Shell.Size.Dialog.MaxWidth, M34h).</summary>
public static class DialogWidth
{
    public static Avalonia.Data.Converters.IValueConverter Converter { get; } =
        new Avalonia.Data.Converters.FuncValueConverter<bool, double>(details => details ? 960 : 720);
}
