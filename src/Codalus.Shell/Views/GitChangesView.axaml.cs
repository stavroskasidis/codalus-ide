using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
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
        foreach (var list in new[] { StagedList, ChangesList, ConflictList })
        {
            list.DoubleTapped += (_, e) =>
            {
                // A hover button acts on each click.
                if (!InButton(e.Source) && OpenSelected(RowAt(e.Source) ?? list.SelectedItem))
                {
                    e.Handled = true;
                }
            };

            // In the tunnel: a list takes Enter itself, to select the item, before a handler added to it hears it (Enter
            // opened nothing). A row's hover button that has the focus (it keeps it after a click) takes its own Enter.
            list.AddHandler(KeyDownEvent, (_, e) =>
            {
                if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None && !InButton(e.Source) && OpenSelected(list.SelectedItem))
                {
                    e.Handled = true;
                }
            }, RoutingStrategies.Tunnel);
        }

        // A tree's rows expand and collapse as a tree view's, with the other trees' code (M57, TreeRows): the chevron, Enter,
        // Left and Right (Ctrl: the folders under it too), + and -, * and / for every folder under it; Enter and a
        // double-click open a file.
        foreach (var tree in new[] { StagedTree, ChangesTree, ConflictTree })
        {
            TreeRows.Attach(tree, () => DataContext as GitChangesViewModel, row => OpenSelected(row), offRowsActivatesSelected: true);
        }

        // The menu of a file, a folder or the repository node: gitChanges/context, built for the node (D38).
        foreach (var list in new[] { StagedList, ChangesList, ConflictList, StagedTree, ChangesTree, ConflictTree })
        {
            list.ContextRequested += (_, e) => OnContextRequested(list, e);
        }

        DataContextChanged += (_, _) =>
        {
            // A closed tool window clears its view's data context before the view leaves the tree: the view model it
            // reported to hears that the window is hidden now.
            if (_reported is { } old && !ReferenceEquals(old, DataContext))
            {
                if (_shown)
                {
                    old.SetViewShown(this, false);
                }

                _reported = null;
                _shown = false;
            }

            if (DataContext is GitChangesViewModel vm && !ReferenceEquals(vm, _reported))
            {
                _reported = vm;
                _shown = false;
                UpdateShown();
            }
        };

        // Shown or hidden: a closed or auto-hidden tool window keeps its view attached, invisible, and its next layout pass
        // tells (no viewport change is raised for an ancestor's visibility).
        AttachedToVisualTree += (_, _) => UpdateShown();
        DetachedFromVisualTree += (_, _) => UpdateShown(detaching: true); // still attached while the event is raised
        Sections.LayoutUpdated += (_, _) => UpdateShown();

        DataContextChanged += (_, _) => AttachBranchPicker();
        BranchPicker.CloseRequested += (_, _) =>
        {
            CloseBranchPicker();
            BranchButton.Focus();
        };
        BranchPopup.Closed += (_, _) =>
        {
            if (Branches is { IsOpen: true } branches)
            {
                branches.IsOpen = false;
            }
        };

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

    private bool _shown;
    private GitChangesViewModel? _reported; // the view model told whether the window is shown

    /// <summary>
    /// Tells the view model whether the window is shown: while it isn't, a status read keeps the files current but builds
    /// no tree or list (M56).
    /// </summary>
    private void UpdateShown(bool detaching = false)
    {
        var shown = !detaching && this.IsAttachedToVisualTree() && Sections.IsEffectivelyVisible;
        if (shown == _shown)
        {
            return;
        }

        _shown = shown;
        _reported?.SetViewShown(this, shown);
    }

    private static object? RowAt(object? source) =>
        (source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext;

    /// <summary>Whether a key or a tap comes from a row's hover button.</summary>
    private static bool InButton(object? source) => TreeRows.InButton(source);

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

    /// <summary>Opens a file row or file node (folders expand or collapse instead); false when <paramref name="selected"/> is neither.</summary>
    private bool OpenSelected(object? selected)
    {
        if (DataContext is GitChangesViewModel tree && selected is GitChangeRow { HasChildren: true } folder)
        {
            tree.Toggle(folder);
            return true;
        }

        if (DataContext is not GitChangesViewModel vm || (selected as GitFileRow ?? (selected as GitChangeRow)?.Node.Row) is not { } row)
        {
            return false;
        }

        // A conflicted file opens in the merge editor (M13).
        _ = Report(row.Status.IsConflict ? vm.RunAsync("git.mergeEditor:" + row.Status.Path) : vm.OpenAsync(row));
        return true;
    }

    /// <summary>
    /// A row's hover button (T1): Stage, Unstage or Discard, which run the menu's commands on the node, on the files the
    /// window shows under it (question 2).
    /// </summary>
    private void OnFileAction(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not GitChangesViewModel vm || sender is not Control { Tag: string action, DataContext: { } target })
        {
            return;
        }

        var command = action switch
        {
            "stage" => "git.stage",
            "unstage" => "git.unstage",
            _ => "git.discard",
        };
        _ = Report(target switch
        {
            GitChangeRow node => vm.RunOnAsync(command, node.Node),
            GitFileRow row => vm.RunOnAsync(command, row),
            _ => Task.CompletedTask,
        });
    }

    /// <summary>The menu of the node under the pointer (or the selected one, from the keyboard), which becomes selected.</summary>
    private void OnContextRequested(ListBox list, ContextRequestedEventArgs e)
    {
        if (DataContext is not GitChangesViewModel vm)
        {
            return;
        }

        var item = RowAt(e.Source) ?? list.SelectedItem;
        var menu = item switch
        {
            GitChangeRow node => vm.MenuOf(node.Node),
            GitFileRow row => vm.MenuOf(row),
            _ => null,
        };
        if (menu is null)
        {
            return;
        }

        list.SelectedItem = item;
        e.Handled = true;

        // From the keyboard (Shift+F10, the Menu key), under the selected row rather than where the pointer last was.
        if (!e.TryGetPosition(list, out _) && list.ContainerFromItem(item!) is Control container)
        {
            menu.Placement = PlacementMode.BottomEdgeAlignedLeft;
            menu.Open(container);
            return;
        }

        menu.Open(list);
    }

    // ------------------------------------------------------------------ the branch drop-down (D38)

    private GitBranchPickerViewModel? Branches => (DataContext as GitChangesViewModel)?.Branches;

    private GitBranchPickerViewModel? _attached;

    private void AttachBranchPicker()
    {
        if (_attached is not null)
        {
            _attached.Opened -= OnBranchPickerOpened;
        }

        _attached = Branches;
        if (_attached is not null)
        {
            _attached.Opened += OnBranchPickerOpened;
        }
    }

    private void OnBranchButton(object? sender, RoutedEventArgs e)
    {
        if (Branches is { } branches)
        {
            branches.IsOpen = !branches.IsOpen;
        }
    }

    private void OnBranchPickerOpened(object? sender, bool open)
    {
        BranchPopup.IsOpen = open;
        var top = TopLevel.GetTopLevel(this);
        if (open)
        {
            BranchPicker.FocusSearch();
            if (top is not null)
            {
                top.AddHandler(PointerPressedEvent, OnWindowPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
            }

            if (top is WindowBase window)
            {
                window.Deactivated += OnWindowDeactivated;
            }
        }
        else
        {
            top?.RemoveHandler(PointerPressedEvent, OnWindowPointerPressed);
            if (top is WindowBase window)
            {
                window.Deactivated -= OnWindowDeactivated;
            }
        }
    }

    /// <summary>A press outside the drop-down and its button closes it (the menu over it is a popup of its own, B7).</summary>
    private void OnWindowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (BranchPicker.IsMenuOpen)
        {
            return;
        }

        if (e.Source is Avalonia.Visual source
            && (source.FindAncestorOfType<GitBranchPickerView>(includeSelf: true) is not null || source.FindAncestorOfType<Button>(includeSelf: true) == BranchButton))
        {
            return;
        }

        CloseBranchPicker();
    }

    private void OnWindowDeactivated(object? sender, EventArgs e)
    {
        // The branch's menu takes the focus on some OSes; the drop-down stays open under it (B7).
        if (!BranchPicker.IsMenuOpen)
        {
            CloseBranchPicker();
        }
    }

    private void CloseBranchPicker()
    {
        if (Branches is { IsOpen: true } branches)
        {
            branches.IsOpen = false;
        }
    }

    /// <summary>The drop-down's content (tests and the smoke test).</summary>
    internal GitBranchPickerView BranchPickerView => BranchPicker;

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

    private void OnCancelProgress(object? sender, RoutedEventArgs e) => (DataContext as GitChangesViewModel)?.CancelProgress();

    public void FocusPrimary() => Message.Focus();

    // ------------------------------------------------------------------ the repository picker (ADR 0085)

    private void OnRepositoryFlyoutOpened(object? sender, EventArgs e)
    {
        if (DataContext is GitChangesViewModel vm)
        {
            vm.RepositoryFilter = string.Empty;
            RepositoryList.SelectedItem = vm.SelectedRepository;
        }

        RepositoryFilter.Focus();
    }

    private void OnRepositoryChosen(object? sender, TappedEventArgs e)
    {
        if (e.Source is Visual { DataContext: GitRepositoryChoice choice } source && source.FindAncestorOfType<Button>(includeSelf: true) is null)
        {
            ChooseRepository(choice);
        }
    }

    /// <summary>Down from the filter box goes to the list; Enter picks its first repository.</summary>
    private void OnRepositoryFilterKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Down && RepositoryList.ItemCount > 0)
        {
            RepositoryList.SelectedIndex = Math.Max(0, RepositoryList.SelectedIndex);
            (RepositoryList.ContainerFromIndex(RepositoryList.SelectedIndex) as Control)?.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && DataContext is GitChangesViewModel { ShownRepositories: [var first, ..] })
        {
            ChooseRepository(RepositoryList.SelectedItem as GitRepositoryChoice ?? first);
            e.Handled = true;
        }
    }

    private void OnRepositoryListKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && RepositoryList.SelectedItem is GitRepositoryChoice choice)
        {
            ChooseRepository(choice);
            e.Handled = true;
        }
    }

    private void ChooseRepository(GitRepositoryChoice choice)
    {
        RepositoryButton.Flyout?.Hide();
        if (DataContext is GitChangesViewModel vm && !choice.IsSelected)
        {
            vm.SelectedRepository = choice;
        }
    }

    private void OnAddRepository(object? sender, RoutedEventArgs e)
    {
        RepositoryButton.Flyout?.Hide();
        if (DataContext is GitChangesViewModel { AddRepository: { } add })
        {
            _ = Report(add());
        }
    }

    private void OnRemoveRepository(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (DataContext is GitChangesViewModel { RemoveRepository: { } remove } && sender is Button { DataContext: GitRepositoryChoice { CanRemove: true } choice })
        {
            RepositoryButton.Flyout?.Hide();
            _ = Report(remove(choice.Path));
        }
    }
}
