using System.Collections.Specialized;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
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
    public async Task A_reader_scrolled_up_or_with_scroll_lock_on_keeps_its_lines_while_the_oldest_are_dropped()
    {
        // Past the cap each batch drops about as many of the oldest lines as it adds. The lines in view stay put unless the
        // view follows the end: with Scroll Lock on at the end (the extent the same after each batch), they moved up at
        // every batch.
        await using var f = ShellFixture.Create();
        var channel = f.Shell.Workbench.Output.GetOrCreate("test.reader", "Reader");
        var view = new OutputView { DataContext = f.Shell.Output };
        f.Shell.Output.WordWrap = false;
        f.Shell.Output.Selected = channel;
        var host = new Window { Width = 700, Height = 300, Content = view };
        host.Show();
        await ShellFixture.PumpUntil(() => view.Coloring.Channel == channel);
        var editor = view.GetVisualDescendants().OfType<TextEditor>().Single();
        var textView = editor.TextArea.TextView;
        var viewer = editor.GetVisualDescendants().OfType<ScrollViewer>().First();
        string LastLine() => editor.Document.LineCount < 2 ? string.Empty : editor.Document.GetText(editor.Document.GetLineByNumber(editor.Document.LineCount - 1));
        string TopLine()
        {
            ShellFixture.Pump(); // laid out
            return editor.Document.GetText(textView.GetDocumentLineByVisualTop(textView.VerticalOffset));
        }

        var next = 0;
        async Task AppendAsync(int lines)
        {
            var text = new StringBuilder();
            for (var i = 0; i < lines; i++)
            {
                text.Append("line ").Append((next++).ToString("D7", CultureInfo.InvariantCulture)).Append('.', 87).Append('\n');
            }

            var chunk = text.ToString();
            var last = chunk[^100..^1];
            await Task.Run(() => channel.Append(chunk), TestContext.Current.CancellationToken);
            await ShellFixture.PumpUntil(() => LastLine() == last, seconds: 60);
        }

        for (var i = 0; i < 50; i++)
        {
            await AppendAsync(1000); // 5M characters: past the cap, at the end
        }

        Assert.True(view.IsFollowing);

        // Scrolled up: the lines in view stay as the oldest go.
        viewer.Offset = new Vector(0, viewer.Offset.Y / 2);
        var top = TopLine();
        Assert.False(view.IsFollowing);
        for (var i = 0; i < 10; i++)
        {
            await AppendAsync(1000);
            Assert.Equal(top, TopLine());
        }

        // At the end with Scroll Lock on: the same.
        viewer.Offset = new Vector(0, viewer.Extent.Height - viewer.Viewport.Height);
        ShellFixture.Pump();
        Assert.True(view.IsFollowing);
        f.Shell.Output.ScrollLock = true;
        top = TopLine();
        for (var i = 0; i < 10; i++)
        {
            await AppendAsync(1000);
            Assert.Equal(top, TopLine());
        }

        Assert.InRange(editor.Document.TextLength, OutputPresenter.MaxLength - 100, OutputPresenter.MaxLength);

        // More than the editor keeps in one batch: its end replaces the text, once, from a line's start, its runs in place.
        f.Shell.Output.ScrollLock = false;
        var loads = view.Coloring.Loads;
        var big = new StringBuilder();
        for (var i = 0; i < 50_000; i++)
        {
            big.Append("big ").Append(i.ToString("D7", CultureInfo.InvariantCulture)).Append('.', 88).Append('\n');
        }

        var flood = big.ToString();
        await Task.Run(() =>
        {
            channel.Append(flood);
            channel.AppendErrorLine("the end");
        }, TestContext.Current.CancellationToken);
        await ShellFixture.PumpUntil(() => LastLine() == "the end", seconds: 60);
        Assert.Equal(loads + 1, view.Coloring.Loads);
        Assert.InRange(editor.Document.TextLength, OutputPresenter.MaxLength / 2, OutputPresenter.MaxLength);
        var first = editor.Document.GetText(editor.Document.GetLineByNumber(1));
        Assert.True(first.StartsWith("big ", StringComparison.Ordinal) && first.Length == 99, first);
        var error = editor.Document.TextLength - "the end\n".Length;
        Assert.Contains(view.Coloring.Runs, r => r.Start == error && (r.Style.Attributes & OutputAttributes.Error) != 0);
        host.Close();
    }

    [AvaloniaFact]
    public async Task The_error_list_shows_a_builds_diagnostics_at_most_every_quarter_second_and_at_once_when_it_ends()
    {
        // A build reports its diagnostics one at a time: the list was built again and reset for each, 20-30 times a second
        // for the whole build.
        await using var f = ShellFixture.Create();
        var list = f.Shell.ErrorList;
        f.Shell.Layout.ShowToolWindow(ErrorListViewModel.ToolWindowId, focus: false);
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<ErrorListView>().Any());
        var listBox = f.Window.GetVisualDescendants().OfType<ErrorListView>().First().GetVisualDescendants().OfType<ListBox>().First(l => l.Name == "List");
        var build = new TaskCompletionSource<BuildResultInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Shell.Builds.Runner = (_, _) => build.Task;
        var building = f.Shell.Builds.BuildAsync("Build", null, CancellationToken.None);
        await ShellFixture.PumpUntil(() => f.Shell.Builds.IsBuilding);
        await ShellFixture.PumpUntil(() => !list.IsRefreshing);

        var resets = 0;
        list.Items.CollectionChanged += (_, e) => resets += e.Action == NotifyCollectionChangedAction.Reset ? 1 : 0;
        var root = Path.Combine(Path.GetTempPath(), "codalus-build-" + Guid.NewGuid().ToString("N"));
        void Report(string file) => f.Shell.Languages.Diagnostics.Set(Path.Combine(root, file), "csharp", null,
            [new Diagnostic(new Range(new Position(0, 0), new Position(0, 4)), DiagnosticSeverity.Error, "CS0246", "csharp", "The type or namespace name could not be found")]);
        var watch = Stopwatch.StartNew();
        for (var i = 20; i < 40; i++)
        {
            Report($"File{i}.cs");
            await WaitAsync(25);
        }

        // A row selected meanwhile stays selected as rows come before it: each reset selects the same row again.
        await ShellFixture.PumpUntil(() => list.Rows.Count == 20 && !list.IsRefreshing);
        var selected = list.Rows.Single(r => r.File == "File30.cs");
        listBox.SelectedItem = selected;
        for (var i = 0; i < 20; i++)
        {
            Report($"File{i:00}.cs");
            await WaitAsync(25);
        }

        var elapsed = watch.Elapsed;
        Assert.InRange(resets, 2, (int)(elapsed.TotalMilliseconds / list.BuildRefreshInterval.TotalMilliseconds) + 3);

        // The build's end shows the rest.
        build.SetResult(new BuildResultInfo(false, false, 40, 0, 1000));
        await building;
        await ShellFixture.PumpUntil(() => list.Rows.Count == 40 && !list.IsRefreshing);
        Assert.Equal(selected, listBox.SelectedItem);
        Assert.Equal(30, listBox.SelectedIndex);

        // At once: a diagnostic waits for the next turn while the build runs (here, one too far off to wait for), and shows
        // when the build ends.
        list.BuildRefreshInterval = TimeSpan.FromMinutes(10);
        build = new TaskCompletionSource<BuildResultInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        building = f.Shell.Builds.BuildAsync("Build", null, CancellationToken.None);
        await ShellFixture.PumpUntil(() => f.Shell.Builds.IsBuilding);
        Report("File40.cs");
        await WaitAsync(300);
        Assert.Equal(40, list.Rows.Count);
        build.SetResult(new BuildResultInfo(false, false, 41, 0, 1000));
        await building;
        await ShellFixture.PumpUntil(() => list.Rows.Count == 41 && !list.IsRefreshing, seconds: 60);
        Assert.Equal(selected, listBox.SelectedItem);
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

        // Replace All lists the same files: the open document's text changes.
        var checksBefore = checks;
        find.LookIn = FindScopes.EntireSolution;
        find.ReplaceWith = "zzflooded";
        var replacing = find.ReplaceAllAsync();
        await ShellFixture.PumpUntil(() => f.Shell.Dialogs.IsOpen);
        f.Shell.Dialogs.Accept();
        await replacing;
        Assert.StartsWith("// TODO: zzflooded unsaved\n", editor.Model.Text.Text, StringComparison.Ordinal);
        Assert.True(checks > checksBefore, $"{checks - checksBefore} files checked by Replace All");
        Assert.Equal(0, onUiThread);
    }

    [AvaloniaFact]
    public async Task Without_a_solution_find_all_and_the_task_list_read_the_open_documents_on_disk_or_not()
    {
        // Without a solution the open documents are what Find All (Entire solution) and the Task List read: one deleted on
        // disk but still open was left out once its existence was checked on the thread pool.
        await using var f = ShellFixture.Create();
        var folder = Path.Combine(f.UserData, "loose");
        Directory.CreateDirectory(folder);
        var kept = Path.Combine(folder, "Kept.cs");
        var gone = Path.Combine(folder, "Gone.cs");
        await File.WriteAllTextAsync(kept, "// TODO: zzloose kept\n", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(gone, "// TODO: zzloose gone\n", TestContext.Current.CancellationToken);
        Assert.NotNull(await f.Shell.OpenFileAsync(kept));
        Assert.NotNull(await f.Shell.OpenFileAsync(gone));
        File.Delete(gone);

        var find = f.Shell.FindReplace;
        var results = f.Shell.FindResults;
        find.Query = "zzloose";
        find.LookIn = FindScopes.EntireSolution;
        await find.FindAllAsync();
        await ShellFixture.PumpUntil(() => !results.CurrentPage.IsSearching);
        Assert.Contains(results.CurrentPage.Rows, r => !r.IsHeader && r.Path == kept);
        Assert.Contains(results.CurrentPage.Rows, r => !r.IsHeader && r.Path == gone);

        await f.Shell.TaskList.RefreshAsync();
        Assert.Contains(f.Shell.TaskList.Rows, r => r.Text == "zzloose kept" && r.Path == kept);
        Assert.Contains(f.Shell.TaskList.Rows, r => r.Text == "zzloose gone" && r.Path == gone);
    }

    [Fact]
    public async Task A_task_list_refresh_replaced_while_it_lists_the_files_ends_quietly_and_shows_the_newer_rows()
    {
        // The listing moved off the UI thread and can take seconds: a refresh replaced meanwhile read its token source
        // after the newer refresh had disposed it, and its task faulted unobserved. Its rows must not replace the newer ones.
        var listings = new List<TaskCompletionSource<FilesToSearch>>();
        using var list = new TaskListViewModel(
            _ =>
            {
                var listing = new TaskCompletionSource<FilesToSearch>(TaskCreationOptions.RunContinuationsAsynchronously);
                listings.Add(listing);
                return listing.Task;
            },
            () => ["TODO"],
            (_, _) => Task.CompletedTask);
        var older = list.RefreshAsync();
        var newer = list.RefreshAsync();
        var file = Path.Combine(Path.GetTempPath(), "Tasks.cs");
        listings[1].SetResult(new FilesToSearch([file], new Dictionary<string, string> { [file] = "// TODO: newer\n" }));
        await newer;
        listings[0].SetResult(new FilesToSearch([file], new Dictionary<string, string> { [file] = "// TODO: older\n" }));
        await older;
        Assert.Equal("newer", Assert.Single(list.Rows).Text);
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

        // The active item (the current branch) shows, selected, even past the first thousand.
        QuickPickItem[] items = [.. Enumerable.Range(0, 50_000).Select(i => new QuickPickItem($"origin/feature/branch-{i:00000}"))];
        _ = pick.ShowAsync(items, "Switch branch", activeIndex: 30_000);
        Assert.Equal(QuickPickViewModel.MaxRows + 1, pick.Rows.Count);
        Assert.Same(items[30_000], pick.Rows[^1].Item);
        Assert.Equal(QuickPickViewModel.MaxRows, pick.SelectedIndex);
        Assert.Contains((50_000 - QuickPickViewModel.MaxRows - 1).ToString("N0", CultureInfo.CurrentCulture), pick.MoreText, StringComparison.Ordinal);
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
            new TestItemDto("p|b", "p|n", "Broken", null, null, null, null, null, "error CS1002: " + new string('b', 40_000)),
        ], []));
        var chunk = new string('o', 4999) + "\n";

        // A run that starts with no other in progress starts Output > Tests afresh; one that starts during it doesn't.
        var channel = f.Shell.Workbench.Output.GetOrCreate("codalus.tests", "Tests");
        channel.AppendLine("an earlier run");
        tests.RunStarted(new TestRunStartedParams(controller, "r1", "Run", null, "run"));
        Assert.Equal(string.Empty, channel.GetText());
        tests.RunUpdated(new TestRunUpdateParams("r1", [], [new TestOutputDto(null, "the first run\n")], []));
        tests.RunStarted(new TestRunStartedParams(controller, "r2", "Run", null, "run"));
        tests.RunUpdated(new TestRunUpdateParams("r2", [], [new TestOutputDto(null, "the second run\n")], []));
        tests.RunEnded("r2");
        Assert.Equal("the first run\nthe second run\n", channel.GetText());
        for (var i = 0; i < 1000; i++)
        {
            tests.RunUpdated(new TestRunUpdateParams("r1", [], [new TestOutputDto("p|m", chunk)], []));
        }

        tests.RunUpdated(new TestRunUpdateParams("r1", [new TestStateDto("p|m", TestStates.Failed, 5, [new TestMessageDto(new string('e', 100_000), null, null, null, null, null)])], [], []));
        tests.RunEnded("r1");

        // The test keeps the end of its output, from a line's start; the run's output went to Output > Tests as it came,
        // capped.
        var node = tests.Find(TestNode.KeyOf(controller, "p|m"))!;
        var output = node.Result!.Output;
        Assert.InRange(output.Length, TestService.MaxTestOutputLength - 4096, TestService.MaxTestOutputLength + 100);
        Assert.StartsWith("[", output, StringComparison.Ordinal);
        Assert.EndsWith(chunk, output, StringComparison.Ordinal);
        Assert.Equal(chunk, output[(output.IndexOf('\n', StringComparison.Ordinal) + 1)..][..chunk.Length]);
        var text = channel.GetText();
        Assert.InRange(text.Length, OutputChannel.MaxLength / 2, OutputChannel.MaxLength);
        Assert.EndsWith(chunk, text, StringComparison.Ordinal);

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
        text = f.Shell.ActiveEditor!.Model.Text.Text;
        Assert.Contains(new string('e', 100_000), text, StringComparison.Ordinal);
        Assert.EndsWith(output + Environment.NewLine, text, StringComparison.Ordinal);
        Assert.True(f.Shell.ActiveEditor.Model.IsReadOnly);

        // A test that only has an error (a discovery or build error, as a group can have): the pane shows its start, and
        // Show Full Output opens all of it.
        var broken = tests.Find(TestNode.KeyOf(controller, "p|b"))!;
        explorer.Reveal(broken.Key);
        Assert.True(explorer.IsDetailsCut);
        Assert.InRange(explorer.DetailsMessage!.Length, 1, TestExplorerViewModel.DetailsTextLength + 2);
        explorer.ShowFullOutputCommand.Execute(null);
        await ShellFixture.PumpUntil(() => f.Shell.ActiveEditor?.Model.Text.Text.Contains(broken.Error!, StringComparison.Ordinal) == true);
    }

    [AvaloniaFact]
    public async Task Import_trx_import_playlist_and_export_trx_leave_the_ui_thread_while_the_test_model_is_busy()
    {
        // They read the file, matched every result against every test (or built the document) on the UI thread, under the
        // test model's lock: for the seconds the matching took, or while a controller held the model, the window froze.
        // Now each returns to the window while a controller holds the model, and finishes once it's free.
        await using var f = ShellFixture.Create();
        var solution = f.CopyFixture();
        var folder = Path.GetDirectoryName(solution)!;
        Assert.True(await f.Shell.OpenSolutionAsync(solution, trusted: false)); // a solution to keep playlists with; no build needed
        await ShellFixture.PumpUntil(() => f.Shell.Solutions.Solution is not null);
        const string controller = "codalus.synthetic:tests";
        var tests = f.Shell.Tests;
        tests.ControllerCreated(new TestControllerParams("codalus.synthetic", controller, "Synthetic"), new TestRefreshTests.Connection());
        tests.ItemsUpdated(new TestItemsUpdateParams(controller,
        [
            new TestItemDto("p", null, "P", null, null, null, null, null, null),
            new TestItemDto("p|n", "p", "Tests", null, null, null, null, null, null),
            new TestItemDto("p|c", "p|n", "Calc", null, null, null, null, null, null),
            new TestItemDto("p|a", "p|c", "Adds", null, null, null, null, null, null),
        ], []));
        var trx = Path.Combine(folder, "results.trx");
        await File.WriteAllTextAsync(trx, """
            <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
              <Results><UnitTestResult testName="Tests.Calc.Adds" outcome="Failed" /></Results>
            </TestRun>
            """, TestContext.Current.CancellationToken);
        var playlist = Path.Combine(folder, "smoke.playlist");
        await File.WriteAllTextAsync(playlist, "<Playlist Version=\"1.0\"><Add Test=\"Tests.Calc.Adds\" /></Playlist>", TestContext.Current.CancellationToken);
        var exported = Path.Combine(folder, "exported.trx");
        f.Shell.Window = new PickingWindow(playlist, exported);

        async Task WhileHeldAsync(Func<Task> start)
        {
            using var held = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var holder = Task.Run(() => tests.Read(_ =>
            {
                held.Set();
                return release.Wait(TimeSpan.FromSeconds(30));
            }), TestContext.Current.CancellationToken);
            Assert.True(held.Wait(TimeSpan.FromSeconds(30)));

            // No dispatcher pass in between: what's measured is the command alone (the Test Explorer's refresh reads the
            // model on the UI thread too, briefly).
            var started = start();
            var returned = !holder.IsCompleted; // it didn't wait for the model on the UI thread
            release.Set();
            Assert.True(await holder, "the model was held until it was released");
            await started;
            Assert.True(returned, "returned while the model was held");
        }

        await WhileHeldAsync(() => f.Shell.ImportTrxAsync(trx));
        Assert.Equal(TestOutcome.Failed, tests.Find(TestNode.KeyOf(controller, "p|a"))!.Outcome);
        await WhileHeldAsync(f.Shell.ImportVisualStudioPlaylistAsync);
        await ShellFixture.PumpUntil(() => f.Shell.TestExplorer.Playlist?.Name == "smoke");
        await WhileHeldAsync(f.Shell.ExportTrxAsync);
        Assert.Contains("Tests.Calc.Adds", await File.ReadAllTextAsync(exported, TestContext.Current.CancellationToken), StringComparison.Ordinal);
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

        // A newer find replaces one still looking: the older one, whose match is farther, selects nothing.
        vm.FindText = "row 30000";
        Assert.True(view.IsFinding);
        vm.FindText = "row 7777";
        await ShellFixture.PumpUntil(() => !view.IsFinding);
        Assert.Equal("row 7777", control.SelectedText());

        // Find Next while a find is still looking goes on with it; closing the find bar drops it. (Rows from 6000 on are
        // the only ones to start with their number.)
        vm.FindText = "row 9999";
        vm.FindNext(backwards: false);
        vm.FindNext(backwards: false);
        Assert.True(view.IsFinding);
        await ShellFixture.PumpUntil(() => !view.IsFinding);
        Assert.Equal("row 9999", control.SelectedText());
        vm.FindText = "row 20000";
        Assert.True(view.IsFinding);
        vm.FindVisible = false;
        Assert.False(view.IsFinding);
        await WaitAsync(500);
        Assert.Equal("row 9999", control.SelectedText());
    }

    /// <summary>The window, with Open answering <paramref name="pick"/> and Save As <paramref name="save"/> instead of native dialogs.</summary>
    private sealed class PickingWindow(string pick, string save) : IShellWindow
    {
        public Avalonia.Input.Platform.IClipboard? Clipboard => null;

        public bool IsFullScreen
        {
            get => false;
            set { }
        }

        public void FocusPane(bool backwards)
        {
        }

        public void RequestClose()
        {
        }

        public void BringToFront()
        {
        }

        public Task<string?> PickSolutionAsync() => Task.FromResult<string?>(null);

        public Task<string?> PickProjectAsync() => Task.FromResult<string?>(null);

        public Task<string?> PickFileAsync(string title, string filterName, string[] patterns) => Task.FromResult<string?>(pick);

        public Task<string?> SaveFileAsync(string title, string suggestedName, string filterName, string[] patterns) => Task.FromResult<string?>(save);

        public Task<IReadOnlyList<string>> PickFilesAsync(string title) => Task.FromResult<IReadOnlyList<string>>([]);

        public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);
    }
}
