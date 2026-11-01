using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Codalus.Shell.Controls;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Output;

namespace Codalus.Shell.Views;

/// <summary>
/// The Aspire window: the toolbar acts on the selected resource; a double-click opens its endpoint. The selected
/// resource's console shows as the Output window shows a channel (<see cref="OutputPresenter"/>).
/// </summary>
public sealed partial class AspireView : UserControl, IPaneView
{
    private readonly OutputPresenter _console;
    private readonly FollowTail _tail;
    private AspireViewModel? _viewModel;

    public AspireView()
    {
        InitializeComponent();
        _tail = Controls.FollowTail.Attach(LogText); // the console output follows new lines (M34a)
        _console = new OutputPresenter(LogText, _tail);
        AspireViewModel? Vm() => DataContext as AspireViewModel;
        Dashboard.Click += (_, _) => Vm()?.OpenDashboard();
        Start.Click += (_, _) => _ = Vm()?.StartAsync();
        Stop.Click += (_, _) => _ = Vm()?.StopAsync();
        Restart.Click += (_, _) => _ = Vm()?.RestartAsync();
        Browse.Click += (_, _) => Vm()?.OpenEndpoint();
        ResourceList.DoubleTapped += (_, _) => Vm()?.OpenEndpoint();
    }

    /// <summary>The console's text as shown, without escape sequences (tests).</summary>
    internal string ConsoleText => LogText.Text;

    public void FocusPrimary() => ResourceList.Focus();

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        Bind(this.IsAttachedToVisualTree() ? DataContext as AspireViewModel : null);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Bind(DataContext as AspireViewModel);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Bind(null);
    }

    /// <summary>Follows <paramref name="viewModel"/>'s console while the view is in the window; null pauses it.</summary>
    private void Bind(AspireViewModel? viewModel)
    {
        if (ReferenceEquals(viewModel, _viewModel))
        {
            return;
        }

        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelChanged;
        }

        _viewModel = viewModel;
        if (viewModel is null)
        {
            _console.Pause(); // shown again, the same console goes on from what it shows
            return;
        }

        viewModel.PropertyChanged += OnViewModelChanged;
        ShowConsole(viewModel.ConsoleOutput);
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AspireViewModel.ConsoleOutput))
        {
            ShowConsole(_viewModel?.ConsoleOutput);
        }
    }

    private void ShowConsole(OutputChannel? console)
    {
        if (console is not null && ReferenceEquals(console, _console.Channel))
        {
            return;
        }

        _console.Show(console);
        _tail.Reset();
    }
}
