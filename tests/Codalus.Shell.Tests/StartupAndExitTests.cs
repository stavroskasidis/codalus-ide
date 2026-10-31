using System.Diagnostics;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Codalus.Shell.Docking;
using Codalus.Shell.Editor;
using Codalus.Shell.Infrastructure;
using Codalus.Shell.ViewModels;
using Codalus.Shell.Views;
using Dock.Model.Core;

namespace Codalus.Shell.Tests;

/// <summary>
/// The UI never waits at startup, at exit, or when many tabs open or close together (M56): a restored layout's hidden tool
/// windows make nothing (the start window's recent entries: StartWindowTests), tabs opened or closed together make one of
/// them active, and the exit hides the window at once and ends the app whatever its shutdown does. What the startup does
/// while the login shell's environment comes in: <see cref="StartupEnvironmentTests"/>.
/// </summary>
public sealed class StartupAndExitTests
{
    internal static async Task PumpForAsync(int milliseconds)
    {
        var until = DateTime.UtcNow.AddMilliseconds(milliseconds);
        await ShellFixture.PumpUntil(() => DateTime.UtcNow >= until);
    }

    internal static bool Interactive(ShellFixture f) => f.Shell.Workbench.Log.GetText().Contains("[setup] First-run setup:", StringComparison.Ordinal);

    private static List<string> Files(ShellFixture f, string folder, int count)
    {
        var directory = Directory.CreateDirectory(Path.Combine(f.UserData, folder)).FullName;
        var files = Enumerable.Range(0, count).Select(i => Path.Combine(directory, $"file{i:00}.txt")).ToList();
        foreach (var file in files)
        {
            File.WriteAllText(file, $"{Path.GetFileName(file)}\n");
        }

        return files;
    }

    private static CodeEditorDocument Editor(ShellFixture f, string path) => f.Shell.Editors.Single(e => e.Model.Path == path);

    /// <summary>
    /// A tool window's content is made when its tab first shows: a restored layout made every saved tool window's before the
    /// first frame, and the Test Explorer's behind another tab started the test plugin's host.
    /// </summary>
    [AvaloniaFact]
    public async Task A_restored_layout_makes_a_tool_windows_content_when_its_tab_first_shows()
    {
        await using var f = ShellFixture.Create();
        var snapshot = new LayoutSnapshot(LayoutSnapshot.CurrentVersion, new LayoutNode("proportional")
        {
            Id = "Main",
            Orientation = "horizontal",
            Children =
            [
                new LayoutNode("tools") { Id = "LeftPane", Alignment = "left", Proportion = 0.25, Active = SolutionExplorerViewModel.ToolWindowId, Tools = [SolutionExplorerViewModel.ToolWindowId, TestExplorerViewModel.ToolWindowId] },
                new LayoutNode("splitter"),
                new LayoutNode("documents") { Id = LayoutManager.DocumentsId },
            ],
        });

        Assert.True(f.Shell.Layout.Restore(snapshot));
        await PumpForAsync(200);
        var testExplorer = f.Shell.Layout.ToolWindow(TestExplorerViewModel.ToolWindowId)!;
        Assert.False(testExplorer.HasContent);
        Assert.True(f.Shell.Layout.ToolWindow(SolutionExplorerViewModel.ToolWindowId)!.HasContent);

        f.Shell.Layout.ShowToolWindow(TestExplorerViewModel.ToolWindowId, focus: false);
        await ShellFixture.PumpUntil(() => testExplorer.HasContent, describe: () => DescribeTab(f, testExplorer));
        Assert.Same(f.Shell.TestExplorer, testExplorer.Content);
    }

