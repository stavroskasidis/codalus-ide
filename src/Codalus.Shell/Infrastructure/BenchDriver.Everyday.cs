using System.Diagnostics;
using Codalus.Diagnostics.BenchEvents;
using Codalus.LanguageServices.Protocol;
using Codalus.Projects.Protocol;
using Codalus.Shell.Editor;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Projects;

namespace Codalus.Shell.Infrastructure;

/// <summary>
/// <c>--bench everyday</c> (M56, with a solution: the harness's <c>everyday</c> scenario opens a restored copy of
/// samples/Solution50): the everyday flows of the stall-budget tests (EverydayResponsivenessTests) in the real app, each once
/// to warm up and once measured, while a <see cref="UiStallMonitor"/> times how long input waits for the UI thread. Each
/// measured flow emits <see cref="BenchEventNames.EverydayFlow"/>, and a flow that didn't do its work a
/// <see cref="BenchEventNames.EverydayNote"/>. The tests catch hangs on every pull request; these numbers
/// show the slowdowns, on CI's runners.
/// </summary>
internal sealed partial class BenchDriver
{
    /// <summary>The files the flows open, spread over the solution's projects.</summary>
    private const int EverydayFiles = 20;

    private const int EverydayKeystrokes = 80;

    /// <summary>
    /// What the Solution Explorer's search box gets: one letter, which matches nearly every node, then more a letter at a time
    /// (the solutions' files are named Type00.cs and on), each shown before the box is cleared.
    /// </summary>
    private const string EverydaySearch = "type07";

    /// <summary>How long the flows wait for what someone would wait to see (a filter's matches, a revealed file) before noting it.</summary>
    private static readonly TimeSpan s_everydayShown = TimeSpan.FromSeconds(10);

    /// <summary>After a flow's last step, the work it left queued (reveals, colors, diagnostics) still counts for this long.</summary>
    private static readonly TimeSpan s_settle = TimeSpan.FromMilliseconds(500);

    /// <summary>One measured flow: its name, what the stall monitor saw and how long it took.</summary>
    internal readonly record struct MeasuredFlow(string Name, UiStallReport Stalls, TimeSpan Elapsed);

    /// <summary>What the flows couldn't do, or didn't do as planned, as emitted in <see cref="BenchEventNames.EverydayNote"/> (tests).</summary>
    internal List<string> EverydayNotes { get; } = [];

    /// <summary>The current flow's problems: noted once, after both of its runs.</summary>
    private readonly SortedSet<string> _flowProblems = new(StringComparer.Ordinal);

    private async Task EverydayAsync()
    {
        try
        {
            await RunEverydayFlowsAsync().ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Note($"the flows stopped: {ex.Message}");
        }

        BenchEvents.Emit(BenchEventNames.ScenarioDone);
        if (shell.Options.ExitOn == "done")
        {
            Exit();
        }
    }

