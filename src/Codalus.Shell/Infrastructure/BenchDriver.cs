using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using Codalus.Diagnostics.BenchEvents;
using Codalus.LanguageServices.Protocol;
using Codalus.Shell.Editor;

namespace Codalus.Shell.Infrastructure;

/// <summary>
/// Benchmark support (<c>--open</c>, <c>--bench typing|scroll|test-filter|packages|terminal</c>, <c>--exit-on highlight|done|language</c>): opens a
/// file, reports when it first shows semantic highlighting, and runs self-driven typing and scrolling with in-app
/// frame probes. These complement the harness's external measurements and are the only source on CI runners.
/// </summary>
internal sealed class BenchDriver(Window window, ShellHost shell)
{
    private const int TypingKeystrokes = 300;
    private const double ScrollPixelsPerSecond = 4000;
    private static readonly TimeSpan s_typingInterval = TimeSpan.FromMilliseconds(40);
    private static readonly TimeSpan s_scrollDuration = TimeSpan.FromSeconds(10);
    private bool _synthesizing;

    public static bool IsRequested(ShellOptions options) =>
        options.OpenFile is not null || options.Bench is not null || options.ExitOn is "language";

    /// <summary>Filters the Test Explorer applies in <c>--bench test-filter</c>: names, fields, negation, back to none.</summary>
    private static readonly string[] s_filters = ["Case0", "Feature03", "outcome:notrun", "Area1 Case1", "-Area2", "project:Tests07", "zzz", string.Empty, "Case", string.Empty];

    public async Task RunAsync()
    {
        if (shell.Options.Bench == "test-filter")
        {
            await TestFilterAsync().ConfigureAwait(true);
            return;
        }

        if (shell.Options.Bench == "packages")
        {
            await PackagesAsync().ConfigureAwait(true);
            return;
        }

        if (shell.Options.Bench == "terminal")
        {
            await TerminalAsync().ConfigureAwait(true);
            return;
        }

        if (shell.Options.ExitOn == "language")
        {
            shell.Languages.StatusChanged += (_, e) =>
            {
                if (e.Status.State == WorkspaceStates.Ready)
                {
                    Exit();
                }
            };
        }

        if (shell.Options.OpenFile is not { } file)
        {
            return;
        }

        var document = await shell.OpenFileAsync(file, at: shell.Options.OpenAt is { } at ? new LanguageServices.Protocol.Position(at.Line, at.Column) : null).ConfigureAwait(true);
        if (document is null)
        {
            return;
        }

        var controller = await WaitForControllerAsync(document).ConfigureAwait(true);
        if (shell.Options.Bench == "completion")
        {
            // While the solution loads and compiles (M34c): no waiting for highlighting.
            await CompletionAsync(controller, file).ConfigureAwait(true);
            BenchEvents.Emit(BenchEventNames.ScenarioDone);
            if (shell.Options.ExitOn == "done")
            {
                Exit();
            }

            return;
        }

        var highlighted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        controller.TokensApplied += (_, _) => highlighted.TrySetResult();
        if (controller.Colorizer.HasTokens)
        {
            highlighted.TrySetResult();
        }
        else if (controller.GrammarScope is not null)
        {
            // TextMate grammars tokenize on a background thread and raise no event: check each frame.
            DispatcherTimer.Run(() =>
            {
                if (controller.GrammarColored())
                {
                    highlighted.TrySetResult();
                }

                return !highlighted.Task.IsCompleted;
            }, TimeSpan.FromMilliseconds(5));
        }

        await highlighted.Task.ConfigureAwait(true);
        await RenderedAsync(window).ConfigureAwait(true);
        BenchEvents.Emit(BenchEventNames.HighlightReady);
        if (shell.Options.ExitOn == "highlight")
        {
            Exit();
            return;
        }

        // Real keyboard input is always probed, so external drivers (the harness) also get in-app numbers.
        controller.Editor.TextArea.TextEntering += (_, _) =>
        {
            if (!_synthesizing)
            {
                ProbeInputToRender(Stopwatch.GetTimestamp(), synthetic: false);
            }
        };

        switch (shell.Options.Bench)
        {
            case "typing":
                await TypeAsync(controller).ConfigureAwait(true);
                break;
            case "scroll":
                await ScrollAsync(controller).ConfigureAwait(true);
                break;
            default:
                return;
        }

        BenchEvents.Emit(BenchEventNames.ScenarioDone);
        if (shell.Options.ExitOn == "done")
        {
            Exit();
        }
    }

