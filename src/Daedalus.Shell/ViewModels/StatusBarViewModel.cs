using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using System.Windows.Input;
using Daedalus.Shell.Infrastructure;
using Daedalus.Workbench.StatusBar;

namespace Daedalus.Shell.ViewModels;

/// <summary>A status bar entry. Text may start with <c>$(icon)</c> to show a built-in icon.</summary>
public sealed partial class StatusItemViewModel
{
    public StatusItemViewModel(StatusBarItem item, ICommand? command)
    {
        Item = item;
        Command = command;
        var match = IconPrefix().Match(item.Text);
        Icon = match.Success ? match.Groups[1].Value : null;
        Text = match.Success ? item.Text[match.Length..] : item.Text;
    }

    public StatusBarItem Item { get; }

    public string? Icon { get; }

    public bool HasIcon => Icon is not null;

    public string Text { get; }

    public bool HasText => Text.Length > 0;

    public string? Tooltip => Item.Tooltip;

    public string AccessibleName => Item.AccessibleName ?? (Text.Length > 0 ? Text : Item.Tooltip ?? Item.Id);

    public ICommand? Command { get; }

    public bool IsInteractive => Command is not null;

    [GeneratedRegex(@"^\$\(([a-z0-9-]+)\)\s*")]
    private static partial Regex IconPrefix();
}

public sealed class StatusBarViewModel : ObservableObject
{
    private readonly StatusBarService _service;
    private readonly Func<string, ICommand> _commandFor;
    private string? _message;
    private bool _isVisible = true;
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
            }
        }
    }

    public bool HasMessage => !string.IsNullOrEmpty(_message);

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

        target.Clear();
        foreach (var item in items)
        {
            target.Add(new StatusItemViewModel(item, item.Command is { } command ? _commandFor(command) : null));
        }
    }
}
