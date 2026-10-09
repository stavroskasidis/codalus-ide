using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Daedalus.Shell.Controls;
using Daedalus.Shell.ViewModels;

namespace Daedalus.Shell.Views;

/// <summary>The Git Changes window: double-click (or Enter) opens a file's diff; row buttons stage, unstage and discard.</summary>
public sealed partial class GitChangesView : UserControl, IPaneView
{
    public GitChangesView()
    {
        InitializeComponent();
        foreach (var list in new[] { StagedList, ChangesList, ConflictList })
        {
            list.DoubleTapped += (_, _) => OpenSelected(list);
            list.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    OpenSelected(list);
                    e.Handled = true;
                }
            };
        }

        Message.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Control) && DataContext is GitChangesViewModel vm)
            {
                _ = Report(vm.CommitAsync(all: !vm.HasStaged));
                e.Handled = true;
            }
        };
    }

    /// <summary>Shows git errors (set by the shell).</summary>
    public static Action<Exception>? ReportError { get; set; }

    private static async Task Report(Task task)
    {
        try
        {
            await task.ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is Workbench.Git.GitException or InvalidOperationException or IOException)
        {
            ReportError?.Invoke(ex);
        }
    }

    private void OpenSelected(ListBox list)
    {
        if (DataContext is GitChangesViewModel vm && list.SelectedItem is GitFileRow row)
        {
            _ = Report(vm.OpenDiffAsync(row));
        }
    }

    private void OnRowAction(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not GitChangesViewModel vm || sender is not Button { DataContext: GitFileRow row, Tag: string action })
        {
            return;
        }

        _ = Report(action switch
        {
            "stage" => vm.StageAsync(row),
            "unstage" => vm.UnstageAsync(row),
            _ => vm.RunAsync("git.discard:" + row.Status.Path),
        });
    }

    private void OnAll(object? sender, RoutedEventArgs e)
    {
        if (DataContext is GitChangesViewModel vm && sender is Button { Tag: string action })
        {
            _ = Report(action == "stageAll" ? vm.StageAllAsync() : vm.UnstageAllAsync());
        }
    }

    private void OnCommit(object? sender, RoutedEventArgs e)
    {
        if (DataContext is GitChangesViewModel vm && sender is Button { Tag: string kind })
        {
            _ = Report(vm.CommitAsync(all: kind == "all"));
        }
    }

    private void OnCommand(object? sender, RoutedEventArgs e)
    {
        if (DataContext is GitChangesViewModel vm && sender is Button { Tag: string command })
        {
            _ = Report(vm.RunAsync(command));
        }
    }

    public void FocusPrimary() => Message.Focus();
}