    /// <summary>
    /// <c>--bench test-filter</c> (with a solution): waits until the Test Explorer lists every test of the solution from
    /// source, applies filters and reports how long each took to render, then runs one class and reports when its first
    /// result arrived (that includes its build).
    /// </summary>
    private async Task TestFilterAsync()
    {
        shell.Layout.ShowToolWindow(ViewModels.TestExplorerViewModel.ToolWindowId);
        var explorer = shell.TestExplorer;
        var stable = 0;
        var last = -1;
        for (var i = 0; i < 2400 && stable < 20; i++)
        {
            await Task.Delay(50).ConfigureAwait(true);
            var count = shell.Tests.Read(roots => roots.Sum(r => r.Leaves().Count()));
            stable = count > 0 && count == last ? stable + 1 : 0;
            last = count;
        }

        explorer.Grouping = Workbench.Testing.TestGrouping.Hierarchy;
        foreach (var filter in s_filters)
        {
            var started = Stopwatch.GetTimestamp();
            explorer.FilterText = filter;
            explorer.ApplyFilter();
            await RenderedAsync(window).ConfigureAwait(true);
            BenchEvents.Emit(BenchEventNames.TestFilterFrame, "frame_us", (long)Stopwatch.GetElapsedTime(started).TotalMicroseconds);
            await Task.Delay(100).ConfigureAwait(true);
        }

        var target = shell.Tests.Read(roots => roots.SelectMany(r => r.SelfAndDescendants()).FirstOrDefault(n => n.Children.Count > 0 && n.Children.All(c => c.Range is not null && c.Children.Count == 0)));
        if (target is not null)
        {
            var requested = Stopwatch.GetTimestamp();
            var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnChanged(object? sender, EventArgs e)
            {
                if (shell.Tests.Read(_ => target.Children.Any(c => c.Result is not null)))
                {
                    first.TrySetResult();
                }
            }

            shell.Tests.Changed += OnChanged;
            var run = shell.RunTestsAsync(Workbench.Testing.TestRunKind.Run, [target.Key]);
            if (await Task.WhenAny(first.Task, Task.Delay(TimeSpan.FromMinutes(5))).ConfigureAwait(true) == first.Task)
            {
                BenchEvents.Emit(BenchEventNames.TestFirstResult, "elapsed_us", (long)Stopwatch.GetElapsedTime(requested).TotalMicroseconds);
            }

            shell.Tests.Changed -= OnChanged;
            await run.ConfigureAwait(true);
        }

        BenchEvents.Emit(BenchEventNames.ScenarioDone);
        if (shell.Options.ExitOn == "done")
        {
            Exit();
        }
    }

    /// <summary>Searches of <c>--bench packages</c> on samples/Packages' local feed.</summary>
    private static readonly string[] s_packageQueries = ["Codalus.Sample", "Greeting", "Licensed", "Text", "Codalus.Sample.G", string.Empty];

