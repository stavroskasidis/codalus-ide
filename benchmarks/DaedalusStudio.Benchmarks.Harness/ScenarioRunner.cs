using System.Diagnostics;
using System.IO.Compression;
using DaedalusStudio.Benchmarks.Harness.Model;
using DaedalusStudio.Benchmarks.Harness.Platform;
using DaedalusStudio.Diagnostics.BenchEvents;

namespace DaedalusStudio.Benchmarks.Harness;

internal sealed record RunOptions(int? Runs, bool AllowInput, TextWriter Log, bool KeepWork = false);

/// <summary>
/// Runs one scenario against one target. Scenario definitions are documented in benchmarks/README.md;
/// docs/adr/0002-performance-budgets.md explains how they map to the budgets.
/// </summary>
internal sealed class ScenarioRunner(string repoRoot, RunOptions options)
{
    public static readonly string[] Scenarios = ["cold-start", "editor-100k", "open-solution-50", "self-driven", "test-explorer", "packages", "terminal", "binary-size"];

    /// <summary>The tests in samples/Tests5k (FixtureGen <c>tests</c>) as found in source: 20 projects × 250 (a theory is one test until a runner lists its cases).</summary>
    private const int Tests5kLeaves = 20 * 10 * 25;

    private const int TypingKeystrokes = 200;
    private static readonly TimeSpan s_typingInterval = TimeSpan.FromMilliseconds(60);
    private const int ScrollPresses = 150;
    private static readonly TimeSpan s_scrollInterval = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan s_readyTimeout = TimeSpan.FromMinutes(3);

    private readonly string _workRoot = Path.Combine(repoRoot, "benchmarks", ".work");
    private readonly string? _presentMon = OperatingSystem.IsWindows() ? PresentMonSession.Locate(repoRoot) : null;

    private static bool BlackBox => OperatingSystem.IsWindows();

    public static bool Applies(Target target, string scenario) => target.SupportedScenarios?.Contains(scenario) != false && scenario switch
    {
        "self-driven" => target.IsPrototype,
        "test-explorer" or "packages" or "terminal" => target.Name == "shell", // in-app events only; reference IDEs are measured by hand (benchmarks/README.md)
        "open-solution-50" => target.Name != "vscode", // VS Code has no solution explorer without proprietary C# Dev Kit.
        "cold-start" or "editor-100k" => target.IsPrototype || OperatingSystem.IsWindows() || target.Name == "vscode",
        _ => true,
    };

    public async Task<ScenarioResult> RunAsync(Target target, string scenario, CancellationToken cancellationToken)
    {
        var result = new ScenarioResult
        {
            Target = target.Name,
            Scenario = scenario,
            Machine = Machine.Describe(),
            StartedAt = DateTimeOffset.UtcNow,
            Method = scenario == "binary-size" ? "static" : target.Instrumented && !BlackBox ? "instrumented" : target.Instrumented ? "instrumented+black-box" : "black-box",
        };
        if (target.Notes is not null)
        {
            result.Notes.Add(target.Notes);
        }

        switch (scenario)
        {
            case "cold-start":
                await ColdStartAsync(target, result, options.Runs ?? 6, cancellationToken).ConfigureAwait(false);
                break;
            case "editor-100k":
                await Editor100kAsync(target, result, options.Runs ?? 3, cancellationToken).ConfigureAwait(false);
                break;
            case "open-solution-50":
                await OpenSolutionAsync(target, result, options.Runs ?? 3, cancellationToken).ConfigureAwait(false);
                break;
            case "self-driven":
                await SelfDrivenAsync(target, result, options.Runs ?? 3, cancellationToken).ConfigureAwait(false);
                break;
            case "test-explorer":
                await TestExplorerAsync(target, result, options.Runs ?? 3, cancellationToken).ConfigureAwait(false);
                break;
            case "packages":
                await PackagesAsync(target, result, options.Runs ?? 3, cancellationToken).ConfigureAwait(false);
                break;
            case "terminal":
                await TerminalAsync(target, result, options.Runs ?? 3, cancellationToken).ConfigureAwait(false);
                break;
            case "binary-size":
                BinarySize(target, result);
                break;
            default:
                throw new ArgumentException($"Unknown scenario '{scenario}'.", nameof(scenario));
        }

        return result.Summarize();
    }

