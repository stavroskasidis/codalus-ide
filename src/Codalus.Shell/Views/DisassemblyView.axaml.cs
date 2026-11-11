using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Codalus.Shell.Controls;
using Codalus.Shell.ViewModels;

namespace Codalus.Shell.Views;

/// <summary>
/// The Disassembly window (M58): the selected frame's machine code under its source lines; the list follows the
/// instruction the frame is at, and the two buttons step one instruction.
/// </summary>
public sealed partial class DisassemblyView : UserControl, IPaneView
{
    private DisassemblyViewModel? _model;

    public DisassemblyView()
    {
        InitializeComponent();
        ShownTracker.Track(this); // read only while shown, as the other debugger windows (M56)
        DataContextChanged += (_, _) =>
        {
            if (_model is not null)
            {
                _model.PropertyChanged -= OnModelChanged;
            }

            _model = DataContext as DisassemblyViewModel;
            if (_model is not null)
            {
                _model.PropertyChanged += OnModelChanged;
            }
        };
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DisassemblyViewModel.CurrentIndex) && _model is { CurrentIndex: >= 0 and var index } && index < _model.Rows.Count)
        {
            List.SelectedIndex = index;
            List.ScrollIntoView(index);
        }
    }

    private void OnStepOver(object? sender, RoutedEventArgs e) => _ = _model?.StepOverAsync();

    private void OnStepInto(object? sender, RoutedEventArgs e) => _ = _model?.StepIntoAsync();

    public void FocusPrimary() => List.Focus();
}