    /// <summary>
    /// Why a tool window's tab hasn't shown (a run where the Test Explorer's never did, with other tests' windows about): its
    /// group and the group's active tab, the panes in the window, and Dock's deferred presentations still waiting (one queue
    /// for every window of the process).
    /// </summary>
    private static string DescribeTab(ShellFixture f, ToolWindowDockable tool)
    {
        var group = tool.Owner is IDock { ActiveDockable: var active } dock ? $"in {dock.Id}, whose active tab is {active?.Id ?? "none"}" : "in no group";
        var panes = string.Join(", ", f.Window.GetVisualDescendants().OfType<Codalus.Shell.Controls.PaneHost>().Select(p => p.Dockable?.Id ?? "?"));
        var timeline = Dock.Controls.DeferredContentControl.DeferredContentPresentationSettings.DefaultTimeline;
        var queue = typeof(Dock.Controls.DeferredContentControl.DeferredContentPresentationTimeline).GetField("_queue", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(timeline);
        var pending = queue?.GetType().GetProperty("PendingCount", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(queue);
        var scheduled = queue?.GetType().GetField("_isScheduled", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(queue);
        var due = queue?.GetType().GetField("_scheduledDueAt", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(queue);
        return $"the {tool.Id} tool window is {group}; the window's panes: {panes}; deferred presentations pending: {pending}, a flush scheduled: {scheduled} (due {due}, now {DateTimeOffset.UtcNow:O})";
    }

    /// <summary>
    /// Close All, and closing a solution, close the tabs together: no tab becomes active on the way. One by one, every close
    /// after the active tab's place made the next tab active, and each activation ran every reaction to a tab switch.
    /// </summary>
    [AvaloniaFact]
    public async Task Closing_tabs_together_activates_no_tab_on_the_way()
    {
        await using var f = ShellFixture.Create();
        var files = Files(f, "files", 30);
        var changes = 0;
        f.Shell.Layout.ActiveDocumentChanged += (_, _) => changes++;
        async Task OpenAllAsync()
        {
            foreach (var file in files)
            {
                await f.Shell.OpenFileAsync(file);
            }

            f.Shell.Layout.OpenDocument(Editor(f, files[1]));
            ShellFixture.Pump();
            changes = 0;
        }

        await OpenAllAsync();
        f.Shell.CloseAllDocuments(exceptActive: false);
        Assert.Equal(1, changes);
        Assert.Empty(f.Shell.Layout.Documents);
        Assert.Null(f.Shell.Layout.ActiveDocument);

        await OpenAllAsync();
        await f.Shell.CloseSolutionAsync();
        Assert.Equal(1, changes);
        Assert.Empty(f.Shell.Editors);
    }

    /// <summary>
    /// Close All But This with a second group of tabs: no tab becomes active on the way, and the kept one stays the active
    /// document. One by one, each close of the other group's active tab made its neighbor the active document.
    /// </summary>
    [AvaloniaFact]
    public async Task Close_all_but_this_across_two_groups_keeps_the_active_document()
    {
        await using var f = ShellFixture.Create();
        var files = Files(f, "groups", 12);
        foreach (var file in files.Take(6))
        {
            await f.Shell.OpenFileAsync(file);
        }

        // The sixth file in a group of its own to the right, where the next ones open.
        var moved = Editor(f, files[5]);
        f.Shell.Layout.Factory.NewHorizontalDocumentDock(moved);
        ShellFixture.Pump();
        f.Shell.Layout.OpenDocument(moved);
        foreach (var file in files.Skip(6))
        {
            await f.Shell.OpenFileAsync(file);
        }

        // The right group's active tab is its first: closed one by one, its neighbor became the active document.
        f.Shell.Layout.OpenDocument(moved);
        var groups = f.Shell.Layout.Documents.Select(d => d.Owner).Distinct().ToList();
        Assert.Equal(2, groups.Count);
        var kept = Editor(f, files[1]);
        f.Shell.Layout.OpenDocument(kept);
        ShellFixture.Pump();
        Assert.Same(kept, f.Shell.Layout.ActiveDocument);
        var changes = 0;
        f.Shell.Layout.ActiveDocumentChanged += (_, _) => changes++;

        f.Shell.CloseAllDocuments(exceptActive: true);

        Assert.Equal(0, changes);
        Assert.Same(kept, f.Shell.Layout.ActiveDocument);
        Assert.Same(kept, Assert.Single(f.Shell.Editors));
    }

    /// <summary>
    /// Files opened together (the OS's multi-open, a drop, several <c>codalus file</c> at once) open as tabs behind the active
    /// one, and only the last becomes active: opening each as the active tab built every one's editor in turn.
    /// </summary>
    [AvaloniaFact]
    public async Task Files_opened_together_make_only_the_last_one_active()
    {
        await using var f = ShellFixture.Create();
        var files = Files(f, "together", 20);
        var changes = 0;
        f.Shell.Layout.ActiveDocumentChanged += (_, _) => changes++;

        f.Shell.OpenFromOs(files);
        await ShellFixture.PumpUntil(() => f.Shell.ActiveEditor?.Controller is not null);
        Assert.Equal(files.Count, f.Shell.Layout.Documents.Count());
        Assert.Equal(1, changes);
        Assert.Equal(files[^1], f.Shell.ActiveEditor!.Model.Path);
        Assert.Equal(1, f.Shell.Editors.Count(e => e.Controller is not null));

        // Explorer's Open with on several files starts a codalus per file: their requests open together too.
        changes = 0;
        var more = Files(f, "requests", 10);
        var opened = more.Select(path => f.Shell.OpenTogetherAsync(path, null)).ToList();
        await ShellFixture.PumpUntil(() => opened.All(t => t.IsCompleted));
        Assert.All(await Task.WhenAll(opened), Assert.True);
        Assert.Equal(1, changes);
        Assert.Equal(more[^1], f.Shell.ActiveEditor!.Model.Path);
    }

    /// <summary>
    /// An image among files opened together opens behind the active tab too: the image viewer, the hex viewer, the resource
    /// editor and the designers took no <c>activate</c>, so such a file became the active tab, and took the focus, partway.
    /// </summary>
    [AvaloniaFact]
    public async Task An_image_among_files_opened_together_neither_becomes_active_nor_takes_the_focus()
    {
        await using var f = ShellFixture.Create();
        var files = Files(f, "mixed", 4);
        var image = Path.Combine(f.UserData, "mixed", "picture.png");
        File.WriteAllBytes(image, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg=="));
        files.Insert(1, image);
        var changes = 0;
        var focused = new List<IDockable>();
        f.Shell.Layout.ActiveDocumentChanged += (_, _) => changes++;
        f.Shell.Layout.FocusRequested += (_, dockable) => focused.Add(dockable);

        var opened = await f.Shell.OpenFilesAsync([.. files.Select(path => (path, (LanguageServices.Protocol.Position?)null))]);

        Assert.Equal([true, false, true, true, true], opened);
        Assert.NotNull(f.Shell.Layout.FindDocument<ImageDocument>(ImageDocument.IdFor(image)));
        Assert.Equal(1, changes);
        Assert.Equal(files[^1], Assert.IsType<CodeEditorDocument>(f.Shell.Layout.ActiveDocument).Model.Path);
        Assert.Equal(files[^1], Assert.IsType<CodeEditorDocument>(Assert.Single(focused)).Model.Path);
    }

    /// <summary>
    /// The last of the files opened together is already open in another group: it's the active document then, in its group.
    /// The active document was worked out in the group that was active before, without the tab made active since.
    /// </summary>
    [AvaloniaFact]
    public async Task The_last_file_opened_together_is_the_active_document_in_its_own_group()
    {
        await using var f = ShellFixture.Create();
        var files = Files(f, "split", 4);
        foreach (var file in files.Take(3))
        {
            await f.Shell.OpenFileAsync(file);
        }

        var right = Editor(f, files[2]);
        f.Shell.Layout.Factory.NewHorizontalDocumentDock(right);
        ShellFixture.Pump();
        f.Shell.Layout.OpenDocument(Editor(f, files[0]));
        ShellFixture.Pump();
        Assert.Equal(files[0], Assert.IsType<CodeEditorDocument>(f.Shell.Layout.ActiveDocument).Model.Path);
        var changes = 0;
        f.Shell.Layout.ActiveDocumentChanged += (_, _) => changes++;

        await f.Shell.OpenFilesAsync([(files[3], null), (files[2], null)]);

        Assert.Same(right, f.Shell.Layout.ActiveDocument);
        Assert.Equal(1, changes);
        Assert.NotSame(right.Owner, Editor(f, files[3]).Owner);
    }

    /// <summary>
    /// Closing the window hides it at once, and the shell shuts down behind it with the UI thread free; another codalus
    /// started meanwhile isn't handed to this closing process, and the window goes for good once the shell is down.
    /// </summary>
    [AvaloniaFact]
    public async Task Closing_the_window_hides_it_at_once_and_stops_taking_other_processes_requests()
    {
        var f = ShellFixture.Create();
        await using (f)
        {
            await ShellFixture.PumpUntil(() => Interactive(f));
            var pipe = SingleInstance.PipeName(f.Shell.Paths.State);
            var ping = new InstanceRequest("ping", []);
            Assert.True(SingleInstance.TrySend(pipe, ping));
            var closed = false;
            f.Window.Closed += (_, _) => closed = true;

            f.Window.Close();

            Assert.False(f.Window.IsVisible);
            Assert.True(f.Shell.IsShuttingDown);
            await ShellFixture.PumpUntil(() => !SingleInstance.TrySend(pipe, ping));
            await ShellFixture.PumpUntil(() => closed, seconds: 60);
        }
    }

    /// <summary>
    /// A service that fails to shut down: the window still goes for good (the app ends) and a requested restart still starts.
    /// The failure escaped the shutdown, which then neither restarted nor ended the app, with its window hidden and its
    /// listener closed, out of the user's reach.
    /// </summary>
    [AvaloniaFact]
    public async Task A_shutdown_that_fails_still_ends_the_app_and_restarts_it()
    {
        var f = ShellFixture.Create();
        await using (f)
        {
            await ShellFixture.PumpUntil(() => Interactive(f));
            var restarts = new List<ProcessStartInfo>();
            f.Shell.RestartStarter = restarts.Add;
            f.Shell.ShutdownWork = () => Task.FromException(new InvalidOperationException("A host failed to stop."));
            var closed = false;
            f.Window.Closed += (_, _) => closed = true;

            f.Shell.Restart();

            await ShellFixture.PumpUntil(() => closed, seconds: 60);
            Assert.Single(restarts);
            Assert.Contains("[exit] The shutdown failed: System.InvalidOperationException: A host failed to stop.", f.Shell.Workbench.Log.GetText(), StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// A host that doesn't stop within the shutdown's limit: the restart starts after the limit, and the app ends, without
    /// waiting for it. Last in the disposal, the restart was lost with the hosts that outlasted the limit.
    /// </summary>
    [AvaloniaFact]
    public async Task A_shutdown_that_outlasts_its_limit_still_restarts_the_app()
    {
        var f = ShellFixture.Create();
        var never = new TaskCompletionSource();
        await using (f)
        {
            try
            {
                await ShellFixture.PumpUntil(() => Interactive(f));
                var restarts = new List<ProcessStartInfo>();
                f.Shell.RestartStarter = restarts.Add;
                f.Shell.ShutdownWork = () => never.Task;
                var closed = false;
                f.Window.Closed += (_, _) => closed = true;

                f.Shell.Restart();

                await ShellFixture.PumpUntil(() => closed, seconds: 60);
                Assert.Single(restarts);
                Assert.False(never.Task.IsCompleted);
                Assert.Contains("[exit] The shutdown took longer than 3 s", f.Shell.Workbench.Log.GetText(), StringComparison.Ordinal);
            }
            finally
            {
                never.TrySetResult(); // the rest of the disposal, before the folder goes
            }
        }
    }
}

/// <summary>Tests that hold this process's environment (Rpc.ProcessEnvironment): every process start waits for it, so alone.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessEnvironmentTests
{
    public const string Name = "Tests that hold the process environment";
}

/// <summary>
/// Started from the Finder or a desktop launcher, the login shell's PATH comes in while the window shows (M56): the startup's
/// work waits for it, and what the user does meanwhile (closing the window, opening something) counts.
/// </summary>
[Collection(ProcessEnvironmentTests.Name)]
public sealed class StartupEnvironmentTests
{
    /// <summary>A shell whose login shell's environment comes in when the test says.</summary>
    private static ShellFixture CreateWaiting(TaskCompletionSource<bool> import, bool firstRun = false)
    {
        var before = App.LoginShellImport;
        App.LoginShellImport = import.Task;
        try
        {
            return ShellFixture.Create(firstRun: firstRun);
        }
        finally
        {
            App.LoginShellImport = before;
        }
    }

    /// <summary>The startup's work (which starts processes and goes online) waits for the login shell's environment, as processes do.</summary>
    [AvaloniaFact]
    public async Task The_startup_waits_for_the_login_shells_environment()
    {
        var import = new TaskCompletionSource<bool>();
        var f = CreateWaiting(import);
        await using (f)
        {
            try
            {
                await StartupAndExitTests.PumpForAsync(500);
                Assert.False(f.Shell.EnvironmentReady.IsCompleted);
                Assert.False(Rpc.ProcessEnvironment.Ready.IsCompleted);
                Assert.False(StartupAndExitTests.Interactive(f));

                import.SetResult(true);
                await ShellFixture.PumpUntil(() => StartupAndExitTests.Interactive(f));
                Assert.True(Rpc.ProcessEnvironment.Ready.IsCompleted);
            }
            finally
            {
                import.TrySetResult(false);
            }
        }
    }

    /// <summary>
    /// The window closed before the environment came in: the startup then does nothing. It ran on the shell being disposed:
    /// a single-instance listener a codalus started then was handed to, the first-run setup (or the start window) over a
    /// closed IDE, and the startup plugins' host, none of which the shutdown stopped.
    /// </summary>
    [AvaloniaFact]
    public async Task A_window_closed_before_the_environment_came_in_starts_nothing()
    {
        var import = new TaskCompletionSource<bool>();
        var f = CreateWaiting(import, firstRun: true);
        await using (f)
        {
            try
            {
                var closed = false;
                f.Window.Closed += (_, _) => closed = true;
                await StartupAndExitTests.PumpForAsync(300);

                f.Window.Close();
                Assert.True(f.Shell.IsShuttingDown);
                import.SetResult(true);
                await ShellFixture.PumpUntil(() => closed, seconds: 60);
                await StartupAndExitTests.PumpForAsync(300);

                Assert.False(StartupAndExitTests.Interactive(f));
                Assert.False(SingleInstance.TrySend(SingleInstance.PipeName(f.Shell.Paths.State), new InstanceRequest("ping", [])));
                Assert.False(f.Shell.IsSetupOpen);
                Assert.False(f.Shell.IsStartWindowOpen);
                Assert.Empty(f.Shell.Plugins.Hosts);
            }
            finally
            {
                import.TrySetResult(false);
            }
        }
    }

    /// <summary>
    /// Something opened while the startup waited for the environment (here a folder, as the Finder's open-documents event
    /// that started the app opens one): the first-run setup and the start window stay away, as for a command line that opens
    /// something. Both showed over what had opened.
    /// </summary>
    [AvaloniaFact]
    public async Task What_opened_while_the_startup_waited_keeps_the_setup_and_the_start_window_away()
    {
        var import = new TaskCompletionSource<bool>();
        var f = CreateWaiting(import, firstRun: true);
        await using (f)
        {
            try
            {
                var folder = Directory.CreateDirectory(Path.Combine(f.UserData, "work", "Notes")).FullName;
                File.WriteAllText(Path.Combine(folder, "notes.md"), "# Notes\n");
                f.Shell.Trust.Trust(folder);
                await StartupAndExitTests.PumpForAsync(300);

                var opening = f.Shell.OpenFolderAsync(folder);
                import.SetResult(true);
                await ShellFixture.PumpUntil(() => StartupAndExitTests.Interactive(f) && opening.IsCompleted);
                await StartupAndExitTests.PumpForAsync(500);

                Assert.True(f.Shell.Solutions.IsOpen);
                Assert.Contains("[setup] First-run setup: OpensSomething.", f.Shell.Workbench.Log.GetText(), StringComparison.Ordinal);
                Assert.False(f.Shell.IsSetupOpen);
                Assert.False(f.Shell.IsStartWindowOpen);
            }
            finally
            {
                import.TrySetResult(false);
            }
        }
    }

    /// <summary>
    /// The terminal profiles are found again once the login shell's PATH is in, and the Terminal window's list follows: it
    /// copied the profiles found before (here none), so a shell only that PATH has never showed in it until a restart.
    /// </summary>
    [AvaloniaFact]
    public async Task The_terminal_windows_profiles_follow_the_ones_found_with_the_login_shells_path()
    {
        var import = new TaskCompletionSource<bool>();
        var f = CreateWaiting(import);
        await using (f)
        {
            try
            {
                f.Shell.Terminals.Profiles = [];
                f.Shell.TerminalWindow.RefreshProfiles();
                Assert.Empty(f.Shell.TerminalWindow.Profiles);

                import.SetResult(true);
                await ShellFixture.PumpUntil(() => f.Shell.Terminals.Profiles.Count > 0 && f.Shell.TerminalWindow.Profiles.Count > 0);

                Assert.Equal(f.Shell.Terminals.Profiles.Select(p => p.Id), f.Shell.TerminalWindow.Profiles.Select(p => p.Id));
            }
            finally
            {
                import.TrySetResult(false);
            }
        }
    }
}