    // ------------------------------------------------------------------ cold start + idle memory

    private async Task ColdStartAsync(Target target, ScenarioResult result, int runs, CancellationToken cancellationToken)
    {
        result.Notes.Add("Run 0 is the first launch of the session (reported separately as *_first); distributions use runs 1..N-1. "
                         + "OS file caches are not flushed, so runs 1..N-1 are warm starts.");
        for (var run = 0; run < runs; run++)
        {
            await using var session = await LaunchAsync(target, new LaunchRequest(NewWorkDir()), cancellationToken).ConfigureAwait(false);
            var suffix = run == 0 ? "_first" : string.Empty;

            if (target.Instrumented)
            {
                var interactive = await BenchEventLog.WaitForAsync(session.EventsFile, BenchEventNames.Interactive, s_readyTimeout, session.Process, cancellationToken).ConfigureAwait(false);
                if (interactive is not null)
                {
                    result.Add("startup_interactive_ms" + suffix, (interactive.WallMicros - session.LaunchWallMicros) / 1000.0);
                }
            }

            if (BlackBox && await WindowProbe.WaitUntilReadyAsync(session.Process.Id, session.LaunchTimestamp, s_readyTimeout, target.IsMainWindow, cancellationToken).ConfigureAwait(false) is { } ready)
            {
                result.Add("startup_ready_ms" + suffix, ready.Ready.TotalMilliseconds);
            }

            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            var memory = ProcessTree.Memory(session.Process.Id);
            result.Add("memory_idle_private_mb" + suffix, memory.PrivateBytes / 1048576.0);
            result.Add("memory_idle_ws_mb" + suffix, memory.WorkingSetBytes / 1048576.0);
            result.Add("memory_idle_shell_private_mb" + suffix, memory.RootPrivateBytes / 1048576.0);
            result.Add("process_count" + suffix, memory.ProcessCount);
            Log($"  {target.Name} cold-start run {run}: {Last(result, "startup_interactive_ms" + suffix)} / {Last(result, "startup_ready_ms" + suffix)} ms, {memory.PrivateBytes / 1048576.0:F0} MB private");
        }

        if (target.Name == "vscode")
        {
            await VsCodeSelfReportedAsync(target, result, runs, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// VS Code's own startup measurement (--prof-append-timers): main-process start to workbench ready.
    /// Available on every OS, so CI gets a VS Code number without window probes.
    /// </summary>
    private async Task VsCodeSelfReportedAsync(Target target, ScenarioResult result, int runs, CancellationToken cancellationToken)
    {
        result.Notes.Add("startup_selfreported_ms: VS Code's own --prof-append-timers measurement (process start to workbench restored).");
        for (var run = 0; run < runs; run++)
        {
            var work = NewWorkDir();
            var timers = Path.Combine(work, "timers.txt");
            await using var session = await LaunchAsync(target, new LaunchRequest(work, ExtraArgs: ["--prof-append-timers", timers]), cancellationToken).ConfigureAwait(false);
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(90);
            while (DateTime.UtcNow < deadline && !File.Exists(timers))
            {
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }

            await Task.Delay(300, cancellationToken).ConfigureAwait(false);
            var line = File.Exists(timers) ? File.ReadLines(timers).LastOrDefault() : null;
            if (line is not null && double.TryParse(line.Split('\t')[0], System.Globalization.CultureInfo.InvariantCulture, out var ms))
            {
                result.Add(run == 0 ? "startup_selfreported_ms_first" : "startup_selfreported_ms", ms);
            }
        }
    }

    // ------------------------------------------------------------------ 100k-line file: open, memory, typing, scrolling

    private async Task Editor100kAsync(Target target, ScenarioResult result, int runs, CancellationToken cancellationToken)
    {
        var source = Path.Combine(repoRoot, "samples", "Large100k");
        RequireFixture(source);
        var inject = BlackBox && options.AllowInput;
        if (!inject)
        {
            result.Notes.Add("Input injection not allowed (--allow-input) or not supported on this OS: typing/scrolling not measured.");
        }

        for (var run = 0; run < runs; run++)
        {
            var work = NewWorkDir();
            var copy = CopyDirectory(source, Path.Combine(work, "Large100k"));
            var file = Path.Combine(copy, "Large.cs");
            await using var session = await LaunchAsync(target, new LaunchRequest(work, File: file), cancellationToken).ConfigureAwait(false);

            if (target.Instrumented && await BenchEventLog.WaitForAsync(session.EventsFile, BenchEventNames.HighlightReady, s_readyTimeout, session.Process, cancellationToken).ConfigureAwait(false) is { } highlight)
            {
                result.Add("file_highlight_ms", (highlight.WallMicros - session.LaunchWallMicros) / 1000.0);
            }

            nint window = 0;
            if (BlackBox && await WindowProbe.WaitUntilReadyAsync(session.Process.Id, session.LaunchTimestamp, s_readyTimeout, target.IsMainWindow, cancellationToken).ConfigureAwait(false) is { } ready)
            {
                result.Add("file_ready_ms", ready.Ready.TotalMilliseconds);
                window = ready.Window;
            }

            await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false); // Let IDEs finish background analysis.
            var memory = ProcessTree.Memory(session.Process.Id);
            result.Add("memory_file_private_mb", memory.PrivateBytes / 1048576.0);
            result.Add("memory_file_ws_mb", memory.WorkingSetBytes / 1048576.0);
            result.Add("memory_file_shell_private_mb", memory.RootPrivateBytes / 1048576.0);

            if (inject && window != 0)
            {
                if (!await TypeAndScrollAsync(target, session, window, result, cancellationToken).ConfigureAwait(false))
                {
                    result.DiscardedRuns++;
                    Log($"  {target.Name} editor-100k run {run}: discarded (lost foreground)");
                    continue;
                }
            }

            Log($"  {target.Name} editor-100k run {run}: ready {Last(result, "file_ready_ms")} ms, typing p50 {P50(result, "typing_input_to_display_ms")} ms, {memory.PrivateBytes / 1048576.0:F0} MB");
        }
    }

    private async Task<bool> TypeAndScrollAsync(Target target, Session session, nint window, ScenarioResult result, CancellationToken cancellationToken)
    {
        var foreground = false;
        for (var attempt = 0; attempt < 5 && !foreground; attempt++)
        {
            foreground = WindowProbe.BringToForeground(window);
        }

        if (!foreground)
        {
            return false;
        }

        await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        using var frames = DesktopFrameMonitor.Start(window);
        var driver = new InputDriver(window);
        var eventsBefore = target.Instrumented ? BenchEventLog.Read(session.EventsFile).Count : 0;

        // Go to the end of the file and start a new line, so every target types in the same place.
        driver.Chord(InputDriver.VkControl, InputDriver.VkEnd);
        await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        driver.Press(InputDriver.VkReturn);
        await Task.Delay(1000, cancellationToken).ConfigureAwait(false);

        var typingStart = driver.SentAtQpc.Count;
        const string Sample = "daedalus typing latency probe ";
        for (var i = 0; i < TypingKeystrokes && !driver.Aborted; i++)
        {
            driver.Type(Sample[i % Sample.Length]);
            await Task.Delay(s_typingInterval, cancellationToken).ConfigureAwait(false);
        }

        var typingInputs = driver.SentAtQpc.Skip(typingStart).ToList();
        await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
        if (options.KeepWork)
        {
            SaveWindowCapture(window, Path.Combine(session.WorkDir, "after-typing.png"));
        }

        driver.Chord(InputDriver.VkControl, InputDriver.VkHome);
        await Task.Delay(1000, cancellationToken).ConfigureAwait(false);

        var scrollStart = driver.SentAtQpc.Count;
        for (var i = 0; i < ScrollPresses && !driver.Aborted; i++)
        {
            driver.Press(InputDriver.VkPageDown);
            await Task.Delay(s_scrollInterval, cancellationToken).ConfigureAwait(false);
        }

        var scrollInputs = driver.SentAtQpc.Skip(scrollStart).ToList();
        await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
        var displayed = frames.Stop();
        Log($"    keys sent {driver.SentAtQpc.Count}, frames touching the window {displayed.Count}, aborted: {driver.Aborted}");
        if (driver.FocusThieves.Count > 0)
        {
            result.Notes.Add($"focus was taken by {string.Join(", ", driver.FocusThieves.Distinct())} and regained during a run");
        }

        if (driver.Aborted)
        {
            return false;
        }

        result.AddRange("typing_input_to_display_ms", DesktopFrameMonitor.InputToDisplay(typingInputs, displayed));
        var scroll = DesktopFrameMonitor.InputToDisplay(scrollInputs, displayed);
        result.AddRange("scroll_input_to_display_ms", scroll);
        result.Add("scroll_responses_over_50ms", scroll.Count(ms => ms > 50));
        result.Add("scroll_responses_missing", scrollInputs.Count - scroll.Count);

        if (target.Instrumented)
        {
            result.AddRange("typing_in_app_ms", BenchEventLog.Read(session.EventsFile).Skip(eventsBefore)
                .Where(e => e.Name == BenchEventNames.KeyLatency && e.Field("synthetic") == 0)
                .Select(e => e.Field("latency_us") / 1000.0));
        }

        return true;
    }

    private static void SaveWindowCapture(nint window, string path)
    {
        if (!Win32.GetWindowRect(window, out var rect))
        {
            return;
        }

        using var bitmap = new System.Drawing.Bitmap(rect.Width, rect.Height);
        using (var g = System.Drawing.Graphics.FromImage(bitmap))
        {
            var hdc = g.GetHdc();
            Win32.PrintWindowFull(window, hdc);
            g.ReleaseHdc(hdc);
        }

        bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }
    // ------------------------------------------------------------------ 50-project solution

    private async Task OpenSolutionAsync(Target target, ScenarioResult result, int runs, CancellationToken cancellationToken)
    {
        var source = Path.Combine(repoRoot, "samples", "Solution50");
        RequireFixture(source);
        result.Notes.Add("The fixture copy is restored (dotnet restore) before each launch; restore time is not measured.");
        for (var run = 0; run < runs; run++)
        {
            var work = NewWorkDir();
            var copy = CopyDirectory(source, Path.Combine(work, "Solution50"));
            var solution = Path.Combine(copy, "Solution50.sln");
            Machine.Run("dotnet", $"restore \"{solution}\" -v q");
            await using var session = await LaunchAsync(target, new LaunchRequest(work, Solution: solution), cancellationToken).ConfigureAwait(false);

            if (target.Name == "shell" && await BenchEventLog.WaitForAsync(session.EventsFile, BenchEventNames.SolutionStructure, s_readyTimeout, session.Process, cancellationToken).ConfigureAwait(false) is { } structure)
            {
                result.Add("solution_structure_ms", (structure.WallMicros - session.LaunchWallMicros) / 1000.0);
            }

            if (target.Instrumented && await BenchEventLog.WaitForAsync(session.EventsFile, BenchEventNames.SolutionLoaded, s_readyTimeout, session.Process, cancellationToken).ConfigureAwait(false) is { } loaded)
            {
                result.Add("solution_loaded_ms", (loaded.WallMicros - session.LaunchWallMicros) / 1000.0);
            }

            if (BlackBox && await WindowProbe.WaitUntilReadyAsync(session.Process.Id, session.LaunchTimestamp, s_readyTimeout, target.IsMainWindow, cancellationToken).ConfigureAwait(false) is { } ready)
            {
                result.Add("solution_ready_ms", ready.Ready.TotalMilliseconds);
            }

            // Language services ready: every project's design-time build loaded and compiled once (informational, ADR 0005).
            if (target.Name == "shell" && await BenchEventLog.WaitForAsync(session.EventsFile, BenchEventNames.LanguageReady, s_readyTimeout, session.Process, cancellationToken).ConfigureAwait(false) is { } language)
            {
                result.Add("language_ready_ms", (language.WallMicros - session.LaunchWallMicros) / 1000.0);
            }

            await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
            var memory = ProcessTree.Memory(session.Process.Id);
            result.Add("memory_solution_private_mb", memory.PrivateBytes / 1048576.0);
            Log($"  {target.Name} open-solution-50 run {run}: loaded {Last(result, "solution_loaded_ms")} / ready {Last(result, "solution_ready_ms")} / language {Last(result, "language_ready_ms")} ms");
        }
    }

    // ------------------------------------------------------------------ Test Explorer (M8)

    private async Task TestExplorerAsync(Target target, ScenarioResult result, int runs, CancellationToken cancellationToken)
    {
        var source = Path.Combine(repoRoot, "samples", "Tests5k");
        RequireFixture(source);
        result.Notes.Add("In-app: test_tree_ms is from the solution's structure to the Test Explorer listing every test from source "
                         + "(before design-time builds and builds); filter frames are self-driven; test_first_result_ms includes the build.");
        for (var run = 0; run < runs; run++)
        {
            var work = NewWorkDir();
            var copy = CopyDirectory(source, Path.Combine(work, "Tests5k"));
            var solution = Path.Combine(copy, "Tests5k.slnx");
            Machine.Run("dotnet", $"restore \"{solution}\" -v q");
            await using var session = await LaunchAsync(target, new LaunchRequest(work, Solution: solution, Bench: "test-filter", ExitOn: "done"), cancellationToken).ConfigureAwait(false);
            try
            {
                await session.Process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromMinutes(8), cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                result.Notes.Add($"run {run}: the scenario did not finish within 8 minutes");
            }

            var events = BenchEventLog.Read(session.EventsFile);
            var structure = events.FirstOrDefault(e => e.Name == BenchEventNames.SolutionStructure);
            var tree = events.FirstOrDefault(e => e.Name == BenchEventNames.TestTree && e.Field("tests") >= Tests5kLeaves);
            if (structure is not null && tree is not null)
            {
                result.Add("test_tree_ms", (tree.WallMicros - structure.WallMicros) / 1000.0);
            }

            var frames = events.Where(e => e.Name == BenchEventNames.TestFilterFrame).Select(e => e.Field("frame_us") / 1000.0).ToList();
            result.AddRange("test_filter_frame_ms", frames);
            if (events.FirstOrDefault(e => e.Name == BenchEventNames.TestFirstResult) is { } first)
            {
                result.Add("test_first_result_ms", first.Field("elapsed_us") / 1000.0);
            }

            Log($"  {target.Name} test-explorer run {run}: tree {Last(result, "test_tree_ms")} ms, filter max {Max(result, "test_filter_frame_ms")} ms, first result {Last(result, "test_first_result_ms")} ms");
        }
    }

    /// <summary>
    /// The NuGet Package Manager (M9) on a copy of samples/Packages and its local feed: search pages from the host's
    /// response to the rendered list, and an update from the click to the project's new compiler inputs, minus restore.
    /// </summary>
    private async Task PackagesAsync(Target target, ScenarioResult result, int runs, CancellationToken cancellationToken)
    {
        var source = Path.Combine(repoRoot, "samples", "Packages");
        RequireFixture(source);
        result.Notes.Add("In-app: package_search_frame_ms is from the host's search response to the rendered page; package_install_overhead_ms is "
                         + "Update → the project's new compiler inputs, minus the restore in between (local feed).");
        for (var run = 0; run < runs; run++)
        {
            var work = NewWorkDir();
            var copy = CopyDirectory(source, Path.Combine(work, "Packages"));
            var solution = Path.Combine(copy, "Packages.slnx");
            Machine.Run("dotnet", $"restore \"{solution}\" -v q");
            await using var session = await LaunchAsync(target, new LaunchRequest(work, Solution: solution, Bench: "packages", ExitOn: "done"), cancellationToken).ConfigureAwait(false);
            try
            {
                await session.Process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromMinutes(8), cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                result.Notes.Add($"run {run}: the scenario did not finish within 8 minutes");
            }

            var events = BenchEventLog.Read(session.EventsFile);
            foreach (var search in events.Where(e => e.Name == BenchEventNames.PackageSearch))
            {
                result.Add("package_search_frame_ms", search.Field("frame_us") / 1000.0);
                result.Add("package_search_ms", search.Field("elapsed_us") / 1000.0);
            }

            foreach (var open in events.Where(e => e.Name == BenchEventNames.PropertiesOpen))
            {
                result.Add("properties_open_ms", open.Field("elapsed_us") / 1000.0);
            }

            if (events.FirstOrDefault(e => e.Name == BenchEventNames.PackageInstall) is { } install)
            {
                result.Add("package_install_ms", install.Field("elapsed_us") / 1000.0);
                result.Add("package_install_overhead_ms", (install.Field("elapsed_us") - install.Field("restore_us")) / 1000.0);
            }

            Log($"  {target.Name} packages run {run}: search frame max {Max(result, "package_search_frame_ms")} ms, install {Last(result, "package_install_ms")} ms (overhead {Last(result, "package_install_overhead_ms")} ms)");
        }
    }

    private async Task TerminalAsync(Target target, ScenarioResult result, int runs, CancellationToken cancellationToken)
    {
        result.Notes.Add("In-app (ADR 0014): terminal_frame_ms are frame intervals while a terminal prints a 1M-line file (cat, or type on "
                         + "Windows); terminal_cat_ms is until the program exited and its output was parsed; terminal_memory_mb is the managed "
                         + "memory the terminal keeps (10k lines of scrollback); terminal_echo_ms is a keystroke written to the default shell "
                         + "until its echo is rendered.");
        for (var run = 0; run < runs; run++)
        {
            await using var session = await LaunchAsync(target, new LaunchRequest(NewWorkDir(), Bench: "terminal", ExitOn: "done"), cancellationToken).ConfigureAwait(false);
            try
            {
                await session.Process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromMinutes(8), cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                result.Notes.Add($"run {run}: the scenario did not finish within 8 minutes");
            }

            var events = BenchEventLog.Read(session.EventsFile);
            foreach (var frame in events.Where(e => e.Name == BenchEventNames.TerminalFrame))
            {
                result.Add("terminal_frame_ms", frame.Field("frame_us") / 1000.0);
            }

            if (events.FirstOrDefault(e => e.Name == BenchEventNames.TerminalCat) is { } cat)
            {
                result.Add("terminal_cat_ms", cat.Field("elapsed_us") / 1000.0);
                result.Add("terminal_cat_lines_per_s", cat.Field("lines") / (cat.Field("elapsed_us") / 1_000_000.0));
                result.Add("terminal_memory_mb", cat.Field("memory_bytes") / (1024.0 * 1024.0));
            }

            foreach (var echo in events.Where(e => e.Name == BenchEventNames.TerminalEcho))
            {
                result.Add("terminal_echo_ms", echo.Field("latency_us") / 1000.0);
            }

            Log($"  {target.Name} terminal run {run}: cat {Last(result, "terminal_cat_ms")} ms, frame max {Max(result, "terminal_frame_ms")} ms, memory {Last(result, "terminal_memory_mb")} MB, echo max {Max(result, "terminal_echo_ms")} ms");
        }
    }

    // ------------------------------------------------------------------ self-driven (prototypes, all OSes incl. CI)

    private async Task SelfDrivenAsync(Target target, ScenarioResult result, int runs, CancellationToken cancellationToken)
    {
        var source = Path.Combine(repoRoot, "samples", "Large100k");
        RequireFixture(source);
        result.Notes.Add("In-app measurements: the prototype drives the scenario itself and reports input→rendered and frame times. "
                         + "Comparable between prototypes, not with black-box numbers.");
        for (var run = 0; run < runs; run++)
        {
            // Instrumented cold start (works without window probes, e.g. on CI).
            await using (var start = await LaunchAsync(target, new LaunchRequest(NewWorkDir(), ExitOn: "interactive"), cancellationToken).ConfigureAwait(false))
            {
                if (await BenchEventLog.WaitForAsync(start.EventsFile, BenchEventNames.Interactive, s_readyTimeout, start.Process, cancellationToken).ConfigureAwait(false) is { } interactive)
                {
                    result.Add("startup_interactive_ms", (interactive.WallMicros - start.LaunchWallMicros) / 1000.0);
                }

                try
                {
                    await start.Process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    result.Notes.Add($"run {run}: --exit-on interactive did not exit within 30 s");
                }
            }

            foreach (var bench in new[] { "typing", "scroll" })
            {
                var work = NewWorkDir();
                var file = Path.Combine(CopyDirectory(source, Path.Combine(work, "Large100k")), "Large.cs");
                await using var session = await LaunchAsync(target, new LaunchRequest(work, File: file, Bench: bench, ExitOn: "done"), cancellationToken).ConfigureAwait(false);
                if (BlackBox && options.AllowInput)
                {
                    // Unattended machine: keep the prototype in the foreground (an occluded or background window
                    // may be frame-throttled by the compositor). No input is sent.
                    for (var i = 0; i < 200 && WindowProbe.FindMainWindow(session.Process.Id) == 0; i++)
                    {
                        await Task.Delay(25, cancellationToken).ConfigureAwait(false);
                    }

                    if (WindowProbe.FindMainWindow(session.Process.Id) is var hwnd and not 0 && !WindowProbe.BringToForeground(hwnd))
                    {
                        result.Notes.Add($"run {run} {bench}: could not bring the window to the foreground");
                    }
                }

                if (bench == "typing" && await BenchEventLog.WaitForAsync(session.EventsFile, BenchEventNames.HighlightReady, s_readyTimeout, session.Process, cancellationToken).ConfigureAwait(false) is { } highlight)
                {
                    result.Add("file_highlight_ms", (highlight.WallMicros - session.LaunchWallMicros) / 1000.0);
                    await Task.Delay(2000, cancellationToken).ConfigureAwait(false);
                    var memory = ProcessTree.Memory(session.Process.Id);
                    result.Add("memory_file_private_mb", memory.PrivateBytes / 1048576.0);
                }

                try
                {
                    await session.Process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromMinutes(3), cancellationToken).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    result.Notes.Add($"run {run} {bench}: scenario did not finish within 3 minutes");
                }

                var events = BenchEventLog.Read(session.EventsFile);
                if (bench == "typing")
                {
                    result.AddRange("selfdriven_typing_ms", events.Where(e => e.Name == BenchEventNames.KeyLatency && e.Field("synthetic") == 1).Select(e => e.Field("latency_us") / 1000.0));
                }
                else
                {
                    var frames = events.Where(e => e.Name == BenchEventNames.ScrollFrame).Select(e => e.Field("frame_us") / 1000.0).ToList();
                    result.AddRange("selfdriven_scroll_frame_ms", frames);
                    result.Add("selfdriven_scroll_frames_over_50ms", frames.Count(f => f > 50));
                    result.AddRange("selfdriven_jump_ms", events.Where(e => e.Name == "jump_frame").Select(e => e.Field("frame_us") / 1000.0));
                }
            }

            Log($"  {target.Name} self-driven run {run}: typing p99 {Pct(result, "selfdriven_typing_ms", 0.99)} ms, scroll max {Max(result, "selfdriven_scroll_frame_ms")} ms");
        }
    }

    // ------------------------------------------------------------------ binary size

    private void BinarySize(Target target, ScenarioResult result)
    {
        if (target.InstallDirectory is not { } dir || !Directory.Exists(dir))
        {
            result.Notes.Add("Install directory not found.");
            return;
        }

        if (target.IsPrototype)
        {
            var helpers = (target.HelperDirectories ?? []).Select(h => Path.Combine(dir, h)).ToList();
            result.Add("shell_mb", DirectorySize(dir, exclude: helpers) / 1048576.0);
            result.Add("shell_zip_mb", ZipSize(dir, exclude: helpers) / 1048576.0);
            foreach (var helper in helpers.Where(Directory.Exists))
            {
                result.Add($"{Path.GetFileName(helper)}_mb", DirectorySize(helper, exclude: []) / 1048576.0);
            }

            result.Notes.Add("Debug symbols (*.pdb) excluded. Helper processes (language service host, plugin host) need the .NET 10 runtime.");
        }
        else
        {
            result.Add("install_mb", DirectorySize(dir, exclude: []) / 1048576.0);
            result.Notes.Add("Informative: whole install directory, including bundled runtimes and plugins.");
        }
    }

    private static long DirectorySize(string dir, IReadOnlyList<string> exclude) =>
        Files(dir, exclude).Sum(f => new FileInfo(f).Length);

    /// <summary>Shipped files: everything but debug symbols and the <paramref name="exclude"/>d folders.</summary>
    private static IEnumerable<string> Files(string dir, IReadOnlyList<string> exclude) =>
        Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
            .Where(f => !f.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)
                        && !exclude.Any(e => f.StartsWith(e + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)));

