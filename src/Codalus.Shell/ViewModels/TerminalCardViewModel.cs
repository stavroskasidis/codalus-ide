using System.Windows.Input;
using Codalus.Shell.Infrastructure;
using Codalus.Workbench.Localization;
using Codalus.Workbench.Terminal;

namespace Codalus.Shell.ViewModels;

/// <summary>
/// The terminal card: a command the IDE runs in the Terminal (an install under sudo, an agent's sign-in) while something
/// covers the Terminal tool window (the setup and the start window, which stay above the main window; Connect, a dialog,
/// Report a Problem), shown over it, so its password prompt or sign-in code is where the user is looking. It closes when
/// the command ends; Hide closes it sooner, the command still running in the Terminal tool window, where it shows too.
/// </summary>
public sealed class TerminalCardViewModel : ObservableObject
{
    private TerminalSession? _session;
    private string _title = string.Empty;

    public TerminalCardViewModel(TerminalAppearance appearance)
    {
        Appearance = appearance;
        HideCommand = new RelayCommand(Hide);
    }

    /// <summary>The Terminal tool window's font and colors.</summary>
    public TerminalAppearance Appearance { get; }

    public bool IsOpen => _session is not null;

    /// <summary>The command running, while the card shows.</summary>
    public TerminalSession? Session => _session;

    /// <summary>What runs: the install's or the sign-in's name.</summary>
    public string Title
    {
        get => _title;
        private set => SetField(ref _title, value);
    }

    public string Hint => Localizer.Get("msg.terminalCard.hint", "Type here if it asks for something, such as your password. This closes when the command ends.");

    public ICommand HideCommand { get; }

    /// <summary>The last command the card showed, while it runs: Show Terminal shows it again.</summary>
    public TerminalSession? Hidden { get; private set; }

    /// <summary>Shows <paramref name="session"/> (any thread); it closes when the session ends.</summary>
    public void Show(TerminalSession session, string title)
    {
        Ui.Post(() =>
        {
            if (!ReferenceEquals(Hidden, session) && !ReferenceEquals(_session, session))
            {
                session.Exited += OnExited;
            }

            // Ended before it showed (or before the subscription above): nothing to type into.
            if (session.ExitCode is not null)
            {
                session.Exited -= OnExited;
                return;
            }

            Hidden = null;
            Title = title;
            if (SetField(ref _session, session, nameof(Session)))
            {
                OnPropertyChanged(nameof(IsOpen));
            }
        });
    }

    /// <summary>The last command hidden with Hide, again, while it runs; false when there's none.</summary>
    public bool ShowHidden()
    {
        if (Hidden is not { ExitCode: null } hidden)
        {
            return false;
        }

        Show(hidden, Title);
        return true;
    }

    /// <summary>Hide: the card closes, the command goes on in the Terminal tool window.</summary>
    public void Hide()
    {
        if (_session is { } session)
        {
            Hidden = session;
            Close();
        }
    }

    private void Close()
    {
        if (SetField(ref _session, null, nameof(Session)))
        {
            OnPropertyChanged(nameof(IsOpen));
        }
    }

    private void OnExited(object? sender, int code) => Ui.Post(() =>
    {
        if (sender is TerminalSession session)
        {
            session.Exited -= OnExited;
            if (ReferenceEquals(Hidden, session))
            {
                Hidden = null;
            }

            if (ReferenceEquals(_session, session))
            {
                Close();
            }
        }
    });
}
