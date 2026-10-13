using Avalonia.Controls;
using Avalonia.Input;
using Codalus.Shell.Controls;
using Codalus.Shell.ViewModels;

namespace Codalus.Shell.Views;

/// <summary>The Task Runner Explorer: double-click or Enter runs a script (or opens a package.json); the context menu restores.</summary>
public sealed partial class TaskRunnerView : UserControl, IPaneView
{
    public TaskRunnerView()
    {
        InitializeComponent();
        TaskRunnerViewModel? Vm() => DataContext as TaskRunnerViewModel;
        TaskRunnerRow? Row() => List.SelectedItem as TaskRunnerRow;
        Refresh.Click += (_, _) => _ = Vm()?.RefreshAsync();
        Run.Click += (_, _) => RunSelected();
        Restore.Click += (_, _) =>
        {
            if (Vm() is { } vm && Row() is { } row)
            {
                _ = vm.RestoreAsync(row);
            }
        };
        List.DoubleTapped += (_, _) => RunSelected();
        List.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                RunSelected();
                e.Handled = true;
            }
        };
        List.ContextRequested += (_, e) =>
        {
            if (Vm() is not { } vm || Row() is not { } row)
            {
                return;
            }

            var items = new List<MenuItem>();
            if (!row.IsHeader)
            {
                var run = new MenuItem { Header = "Run" };
                run.Click += (_, _) => _ = vm.RunAsync(row);
                items.Add(run);
            }

            var restore = new MenuItem { Header = "Restore Packages" };
            restore.Click += (_, _) => _ = vm.RestoreAsync(row);
            var open = new MenuItem { Header = "Open package.json" };
            open.Click += (_, _) => _ = vm.OpenAsync(row);
            items.Add(restore);
            items.Add(open);
            new ContextMenu { ItemsSource = items }.Open(List);
            e.Handled = true;
        };
    }

    private void RunSelected()
    {
        if (DataContext is TaskRunnerViewModel vm && List.SelectedItem is TaskRunnerRow row)
        {
            _ = vm.RunAsync(row);
        }
    }

    public void FocusPrimary() => List.Focus();
}