    /// <summary>
    /// Waits until the open solution has loaded, then runs the everyday flows (<see cref="BenchEventNames.EverydayFlows"/>):
    /// Track Active Item on while opening files far apart, Next Document held down through the open tabs, Ctrl+Tab held down
    /// through the switcher, Sync with Active Document after Collapse All, typing in the Solution Explorer's search box and
    /// clearing it, Close All Documents and opening the files again, typing in a file, and a rebuild of the solution
    /// (Output, then the Error List). Each flow checks that it did its work and notes what it didn't. Returns the measured
    /// runs (the tests run it on a small solution).
    /// </summary>
    internal async Task<IReadOnlyList<MeasuredFlow>> RunEverydayFlowsAsync()
    {
        // The flows, not the load: every project evaluated, its compiler inputs known and the C# service ready.
        if (!await UntilAsync(() => shell.Solutions.Progress is { Completed: true } && shell.Solutions.Projects.Count > 0 && shell.Solutions.DesignTimeProgress is { Completed: true, Total: > 0 }, TimeSpan.FromMinutes(5)).ConfigureAwait(true))
        {
            Note("the solution didn't load within 5 minutes");
        }

        if (!await UntilAsync(() => shell.Languages.Status(ShellHost.CSharpLanguageId) is { State: WorkspaceStates.Ready }, TimeSpan.FromMinutes(3)).ConfigureAwait(true))
        {
            Note("the C# language service wasn't ready within 3 minutes");
        }

        // The Start page opens in the Solution Explorer's column once the Welcome plugin's host runs, and takes its tab.
        await UntilAsync(() => shell.Layout.IsVisible("welcome.view"), TimeSpan.FromSeconds(30)).ConfigureAwait(true);
        var explorer = shell.Explorer;
        shell.Layout.ShowToolWindow(SolutionExplorerViewModel.ToolWindowId, focus: false);
        explorer.TrackActiveItem = true;
        await UntilAsync(() => explorer.TrackActiveItem, TimeSpan.FromSeconds(30)).ConfigureAwait(true);
        var files = EverydayFilesToOpen();
        if (files.Count < EverydayFiles)
        {
            Note($"{files.Count} files to open, not {EverydayFiles}");
        }

        // The longest of them to type in: an app's one-line Program.cs would leave the language service little to do.
        var typedFile = files.OrderByDescending(file => new FileInfo(file).Length).FirstOrDefault();

        await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(true);
        var flows = new List<MeasuredFlow>();
        using var monitor = new UiStallMonitor();
        void Step(string step) => monitor.Step = step;

        // Once to warm up (the first editor, grammar and view of each kind), once measured.
        async Task Flow(string name, Func<Task> run, Func<Task>? prepare = null, Func<Task>? cleanup = null)
        {
            _flowProblems.Clear();
            for (var pass = 0; pass < 2; pass++)
            {
                if (prepare is not null)
                {
                    await prepare().ConfigureAwait(true);
                }

                await Task.Delay(s_settle).ConfigureAwait(true);
                monitor.Reset();
                Step(name);
                var gc = GC.GetTotalPauseDuration();
                var started = Stopwatch.GetTimestamp();
                await run().ConfigureAwait(true);
                var elapsed = Stopwatch.GetElapsedTime(started);
                Step($"{name}: settling");
                await Task.Delay(s_settle).ConfigureAwait(true);
                var stalls = monitor.Report();
                var paused = GC.GetTotalPauseDuration() - gc;
                if (cleanup is not null)
                {
                    await cleanup().ConfigureAwait(true);
                }

                if (pass == 1)
                {
                    flows.Add(new MeasuredFlow(name, stalls, elapsed));
                    List<(string, string)> text = [("flow", name)];
                    if (stalls.LongestStep is { } step)
                    {
                        text.Add(("step", step));
                    }

                    if (stalls.LongestJob is { } job)
                    {
                        text.Add(("job", job));
                    }

                    BenchEvents.Emit(
                        BenchEventNames.EverydayFlow,
                        [
                            ("stall_max_us", (long)(stalls.LongestMilliseconds * 1000)),
                            ("stall_total_us", (long)(stalls.StalledMilliseconds * 1000)),
                            ("stalls", stalls.Stalls),
                            ("probes", stalls.Probes),
                            ("gc_pause_us", (long)paused.TotalMicroseconds),
                            ("elapsed_us", (long)elapsed.TotalMicroseconds),
                        ],
                        [.. text]);
                }
            }

            foreach (var problem in _flowProblems)
            {
                Note($"{name}: {problem}");
            }
        }

        await Flow("open_tracked", prepare: () =>
        {
            shell.CloseAllDocuments(exceptActive: false);
            shell.Layout.ShowToolWindow(SolutionExplorerViewModel.ToolWindowId, focus: false);
            explorer.CollapseAll();
            return Task.CompletedTask;
        }, run: async () =>
        {
            foreach (var file in files)
            {
                Step($"open_tracked: {Path.GetFileName(file)}");
                await shell.OpenFileAsync(file).ConfigureAwait(true);
                await Task.Delay(20).ConfigureAwait(true);
            }

            if (files.Count > 0 && !await UntilAsync(() => IsSelected(files[^1]), s_everydayShown).ConfigureAwait(true))
            {
                Problem("Track Active Item didn't select the file opened last");
            }
        }).ConfigureAwait(true);

        // Next Document (Ctrl+Alt+PageDown) held down, or a click on each tab in turn: every tab activated.
        await Flow("next_document", async () =>
        {
            foreach (var editor in shell.Editors.Where(e => e.Owner is not null).ToList())
            {
                Step($"next_document: {editor.Title}");
                shell.Layout.OpenDocument(editor);
                if (!ReferenceEquals(shell.Layout.ActiveDocument, editor))
                {
                    Problem("a tab didn't become the active document");
                }

                await Task.Delay(30).ConfigureAwait(true);
            }
        }).ConfigureAwait(true);

        // Ctrl+Tab held down: the switcher opens on the documents, each Tab moves its selection, and releasing Ctrl
        // activates the one selected (MainWindow). Halfway through the tabs, three times.
        await Flow("ctrl_tab", async () =>
        {
            for (var round = 0; round < 3; round++)
            {
                var before = shell.Layout.ActiveDocument;
                Step($"ctrl_tab: switcher {round}");
                shell.SwitchDocument(+1);
                for (var tab = 1; tab < files.Count / 2; tab++)
                {
                    await Task.Delay(30).ConfigureAwait(true);
                    shell.SwitchDocument(+1);
                }

                await Task.Delay(30).ConfigureAwait(true);
                var chosen = shell.Switcher.Selected?.Document;
                Step($"ctrl_tab: release {round}");
                shell.Switcher.Commit();
                if (chosen is null || ReferenceEquals(chosen, before) || !ReferenceEquals(shell.Layout.ActiveDocument, chosen))
                {
                    Problem("releasing Ctrl didn't switch to another document");
                }

                await Task.Delay(100).ConfigureAwait(true);
            }
        }).ConfigureAwait(true);

        await Flow("sync_collapsed", async () =>
        {
            foreach (var editor in shell.Editors.Where(e => e.Owner is not null).Take(10).ToList())
            {
                Step($"sync_collapsed: {editor.Title}");
                shell.Layout.OpenDocument(editor);
                explorer.CollapseAll();
                await Task.Delay(30).ConfigureAwait(true);
                explorer.SyncWithActiveDocument();

                // As someone who looks for the file in the tree before going on.
                if (!await UntilAsync(() => editor.Model.Path is { } path && IsSelected(path), s_everydayShown).ConfigureAwait(true))
                {
                    Problem("Sync with Active Document didn't select the file");
                }

                await Task.Delay(30).ConfigureAwait(true);
            }
        }).ConfigureAwait(true);

        // With the tree collapsed, a file's row shows only while a filter matches it. Each filter is waited for and
        // looked at: cleared sooner, the Solution Explorer's debounce (200 ms) would never apply one.
        await Flow("explorer_search", prepare: () =>
        {
            explorer.CollapseAll();
            return Task.CompletedTask;
        }, run: async () =>
        {
            var collapsed = explorer.Rows.Count;
            for (var length = 1; length <= EverydaySearch.Length; length++)
            {
                var text = EverydaySearch[..length];
                Step($"explorer_search: \"{text}\"");
                explorer.Filter = text;
                if (length > 1 && length < EverydaySearch.Length)
                {
                    await Task.Delay(80).ConfigureAwait(true); // typing on
                    continue;
                }

                if (!await UntilAsync(() => !explorer.IsFiltering, s_everydayShown).ConfigureAwait(true)
                    || !explorer.Rows.Any(row => row.Node.Kind == ExplorerNodeKind.File && row.Node.Name.Contains(text, StringComparison.OrdinalIgnoreCase)))
                {
                    Problem($"the filter \"{text}\" didn't show the files it matches");
                }

                await Task.Delay(300).ConfigureAwait(true);
            }

            Step("explorer_search: cleared");
            explorer.Filter = string.Empty;
            await UntilAsync(() => !explorer.IsFiltering, s_everydayShown).ConfigureAwait(true);
            if (explorer.Rows.Count != collapsed)
            {
                Problem("clearing the filter didn't show the tree again");
            }
        }).ConfigureAwait(true);

        await Flow("close_reopen", async () =>
        {
            Step("close_reopen: Close All");
            shell.CloseAllDocuments(exceptActive: false);
            await Task.Delay(100).ConfigureAwait(true);
            foreach (var file in files)
            {
                Step($"close_reopen: {Path.GetFileName(file)}");
                await shell.OpenFileAsync(file).ConfigureAwait(true);
            }

            if (shell.Editors.Count() != files.Count)
            {
                Problem($"{shell.Editors.Count()} files open again, not {files.Count}");
            }
        }).ConfigureAwait(true);

        CodeEditorController? typed = null;
        await Flow("typing", prepare: async () =>
        {
            typed = typedFile is not null && await shell.OpenFileAsync(typedFile).ConfigureAwait(true) is { } document ? await WaitForControllerAsync(document).ConfigureAwait(true) : null;
        }, run: async () =>
        {
            if (typed?.Editor is not { } editor)
            {
                Problem("no file to type in");
                return;
            }

            // A comment in the middle of the file: the language service has the whole file to analyze, and it still builds.
            var length = editor.Document.TextLength;
            var line = editor.Document.GetLineByNumber(Math.Max(1, editor.Document.LineCount / 2));
            editor.TextArea.Caret.Offset = line.EndOffset;
            editor.ScrollToLine(line.LineNumber);
            Step("typing");
            editor.TextArea.PerformTextInput("\n// ");
            const string Sample = "codalus everyday typing ";
            for (var i = 0; i < EverydayKeystrokes; i++)
            {
                editor.TextArea.PerformTextInput(Sample[i % Sample.Length].ToString());
                await Task.Delay(s_typingInterval).ConfigureAwait(true);
            }

            if (editor.Document.TextLength - length < EverydayKeystrokes)
            {
                Problem("the keystrokes didn't reach the file");
            }
        }, cleanup: () =>
        {
            // Back to the file as it was: the build saves changed documents, and closing one would ask about it.
            var undo = typed?.Editor.Document.UndoStack;
            while (undo is { IsOriginalFile: false, CanUndo: true })
            {
                undo.Undo();
            }

            return Task.CompletedTask;
        }).ConfigureAwait(true);

        // A rebuild each time, so the measured one compiles as much as the first.
        await Flow("build", async () =>
        {
            Step("build: rebuild");
            if (!await shell.BuildAsync(BuildTargets.Rebuild, project: null).ConfigureAwait(true))
            {
                Problem("the rebuild failed or didn't run");
            }

            Step("build: Error List");
            shell.Layout.ShowToolWindow(ErrorListViewModel.ToolWindowId, focus: false);
            await Task.Delay(200).ConfigureAwait(true);
            await UntilAsync(() => !shell.ErrorList.IsRefreshing, TimeSpan.FromMinutes(1)).ConfigureAwait(true);

            // The solutions' warnings (unused events and fields in Solution50) fill it.
            if (shell.ErrorList.Rows.Count == 0)
            {
                Problem("the Error List showed none of the build's warnings");
            }
        }).ConfigureAwait(true);

        return flows;
    }

