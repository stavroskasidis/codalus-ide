using Avalonia.Headless.XUnit;
using Codalus.Shell.Docking;
using Codalus.Shell.Infrastructure;
using Codalus.Shell.ViewModels;

namespace Codalus.Shell.Tests;

/// <summary>
/// The UI never waits at startup, at exit, or when many tabs open or close together (M56): the window doesn't wait for the
/// login shell's environment, a restored layout's hidden tool windows make nothing (the start window's recent entries:
/// StartWindowTests), tabs opened or closed together make one of them active, and the exit hides the window at once.
/// </summary>
public sealed class StartupAndExitTests
{
    private static async Task PumpForAsync(int milliseconds)
    {
        var until = DateTime.UtcNow.AddMilliseconds(milliseconds);
        await ShellFixture.PumpUntil(() => DateTime.UtcNow >= until);
    }

    private static bool Interactive(ShellFixture f) => f.Shell.Workbench.Log.GetText().Contains("[setup] First-run setup:", StringComparison.Ordinal);

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

    /// <summary>
    /// Started from the Finder or a desktop launcher, the login shell's PATH comes in while the window shows: the window draws
    /// and answers meanwhile, and the startup's work (which starts processes and goes online) waits for it, as processes do.
    /// </summary>
    [AvaloniaFact]
    public async Task The_window_shows_before_the_login_shells_environment_and_the_startup_waits_for_it()
    {
        var import = new TaskCompletionSource<bool>();
        var before = App.LoginShellImport;
        App.LoginShellImport = import.Task;
        ShellFixture f;
        try
        {
            f = ShellFixture.Create();
        }
        finally
        {
            App.LoginShellImport = before;
        }

        await using (f)
        {
            try
            {
                await PumpForAsync(500);
                Assert.True(f.Window.IsVisible);
                Assert.False(f.Shell.EnvironmentReady.IsCompleted);
                Assert.False(Rpc.ProcessEnvironment.Ready.IsCompleted);
                Assert.False(Interactive(f));

                import.SetResult(true);
                await ShellFixture.PumpUntil(() => Interactive(f));
                Assert.True(Rpc.ProcessEnvironment.Ready.IsCompleted);
            }
            finally
            {
                import.TrySetResult(false);
            }
        }
    }

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
        await ShellFixture.PumpUntil(() => testExplorer.HasContent);
        Assert.Same(f.Shell.TestExplorer, testExplorer.Content);
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

            f.Shell.Layout.OpenDocument(f.Shell.Editors.Single(e => e.Model.Path == files[1]));
            ShellFixture.Pump();
            changes = 0;
        }

        await OpenAllAsync();
        f.Shell.CloseAllDocuments(exceptActive: true);
        Assert.Equal(0, changes);
        Assert.Equal(files[1], Assert.Single(f.Shell.Editors).Model.Path);

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
}
