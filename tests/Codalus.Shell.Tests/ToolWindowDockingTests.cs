using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Codalus.Shell.Controls;
using Codalus.Shell.Docking;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Commands;
using Codalus.Workbench.ToolWindows;
using Dock.Avalonia.Controls;
using Dock.Model;
using Dock.Model.Controls;
using Dock.Model.Core;

namespace Codalus.Shell.Tests;

/// <summary>
/// Tool windows open as tabs in their location's group, and look like tabs wherever they're docked (the maintainer's
/// reports, 2026-10-16: the Plugin Gallery, Coverage Results and Code Metrics Results opened in a new group above the
/// bottom tool windows, and a tool window dragged among the documents had its whole view in its tab).
/// </summary>
public sealed class ToolWindowDockingTests
{
    private const string PluginView = "sample.bottomView";

    // Bottom tool windows opened on demand by commands and events, and a plugin's.
    private static readonly string[] s_bottomWindows =
    [
        ShellHost.PluginGalleryWindow, CoverageResultsViewModel.ToolWindowId, CodeMetricsViewModel.ToolWindowId,
        CallStackViewModel.ToolWindowId, VariablesViewModel.LocalsWindowId, DebugConsoleViewModel.ToolWindowId,
        FindResultsViewModel.ToolWindowId, TaskListViewModel.ToolWindowId, BuiltIns.NotificationsWindow, PluginView,
    ];

    [AvaloniaFact]
    public async Task Tool_windows_open_as_tabs_in_the_bottom_group_even_after_the_user_moved_it()
    {
        await using var f = ShellFixture.Create();
        RegisterPluginView(f);
        var layout = f.Shell.Layout;

        // The default layout: every bottom tool window joins the group the Output and the Error List are in.
        var bottom = Owner(layout.ShowToolWindow(BuiltIns.OutputWindow, focus: false)!);
        foreach (var id in s_bottomWindows)
        {
            Assert.Same(bottom, Owner(layout.ShowToolWindow(id, focus: false)!));
        }

        Assert.Same(bottom, Assert.Single(BottomGroups(layout)));
        foreach (var id in s_bottomWindows)
        {
            layout.CloseToolWindow(id);
        }

        // The user drags the Error List to the window's bottom edge (Dock's own group, without our id, across the window)
        // and the Output into it: the default bottom group, emptied, goes away.
        var manager = new DockManager(new DockService());
        var errorList = layout.ShowToolWindow(ErrorListViewModel.ToolWindowId, focus: false)!;
        var output = layout.ShowToolWindow(BuiltIns.OutputWindow, focus: false)!;
        Assert.True(manager.ValidateDockable(errorList, (IDock)layout.Root.VisibleDockables![0], DragAction.Move, DockOperation.Bottom, bExecute: true));
        var moved = Owner(errorList);
        Assert.NotSame(bottom, moved);
        Assert.True(manager.ValidateDockable(output, moved, DragAction.Move, DockOperation.Fill, bExecute: true));
        ShellFixture.Pump();
        Assert.Same(moved, Owner(output));

        // Every bottom tool window opens as a tab there: no new group stacked above it.
        foreach (var id in s_bottomWindows)
        {
            var tool = layout.ShowToolWindow(id, focus: false)!;
            ShellFixture.Pump();
            Assert.True(ReferenceEquals(moved, tool.Owner), $"{tool.Title} opened in the group of {string.Join(", ", Tabs(tool.Owner))}, not the Output's");
        }

        Assert.Same(moved, Assert.Single(BottomGroups(layout)));
        Assert.Equal(
            [ErrorListViewModel.ToolWindowId, BuiltIns.OutputWindow, .. s_bottomWindows],
            (moved.VisibleDockables ?? []).Select(d => d.Id));
        Snapshot(f, "tool-windows-in-the-moved-bottom-group.png");
    }

    [AvaloniaFact]
    public async Task A_tool_window_reopens_in_the_group_it_was_closed_in()
    {
        await using var f = ShellFixture.Create();
        var layout = f.Shell.Layout;
        var gallery = layout.ShowToolWindow(ShellHost.PluginGalleryWindow, focus: false)!;
        var right = Owner(layout.ShowToolWindow(SolutionExplorerViewModel.ToolWindowId, focus: false)!);
        Assert.NotSame(right, Owner(gallery));

        // Moved to the right pane by the user, closed and shown again: back in the right pane, not its default place.
        Assert.True(new DockManager(new DockService()).ValidateDockable(gallery, right, DragAction.Move, DockOperation.Fill, bExecute: true));
        layout.CloseToolWindow(ShellHost.PluginGalleryWindow);
        ShellFixture.Pump();
        Assert.False(layout.IsVisible(ShellHost.PluginGalleryWindow));
        Assert.Same(right, Owner(layout.ShowToolWindow(ShellHost.PluginGalleryWindow, focus: false)!));
    }