    /// <summary>
    /// <c>--bench packages</c> (with a copy of samples/Packages): opens the NuGet Package Manager of Plain, measures
    /// search pages (response → rendered) on the local feed, then updates Codalus.Sample.Greeting and measures until the
    /// project's new compiler inputs arrive, and how much of that was restore.
    /// </summary>
    private async Task PackagesAsync()
    {
        for (var i = 0; i < 6000 && !(shell.Solutions.Progress is { Completed: true } && shell.Solutions.RestoreState is "succeeded" or "failed"); i++)
        {
            await Task.Delay(50).ConfigureAwait(true);
        }

        var plain = shell.Solutions.Projects.Values.FirstOrDefault(p => p.Name == "Plain")?.Path;
        if (plain is null || shell.OpenPackageManager(plain) is not { } vm)
        {
            BenchEvents.Emit(BenchEventNames.ScenarioDone);
            Exit();
            return;
        }

        for (var i = 0; i < 600 && !vm.Sources.Contains("local"); i++)
        {
            await Task.Delay(50).ConfigureAwait(true);
        }

        vm.SelectedSource = "local";
        await Task.Delay(500).ConfigureAwait(true);
        foreach (var query in s_packageQueries)
        {
            var loaded = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnPage(object? sender, EventArgs e) => loaded.TrySetResult(Stopwatch.GetTimestamp());
            vm.PageLoaded += OnPage;
            var requested = Stopwatch.GetTimestamp();
            vm.Search = query;
            _ = vm.SearchNowAsync();
            if (await Task.WhenAny(loaded.Task, Task.Delay(TimeSpan.FromSeconds(60))).ConfigureAwait(true) == loaded.Task)
            {
                var arrived = loaded.Task.Result;
                await RenderedAsync(window).ConfigureAwait(true);
                var now = Stopwatch.GetTimestamp();
                BenchEvents.Emit(
                    BenchEventNames.PackageSearch,
                    [("frame_us", (long)Stopwatch.GetElapsedTime(arrived, now).TotalMicroseconds), ("elapsed_us", (long)Stopwatch.GetElapsedTime(requested, now).TotalMicroseconds)]);
            }

            vm.PageLoaded -= OnPage;
            await Task.Delay(200).ConfigureAwait(true);
        }

        // Update Greeting 1.0.0 → latest: until the project's compiler inputs reflect it (what IntelliSense sees).
        vm.Search = "Codalus.Sample.Greeting";
        await vm.SearchNowAsync().ConfigureAwait(true);
        vm.Selected = vm.Rows.FirstOrDefault(r => r.Id == "Codalus.Sample.Greeting");
        for (var i = 0; i < 600 && vm.Details is not { IsLoading: false, CanInstall: true }; i++)
        {
            await Task.Delay(50).ConfigureAwait(true);
        }

        if (vm.Details is { CanInstall: true } details)
        {
            long restoreStarted = 0, restoreEnded = 0;
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnRestore(object? sender, Workbench.Projects.RestoreStateChangedEventArgs e)
            {
                if (e.State == "running")
                {
                    restoreStarted = Stopwatch.GetTimestamp();
                }
                else if (restoreStarted != 0)
                {
                    restoreEnded = Stopwatch.GetTimestamp();
                }
            }

            void OnInputs(object? sender, Projects.Protocol.CompilerInputsInfo inputs)
            {
                if (restoreEnded != 0 && Workbench.Projects.SolutionService.PathComparer.Equals(inputs.Path, plain))
                {
                    done.TrySetResult();
                }
            }

            shell.Solutions.RestoreStateChanged += OnRestore;
            shell.Solutions.CompilerInputsChanged += OnInputs;
            var started = Stopwatch.GetTimestamp();
            details.InstallCommand.Execute(null);
            if (await Task.WhenAny(done.Task, Task.Delay(TimeSpan.FromMinutes(5))).ConfigureAwait(true) == done.Task)
            {
                BenchEvents.Emit(
                    BenchEventNames.PackageInstall,
                    [("elapsed_us", (long)Stopwatch.GetElapsedTime(started).TotalMicroseconds), ("restore_us", (long)Stopwatch.GetElapsedTime(restoreStarted, restoreEnded).TotalMicroseconds)]);
            }

            shell.Solutions.RestoreStateChanged -= OnRestore;
            shell.Solutions.CompilerInputsChanged -= OnInputs;
        }

        // Project properties: from the command to the Application page rendered with its values (three times: cold, warm).
        for (var run = 0; run < 3; run++)
        {
            foreach (var open in shell.Layout.Documents.OfType<ViewModels.ProjectPropertiesDocument>().ToList())
            {
                shell.Layout.CloseDocument(open);
            }

            await RenderedAsync(window).ConfigureAwait(true);
            var started = Stopwatch.GetTimestamp();
            if (shell.OpenProjectProperties(plain) is { } properties)
            {
                for (var i = 0; i < 1200 && !(properties.Sections.Count > 0 && !properties.IsLoading); i++)
                {
                    await Task.Delay(5).ConfigureAwait(true);
                }

                await RenderedAsync(window).ConfigureAwait(true);
                BenchEvents.Emit(BenchEventNames.PropertiesOpen, "elapsed_us", (long)Stopwatch.GetElapsedTime(started).TotalMicroseconds);
            }
        }

        BenchEvents.Emit(BenchEventNames.ScenarioDone);
        if (shell.Options.ExitOn == "done")
        {
            Exit();
        }
    }

