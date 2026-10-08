using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Daedalus.Shell.ViewModels;

namespace Daedalus.Shell.Views;

/// <summary>The command palette / quick pick. The text box keeps focus; the list follows Up/Down.</summary>
public sealed partial class QuickPickView : UserControl
{
    private QuickPickViewModel? _viewModel;

    public QuickPickView()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        List.Tapped += (_, e) =>
        {
            if (e.Source is Visual visual && visual.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext is QuickPickRow row)
            {
                _viewModel?.Accept(row);
            }
        };
    }

    public void FocusInput()
    {
        Input.Focus();
        Input.CaretIndex = Input.Text?.Length ?? 0;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelChanged;
        }

        _viewModel = DataContext as QuickPickViewModel;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelChanged;
        }
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(QuickPickViewModel.SelectedIndex) && _viewModel is { SelectedIndex: >= 0 and var index })
        {
            List.ScrollIntoView(index);
        }
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (_viewModel is not { } vm)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Down:
                vm.Move(1);
                break;
            case Key.Up:
                vm.Move(-1);
                break;
            case Key.PageDown:
                vm.Move(10);
                break;
            case Key.PageUp:
                vm.Move(-10);
                break;
            case Key.Enter:
                vm.Accept();
                break;
            case Key.Escape:
                vm.Cancel();
                break;
            default:
                return;
        }

        e.Handled = true;
    }
}