    [AvaloniaFact]
    public async Task A_tool_window_docked_among_the_documents_has_a_tab_with_only_its_title()
    {
        await using var f = ShellFixture.Create();
        var layout = f.Shell.Layout;
        var manager = new DockManager(new DockService());
        f.Shell.OpenSettings();
        ShellFixture.Pump();
        var settings = layout.Documents.Single();
        var documents = Owner(settings);

        // The Output and the Plugin Gallery dragged onto the documents' tab strip.
        var tools = new[] { BuiltIns.OutputWindow, ShellHost.PluginGalleryWindow }.Select(id => layout.ShowToolWindow(id, focus: false)!).ToList();
        foreach (var tool in tools)
        {
            Assert.True(manager.ValidateDockable(tool, documents, DragAction.Move, DockOperation.Fill, bExecute: true));
            ShellFixture.Pump();
        }

        foreach (var tool in tools)
        {
            layout.Factory.SetActiveDockable(tool);
            ShellFixture.Pump();
            var tab = f.Window.GetVisualDescendants().OfType<DocumentTabStripItem>().Single(t => t.DataContext == tool);
            AssertTitleOnly(tab, tool.Title!);

            // The view is in the group's body, once.
            Assert.Single(f.Window.GetVisualDescendants().OfType<PaneHost>(), p => p.Dockable == tool);
            if (tool.Id == BuiltIns.OutputWindow)
            {
                Snapshot(f, "tool-window-among-the-documents.png");
            }
        }

        // The other way doesn't happen: Dock keeps documents out of tool window groups.
        var errorList = layout.ShowToolWindow(ErrorListViewModel.ToolWindowId, focus: false)!;
        layout.ShowToolWindow(CallStackViewModel.ToolWindowId, focus: false); // two tabs: a group of one shows no strip
        var bottom = Owner(errorList);
        Assert.False(manager.ValidateDockable(settings, bottom, DragAction.Move, DockOperation.Fill, bExecute: true));

        // In floating windows too: a documents' group with the Plugin Gallery, and the bottom group.
        var gallery = tools[1];
        layout.Factory.NewHorizontalDocumentDock(gallery);
        ShellFixture.Pump();
        layout.Factory.FloatDockable(Owner(gallery));
        layout.Factory.FloatDockable(bottom);
        ShellFixture.Pump();
        var windows = (layout.Root.Windows ?? []).Select(w => Assert.IsAssignableFrom<Window>(w.Host)).ToList();
        Assert.Equal(2, windows.Count);
        var tabs = windows.SelectMany(w => w.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.TabStripItem>()).ToList();
        AssertTitleOnly(Assert.Single(tabs, t => t is DocumentTabStripItem && t.DataContext == gallery), gallery.Title!);
        AssertTitleOnly(Assert.Single(tabs, t => t is ToolTabStripItem && t.DataContext == errorList), errorList.Title!);
        Assert.Single(windows.SelectMany(w => w.GetVisualDescendants().OfType<PaneHost>()), p => p.Dockable == gallery);
    }

    /// <summary>The tab shows its title (and icon), never a view: no pane, toolbar or text box, and a tab's height.</summary>
    private static void AssertTitleOnly(Avalonia.Controls.Primitives.TabStripItem tab, string title)
    {
        var header = tab.GetVisualDescendants().OfType<ContentPresenter>().First(p => p.Name == "PART_HeaderPresenter" || p.Name == "PART_ContentPresenter");
        Assert.Empty(tab.GetVisualDescendants().OfType<PaneHost>());
        Assert.Empty(tab.GetVisualDescendants().OfType<TextBox>());
        Assert.Contains(header.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == title);
        Assert.True(tab.Bounds.Height is > 0 and < 40, $"the tab of {title} is {tab.Bounds.Height:0} px high");
    }

    private static void RegisterPluginView(ShellFixture f) =>
        f.Shell.Workbench.ToolWindows.Register(new ToolWindowDescriptor(PluginView, "Sample Bottom View", ToolWindowLocation.Bottom, ToolWindowKind.Markdown, ContributionSource.Plugin("sample.plugin", builtIn: false)));

    private static IDock Owner(IDockable dockable) => Assert.IsAssignableFrom<IDock>(dockable.Owner);

    private static IEnumerable<string?> Tabs(IDockable? dock) => (dock as IDock)?.VisibleDockables?.Select(d => d.Title) ?? [];

    /// <summary>
    /// The main window's groups with bottom tool windows (others, such as the Start page's on the left, may open on their
    /// own while the test runs).
    /// </summary>
    private static List<IToolDock> BottomGroups(LayoutManager layout)
    {
        static IEnumerable<IDockable> Flatten(IDockable dockable) =>
            dockable is IDock { VisibleDockables: { } children } ? children.SelectMany(Flatten).Prepend(dockable) : [dockable];
        return [.. Flatten(layout.Root).OfType<IToolDock>().Where(g => (g.VisibleDockables ?? []).OfType<ToolWindowDockable>().Any(t => t.Descriptor.Location == ToolWindowLocation.Bottom))];
    }

    private static void Snapshot(ShellFixture f, string name)
    {
        if (Environment.GetEnvironmentVariable("CODALUS_UI_SNAPSHOTS") is { Length: > 0 } snapshots)
        {
            Directory.CreateDirectory(snapshots);
            using var frame = Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(f.Window);
            frame!.Save(Path.Combine(snapshots, name), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        }
    }
}