    /// <summary>
    /// <see cref="EverydayFiles"/> C# files of the solution, from projects spread over it (by path) and from different places
    /// in each, as someone working across a solution opens them.
    /// </summary>
    private List<string> EverydayFilesToOpen()
    {
        var obj = $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}";
        var projects = shell.Solutions.Projects.Values
            .OrderBy(p => p.Path, StringComparer.Ordinal)
            .Select(p => p.Items
                .Where(i => i.Kind.Equals("Compile", StringComparison.OrdinalIgnoreCase) && i.Link is null && i.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) && !i.Path.Contains(obj, StringComparison.Ordinal))
                .Select(i => i.Path)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToList())
            .Where(items => items.Count > 0)
            .ToList();
        if (projects.Count == 0)
        {
            return [];
        }

        return [.. Enumerable.Range(0, EverydayFiles)
            .Select(i =>
            {
                var items = projects[i * projects.Count / EverydayFiles];
                return items[i * 7 % items.Count];
            })
            .Distinct(StringComparer.Ordinal)];
    }

    /// <summary>Polls <paramref name="condition"/> on the UI thread until it holds (true) or <paramref name="timeout"/> passes (false).</summary>
    private static async Task<bool> UntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var started = Stopwatch.GetTimestamp();
        while (!condition())
        {
            if (Stopwatch.GetElapsedTime(started) >= timeout)
            {
                return false;
            }

            await Task.Delay(50).ConfigureAwait(true);
        }

        return true;
    }

    private bool IsSelected(string path) => shell.Explorer.Selected?.Node.Path is { } selected && SolutionService.PathComparer.Equals(selected, path);

    private void Problem(string text) => _flowProblems.Add(text);

    private void Note(string text)
    {
        EverydayNotes.Add(text);
        BenchEvents.Emit(BenchEventNames.EverydayNote, [], [("note", text)]);
    }
}
