using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Threading;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Terminal;

namespace Codalus.Shell.Views;

/// <summary>
/// The terminal card's view (<see cref="TerminalCardViewModel"/>): the command's session, sized to the card while it shows
/// (<see cref="TerminalSession.SizeOwner"/>: the Terminal tool window behind it, showing the same session, doesn't resize it
/// back), and the focus in it, so a password is typed there.
/// </summary>
public sealed partial class TerminalCardView : UserControl
{
    private TerminalCardViewModel? _model;
    private TerminalSession? _shown;

    public TerminalCardView()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_model is not null)
        {
            _model.PropertyChanged -= OnModelChanged;
            _model.Appearance.PropertyChanged -= OnAppearanceChanged;
        }

        _model = DataContext as TerminalCardViewModel;
        if (_model is { } model)
        {
            model.PropertyChanged += OnModelChanged;
            model.Appearance.PropertyChanged += OnAppearanceChanged;
            ApplyAppearance();
        }

        Attach(_model?.Session);
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TerminalCardViewModel.Session))
        {
            Attach(_model?.Session);
        }
    }

    private void OnAppearanceChanged(object? sender, PropertyChangedEventArgs e) => ApplyAppearance();

    private void ApplyAppearance()
    {
        if (_model?.Appearance is { } appearance)
        {
            Terminal.SetFont(appearance.FontFamily, appearance.FontSize);
            Terminal.Palette = appearance.Palette;
            Terminal.CopyOnSelect = appearance.CopyOnSelect;
        }
    }

    /// <summary>Shows <paramref name="session"/> (null: none), taking its size from the tool window and giving it back.</summary>
    private void Attach(TerminalSession? session)
    {
        if (ReferenceEquals(_shown, session))
        {
            return;
        }

        if (_shown is { } old && ReferenceEquals(old.SizeOwner, Terminal))
        {
            Terminal.Session = null;
            old.SizeOwner = null; // the tool window sizes it to its own bounds again
        }

        _shown = session;
        if (session is not null)
        {
            session.SizeOwner = Terminal;
        }

        Terminal.Session = session;
        if (session is not null)
        {
            Dispatcher.UIThread.Post(() => Terminal.Focus(), DispatcherPriority.Loaded);
        }
    }
}
