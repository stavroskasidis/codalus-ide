using Avalonia.Headless.XUnit;
using Codalus.Shell.Docking;
using Codalus.Shell.ViewModels;
using Dock.Model.Controls;
using Dock.Model.Core;

namespace Codalus.Shell.Tests;

/// <summary>
/// Where tool windows open by default (the maintainer's review, 2026-11-02): pages among the documents (Plugins,
/// Components, Aspire, Benchmarks), the HTTP Response beside its file, and narrow lists on the right (Notifications,
/// Worktrees, the Task Runner Explorer, .NET Counters); and a layout saved before the move keeps the rest of itself.
/// </summary>
public sealed class DefaultToolWindowPlacementTests
{
    private static readonly string[] s_documentWindows =
        [BuiltIns.PluginsWindow, ShellHost.ComponentsWindow, AspireViewModel.ToolWindowId, BenchmarksViewModel.ToolWindowId];

    private static readonly string[] s_rightWindows =
        [BuiltIns.NotificationsWindow, WorktreesViewModel.ToolWindowId, TaskRunnerViewModel.ToolWindowId, CountersViewModel.ToolWindowId];

    [AvaloniaFact]
    public async Task Pages_open_among_the_documents_and_lists_on_the_right()
    {
        await using var f = ShellFixture.Create();
        var layout = f.Shell.Layout;
        f.Shell.OpenSettings();
        ShellFixture.Pump();
        var settings = layout.Documents.Single();
        var documents = Assert.IsAssignableFrom<IDocumentDock>(settings.Owner);

        foreach (var id in s_documentWindows)
        {
            var tool = layout.ShowToolWindow(id, focus: true)!;
            ShellFixture.Pump();
            Assert.Same(documents, tool.Owner);
            Assert.Same(tool, documents.ActiveDockable);
        }

        // Going to them isn't going to a document: the active document stays the one the user was in.
        Assert.Same(settings, layout.ActiveDocument);

        var right = Owner(layout.ShowToolWindow(SolutionExplorerViewModel.ToolWindowId, focus: false)!);
        foreach (var id in s_rightWindows)
        {
            Assert.Same(right, Owner(layout.ShowToolWindow(id, focus: false)!));
        }
    }

    [AvaloniaFact]
    public async Task A_page_shown_without_the_focus_waits_behind_the_document_the_user_is_in()
    {
        await using var f = ShellFixture.Create();
        var layout = f.Shell.Layout;
        f.Shell.OpenSettings();
        ShellFixture.Pump();
        var settings = layout.Documents.Single();

        // A run brings up Aspire: its tab joins the group, and the editor stays in front.
        var aspire = layout.ShowToolWindow(AspireViewModel.ToolWindowId, focus: false)!;
        ShellFixture.Pump();
        Assert.Same(settings.Owner, aspire.Owner);
        Assert.Same(settings, Owner(settings).ActiveDockable);
        Assert.Same(settings, layout.ActiveDocument);

        // Asked for, it comes to the front.
        f.Shell.ShowAspire();
        ShellFixture.Pump();
        Assert.Same(aspire, Owner(settings).ActiveDockable);
    }

    [AvaloniaFact]
    public async Task The_http_response_opens_beside_the_documents_and_files_keep_opening_in_the_users_group()
    {
        await using var f = ShellFixture.Create();
        var layout = f.Shell.Layout;
        f.Shell.OpenSettings();
        ShellFixture.Pump();
        var settings = layout.Documents.Single();
        var documents = Owner(settings);

        var response = layout.ShowToolWindow(HttpResponseViewModel.ToolWindowId, focus: false)!;
        ShellFixture.Pump();
        var beside = Assert.IsAssignableFrom<IDocumentDock>(response.Owner);
        Assert.NotSame(documents, beside);
        Assert.Same(response, beside.ActiveDockable);
        Assert.Same(settings, documents.ActiveDockable);
        Assert.Same(settings, layout.ActiveDocument);

        // Going to the response and then opening a page: it opens with the documents, not beside the response.
        layout.ShowToolWindow(HttpResponseViewModel.ToolWindowId, focus: true);
        ShellFixture.Pump();
        f.Shell.OpenKeyboardShortcuts();
        ShellFixture.Pump();
        Assert.Same(documents, Assert.Single(layout.Documents, d => d is KeyboardShortcutsDocument).Owner);

        // Closed (its group goes away) and sent again: beside again, not on top of the documents.
        layout.CloseToolWindow(HttpResponseViewModel.ToolWindowId);
        ShellFixture.Pump();
        var again = layout.ShowToolWindow(HttpResponseViewModel.ToolWindowId, focus: false)!;
        ShellFixture.Pump();
        Assert.IsAssignableFrom<IDocumentDock>(again.Owner);
        Assert.NotSame(documents, again.Owner);

        // The group of its own isn't the documents' group: restored, the documents go back to theirs.
        Assert.True(layout.Restore(layout.Capture(null)));
        ShellFixture.Pump();
        Assert.True(layout.IsVisible(HttpResponseViewModel.ToolWindowId));
        Assert.All(layout.Documents, d => Assert.NotSame(layout.ToolWindow(HttpResponseViewModel.ToolWindowId)!.Owner, d.Owner));
    }

