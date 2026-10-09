using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using DaedalusStudio.Shell.Controls;
using DaedalusStudio.Shell.Input;
using DaedalusStudio.Shell.ViewModels;

namespace DaedalusStudio.Shell.Views;

/// <summary>
/// The Keyboard Shortcuts editor. While recording, every key press goes to the recorder, including Tab and
/// keys bound to commands: Enter accepts, Escape cancels.
/// </summary>
public sealed partial class KeyboardShortcutsView : UserControl, IPaneView
{
    private KeyboardShortcutsViewModel? _viewModel;

    public KeyboardShortcutsView()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        List.DoubleTapped += (_, _) => _viewModel?.StartRecording(_viewModel.Selected);
    }

    public void FocusPrimary() => SearchBox.Focus();

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_viewModel is not null)
        {
            _viewModel.RecordingChanged -= OnRecordingChanged;
        }

        _viewModel = DataContext as KeyboardShortcutsViewModel;
        if (_viewModel is not null)
        {
            _viewModel.RecordingChanged += OnRecordingChanged;
        }
    }

    /// <summary>While recording, the window hands every key press to the recorder (before key bindings).</summary>
    private void OnRecordingChanged(object? sender, EventArgs e)
    {
        var recording = _viewModel is { IsRecording: true };
        if (TopLevel.GetTopLevel(this) is MainWindow window)
        {
            window.KeyInterceptor = recording ? Record : null;
        }

        if (recording)
        {
            Dispatcher.UIThread.Post(() => Recorder.Focus(), DispatcherPriority.Loaded);
        }
        else if (!List.IsKeyboardFocusWithin)
        {
            List.Focus();
        }
    }

    private bool Record(KeyEventArgs e)
    {
        if (_viewModel is not { IsRecording: true } vm)
        {
            return false;
        }

        switch (e.Key)
        {
            case Key.Enter:
                _ = vm.AcceptRecordingAsync();
                break;
            case Key.Escape:
                vm.CancelRecording();
                break;
            default:
                if (KeyMapper.ToChord(e.Key, e.KeyModifiers) is { } chord)
                {
                    vm.Record(chord);
                }

                break;
        }

        return true;
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (_viewModel is not { } vm)
        {
            return;
        }

        if (vm.IsRecording)
        {
            e.Handled = Record(e);
            return;
        }

        if (e.Source is TextBox)
        {
            if (e.Key == Key.Down && vm.Rows.Count > 0)
            {
                vm.Selected ??= vm.Rows[0];
                List.Focus();
                e.Handled = true;
            }

            return;
        }

        switch (e.Key)
        {
            case Key.Enter when vm.Selected is not null:
                vm.StartRecording(vm.Selected);
                e.Handled = true;
                break;
            case Key.Delete when vm.Selected is not null:
                vm.RemoveCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }
}
