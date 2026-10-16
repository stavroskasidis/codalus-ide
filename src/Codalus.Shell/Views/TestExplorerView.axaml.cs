using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Codalus.Shell.Controls;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Testing;

namespace Codalus.Shell.Views;

/// <summary>
/// The Test Explorer: Enter runs the selection (Ctrl+Enter debugs), double-click opens the test's source,
/// Left/Right collapse and expand, Ctrl+F focuses the filter; the context menu runs, debugs, measures coverage and adds
/// tests to playlists.
/// </summary>
public sealed partial class TestExplorerView : UserControl, IPaneView
{
    public TestExplorerView()
    {
        InitializeComponent();
        List.AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        Filter.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                ViewModel?.ApplyFilter();
                e.Handled = true;
            }
            else if (e.Key == Key.Down && ViewModel is { HasTests: true } vm)
            {
                vm.Selected ??= vm.Rows[0];
                List.ContainerFromItem(vm.Selected)?.Focus();
                e.Handled = true;
            }
        };
        List.DoubleTapped += (_, e) =>
        {
            if (RowAt(e.Source) is not null && !IsChevron(e.Source))
            {
                _ = ViewModel?.OpenSelectedAsync();
            }
        };
        List.Tapped += (_, e) =>
        {
            if (RowAt(e.Source) is { } row && IsChevron(e.Source))
            {
                ViewModel?.SetExpanded(row, !row.IsExpanded);
            }
        };
        List.ContextRequested += OnContextRequested;
    }

    private TestExplorerViewModel? ViewModel => DataContext as TestExplorerViewModel;

    private ShellHost? Shell => (TopLevel.GetTopLevel(this) as MainWindow)?.DataContext as ShellHost;

    public void FocusPrimary()
    {
        if (ViewModel is { HasTests: true } vm)
        {
            vm.Selected ??= vm.Rows.FirstOrDefault();
            if (vm.Selected is { } selected && List.ContainerFromItem(selected) is { } container && container.Focus())
            {
                return;
            }

            List.Focus();
        }
        else
        {
            Filter.Focus();
        }
    }

    private static TestRowViewModel? RowAt(object? source) =>
        (source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext as TestRowViewModel;

    private static bool IsChevron(object? source) =>
        (source as Visual)?.FindAncestorOfType<Border>(includeSelf: true) is { } border && border.Classes.Contains("chevron");

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (ViewModel is not { Selected: { } row } vm)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Enter when e.KeyModifiers == KeyModifiers.Control:
                vm.DebugSelectedCommand.Execute(null);
                break;
            case Key.Enter when e.KeyModifiers == KeyModifiers.None:
                vm.RunSelectedCommand.Execute(null);
                break;
            case Key.F12 when e.KeyModifiers == KeyModifiers.None:
                _ = vm.OpenSelectedAsync();
                break;
            case Key.Right when e.KeyModifiers == KeyModifiers.None && row.HasChildren && !row.IsExpanded:
                vm.SetExpanded(row, true);
                break;
            case Key.Left when e.KeyModifiers == KeyModifiers.None && row.HasChildren && row.IsExpanded:
                vm.SetExpanded(row, false);
                break;
            case Key.Left when e.KeyModifiers == KeyModifiers.None && vm.Rows.IndexOf(row) is var index and > 0:
                for (var i = index - 1; i >= 0; i--)
                {
                    if (vm.Rows[i].Depth < row.Depth)
                    {
                        vm.Selected = vm.Rows[i];
                        List.ScrollIntoView(vm.Rows[i]);
                        List.ContainerFromItem(vm.Rows[i])?.Focus();
                        break;
                    }
                }

                break;
            default:
                return;
        }

        e.Handled = true;
    }

    private void OnContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (ViewModel is not { } vm || RowAt(e.Source) is not { } row || Shell is not { } shell)
        {
            return;
        }

        vm.Selected = row;
        var keys = vm.SelectedKeys;
        MenuItem Item(string header, Action action, string? icon = null, bool enabled = true)
        {
            var item = new MenuItem { Header = header, IsEnabled = enabled, Icon = icon is null ? null : new Icon { Kind = icon } };
            item.Click += (_, _) => action();
            return item;
        }

        var idle = !shell.Tests.IsRunning;
        var menu = new ContextMenu();
        menu.Items.Add(Item("Run", () => _ = shell.RunTestsAsync(TestRunKind.Run, keys), "play", idle));
        menu.Items.Add(Item("Debug", () => _ = shell.RunTestsAsync(TestRunKind.Debug, keys), "debug", idle));
        menu.Items.Add(Item("Run with Coverage", () => _ = shell.RunTestsAsync(TestRunKind.Coverage, keys), "coverage", idle));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Go to Test", () => _ = vm.OpenSelectedAsync(), enabled: row.Group.Node?.Uri is not null));
        menu.Items.Add(Item("Add to Playlist…", () => _ = shell.AddToPlaylistAsync(keys)));
        if (vm.Playlist is { } playlist)
        {
            menu.Items.Add(Item($"Remove from '{playlist.Name}'", () => _ = shell.RemoveFromPlaylistAsync(playlist, keys)));
        }

        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Copy Name", () => Copy(row.Group.Node?.FullLabel ?? row.Label)));
        if (row.Group.Node is { IsLeaf: true, Result: { } result })
        {
            menu.Items.Add(Item("Copy Details", () => Copy(Describe(row.Group.Node, result))));
            if (shell.Ai.IsEnabled && result.Outcome is not TestOutcome.Passed)
            {
                menu.Items.Add(Item("Ask AI Why It Fails", () => _ = shell.ExplainTestWithAiAsync(), "sparkle"));
            }
        }

        menu.Open(List.ContainerFromItem(row) ?? List);
        e.Handled = true;
    }

    private void Copy(string text)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            _ = Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(clipboard, text);
        }
    }

    private static string Describe(TestNode node, TestResult result)
    {
        var text = new System.Text.StringBuilder();
        text.AppendLine(node.FullLabel);
        text.AppendLine(TestExplorerViewModel.OutcomeText(result.Outcome) + (result.Duration is { } d ? " in " + TestExplorerViewModel.Format(d) : string.Empty));
        foreach (var message in result.Messages)
        {
            text.AppendLine(message.Message);
            if (message.StackTrace is { } stack)
            {
                text.AppendLine(stack);
            }
        }

        if (result.Output.Length > 0)
        {
            text.AppendLine().AppendLine(result.Output);
        }

        return text.ToString();
    }

    private void OnOpenFrame(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: StackFrameRow frame })
        {
            ViewModel?.OpenFrameCommand.Execute(frame);
        }
    }

    private void OnOpenSource(object? sender, RoutedEventArgs e) => _ = ViewModel?.OpenSelectedAsync();
}