    private long ZipSize(string dir, IReadOnlyList<string> exclude)
    {
        var zip = Path.Combine(NewWorkDir(), "shell.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            foreach (var file in Files(dir, exclude))
            {
                archive.CreateEntryFromFile(file, Path.GetRelativePath(dir, file), CompressionLevel.Optimal);
            }
        }

        return new FileInfo(zip).Length;
    }

    // ------------------------------------------------------------------ helpers

    private sealed class Session(Process process, long launchTimestamp, long launchWallMicros, string workDir, string eventsFile, bool keepWork) : IAsyncDisposable
    {
        public Process Process { get; } = process;

        public long LaunchTimestamp { get; } = launchTimestamp;

        public long LaunchWallMicros { get; } = launchWallMicros;

        public string WorkDir { get; } = workDir;

        public string EventsFile { get; } = eventsFile;

        public async ValueTask DisposeAsync()
        {
            if (OperatingSystem.IsWindows() && !Process.HasExited && !WindowProbe.CloseGracefully(Process.Id, TimeSpan.FromSeconds(20)))
            {
                // A modal (e.g. 'save changes?') can block closing; only then fall back to killing.
            }

            ProcessTree.Kill(Process.Id);
            Process.Dispose();
            await Task.Delay(1500).ConfigureAwait(false); // Let file handles close and caches settle between runs.
            if (keepWork)
            {
                return;
            }

            try
            {
                Directory.Delete(WorkDir, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private async Task<Session> LaunchAsync(Target target, LaunchRequest request, CancellationToken cancellationToken)
    {
        var events = Path.Combine(request.WorkDir, "events.jsonl");
        var psi = target.CreateStartInfo(target, request with { EventsFile = events });
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        var wall = BenchEventLog.UnixMicrosNow();
        var timestamp = Stopwatch.GetTimestamp();
        var process = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start {target.Executable}.");
        return new Session(process, timestamp, wall, request.WorkDir, events, options.KeepWork);
    }

    private string NewWorkDir()
    {
        var dir = Path.Combine(_workRoot, Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string CopyDirectory(string source, string destination)
    {
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            if (relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(s => s is "bin" or "obj"))
            {
                continue;
            }

            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }

        return destination;
    }

    private static void RequireFixture(string dir)
    {
        if (!Directory.Exists(dir))
        {
            throw new InvalidOperationException($"Fixture missing: {dir}. Generate it with: dotnet run --project benchmarks/DaedalusStudio.Benchmarks.FixtureGen -- all samples");
        }
    }

    private static double QpcToMs(long qpc) => qpc * 1000.0 / Stopwatch.Frequency;

    private void Log(string line) => options.Log.WriteLine(line);

    private static string Last(ScenarioResult r, string metric) =>
        r.Samples.TryGetValue(metric, out var v) && v.Count > 0 ? v[^1].ToString("F0", System.Globalization.CultureInfo.InvariantCulture) : "-";

    private static string P50(ScenarioResult r, string metric) => Pct(r, metric, 0.5);

    private static string Pct(ScenarioResult r, string metric, double p) =>
        r.Samples.TryGetValue(metric, out var v) && v.Count > 0
            ? Distribution.Percentile([.. v.Order()], p).ToString("F1", System.Globalization.CultureInfo.InvariantCulture)
            : "-";

    private static string Max(ScenarioResult r, string metric) => Pct(r, metric, 1.0);
}