    [AvaloniaFact]
    public async Task A_page_among_the_documents_is_kept_in_the_saved_layout()
    {
        await using var f = ShellFixture.Create();
        var layout = f.Shell.Layout;
        f.Shell.ShowPluginsPage(PluginsPageViewModel.BrowseTab);
        ShellFixture.Pump();

        var saved = layout.Capture(null);
        var documents = Flatten(saved.Root).Single(n => n.Kind == "documents");
        Assert.Equal([BuiltIns.PluginsWindow], documents.Tools);
        Assert.Equal(BuiltIns.PluginsWindow, documents.Active);

        layout.ResetLayout();
        Assert.False(layout.IsVisible(BuiltIns.PluginsWindow));
        Assert.True(layout.Restore(saved));
        Assert.IsAssignableFrom<IDocumentDock>(layout.ToolWindow(BuiltIns.PluginsWindow)!.Owner);
    }

    [AvaloniaFact]
    public async Task A_layout_saved_before_the_move_keeps_the_rest_and_the_moved_windows_open_in_their_new_places()
    {
        await using var f = ShellFixture.Create();
        var layout = f.Shell.Layout;
        var before = new LayoutSnapshot(1, new LayoutNode("proportional")
        {
            Id = "Main",
            Orientation = "horizontal",
            Children =
            [
                new LayoutNode("proportional")
                {
                    Id = "Center",
                    Orientation = "vertical",
                    Children =
                    [
                        new LayoutNode("documents") { Id = LayoutManager.DocumentsId },
                        new LayoutNode("splitter"),
                        new LayoutNode("tools")
                        {
                            Id = "BottomPane",
                            Alignment = "bottom",
                            Active = BuiltIns.NotificationsWindow,
                            Tools = [BuiltIns.OutputWindow, BuiltIns.NotificationsWindow, "plugins.gallery", AspireViewModel.ToolWindowId, HttpResponseViewModel.ToolWindowId],
                        },
                    ],
                },
                new LayoutNode("splitter"),
                new LayoutNode("tools") { Id = "RightPane", Alignment = "right", Tools = [SolutionExplorerViewModel.ToolWindowId, BuiltIns.PluginsWindow] },
            ],
        })
        {
            BottomPinned = [WorktreesViewModel.ToolWindowId, CodeMetricsViewModel.ToolWindowId],
        };

        Assert.True(layout.Restore(before));
        ShellFixture.Pump();
        Assert.True(layout.IsVisible(BuiltIns.OutputWindow));
        Assert.True(layout.IsVisible(SolutionExplorerViewModel.ToolWindowId));
        Assert.True(layout.IsVisible(CodeMetricsViewModel.ToolWindowId));
        foreach (var id in new[] { BuiltIns.NotificationsWindow, AspireViewModel.ToolWindowId, HttpResponseViewModel.ToolWindowId, BuiltIns.PluginsWindow, WorktreesViewModel.ToolWindowId })
        {
            Assert.False(layout.IsVisible(id), $"{id} is still where the old layout had it");
        }

        Assert.Equal(LayoutSnapshot.CurrentVersion, layout.Capture(null).Version);
        var right = Owner(layout.ToolWindow(SolutionExplorerViewModel.ToolWindowId)!);
        Assert.Same(right, Owner(layout.ShowToolWindow(BuiltIns.NotificationsWindow, focus: false)!));
        Assert.IsAssignableFrom<IDocumentDock>(layout.ShowToolWindow(BuiltIns.PluginsWindow, focus: false)!.Owner);

        // A layout of a version this build doesn't know is still refused.
        Assert.False(layout.Restore(before with { Version = 99 }));
    }

    private static IDock Owner(IDockable dockable) => Assert.IsAssignableFrom<IDock>(dockable.Owner);

    private static IEnumerable<LayoutNode> Flatten(LayoutNode node) => (node.Children ?? []).SelectMany(Flatten).Prepend(node);
}
