using Avalonia.Controls;
using Avalonia.Input;
using DaedalusStudio.Shell.Controls;
using DaedalusStudio.Shell.ViewModels;

namespace DaedalusStudio.Shell.Views;

/// <summary>The Containers window: the toolbar acts on the selected container; Enter or a double-click opens a file or folder.</summary>
public sealed partial class ContainersView : UserControl, IPaneView
{
    public ContainersView()
    {
        InitializeComponent();
        ContainersViewModel? Vm() => DataContext as ContainersViewModel;
        Refresh.Click += (_, _) => _ = Vm()?.RefreshAsync();
        Start.Click += (_, _) => _ = Vm()?.StartAsync();
        Stop.Click += (_, _) => _ = Vm()?.StopAsync();
        Restart.Click += (_, _) => _ = Vm()?.RestartAsync();
        Remove.Click += (_, _) => _ = Vm()?.RemoveAsync();
        Browse.Click += (_, _) => Vm()?.OpenInBrowser();
        Terminal.Click += (_, _) => _ = Vm()?.OpenTerminalAsync();
        Attach.Click += (_, _) => _ = Vm()?.AttachAsync();
        RemoveImage.Click += (_, _) => _ = Vm()?.RemoveImageAsync();
        PathBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && Vm() is { } vm)
            {
                _ = vm.LoadFilesAsync(vm.FilesPath);
                e.Handled = true;
            }
        };
        void OpenFile()
        {
            if (Vm() is { } vm && FileList.SelectedItem is ContainerFileRow file)
            {
                _ = vm.OpenFileAsync(file);
            }
        }

        FileList.DoubleTapped += (_, _) => OpenFile();
        FileList.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                OpenFile();
                e.Handled = true;
            }
        };
    }

    public void FocusPrimary() => ContainerList.Focus();
}
