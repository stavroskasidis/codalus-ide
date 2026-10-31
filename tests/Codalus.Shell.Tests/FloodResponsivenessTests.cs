using System.Collections.Specialized;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Codalus.LanguageServices.Protocol;
using Codalus.Plugins.Protocol;
using Codalus.Projects.Protocol;
using Codalus.Shell.Controls;
using Codalus.Shell.Terminal;
using Codalus.Shell.ViewModels;
using Codalus.Shell.Views;
using Codalus.Workbench.Output;
using Codalus.Workbench.Testing;
using Range = Codalus.LanguageServices.Protocol.Range;

namespace Codalus.Shell.Tests;

/// <summary>
/// Floods and large inputs keep the UI answering (M56, the maintainer's requirement: the UI never hangs): output channels
/// that pass their cap, a build's stream of diagnostics, a solution's files listed for a search, a picker of tens of
/// thousands of items, a test's megabytes of output and a terminal's long scrollback. Each test counts what the UI thread
/// did (reloads, resets, slices) rather than timing it.
/// </summary>
public sealed class FloodResponsivenessTests
{
    private static async Task WaitAsync(int milliseconds)
    {
        var until = DateTime.UtcNow.AddMilliseconds(milliseconds);
        await ShellFixture.PumpUntil(() => DateTime.UtcNow >= until, seconds: 120);
    }

    [AvaloniaFact]
    public async Task A_flooded_output_channel_drops_its_oldest_lines_without_reloading()
    {
        // Past 4M characters each trim of the channel decoded all of it again and reloaded the editor, 3M characters for
        // each 1M appended; and showing the Output window again read the whole channel again.
        await using var f = ShellFixture.Create();
        var channel = f.Shell.Workbench.Output.GetOrCreate("test.flood", "Flood");
        var view = new OutputView { DataContext = f.Shell.Output };
        f.Shell.Output.Selected = channel;
        var host = new Window { Width = 700, Height = 300, Content = view };
        host.Show();
        await ShellFixture.PumpUntil(() => view.Coloring.Channel == channel);
        var loads = view.Coloring.Loads;

        // 12M characters from another thread, in lines of 100, as a build writes them: the window shows each batch before
        // the next (more than the editor keeps in one batch replaces its text, as all of it would go anyway).
        var line = new string('x', 99) + "\n";
        var chunk = string.Concat(Enumerable.Repeat(line, 1000));
        for (var i = 0; i < 120; i++)
        {
            await Task.Run(() => channel.Append(chunk), TestContext.Current.CancellationToken);
            ShellFixture.Pump();
        }

        await Task.Run(() =>
        {
            channel.AppendErrorLine("the last error");
            channel.AppendLine("done");
        }, TestContext.Current.CancellationToken);
        await ShellFixture.PumpUntil(() => view.Text.EndsWith("the last error\ndone\n", StringComparison.Ordinal), seconds: 60);

        Assert.Equal(loads, view.Coloring.Loads); // the editor dropped its oldest lines as more came: it never reloaded
        Assert.InRange(view.Text.Length, OutputPresenter.MaxLength - 100, OutputPresenter.MaxLength);
        Assert.Equal(line, view.Text[..100]); // from a line's start
        var error = view.Text.IndexOf("the last error", StringComparison.Ordinal);
        Assert.Contains(view.Coloring.Runs, r => r.Start == error && (r.Style.Attributes & OutputAttributes.Error) != 0);

        // Shown again unchanged, the channel isn't read again; changed meanwhile, once.
        host.Content = null;
        ShellFixture.Pump();
        host.Content = view;
        ShellFixture.Pump();
        Assert.Equal(loads, view.Coloring.Loads);
        Assert.EndsWith("done\n", view.Text, StringComparison.Ordinal);
        host.Content = null;
        ShellFixture.Pump();
        channel.AppendLine("while away");
        host.Content = view;
        await ShellFixture.PumpUntil(() => view.Text.EndsWith("while away\n", StringComparison.Ordinal));
        Assert.Equal(loads + 1, view.Coloring.Loads);
        host.Close();
    }

