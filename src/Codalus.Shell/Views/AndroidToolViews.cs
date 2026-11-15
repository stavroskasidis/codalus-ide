using Avalonia.Controls;
using Avalonia.Threading;
using Codalus.Shell.Controls;
using Codalus.Shell.ViewModels;

namespace Codalus.Shell.Views;

/// <summary>The Android SDK Manager (ADR 0104).</summary>
public sealed partial class AndroidSdkManagerView : UserControl, IPaneView
{
    public AndroidSdkManagerView() => InitializeComponent();

    public void FocusPrimary() => SearchBox.Focus();
}

/// <summary>The Android Device Manager (ADR 0104).</summary>
public sealed partial class AndroidDeviceManagerView : UserControl, IPaneView
{
    public AndroidDeviceManagerView()
    {
        InitializeComponent();

        // A double-click starts a stopped emulator, as in the Devices window.
        EmulatorList.DoubleTapped += (_, _) =>
        {
            if (DataContext is AndroidDeviceManagerViewModel { StartCommand: { } start } && start.CanExecute(null))
            {
                start.Execute(null);
            }
        };
    }

    public void FocusPrimary()
    {
        if (EmulatorList.ItemCount > 0)
        {
            EmulatorList.Focus();
        }
        else
        {
            NewButton.Focus();
        }
    }
}

/// <summary>The Device Log (ADR 0104): the last entry stays in sight as entries come, unless paused.</summary>
public sealed partial class DeviceLogView : UserControl, IPaneView
{
    private DeviceLogViewModel? _model;

    public DeviceLogView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_model is not null)
            {
                _model.RowsAppended -= OnRowsAppended;
            }

            _model = DataContext as DeviceLogViewModel;
            if (_model is not null)
            {
                _model.RowsAppended += OnRowsAppended;
            }
        };
    }

    private void OnRowsAppended(object? sender, EventArgs e)
    {
        if (_model is { Paused: false, Rows.Count: > 0 } model)
        {
            Dispatcher.UIThread.Post(() => LogList.ScrollIntoView(model.Rows.Count - 1), DispatcherPriority.Background);
        }
    }

    public void FocusPrimary() => SourceBox.Focus();
}