    private static void Exit()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            Dispatcher.UIThread.Post(() => desktop.Shutdown(), DispatcherPriority.Background);
        }
    }

    private static async Task<CodeEditorController> WaitForControllerAsync(CodeEditorDocument document)
    {
        for (var i = 0; i < 1200 && document.Controller is null; i++)
        {
            await Task.Delay(50).ConfigureAwait(true);
        }

        return document.Controller ?? throw new InvalidOperationException("The editor did not appear.");
    }

    private async Task TypeAsync(CodeEditorController controller)
    {
        var editor = controller.Editor;
        var document = editor.Document;

        // Type inside the middle of the file, so the classifier has real work to do.
        var line = document.GetLineByNumber(Math.Max(1, document.LineCount / 2));
        editor.TextArea.Caret.Offset = line.EndOffset;
        editor.ScrollToLine(line.LineNumber);
        _synthesizing = true;
        editor.TextArea.PerformTextInput("\n// ");
        _synthesizing = false;
        await Task.Delay(500).ConfigureAwait(true);

        const string Sample = "codalus typing latency probe ";
        for (var i = 0; i < TypingKeystrokes; i++)
        {
            var t0 = Stopwatch.GetTimestamp();
            _synthesizing = true;
            editor.TextArea.PerformTextInput(Sample[i % Sample.Length].ToString());
            _synthesizing = false;
            ProbeInputToRender(t0, synthetic: true);
            await Task.Delay(s_typingInterval).ConfigureAwait(true);
        }

        await Task.Delay(1000).ConfigureAwait(true);
    }

    /// <summary>
    /// <c>--bench completion</c> (M34c, with a solution and <c>--open</c>): from the moment the file opens, typing in it
    /// (<c>key_latency</c>) and, every 250 ms, completion after a member access near its top (<c>completion</c>:
    /// <c>latency_us</c>, <c>items</c>, <c>ready</c> once the C# language service is ready), until the service has been
    /// ready for a few seconds.
    /// </summary>
    private async Task CompletionAsync(CodeEditorController controller, string file)
    {
        var text = controller.Editor.Document.Text;
        var member = System.Text.RegularExpressions.Regex.Match(text, @"\b[A-Za-z_]\w*\.[A-Z]\w*\(");
        var offset = member.Success ? member.Index + member.Value.IndexOf('.', StringComparison.Ordinal) + 1 : 0;
        var location = controller.Editor.Document.GetLocation(offset);
        var position = new Position(location.Line - 1, location.Column - 1);
        var uri = DocumentUris.FromPath(Path.GetFullPath(file));
        var typing = TypeAsync(controller);
        var started = Stopwatch.GetTimestamp();
        long? readyAt = null;
        while (Stopwatch.GetElapsedTime(started) < TimeSpan.FromMinutes(3))
        {
            var ready = shell.Languages.Status("csharp") is { State: WorkspaceStates.Ready };
            readyAt ??= ready ? Stopwatch.GetTimestamp() : null;
            if (readyAt is { } since && Stopwatch.GetElapsedTime(since) > TimeSpan.FromSeconds(5) && typing.IsCompleted)
            {
                break;
            }

            if (shell.Languages.RunningClient("csharp") is { } client)
            {
                var t0 = Stopwatch.GetTimestamp();
                try
                {
                    var list = await client.CompletionAsync(uri, position, new CompletionContext(2, "."), CancellationToken.None).ConfigureAwait(true);
                    BenchEvents.Emit("completion", [("latency_us", (long)Stopwatch.GetElapsedTime(t0).TotalMicroseconds), ("items", list.Items.Length), ("ready", ready ? 1 : 0)]);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    BenchEvents.Emit("completion_failed");
                }
            }

            await Task.Delay(250).ConfigureAwait(true);
        }

        await typing.ConfigureAwait(true);
    }

    private async Task ScrollAsync(CodeEditorController controller)
    {
        var editor = controller.Editor;
        var compositor = Compositor(window);
        var done = new TaskCompletionSource();
        var start = Stopwatch.GetTimestamp();
        long previousRendered = 0;

        void Frame(TimeSpan _)
        {
            var elapsed = Stopwatch.GetElapsedTime(start);
            editor.ScrollToVerticalOffset(elapsed.TotalSeconds * ScrollPixelsPerSecond);
            compositor?.RequestCompositionBatchCommitAsync().Rendered.ContinueWith(_ =>
            {
                var now = Stopwatch.GetTimestamp();
                var previous = Interlocked.Exchange(ref previousRendered, now);
                if (previous != 0)
                {
                    BenchEvents.Emit(BenchEventNames.ScrollFrame,
                    [
                        ("frame_us", (long)Stopwatch.GetElapsedTime(previous, now).TotalMicroseconds),
                        ("gc_pause_us", (long)GC.GetTotalPauseDuration().TotalMicroseconds),
                        ("gen2", GC.CollectionCount(2)),
                    ]);
                }
            }, TaskScheduler.Default);

            if (elapsed < s_scrollDuration)
            {
                window.RequestAnimationFrame(Frame);
            }
            else
            {
                done.SetResult();
            }
        }

        window.RequestAnimationFrame(Frame);
        await done.Task.ConfigureAwait(true);

        // Large jumps (go-to-line style): time from the jump to the frame showing it.
        foreach (var fraction in new[] { 0.25, 0.5, 0.75, 1.0, 0.0 })
        {
            var t0 = Stopwatch.GetTimestamp();
            editor.ScrollToLine(Math.Max(1, (int)(editor.Document.LineCount * fraction)));
            await RenderedAsync(window).ConfigureAwait(true);
            BenchEvents.Emit("jump_frame", "frame_us", (long)Stopwatch.GetElapsedTime(t0).TotalMicroseconds);
            await Task.Delay(200).ConfigureAwait(true);
        }
    }

    private const int TerminalLines = 1_000_000;
    private const int TerminalKeystrokes = 60;

    /// <summary>
    /// <c>--bench terminal</c> (M10, ADR 0014): prints a million-line file in a terminal (<c>cat</c>, or <c>type</c> on
    /// Windows) while timing every frame, reports how long it took and the managed memory the terminal keeps (screen and
    /// scrollback), then types into an interactive shell and times each echo until it is rendered.
    /// </summary>
    private async Task TerminalAsync()
    {
        var file = Path.Combine(Path.GetTempPath(), $"codalus-bench-{Environment.ProcessId}.txt");
        try
        {
            await using (var writer = new StreamWriter(file))
            {
                for (var i = 1; i <= TerminalLines; i++)
                {
                    await writer.WriteLineAsync($"{i:D7} the quick brown fox jumps over the lazy dog \u001b[32mok\u001b[0m").ConfigureAwait(true);
                }
            }

            await RenderedAsync(window).ConfigureAwait(true);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            var before = GC.GetTotalMemory(forceFullCollection: true);
            var (program, arguments) = OperatingSystem.IsWindows() ? ("cmd.exe", new[] { "/c", "type", file }) : ("cat", new[] { file });
            var started = Stopwatch.GetTimestamp();
            var printing = true;
            var last = started;
            void Frame(TimeSpan _)
            {
                var now = Stopwatch.GetTimestamp();
                BenchEvents.Emit(BenchEventNames.TerminalFrame, "frame_us", (long)Stopwatch.GetElapsedTime(last, now).TotalMicroseconds);
                last = now;
                if (printing)
                {
                    window.RequestAnimationFrame(Frame);
                }
            }

            var session = shell.Terminals.Run("bench", program, arguments, Path.GetTempPath(), null);
            shell.ShowTerminal();
            shell.TerminalWindow.Show(session);
            window.RequestAnimationFrame(Frame);
            while (session.ExitCode is null)
            {
                await Task.Delay(20).ConfigureAwait(true);
            }

            await RenderedAsync(window).ConfigureAwait(true);
            printing = false;
            var elapsed = Stopwatch.GetElapsedTime(started);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            var memory = GC.GetTotalMemory(forceFullCollection: true) - before;
            BenchEvents.Emit(BenchEventNames.TerminalCat,
            [
                ("elapsed_us", (long)elapsed.TotalMicroseconds),
                ("lines", TerminalLines),
                ("memory_bytes", memory),
            ]);
            GC.KeepAlive(session);

            await EchoAsync().ConfigureAwait(true);
        }
        finally
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
            }
        }

        BenchEvents.Emit(BenchEventNames.ScenarioDone);
        if (shell.Options.ExitOn == "done")
        {
            Exit();
        }
    }

    /// <summary>Types into the default shell and times each echo, from the write to the rendered frame showing it.</summary>
    private async Task EchoAsync()
    {
        if (shell.ShowTerminal(newTerminal: true) is not { } session)
        {
            return;
        }

        await Task.Delay(2000).ConfigureAwait(true); // the shell's startup and prompt
        int Count() => session.Terminal.ScreenText().Count(c => c == 'x');
        for (var i = 0; i < TerminalKeystrokes; i++)
        {
            var expected = Count() + 1;
            var echoed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnChanged() => Dispatcher.UIThread.Post(() =>
            {
                if (Count() >= expected)
                {
                    echoed.TrySetResult();
                }
            });
            session.Terminal.Changed += OnChanged;
            var t0 = Stopwatch.GetTimestamp();
            session.Write("x");
            if (await Task.WhenAny(echoed.Task, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(true) == echoed.Task)
            {
                await RenderedAsync(window).ConfigureAwait(true);
                BenchEvents.Emit(BenchEventNames.TerminalEcho, "latency_us", (long)Stopwatch.GetElapsedTime(t0).TotalMicroseconds);
            }

            session.Terminal.Changed -= OnChanged;
            await Task.Delay(s_typingInterval).ConfigureAwait(true);
        }

        session.Write("\u0003");
    }

    /// <summary>Emits the time from <paramref name="t0"/> until the next composition batch has been rendered.</summary>
    private void ProbeInputToRender(long t0, bool synthetic)
    {
        window.RequestAnimationFrame(_ =>
        {
            if (Compositor(window) is not { } compositor)
            {
                return;
            }

            compositor.RequestCompositionBatchCommitAsync().Rendered.ContinueWith(_ =>
                BenchEvents.Emit(BenchEventNames.KeyLatency,
                [
                    ("latency_us", (long)Stopwatch.GetElapsedTime(t0).TotalMicroseconds),
                    ("synthetic", synthetic ? 1 : 0),
                ]), TaskScheduler.Default);
        });
    }

    private static Task RenderedAsync(Window window)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.RequestAnimationFrame(_ =>
        {
            if (Compositor(window) is { } compositor)
            {
                compositor.RequestCompositionBatchCommitAsync().Rendered.ContinueWith(_ => tcs.TrySetResult(), TaskScheduler.Default);
            }
            else
            {
                tcs.TrySetResult();
            }
        });
        return tcs.Task;
    }

    private static Compositor? Compositor(Window window) => ElementComposition.GetElementVisual(window)?.Compositor;
}
