using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Codalus.Shell.Controls;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Git;

namespace Codalus.Shell.Views;

/// <summary>
/// The Git Changes window: double-click (or Enter) opens a file's diff; row buttons stage, unstage and discard; the
/// commit button's menu also pushes or syncs; double-click applies a stash.
/// </summary>
public sealed partial class GitChangesView : UserControl, IPaneView
{
    public GitChangesView()
    {
        InitializeComponent();
        foreach (var (list, selected) in new (Control, Func<object?>)[]
                 {
                     (StagedList, () => StagedList.SelectedItem), (ChangesList, () => ChangesList.SelectedItem), (ConflictList, () => ConflictList.SelectedItem),
                     (StagedTree, () => StagedTree.SelectedItem), (ChangesTree, () => ChangesTree.SelectedItem), (ConflictTree, () => ConflictTree.SelectedItem),
                 })
        {
            list.DoubleTapped += (_, e) =>
            {
                if (OpenSelected(selected()))
                {
                    e.Handled = true;
                }
            };
            list.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter && OpenSelected(selected()))
                {
                    e.Handled = true;
                }
            };
        }

        // The Changes menu's view options say what they'd switch to.
        if (ViewOptionsButton.Flyout is MenuFlyout viewMenu)
        {
            viewMenu.Opening += (_, _) =>
            {
                if (DataContext is not GitChangesViewModel vm)
                {
                    return;
                }

                foreach (var item in viewMenu.Items.OfType<MenuItem>())
                {
                    switch (item.Tag)
                    {
                        case "openFileAsDefault":
                            item.IsChecked = vm.OpenFileAsDefault;
                            break;
                        case "switchView":
                            item.Header = vm.SwitchViewText;
                            break;
                        case "filter":
                            item.Header = vm.ShowFilterText;
                            break;
                    }
                }
            };
        }

        Message.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Control) && DataContext is GitChangesViewModel { CanCommit: true } vm)
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

        StashList.DoubleTapped += (_, _) =>
        {
            if (DataContext is GitChangesViewModel vm && StashList.SelectedItem is GitStash stash)
            {
                _ = Report(vm.ApplyStashAsync(stash, pop: false));
            }
        };

        // The commit menu says what it commits: the staged changes when there are some, otherwise all of them.
        if (CommitButton.Flyout is MenuFlyout commitMenu)
        {
            commitMenu.Opening += (_, _) =>
            {
                if (DataContext is not GitChangesViewModel vm)
                {
                    return;
                }

                foreach (var item in commitMenu.Items.OfType<MenuItem>())
                {
                    item.Header = item.Tag switch
                    {
                        "push" => vm.CommitAndPushText,
                        "sync" => vm.CommitAndSyncText,
                        _ => vm.CommitText,
                    };
                }
            };
        }
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

        (DataContext as GitChangesViewModel)?.RememberItem(item);
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

    /// <summary>Opens a file row or file node (folders expand instead); false when <paramref name="selected"/> isn't a file.</summary>
    private bool OpenSelected(object? selected)
    {
        if (DataContext is not GitChangesViewModel vm || (selected as GitFileRow ?? (selected as GitChangeNode)?.Row) is not { } row)
        {
            return false;
        }

        // A conflicted file opens in the merge editor (M13).
        _ = Report(row.Status.IsConflict ? vm.RunAsync("git.mergeEditor:" + row.Status.Path) : vm.OpenAsync(row));
        return true;
    }

    /// <summary>A row's button or context menu item: on a file (list or tree), or on a folder of the tree (all its files).</summary>
    private void OnFileAction(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not GitChangesViewModel vm || sender is not Control { Tag: string action, DataContext: { } target })
        {
            return;
        }

        var node = target as GitChangeNode;
        if ((target as GitFileRow ?? node?.Row) is { } row)
        {
            _ = Report(action switch
            {
                "stage" => vm.StageAsync(row),
                "unstage" => vm.UnstageAsync(row),
                "discard" => vm.RunAsync("git.discard:" + row.Status.Path),
                "open" => vm.OpenFileAsync(row),
                _ => vm.OpenDiffAsync(row),
            });
        }
        else if (node is not null)
        {
            _ = Report(action switch
            {
                "stage" => vm.StageAsync(node),
                "unstage" => vm.UnstageAsync(node),
                _ => Task.CompletedTask,
            });
        }
    }

    private void OnSortColumn(object? sender, RoutedEventArgs e)
    {
        if (DataContext is GitChangesViewModel vm && sender is Button { Tag: string column })
        {
            vm.SortBy(column);
        }
    }

    private void OnViewOption(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not GitChangesViewModel vm || sender is not MenuItem { Tag: string option })
        {
            return;
        }

        switch (option)
        {
            case "openFileAsDefault":
                vm.OpenFileAsDefault = !vm.OpenFileAsDefault;
                break;
            case "switchView":
                vm.IsTreeView = !vm.IsTreeView;
                break;
            case "filter":
                vm.ShowFilter = !vm.ShowFilter;
                if (vm.ShowFilter)
                {
                    Dispatcher.UIThread.Post(() => Filter.Focus());
                }

                break;
        }
    }

    private void OnAll(object? sender, RoutedEventArgs e)
    {
        if (DataContext is GitChangesViewModel vm && sender is Control { Tag: string action })
        {
            _ = Report(action == "stageAll" ? vm.StageAllAsync() : vm.UnstageAllAsync());
        }
    }

    private void OnCommit(object? sender, RoutedEventArgs e)
    {
        if (DataContext is GitChangesViewModel { CanCommit: true } vm)
        {
            _ = Report(vm.CommitAsync(all: !vm.HasStaged));
        }
    }

    private void OnCommitMenu(object? sender, RoutedEventArgs e)
    {
        if (DataContext is GitChangesViewModel { CanCommit: true } vm && sender is MenuItem { Tag: string kind })
        {
            _ = Report(kind switch
            {
                "push" => vm.CommitAndPushAsync(sync: false),
                "sync" => vm.CommitAndPushAsync(sync: true),
                _ => vm.CommitAsync(all: !vm.HasStaged),
            });
        }
    }

    /// <summary>Starts a mention: a <c>#</c> at the caret (after a space), which opens the issue completion.</summary>
    private void OnMention(object? sender, RoutedEventArgs e)
    {
        var text = Message.Text ?? string.Empty;
        var caret = Math.Clamp(Message.CaretIndex, 0, text.Length);
        var insert = caret > 0 && !char.IsWhiteSpace(text[caret - 1]) && text[caret - 1] is not '(' and not '[' ? " #" : "#";
        Message.Text = text[..caret] + insert + text[caret..];
        Message.CaretIndex = caret + insert.Length;
        Message.Focus();
        _ = UpdateCompletionAsync();
    }

    private void OnStashAction(object? sender, RoutedEventArgs e)
    {
        if (DataContext is GitChangesViewModel vm && sender is MenuItem { Tag: string action, DataContext: GitStash stash })
        {
            _ = Report(action switch
            {
                "apply" => vm.ApplyStashAsync(stash, pop: false),
                "pop" => vm.ApplyStashAsync(stash, pop: true),
                _ => vm.DropStashAsync(stash),
            });
        }
    }

    private void OnMenuCommand(object? sender, RoutedEventArgs e)
    {
        if (DataContext is GitChangesViewModel vm && sender is MenuItem { Tag: string command })
        {
            _ = Report(vm.RunAsync(command));
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
