using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using System.Windows.Input;
using Codalus.Shell.Infrastructure;
using Codalus.Workbench.StatusBar;

namespace Codalus.Shell.ViewModels;

/// <summary>
/// A status bar entry. Text may start with <c>$(icon)</c> to show a built-in icon. Updated in place when only its
/// content changes (the caret position changes on every keystroke), so the button isn't recreated.
/// </summary>
public sealed partial class StatusItemViewModel : ObservableObject
{
    public StatusItemViewModel(StatusBarItem item, ICommand? command)
    {
        Item = item;
        Command = command;
        (Icon, Text) = Split(item.Text);
    }

    public StatusBarItem Item { get; private set; }

    public string? Icon { get; private set; }

    public bool HasIcon => Icon is not null;

    public string Text { get; private set; }

    public bool HasText => Text.Length > 0;

    public string? Tooltip => Item.Tooltip;

    public string AccessibleName => Item.AccessibleName ?? (Text.Length > 0 ? Text : Item.Tooltip ?? Item.Id);

    public ICommand? Command { get; private set; }

    public bool IsInteractive => Command is not null;

    /// <summary>Shows <paramref name="item"/> (same id) without recreating the view.</summary>
    public void Update(StatusBarItem item, ICommand? command)
    {
        var previous = Item;
        Item = item;
        (Icon, Text) = Split(item.Text);
        if (previous.Command != item.Command)
        {
            Command = command;
            OnPropertyChanged(nameof(Command));
            OnPropertyChanged(nameof(IsInteractive));
        }

        OnPropertyChanged(nameof(Item));
        OnPropertyChanged(nameof(Icon));
        OnPropertyChanged(nameof(HasIcon));
        OnPropertyChanged(nameof(Text));
        OnPropertyChanged(nameof(HasText));
        OnPropertyChanged(nameof(Tooltip));
        OnPropertyChanged(nameof(AccessibleName));
    }

    private static (string? Icon, string Text) Split(string text)
    {
        var match = IconPrefix().Match(text);
        return match.Success ? (match.Groups[1].Value, text[match.Length..]) : (null, text);
    }

    [GeneratedRegex(@"^\$\(([a-z0-9-]+)\)\s*")]
    private static partial Regex IconPrefix();
}

public sealed class StatusBarViewModel : ObservableObject
{
    private readonly StatusBarService _service;
    private readonly Func<string, ICommand> _commandFor;
    private string? _message;
    private bool _isVisible = true;
    private bool _hasSolution;
    private bool _isDebugging;
    private int _messageVersion;

    public StatusBarViewModel(StatusBarService service, Func<string, ICommand> commandFor)
    {
        _service = service;
        _commandFor = commandFor;
        service.Changed += (_, _) => Ui.Post(Refresh);
        Refresh();
    }

    public ObservableCollection<StatusItemViewModel> Left { get; } = [];

    public ObservableCollection<StatusItemViewModel> Right { get; } = [];

    /// <summary>A transient message, e.g. a pending key chord.</summary>
    public string? Message
    {
        get => _message;
        private set
        {
            if (SetField(ref _message, value))
            {
                OnPropertyChanged(nameof(HasMessage));
                OnPropertyChanged(nameof(DisplayMessage));
            }
        }
    }

    public bool HasMessage => !string.IsNullOrEmpty(_message);

    /// <summary>The message, or "Ready" when there is none (as the status bar of a classic IDE shows).</summary>
    public string DisplayMessage => string.IsNullOrEmpty(_message) ? "Ready" : _message;

    /// <summary>A solution is open: the bar is the accent color; without one it is purple (M29).</summary>
    public bool HasSolution
    {
        get => _hasSolution;
        set
        {
            if (SetField(ref _hasSolution, value))
            {
                OnPropertyChanged(nameof(IsEmptyState));
            }
        }
    }

    /// <summary>A debug session is active: the bar is orange.</summary>
    public bool IsDebugging
    {
        get => _isDebugging;
        set
        {
            if (SetField(ref _isDebugging, value))
            {
                OnPropertyChanged(nameof(IsEmptyState));
            }
        }
    }

    /// <summary>No solution and no debug session.</summary>
    public bool IsEmptyState => !_hasSolution && !_isDebugging;

    public bool IsVisible
    {
        get => _isVisible;
        set => SetField(ref _isVisible, value);
    }

    /// <summary>Shows <paramref name="message"/>; with a timeout it disappears unless replaced meanwhile.</summary>
    public void ShowMessage(string message, TimeSpan? timeout = null)
    {
        var version = ++_messageVersion;
        Message = message;
        if (timeout is { } delay)
        {
            Avalonia.Threading.DispatcherTimer.RunOnce(
                () =>
                {
                    if (_messageVersion == version)
                    {
                        Message = null;
                    }
                },
                delay);
        }
    }

    public void ClearMessage()
    {
        _messageVersion++;
        Message = null;
    }

    private void Refresh()
    {
        Replace(Left, _service.GetItems(StatusBarAlignment.Left));
        Replace(Right, _service.GetItems(StatusBarAlignment.Right));
    }

    private void Replace(ObservableCollection<StatusItemViewModel> target, IReadOnlyList<StatusBarItem> items)
    {
        if (target.Select(t => t.Item).SequenceEqual(items))
        {
            return;
        }

        // Same items in the same order: update the changed ones in place.
        if (target.Select(t => t.Item.Id).SequenceEqual(items.Select(i => i.Id)))
        {
            for (var i = 0; i < items.Count; i++)
            {
                if (target[i].Item != items[i])
                {
                    target[i].Update(items[i], items[i].Command is { } command ? _commandFor(command) : null);
                }
            }

            return;
        }

        target.Clear();
        foreach (var item in items)
        {
            target.Add(new StatusItemViewModel(item, item.Command is { } command ? _commandFor(command) : null));
        }
    }
}
