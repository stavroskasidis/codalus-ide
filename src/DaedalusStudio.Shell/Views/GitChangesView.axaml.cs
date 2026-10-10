using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using DaedalusStudio.Shell.Controls;
using DaedalusStudio.Shell.ViewModels;

namespace DaedalusStudio.Shell.Views;

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

        // # completion of issues and work items (M13b).
        Message.TextChanged += (_, _) =>
        {
            _completionTimer.Stop();
            _completionTimer.Start();
        };
        _completionTimer.Tick += (_, _) =>
        {
            _completionTimer.Stop();
            _ = UpdateCompletionAsync();
        };
        Message.AddHandler(KeyDownEvent, OnMessageKeyDown, RoutingStrategies.Tunnel);
        CompletionList.DoubleTapped += (_, _) => AcceptCompletion();
    }

    private readonly DispatcherTimer _completionTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };

    // Only the latest request's answer is shown (older ones are ignored; their searches are short).
    private int _completionVersion;

    /// <summary>Whether the completion list is open (tests).</summary>
    internal bool IsCompletionOpen => CompletionPopup.IsOpen;

    /// <summary>The completions shown (tests).</summary>
    internal IReadOnlyList<CommitCompletion> CompletionItems => CompletionList.ItemsSource as IReadOnlyList<CommitCompletion> ?? [];

    /// <summary>Asks for completions of the <c>#…</c> before the caret (also called by tests, without the typing delay).</summary>
    internal async Task UpdateCompletionAsync()
    {
        if (DataContext is not GitChangesViewModel vm)
        {
            return;
        }

        var version = ++_completionVersion;
        var text = Message.Text ?? string.Empty;
        var caret = Message.CaretIndex;
        if (GitChangesViewModel.CompletionQuery(text, caret) is null)
        {
            CompletionPopup.IsOpen = false;
            return;
        }

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var items = await vm.CompleteAsync(text, caret, timeout.Token).ConfigureAwait(true);
            if (version != _completionVersion)
            {
                return;
            }

            CompletionList.ItemsSource = items;
            CompletionList.SelectedIndex = items.Count > 0 ? 0 : -1;
            CompletionPopup.IsOpen = items.Count > 0;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is Workbench.Hosting.HostingRequestException or Workbench.Hosting.HostingSignInRequiredException)
        {
            CompletionPopup.IsOpen = false;
        }
    }

    private void OnMessageKeyDown(object? sender, KeyEventArgs e)
    {
        if (!CompletionPopup.IsOpen)
        {
            return;
        }

        var count = CompletionItems.Count;
        switch (e.Key)
        {
            case Key.Down when count > 0:
                CompletionList.SelectedIndex = (CompletionList.SelectedIndex + 1) % count;
                e.Handled = true;
                break;
            case Key.Up when count > 0:
                CompletionList.SelectedIndex = (CompletionList.SelectedIndex + count - 1) % count;
                e.Handled = true;
                break;
            case Key.Enter or Key.Tab when e.KeyModifiers == KeyModifiers.None:
                AcceptCompletion();
                e.Handled = true;
                break;
            case Key.Escape:
                CompletionPopup.IsOpen = false;
                e.Handled = true;
                break;
        }
    }

    /// <summary>Puts the selected completion's reference in place of the <c>#…</c> being typed.</summary>
    internal void AcceptCompletion()
    {
        CompletionPopup.IsOpen = false;
        if (CompletionList.SelectedItem is not CommitCompletion item)
        {
            return;
        }

        var (text, caret) = GitChangesViewModel.ApplyCompletion(Message.Text ?? string.Empty, Message.CaretIndex, item.Reference);
        Message.Text = text;
        Message.CaretIndex = caret;
        Message.Focus();
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
            // A conflicted file opens in the merge editor (M13).
            _ = Report(row.Status.IsConflict ? vm.RunAsync("git.mergeEditor:" + row.Status.Path) : vm.OpenDiffAsync(row));
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