    [AvaloniaFact]
    public async Task The_error_list_shows_a_builds_diagnostics_at_most_every_quarter_second_and_at_once_when_it_ends()
    {
        // A build reports its diagnostics one at a time: the list was built again and reset for each, 20-30 times a second
        // for the whole build, losing its selection each time.
        await using var f = ShellFixture.Create();
        var list = f.Shell.ErrorList;
        var build = new TaskCompletionSource<BuildResultInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Shell.Builds.Runner = (_, _) => build.Task;
        var building = f.Shell.Builds.BuildAsync("Build", null, CancellationToken.None);
        await ShellFixture.PumpUntil(() => f.Shell.Builds.IsBuilding);
        await ShellFixture.PumpUntil(() => !list.IsRefreshing);

        var resets = 0;
        list.Items.CollectionChanged += (_, e) => resets += e.Action == NotifyCollectionChangedAction.Reset ? 1 : 0;
        var root = Path.Combine(Path.GetTempPath(), "codalus-build-" + Guid.NewGuid().ToString("N"));
        var watch = Stopwatch.StartNew();
        for (var i = 0; i < 40; i++)
        {
            f.Shell.Languages.Diagnostics.Set(Path.Combine(root, $"File{i:00}.cs"), "csharp", null,
                [new Diagnostic(new Range(new Position(0, 0), new Position(0, 4)), DiagnosticSeverity.Error, "CS0246", "csharp", "The type or namespace name could not be found")]);
            await WaitAsync(25);
        }

        var elapsed = watch.Elapsed;
        Assert.InRange(resets, 1, (int)(elapsed.TotalMilliseconds / ErrorListViewModel.BuildRefreshInterval.TotalMilliseconds) + 2);

        // The build's end shows the rest at once.
        build.SetResult(new BuildResultInfo(false, false, 40, 0, 1000));
        await building;
        await ShellFixture.PumpUntil(() => list.Rows.Count == 40 && !list.IsRefreshing);
    }

    [AvaloniaFact]
    public async Task Find_all_replace_all_and_the_task_list_check_the_solutions_files_off_the_ui_thread()
    {
        // Find All (Entire solution, Current project), Replace All and the Task List checked each of the solution's files
        // on disk, sorted them twice and copied each open document's text, on the UI thread, before showing anything:
        // 50,000 files froze the window for seconds.
        await using var f = ShellFixture.Create();
        var solution = f.CopyFixture();
        var folder = Path.GetDirectoryName(solution)!;
        f.Shell.Trust.Trust(Path.GetDirectoryName(folder)!);
        await f.Shell.OpenSolutionAsync(solution);
        await ShellFixture.PumpUntil(() => f.Shell.Solutions.Progress is { Completed: true } && f.Shell.Solutions.Projects.Count == 3, seconds: 120);
        var checks = 0;
        var onUiThread = 0;
        f.Shell.FileExists = path =>
        {
            Interlocked.Increment(ref checks);
            if (Dispatcher.UIThread.CheckAccess())
            {
                Interlocked.Increment(ref onUiThread);
            }

            return File.Exists(path);
        };

        // An open document's unsaved text is what's searched.
        var file = f.Shell.Solutions.Projects.Values.SelectMany(p => p.Items).First(i => i.Path.EndsWith(".cs", StringComparison.Ordinal)).Path;
        var editor = await f.Shell.OpenFileAsync(file);
        Assert.NotNull(editor);
        editor.Model.Text.Insert(0, "// TODO: zzflood unsaved\n");
        var find = f.Shell.FindReplace;
        var results = f.Shell.FindResults;
        find.Query = "zzflood";
        foreach (var scope in new[] { FindScopes.EntireSolution, FindScopes.CurrentProject })
        {
            find.LookIn = scope;
            await find.FindAllAsync();
            await ShellFixture.PumpUntil(() => !results.CurrentPage.IsSearching);
            Assert.Contains(results.CurrentPage.Rows, r => !r.IsHeader && r.Path == file && r.Line == 1);
        }

        await f.Shell.TaskList.RefreshAsync();
        Assert.Contains(f.Shell.TaskList.Rows, r => r is { Token: "TODO", Text: "zzflood unsaved", Line: 1 } && r.Path == file);
        Assert.True(checks > 10, $"{checks} files checked");
        Assert.Equal(0, onUiThread);
    }

