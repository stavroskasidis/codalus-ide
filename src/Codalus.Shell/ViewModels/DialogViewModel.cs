using System.Collections.ObjectModel;
using System.Windows.Input;
using Codalus.Shell.Infrastructure;

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
}

/// <summary>An item of a dialog's checklist.</summary>
public sealed record DialogChoice(string Id, string Label, bool Checked, string? Detail = null);

/// <summary>A checklist row in the dialog.</summary>
public sealed class DialogChoiceViewModel(DialogChoice choice) : ObservableObject
{
    private bool _isChecked = choice.Checked;

    public string Id { get; } = choice.Id;

    public string Label { get; } = choice.Label;

    public string? Detail { get; } = choice.Detail;

    public bool HasDetail => Detail is { Length: > 0 };

    public bool IsChecked
    {
        get => _isChecked;
        set => SetField(ref _isChecked, value);
    }
}

/// <summary>A line of a dialog's details. <see cref="Kind"/> is <c>added</c>, <c>removed</c>, <c>header</c> or null.</summary>
public sealed record DetailLine(string Text, string? Kind = null)
{
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

    public bool IsDefault { get; } = isDefault;

    public ICommand Command { get; } = command;
}

/// <summary>
/// One in-window modal dialog at a time (confirmations, names, the trust prompt). Requests made while one is open
/// wait for it to close.
/// </summary>
public sealed class DialogViewModel : ObservableObject
{
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
            }
        }
    }

    public bool IsOpen => _request is not null;

    public string Title => _request?.Title ?? string.Empty;

    public string Message => _request?.Message ?? string.Empty;

    public bool HasInput => _request?.Input is not null;

    /// <summary>The input's mask: a dot for passwords and passphrases (askpass, M13b), none otherwise.</summary>
    public char PasswordChar => _request?.IsPassword == true ? '●' : '\0';

    public IReadOnlyList<DetailLine> Details => _request?.Details ?? [];

    public bool HasDetails => _request?.Details is { Count: > 0 };

    public ObservableCollection<DialogButtonViewModel> Buttons { get; } = [];

    /// <summary>The checklist (refactoring options).</summary>
    public ObservableCollection<DialogChoiceViewModel> Choices { get; } = [];

    public bool HasChoices => Choices.Count > 0;

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
        foreach (var label in request.Buttons)
        {
            Buttons.Add(new DialogButtonViewModel(label, label == DefaultButton, new RelayCommand(() => Choose(label))));
        }

        Choices.Clear();
        foreach (var choice in request.Choices ?? [])
        {
            Choices.Add(new DialogChoiceViewModel(choice));
        }

        SelectedChoice = Choices.FirstOrDefault();
        OnPropertyChanged(nameof(HasChoices));
        OnPropertyChanged(nameof(Reorderable));
        Opened?.Invoke(this, EventArgs.Empty);
        return await _pending.Task.ConfigureAwait(true);
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

/// <summary>Dialogs with details (previews) are wider.</summary>
public static class DialogWidth
{
    public static Avalonia.Data.Converters.IValueConverter Converter { get; } =
        new Avalonia.Data.Converters.FuncValueConverter<bool, double>(details => details ? 960 : 560);
}
