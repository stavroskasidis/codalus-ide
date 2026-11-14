using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Codalus.Shell.ViewModels;

namespace Codalus.Shell.Views;

/// <summary>
/// Help → About Codalus IDE (<see cref="AboutViewModel"/>): a modal window over the main one. OK (Enter) or Escape
/// closes it; OK comes last on macOS, first elsewhere, as in the IDE's other dialogs (<see cref="DialogViewModel.Order"/>).
/// </summary>
public sealed partial class AboutWindow : Window
{
    private AboutViewModel? _model;

    public AboutWindow()
        : this(DialogViewModel.PrimaryLastOnThisOs)
    {
    }

    /// <summary>A window with OK last (macOS) or first (Windows and Linux); tests choose.</summary>
    internal AboutWindow(bool primaryLast)
    {
        InitializeComponent();
        if (primaryLast)
        {
            FooterButtons.Children.Move(0, FooterButtons.Children.Count - 1);
        }

        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        Opened += (_, _) => OkButton.Focus();
        DataContextChanged += (_, _) => Attach(DataContext as AboutViewModel);
    }

    private void Attach(AboutViewModel? model)
    {
        if (_model is not null)
        {
            _model.CloseRequested -= OnCloseRequested;
        }

        _model = model;
        if (model is not null)
        {
            model.CloseRequested += OnCloseRequested;
        }
    }

    private void OnCloseRequested(object? sender, EventArgs e) => Close();

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None)
        {
            e.Handled = true;
            Close();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        Attach(null);
        base.OnClosed(e);
    }
}
