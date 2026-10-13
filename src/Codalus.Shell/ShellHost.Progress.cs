using System.Globalization;
using Codalus.Shell.Infrastructure;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Commands;
using Codalus.Workbench.Notifications;
using Codalus.Workbench.StatusBar;

namespace Codalus.Shell;

/// <summary>
/// Running tasks (plugin API 1.0's <c>IWindow.WithProgressAsync</c>, M22a): the status bar shows the newest with its
/// message and percentage, and how many more; clicking lists them all, and cancels the one chosen when it allows it.
/// </summary>
public sealed partial class ShellHost
{
    public const string ProgressStatusItem = "codalus.progress";
    public const string ShowTasksCommand = "workbench.showRunningTasks";

    private void InitializeProgress()
    {
        Workbench.Progress.Changed += (_, _) => Ui.Post(UpdateProgressStatus);
        Workbench.Commands.Register(
            new CommandDescriptor(ShowTasksCommand, "Show Running Tasks", ContributionSource.Shell) { Category = "View" },
            async (_, _) =>
            {
                await Ui.RunAsync(ShowRunningTasksAsync).ConfigureAwait(false);
                return null;
            });
    }

    private static string Describe(ProgressTask task) =>
        task.Title
        + (task.Percent is { } percent ? " " + Math.Round(percent).ToString(CultureInfo.InvariantCulture) + "%" : string.Empty)
        + (task.Message is { Length: > 0 } message ? ": " + message : string.Empty)
        + (task.IsCancellationRequested ? " (cancelling)" : string.Empty);

    private void UpdateProgressStatus()
    {
        var running = Workbench.Progress.Running;
        if (running.Count == 0)
        {
            Workbench.StatusBar.Remove(ProgressStatusItem);
            return;
        }

        var newest = running[^1];
        var more = running.Count > 1 ? $" (+{running.Count - 1})" : string.Empty;
        Workbench.StatusBar.Set(new StatusBarItem(ProgressStatusItem, $"$(sync) {Describe(newest)}{more}", StatusBarAlignment.Left, 30, ContributionSource.Shell.Id)
        {
            Tooltip = string.Join('\n', running.Select(t => $"{t.Source}: {Describe(t)}")) + "\nClick for the running tasks.",
            Command = ShowTasksCommand,
            AccessibleName = $"{running.Count} running task{(running.Count == 1 ? string.Empty : "s")}: {newest.Title}",
        });
    }

    /// <summary>The running tasks in a quick pick; choosing a cancellable one cancels it.</summary>
    public async Task ShowRunningTasksAsync()
    {
        var running = Workbench.Progress.Running;
        if (running.Count == 0)
        {
            StatusBar.ShowMessage("Nothing is running.", TimeSpan.FromSeconds(3));
            return;
        }

        var items = running.Select(t => new QuickPickItem(Describe(t))
        {
            Detail = t.Source + (t.Cancellable && !t.IsCancellationRequested ? " · choose to cancel" : string.Empty),
            Tag = t,
        }).ToList();
        if (await Palette.ShowAsync(items, "Running tasks").ConfigureAwait(true) is { Tag: ProgressTask { Cancellable: true } task })
        {
            task.Cancel();
        }
    }
}