    [AvaloniaFact]
    public void A_quick_pick_of_fifty_thousand_items_shows_the_best_thousand_with_one_reset_per_keystroke()
    {
        // Each keystroke added every match to the list a change at a time: with 20,000 branches the picker fell behind.
        using var pick = new QuickPickViewModel();
        var changes = 0;
        pick.Rows.CollectionChanged += (_, e) =>
        {
            changes++;
            Assert.Equal(NotifyCollectionChangedAction.Reset, e.Action);
        };
        _ = pick.ShowAsync([.. Enumerable.Range(0, 50_000).Select(i => new QuickPickItem($"origin/feature/branch-{i:00000}"))], "Switch branch");
        Assert.Equal(1, changes);
        Assert.Equal(QuickPickViewModel.MaxRows, pick.Rows.Count);
        Assert.True(pick.HasMore);
        Assert.Contains(49_000.ToString("N0", CultureInfo.CurrentCulture), pick.MoreText, StringComparison.Ordinal);

        foreach (var query in new[] { "b", "br", "bra", "branch-4999" })
        {
            changes = 0;
            pick.Query = query;
            Assert.Equal(1, changes);
            Assert.InRange(pick.Rows.Count, 1, QuickPickViewModel.MaxRows);
            Assert.Equal(0, pick.SelectedIndex);
        }

        // Few enough match: all show, and nothing says there are more.
        Assert.InRange(pick.Rows.Count, 1, 100);
        Assert.False(pick.HasMore);
        pick.Cancel();
    }

    [AvaloniaFact]
    public async Task The_test_details_show_the_end_of_a_long_output_and_open_all_of_it_in_an_editor()
    {
        // A test's whole output went to a wrapping text block in the details pane, laid out at each selection and resize:
        // megabytes froze the window. The run's output was read whole at its end, on the UI thread.
        await using var f = ShellFixture.Create();
        const string controller = "codalus.synthetic:tests";
        var tests = f.Shell.Tests;
        tests.ControllerCreated(new TestControllerParams("codalus.synthetic", controller, "Synthetic"), new TestRefreshTests.Connection());
        tests.ItemsUpdated(new TestItemsUpdateParams(controller,
        [
            new TestItemDto("p", null, "P", null, null, null, null, null, null),
            new TestItemDto("p|n", "p", "Tests", null, null, null, null, null, null),
            new TestItemDto("p|c", "p|n", "Calc", null, null, null, null, null, null),
            new TestItemDto("p|m", "p|c", "Logs", null, null, null, null, null, null),
        ], []));
        var chunk = new string('o', 4999) + "\n";
        tests.RunStarted(new TestRunStartedParams(controller, "r1", "Run", null, "run"));
        for (var i = 0; i < 1000; i++)
        {
            tests.RunUpdated(new TestRunUpdateParams("r1", [], [new TestOutputDto("p|m", chunk)], []));
        }

        tests.RunUpdated(new TestRunUpdateParams("r1", [new TestStateDto("p|m", TestStates.Failed, 5, [new TestMessageDto(new string('e', 100_000), null, null, null, null, null)])], [], []));
        tests.RunEnded("r1");

        // The test keeps the end of its output; the run's output went to Output > Tests as it came, capped.
        var node = tests.Find(TestNode.KeyOf(controller, "p|m"))!;
        var output = node.Result!.Output;
        Assert.InRange(output.Length, TestService.MaxTestOutputLength, TestService.MaxTestOutputLength + 100);
        Assert.StartsWith("[", output, StringComparison.Ordinal);
        Assert.EndsWith(chunk, output, StringComparison.Ordinal);
        var channel = f.Shell.Workbench.Output.GetOrCreate("codalus.tests", "Tests").GetText();
        Assert.InRange(channel.Length, OutputChannel.MaxLength / 2, OutputChannel.MaxLength);
        Assert.EndsWith(chunk, channel, StringComparison.Ordinal);

        // The details pane shows the end of the output and the start of the message, and opens all of it.
        // The Start page takes the left column once the Welcome plugin runs: the Test Explorer shows after it.
        await ShellFixture.PumpUntil(() => f.Shell.Layout.IsVisible("welcome.view"), seconds: 60);
        f.Shell.Layout.ShowToolWindow(TestExplorerViewModel.ToolWindowId, focus: false);
        var explorer = f.Shell.TestExplorer;
        await ShellFixture.PumpUntil(() => explorer.IsViewShown && explorer.Rows.Count > 0, seconds: 60);
        explorer.Reveal(node.Key);
        Assert.Equal(node.Key, explorer.Selected?.Group.Node?.Key);
        Assert.True(explorer.IsDetailsCut);
        Assert.InRange(explorer.DetailsOutput!.Length, 1, TestExplorerViewModel.DetailsTextLength + 2);
        Assert.EndsWith(chunk, explorer.DetailsOutput, StringComparison.Ordinal);
        Assert.InRange(explorer.DetailsMessage!.Length, 1, TestExplorerViewModel.DetailsTextLength + 2);
        explorer.ShowFullOutputCommand.Execute(null);
        await ShellFixture.PumpUntil(() => f.Shell.ActiveEditor?.Model.Uri.StartsWith("codalus-test-result:", StringComparison.Ordinal) == true);
        var text = f.Shell.ActiveEditor!.Model.Text.Text;
        Assert.Contains(new string('e', 100_000), text, StringComparison.Ordinal);
        Assert.EndsWith(output + Environment.NewLine, text, StringComparison.Ordinal);
        Assert.True(f.Shell.ActiveEditor.Model.IsReadOnly);
    }

    [AvaloniaFact]
    public async Task Terminal_find_looks_through_a_long_scrollback_a_slice_at_a_time()
    {
        // Find walked the whole scrollback at once, on the UI thread and holding the terminal: up to 1,000,000 rows, seconds
        // per keystroke, with the program's output stopped.
        await using var f = ShellFixture.Create();
        f.Window.Width = 1200;
        f.Window.Height = 800;
        var session = f.Shell.ShowTerminal(newTerminal: true, profile: OperatingSystem.IsWindows() ? "cmd" : "sh");
        Assert.NotNull(session);
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<TerminalView>().Any());
        var view = f.Window.GetVisualDescendants().OfType<TerminalView>().First();
        var control = f.Window.GetVisualDescendants().OfType<TerminalControl>().First(c => c.Session == session);
        session.Terminal.ScrollbackLimit = 100_000;
        var text = new StringBuilder();
        for (var i = 0; i < 60_000; i++)
        {
            text.Append("row ").Append(i.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
        }

        session.Terminal.Feed(text.ToString());
        ShellFixture.Pump();

        // A miss: the rows after the first slice wait for the next passes, and then nothing is selected.
        var vm = f.Shell.TerminalWindow;
        vm.FindVisible = true;
        vm.FindText = "zz-missing";
        Assert.True(view.IsFinding);
        await ShellFixture.PumpUntil(() => !view.IsFinding);
        Assert.False(control.HasSelection);

        // A match far from the view is found and selected; a newer find replaces one still looking.
        vm.FindText = "row 1234";
        vm.FindText = "row 12345";
        await ShellFixture.PumpUntil(() => !view.IsFinding);
        Assert.Equal("row 12345", control.SelectedText());
    }
}
