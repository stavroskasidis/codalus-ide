using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DaedalusStudio.Debugging;
using DaedalusStudio.Platform;
using DaedalusStudio.Shell.Editor;
using DaedalusStudio.Shell.Theming;
using DaedalusStudio.Shell.ViewModels;
using DaedalusStudio.Shell.Views;
using DaedalusStudio.Workbench.Debugging;
using DaedalusStudio.Workbench.Plugins;
using DaedalusStudio.Workbench.Settings;

namespace DaedalusStudio.Shell.Infrastructure;

/// <summary>
/// The end-to-end smoke test (<c>--smoke-test samples</c>): drives the real, windowed app through one feature of each
/// milestone (M1–M6 on a copy of <c>samples/BuildAndRun</c>, M8 on part of <c>samples/Testing</c>), with the hosts,
/// plugins and debugger it was installed with. It is what CI runs on the published Native AOT build and on the release packages of every OS, so it catches what
/// the in-process headless tests cannot: trimming and AOT breaks, the installed folder layout, and the real windowing
/// platform. Results go to <c>smoke-test.json</c>, <c>smoke-test.md</c> and screenshots in the logs folder, and to
/// standard output; the process exits with 0 when every step passed.
/// </summary>
internal sealed class SmokeTest(Window window, ShellHost shell, string samples)
{
    private const string Branch = "main";
    private static readonly TimeSpan s_watchdog = TimeSpan.FromMinutes(25);
    private static readonly string[] s_commands = ["workbench.showCommandPalette", "build.buildSolution", "debug.start", "git.commit"];
    private static readonly string[] s_themes = ["light", "highContrast", "dark"];

    private readonly List<StepResult> _results = [];
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private string _work = string.Empty;
    private string _folder = string.Empty;
    private string _testing = string.Empty;

    private sealed record StepResult(string Id, string Milestone, string Title, string Outcome, double Seconds, string? Error);

    /// <summary>A step that did not get the state it waited for.</summary>
    private sealed class SmokeFailure(string message) : Exception(message);

    private string Greeter => Path.Combine(_folder, "src", "Greetings", "Greeter.cs");

    private string Program => Path.Combine(_folder, "src", "Hello", "Program.cs");

    public async Task RunAsync()
    {
        DispatcherTimer.RunOnce(() =>
        {
            Report("watchdog", "-", $"The smoke test did not finish within {s_watchdog.TotalMinutes} minutes.", "failed", 0, null);
            WriteReport();
            Environment.Exit(2);
        }, s_watchdog);

        var exitCode = 1;
        try
        {
            await RunStepsAsync().ConfigureAwait(true);
            exitCode = _results.All(r => r.Outcome == "passed") ? 0 : 1;
        }
        catch (Exception ex)
        {
            Report("driver", "-", "Smoke test driver", "failed", 0, ex.ToString());
        }

        try
        {
            await shell.SaveAllAsync().ConfigureAwait(true);
            Screenshot("smoke-test.png");
        }
        catch (Exception ex)
        {
            shell.Workbench.Log.AppendLine($"[smoke] Could not save the final state: {ex.Message}");
        }

        WriteReport();
        Console.Out.WriteLine(exitCode == 0 ? "SMOKE TEST PASSED" : "SMOKE TEST FAILED");
        Console.Out.Flush();
        await DeleteFixtureAsync().ConfigureAwait(true);
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            Dispatcher.UIThread.Post(() => desktop.Shutdown(exitCode), DispatcherPriority.Background);
        }
    }

    /// <summary>Closes the solution (its hosts and programs hold the folder) and deletes the fixture, retrying briefly.</summary>
    private async Task DeleteFixtureAsync()
    {
        if (_work.Length == 0 || !Directory.Exists(_work))
        {
            return;
        }

        try
        {
            shell.StopRunning();
            await shell.CloseSolutionAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            shell.Workbench.Log.AppendLine($"[smoke] Could not close the solution: {ex.Message}");
        }

        for (var attempt = 0; attempt < 20 && Directory.Exists(_work); attempt++)
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(_work, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal); // git writes its object files read-only
                }

                Directory.Delete(_work, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(500).ConfigureAwait(true);
            }
        }
    }

    private async Task RunStepsAsync()
    {
        Console.Out.WriteLine($"Smoke test: {ShellInfo.VersionText.Replace('\n', ' ')}");

        // M1: shell and plugin foundation.
        await StepAsync("shell.window", "M1", "Main window, status bar and default commands", () =>
        {
            Check(window.IsVisible && window.Bounds.Width > 0, "the main window is visible");
            Check(shell.StatusBar.Right.Any(i => i.Item.Id == "daedalus.notifications"), "the notifications status item");
            Check(shell.StatusBar.Right.Any(i => i.Item.Id == "daedalus.theme"), "the theme status item");
            foreach (var id in s_commands)
            {
                Check(shell.Workbench.Commands.Find(id) is not null, $"the command {id}");
            }

            Check(shell.Workbench.Keybindings.GetBinding("workbench.showCommandPalette") is not null, "a key binding for the command palette");
            return Task.CompletedTask;
        }).ConfigureAwait(true);

        await StepAsync("shell.palette", "M1", "Command palette filters and runs a command", async () =>
        {
            var executed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            shell.Workbench.Commands.Executed += (_, e) =>
            {
                if (e.CommandId == "workbench.resetLayout")
                {
                    executed.TrySetResult();
                }
            };
            shell.ShowCommandPalette();
            await WaitAsync(() => shell.Palette.IsOpen, "the command palette opens").ConfigureAwait(true);
            shell.Palette.Query = "reset layout";
            await WaitAsync(() => shell.Palette.Rows.Any(r => r.Label == "View: Reset Layout"), "the palette finds View: Reset Layout").ConfigureAwait(true);
            shell.Palette.Accept(shell.Palette.Rows.First(r => r.Label == "View: Reset Layout"));
            await WaitAsync(() => executed.Task.IsCompleted && !shell.Palette.IsOpen, "the picked command runs").ConfigureAwait(true);
        }).ConfigureAwait(true);

        await StepAsync("shell.settings", "M1", "Settings persist to settings.json and apply live", async () =>
        {
            await shell.Workbench.Settings.SetAsync("workbench.statusBar.visible", false, SettingScope.User, CancellationToken.None).ConfigureAwait(true);
            await WaitAsync(() => !shell.StatusBar.IsVisible, "the status bar hides").ConfigureAwait(true);
            Check(File.ReadAllText(shell.Paths.SettingsFile).Contains("\"workbench.statusBar.visible\": false", StringComparison.Ordinal), "settings.json has the value");
            await shell.Workbench.Settings.SetAsync("workbench.statusBar.visible", (JsonElement?)null, SettingScope.User, CancellationToken.None).ConfigureAwait(true);
            await WaitAsync(() => shell.StatusBar.IsVisible, "the status bar shows again").ConfigureAwait(true);
        }).ConfigureAwait(true);

        await StepAsync("shell.themes", "M1", "Themes switch live", async () =>
        {
            foreach (var theme in s_themes)
            {
                await shell.Workbench.Settings.SetAsync(ThemeService.SettingKey, theme, SettingScope.User, CancellationToken.None).ConfigureAwait(true);
                await WaitAsync(() => shell.Theme.ActiveTheme == theme && Equals(shell.Workbench.Context.GetValue("theme"), theme), $"the {theme} theme applies").ConfigureAwait(true);
                await RenderedAsync().ConfigureAwait(true);
            }
        }).ConfigureAwait(true);

        await StepAsync("shell.output", "M1", "Output window and notifications", async () =>
        {
            shell.Workbench.Log.AppendLine("smoke test marker");
            shell.ShowLogs();
            await WaitAsync(() => window.GetVisualDescendants().OfType<OutputView>().Any(v => v.Text.Contains("smoke test marker", StringComparison.Ordinal)), "the Output window shows the log").ConfigureAwait(true);
            shell.Workbench.Notifications.Info("Smoke test notification");
            await WaitAsync(() => shell.Notifications.Count > 0, "the notification arrives").ConfigureAwait(true);
            shell.Run("workbench.clearNotifications");
            await WaitAsync(() => shell.Notifications.Count == 0, "notifications clear").ConfigureAwait(true);
        }).ConfigureAwait(true);

        await StepAsync("plugins.host", "M1", "Built-in plugins load and run in the plugin host", async () =>
        {
            var builtIn = shell.Plugins.Plugins.Where(p => p.IsBuiltIn).ToList();
            Check(builtIn.Any(p => p.Id == "daedalus.welcome") && builtIn.Any(p => p.Id == "daedalus.templates"), $"the built-in plugins are discovered (found: {string.Join(", ", builtIn.Select(p => p.Id))})");
            var broken = builtIn.Where(p => p.State is PluginState.Invalid or PluginState.Incompatible or PluginState.Failed).ToList();
            Check(broken.Count == 0, $"no built-in plugin is broken ({string.Join(", ", broken.Select(p => $"{p.Id}: {p.State}"))})");

            // The Start page is a plugin tree view: its items come from the out-of-process plugin host. A plugin command
            // shows it (the layout may have been reset since startup).
            await WaitAsync(() => shell.Plugins.Plugins.Any(p => p is { Id: "daedalus.welcome", State: PluginState.Active }), "the Welcome plugin activates at startup", 90).ConfigureAwait(true);
            await shell.RunAsync("welcome.show", []).ConfigureAwait(true);
            await WaitAsync(() => shell.Layout.IsVisible("welcome.view"), "welcome.show shows the Start page", 60).ConfigureAwait(true);
            var tree = (TreeToolViewModel)shell.Layout.OpenToolWindows.Single(t => t.Id == "welcome.view").Content!;
            await WaitAsync(() => tree.Roots.Count > 0, "the Start page has items").ConfigureAwait(true);
        }).ConfigureAwait(true);

        // M2: project system (build host, MSBuild evaluation, Solution Explorer).
        await StepAsync("projects.open", "M2", "A trusted solution opens and evaluates", async () =>
        {
            PrepareFixture();
            shell.Trust.Trust(_work);
            await shell.OpenSolutionAsync(Path.Combine(_folder, "BuildAndRun.slnx")).ConfigureAwait(true);
            await WaitAsync(() => shell.Solutions.Projects.Count == 2 && shell.Solutions.Progress is { Completed: true }, "both projects are evaluated", 240).ConfigureAwait(true);
            Check(shell.Runs.StartupProject?.EndsWith("Hello.csproj", StringComparison.Ordinal) == true, $"Hello is the startup project (got {shell.Runs.StartupProject})");
            await WaitAsync(() => shell.Explorer.Rows.Any(r => r is { Name: "Hello", IsStartup: true }) && shell.Explorer.Rows.Any(r => r.Name == "Greetings"), "the Solution Explorer shows the projects").ConfigureAwait(true);
        }).ConfigureAwait(true);

        // M3: editor and C# language services.
        await StepAsync("language.ready", "M3", "The C# language service loads the projects", () =>
            WaitAsync(() => shell.Languages.Status(ShellHost.CSharpLanguageId) is { State: "ready", Projects: 2 }, "the C# workspace is ready with 2 projects", 360),
            "projects.open").ConfigureAwait(true);

        await StepAsync("editor.highlighting", "M3", "Semantic highlighting and folding", async () =>
        {
            var greeter = await OpenAsync(Greeter).ConfigureAwait(true);
            await WaitAsync(() => greeter.Colorizer.HasTokens, "semantic tokens arrive", 90).ConfigureAwait(true);
            Check(shell.StatusBar.Right.Any(i => i.Item.Id == "daedalus.editor.language" && i.Text == "C#"), "the status bar shows C#");
        }, "language.ready").ConfigureAwait(true);

        await StepAsync("editor.diagnostics", "M3", "Live diagnostics reach the Error List", async () =>
        {
            var program = await OpenAsync(Program).ConfigureAwait(true);
            var original = program.Model.Text.Text;
            MoveCaret(program, "Console.WriteLine($\"args");
            program.Editor.TextArea.PerformTextInput("int broken = \"x\"; ");
            await WaitAsync(() => shell.ErrorList.Rows.Any(r => r.Code == "CS0029"), "CS0029 shows in the Error List", 120).ConfigureAwait(true);
            Restore(program, original);
            await WaitAsync(() => shell.ErrorList.Rows.All(r => r.Code != "CS0029"), "the error goes away after undo", 120).ConfigureAwait(true);
        }, "language.ready").ConfigureAwait(true);

        await StepAsync("editor.completion", "M3", "Member completion", async () =>
        {
            var program = await OpenAsync(Program).ConfigureAwait(true);
            var original = program.Model.Text.Text;
            MoveCaret(program, "Greeter.Greet(", "Greeter".Length);
            program.Model.Text.Remove(program.Editor.CaretOffset, ".Greet".Length);
            program.Editor.TextArea.PerformTextInput(".");
            try
            {
                await WaitAsync(() => program.CompletionWindow is { } w && w.CompletionList.CompletionData.Any(d => d.Text == "Greet"), "completion lists Greeter.Greet", 90).ConfigureAwait(true);
            }
            catch (SmokeFailure)
            {
                // A cold first request can come back empty (the completion providers are loading); ask once more.
                shell.Run("editor.action.triggerSuggest");
                await WaitAsync(() => program.CompletionWindow is { } w && w.CompletionList.CompletionData.Any(d => d.Text == "Greet"), "completion lists Greeter.Greet (retried)", 90).ConfigureAwait(true);
            }

            program.CompletionWindow?.Close();
            Restore(program, original);
        }, "language.ready").ConfigureAwait(true);

        await StepAsync("editor.navigation", "M3", "Go to Definition across projects and Find All References", async () =>
        {
            var program = await OpenAsync(Program).ConfigureAwait(true);
            MoveCaret(program, "Greeter.Greet(", "Greeter.Gr".Length);
            shell.Run("editor.action.revealDefinition");
            await WaitAsync(() => shell.ActiveEditor is { Model.Name: "Greeter.cs", Controller: not null }, "Go to Definition opens Greeter.cs", 60).ConfigureAwait(true);
            var greeter = shell.ActiveEditor!.Controller!;
            Check(greeter.Model.Text.GetText(greeter.Model.Text.GetLineByOffset(greeter.Editor.CaretOffset)).Contains("Greet(", StringComparison.Ordinal), "the caret is on the declaration");

            await shell.FindReferencesAsync().ConfigureAwait(true);
            await WaitAsync(() => shell.FindResults.Rows.Any(r => r.IsHeader && r.Path == Program), "the references include Program.cs", 60).ConfigureAwait(true);
        }, "language.ready").ConfigureAwait(true);

        await StepAsync("editor.refactoring", "M3", "Rename across projects and Format Document", async () =>
        {
            var greeter = await OpenAsync(Greeter).ConfigureAwait(true);
            MoveCaret(greeter, "Greet(string", 2);
            var renaming = shell.RenameSymbolAsync();
            await WaitAsync(() => shell.Dialogs is { IsOpen: true, HasInput: true }, "the rename dialog asks for a name", 60).ConfigureAwait(true);
            shell.Dialogs.InputText = "SayHello";
            shell.Dialogs.Accept();
            await WaitAsync(() => shell.Dialogs is { IsOpen: true, HasDetails: true }, "the rename preview opens", 90).ConfigureAwait(true);
            shell.Dialogs.Accept();
            await renaming.ConfigureAwait(true);
            Check(greeter.Model.Text.Text.Contains("public static string SayHello(string name)", StringComparison.Ordinal), "Greeter.cs is renamed");

            greeter.Model.Text.Insert(greeter.Model.Text.TextLength, "\ninternal static class Formatting{public static int  Value=1 ;}\n");
            await shell.FormatAsync(selectionOnly: false).ConfigureAwait(true);
            Check(greeter.Model.Text.Text.Contains("public static int Value = 1;", StringComparison.Ordinal), "Format Document fixes spacing");

            await shell.SaveAllAsync().ConfigureAwait(true);
            Check(File.ReadAllText(Program).Contains("Greeter.SayHello(", StringComparison.Ordinal), "the rename reached Program.cs in the other project");
            Check(File.ReadAllText(Greeter).Contains("public static int Value = 1;", StringComparison.Ordinal), "Greeter.cs is saved");
        }, "language.ready").ConfigureAwait(true);

        // M4: build and run.
        await StepAsync("build.solution", "M4", "Build Solution succeeds", async () =>
        {
            var before = shell.Builds.LastBuild;
            shell.Run("build.buildSolution");
            await WaitAsync(() => shell.Builds.LastBuild is { } b && b != before, "the build finishes", 420).ConfigureAwait(true);
            Check(shell.Builds.LastBuild!.Result.Succeeded, "the build succeeded:\n" + Tail(shell.Builds.Channel.GetText(), 3000));
            Check(!shell.ErrorList.Rows.Any(r => r.Source == ErrorListViewModel.BuildSource && r.Code.StartsWith("CS", StringComparison.Ordinal)), "the Error List has no build errors");
        }, "projects.open").ConfigureAwait(true);

        await StepAsync("run.startup", "M4", "Run Without Debugging runs the startup project with its launch profile", async () =>
        {
            shell.Run("run.startWithoutDebugging");
            await WaitAsync(() => RunOutput().Contains("has exited with code 0", StringComparison.Ordinal), "Hello runs and exits with 0", 300).ConfigureAwait(true);
            var output = RunOutput();
            Check(output.Contains("Hello from Hello", StringComparison.Ordinal), "the program printed its greeting");
            Check(output.Contains("args: one two words", StringComparison.Ordinal), "the launch profile's arguments arrived");
            Check(output.Contains("GREETING: from the launch profile", StringComparison.Ordinal), "the launch profile's environment arrived");
        }, "build.solution").ConfigureAwait(true);

        // M5: debugging (netcoredbg).
        await StepAsync("debug.session", "M5", "F5 stops at a breakpoint; locals, evaluation and continue to exit", async () =>
        {
            var adapter = DebugAdapterLocator.Resolve();
            Check(adapter is not null, $"netcoredbg is installed (debugger/ next to the shell, or {DebugAdapterLocator.EnvironmentVariable})");
            Console.Out.WriteLine($"  netcoredbg: {adapter}");
            var line = Array.FindIndex(File.ReadAllLines(Program), l => l.Contains("Console.WriteLine($\"args", StringComparison.Ordinal)) + 1;
            shell.Debugger.Breakpoints.Toggle(Program, line);
            Check(shell.Debugger.Breakpoints.At(Program, line) is not null, "the breakpoint is set");
            shell.Run("debug.start");
            await WaitAsync(() => shell.Debugger.State == DebugState.Stopped, "the debugger stops at the breakpoint", 300).ConfigureAwait(true);
            await WaitAsync(() => shell.ActiveEditor is { Controller: { } c } && c.CurrentDebugLine() == line, $"the editor shows the current line {line}").ConfigureAwait(true);
            await WaitAsync(() => shell.Locals.Roots.Any(r => r.Name == "args"), "Locals lists args").ConfigureAwait(true);
            await WaitAsync(() => shell.CallStack.Frames.Count > 0, "the Call Stack has frames").ConfigureAwait(true);

            // netcoredbg does not evaluate an array's Length; indexing works.
            shell.DebugConsole.Input = "args[1]";
            await shell.DebugConsole.SubmitAsync().ConfigureAwait(true);
            Check(shell.DebugLog.GetText().Contains("> args[1]\n\"two words\"", StringComparison.Ordinal), "the Debug Console evaluates args[1] to \"two words\":\n" + Tail(shell.DebugLog.GetText(), 1000));

            shell.Debugger.Breakpoints.Clear();
            shell.Run("debug.continue");
            await WaitAsync(() => shell.Debugger.State == DebugState.Idle, "the program runs to its end", 120).ConfigureAwait(true);
            Check(shell.DebugLog.GetText().Contains("exited with code", StringComparison.Ordinal), "the session reports the exit");
        }, "build.solution").ConfigureAwait(true);

        // M6: Git.
        await StepAsync("git.changes", "M6", "Git status, diff, stage, commit, blame and branches", async () =>
        {
            await shell.Git.RefreshAsync().ConfigureAwait(true);
            await WaitAsync(() => shell.Git.Status is { Branch: Branch } s && s.Files.Any(f => f.Path == Greeter) && s.Files.Any(f => f.Path == Program), "Git lists the edited files on main", 60).ConfigureAwait(true);
            await WaitAsync(() => shell.StatusBar.Left.Any(i => i.Item.Id == "daedalus.git.branch" && i.Text.Contains(Branch, StringComparison.Ordinal)), "the status bar shows the branch").ConfigureAwait(true);

            Check(await shell.OpenDiffAsync(Greeter, staged: false).ConfigureAwait(true) is not null, "a diff document opens");
            await WaitAsync(() => window.GetVisualDescendants().OfType<DiffView>().Any(), "the diff view renders").ConfigureAwait(true);

            await shell.Git.StageAsync([Greeter, Program]).ConfigureAwait(true);
            await shell.Git.RefreshAsync().ConfigureAwait(true);
            Check(shell.Git.Status!.Staged.Count() == 2, "both files are staged");
            await shell.Git.CommitAsync("Smoke test", amend: false, all: false).ConfigureAwait(true);
            await shell.Git.RefreshAsync().ConfigureAwait(true);
            await WaitAsync(() => shell.Git.Status is { Files.Count: 0 }, "the working tree is clean after the commit").ConfigureAwait(true);

            var greeter = await OpenAsync(Greeter).ConfigureAwait(true);
            await greeter.ToggleBlameAsync().ConfigureAwait(true);
            await WaitAsync(() => greeter.Blame is { Ranges.Count: > 0 } b && b.Ranges.Any(r => r.Summary == "Smoke test"), "blame shows the new commit").ConfigureAwait(true);

            await shell.Git.CreateBranchAsync("smoke/branch", switchTo: true).ConfigureAwait(true);
            await WaitAsync(() => shell.Git.Status is { Branch: "smoke/branch" }, "the new branch is checked out").ConfigureAwait(true);
        }, "projects.open").ConfigureAwait(true);

        // M8: testing, on part of samples/Testing (xUnit on VSTest and on Microsoft.Testing.Platform).
        await StepAsync("testing.discover", "M8", "The Test Explorer lists tests from source", async () =>
        {
            var solution = PrepareTestingFixture();
            await shell.OpenSolutionAsync(solution).ConfigureAwait(true);
            shell.Layout.ShowToolWindow(TestExplorerViewModel.ToolWindowId);
            await WaitAsync(() => TestClass("XunitV2Tests") is not null && TestClass("XunitV3Tests") is not null, "both test projects' CalculatorTests are listed", 300).ConfigureAwait(true);
            await WaitAsync(() => shell.TestExplorer.Rows.Any(r => r.Label == "XunitV3Tests"), "the Test Explorer shows them").ConfigureAwait(true);
        }, "projects.open").ConfigureAwait(true);

        await StepAsync("testing.run", "M8", "Running tests on VSTest and MTP shows outcomes in the explorer and the editor", async () =>
        {
            var classes = new[] { TestClass("XunitV2Tests")!, TestClass("XunitV3Tests")! };
            await shell.RunTestsAsync(Workbench.Testing.TestRunKind.Run, [.. classes.Select(c => c.Key)]).ConfigureAwait(true);
            await WaitAsync(() => !shell.Tests.IsRunning && classes.All(c => Outcome(c, "Adds") == Workbench.Testing.TestOutcome.Passed && Outcome(c, "Fails") == Workbench.Testing.TestOutcome.Failed), "Adds passes and Fails fails in both projects", 300).ConfigureAwait(true);

            var tests = await OpenAsync(Path.Combine(_testing, "tests", "XunitV3Tests", "CalculatorTests.cs")).ConfigureAwait(true);
            await WaitAsync(() => tests.TestMargin.Glyphs.Values.Any(n => n.Label == "Fails"), "the test margin marks the tests").ConfigureAwait(true);
        }, "testing.discover").ConfigureAwait(true);

        await StepAsync("testing.coverage", "M8", "Run with Coverage colors the code under test and fills Coverage Results", async () =>
        {
            var classes = new[] { TestClass("XunitV2Tests")!, TestClass("XunitV3Tests")! };
            await shell.RunTestsAsync(Workbench.Testing.TestRunKind.Coverage, [.. classes.Select(c => c.Key)]).ConfigureAwait(true);
            var calculator = Path.Combine(_testing, "src", "Calculator", "Calculator.cs");
            var uri = LanguageServices.Protocol.DocumentUris.FromPath(calculator);
            await WaitAsync(() => !shell.Tests.IsRunning && shell.Tests.Coverage.LinesOf(uri) is { Count: > 0 } lines && lines.Any(l => l.Hits == 0) && lines.Any(l => l.Hits > 0), "Calculator.cs has covered and uncovered lines", 300).ConfigureAwait(true);

            var editor = await OpenAsync(calculator).ConfigureAwait(true);
            await WaitAsync(() => editor.CoverageMargin.Lines.Count > 0, "the editor colors the lines").ConfigureAwait(true);
            await WaitAsync(() => shell.Layout.IsVisible(CoverageResultsViewModel.ToolWindowId) && shell.CoverageResults.Rows.Any(r => r.Label == "Arithmetic"), "Coverage Results lists the class").ConfigureAwait(true);
        }, "testing.run").ConfigureAwait(true);
    }

    private Workbench.Testing.TestNode? TestClass(string project) =>
        shell.Tests.Read(roots => roots.FirstOrDefault(r => r.Label == project)?.SelfAndDescendants().FirstOrDefault(n => n.Label == "CalculatorTests"));

    private Workbench.Testing.TestOutcome Outcome(Workbench.Testing.TestNode cls, string method) =>
        shell.Tests.Read(_ => cls.Children.FirstOrDefault(c => c.Label == method)?.Outcome ?? Workbench.Testing.TestOutcome.None);

    // ------------------------------------------------------------------ steps

    private async Task StepAsync(string id, string milestone, string title, Func<Task> body, string? requires = null)
    {
        if (requires is not null && _results.FirstOrDefault(r => r.Id == requires) is { Outcome: not "passed" })
        {
            Report(id, milestone, title, "skipped", 0, $"Needs '{requires}', which did not pass.");
            return;
        }

        Console.Out.WriteLine($"[{_clock.Elapsed:mm\\:ss}] {milestone} {id}: {title}");
        Console.Out.Flush();
        var start = Stopwatch.GetTimestamp();
        try
        {
            await body().ConfigureAwait(true);
            Report(id, milestone, title, "passed", Stopwatch.GetElapsedTime(start).TotalSeconds, null);
        }
        catch (Exception ex)
        {
            Report(id, milestone, title, "failed", Stopwatch.GetElapsedTime(start).TotalSeconds, ex is SmokeFailure ? ex.Message : ex.ToString());
            Screenshot($"smoke-test-{id}.png");
        }
    }

    private void Report(string id, string milestone, string title, string outcome, double seconds, string? error)
    {
        _results.Add(new StepResult(id, milestone, title, outcome, seconds, error));
        Console.Out.WriteLine($"  {outcome.ToUpperInvariant()} ({seconds:0.0} s){(error is null ? string.Empty : ": " + error)}");
        Console.Out.Flush();
        shell.Workbench.Log.AppendLine($"[smoke] {id}: {outcome}{(error is null ? string.Empty : ": " + error)}");
    }

    private static void Check(bool condition, string what)
    {
        if (!condition)
        {
            throw new SmokeFailure($"Expected: {what}.");
        }
    }

    private static async Task WaitAsync(Func<bool> condition, string what, int seconds = 30)
    {
        var deadline = Stopwatch.GetTimestamp() + (seconds * Stopwatch.Frequency);
        while (!condition())
        {
            if (Stopwatch.GetTimestamp() > deadline)
            {
                throw new SmokeFailure($"Timed out after {seconds} s waiting for: {what}.");
            }

            await Task.Delay(50).ConfigureAwait(true);
        }
    }

    // ------------------------------------------------------------------ fixture and editor helpers

    /// <summary>Copies samples/BuildAndRun (standalone, as under samples/) to a temporary folder and makes it a git repository.</summary>
    private void PrepareFixture()
    {
        Check(File.Exists(Path.Combine(samples, "BuildAndRun", "BuildAndRun.slnx")), $"the samples folder {samples} has BuildAndRun");
        _work = Path.Combine(RealPath.Resolve(Path.GetTempPath()), "daedalus-smoke-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_work);
        File.Copy(Path.Combine(samples, "Directory.Build.props"), Path.Combine(_work, "Directory.Build.props"));
        File.Copy(Path.Combine(samples, "Directory.Packages.props"), Path.Combine(_work, "Directory.Packages.props"));
        _folder = Path.Combine(_work, "BuildAndRun");
        Copy(Path.Combine(samples, "BuildAndRun"), _folder);
        File.WriteAllText(Path.Combine(_folder, ".gitignore"), "bin/\nobj/\n");

        Git("init", "-q", "-b", Branch);
        Git("config", "user.name", "Smoke Test");
        Git("config", "user.email", "smoke@example.com");
        Git("config", "commit.gpgsign", "false");
        Git("config", "core.autocrlf", "false");
        Git("add", ".");
        Git("commit", "-q", "-m", "initial");
    }

    /// <summary>
    /// Copies samples/Testing next to BuildAndRun and returns a solution with the code under test and the two xUnit
    /// projects (one per test platform), so the step builds three projects rather than eight.
    /// </summary>
    private string PrepareTestingFixture()
    {
        Check(File.Exists(Path.Combine(samples, "Testing", "Testing.slnx")), $"the samples folder {samples} has Testing");
        _testing = Path.Combine(_work, "Testing");
        Copy(Path.Combine(samples, "Testing"), _testing);
        var solution = Path.Combine(_testing, "Smoke.slnx");
        File.WriteAllText(solution, """
            <Solution>
              <Project Path="src/Calculator/Calculator.csproj" />
              <Project Path="tests/XunitV2Tests/XunitV2Tests.csproj" />
              <Project Path="tests/XunitV3Tests/XunitV3Tests.csproj" />
            </Solution>
            """);
        return solution;
    }

    private static void Copy(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        }

        foreach (var directory in Directory.EnumerateDirectories(source).Where(d => Path.GetFileName(d) is not ("bin" or "obj")))
        {
            Copy(directory, Path.Combine(target, Path.GetFileName(directory)));
        }
    }

    private void Git(params string[] arguments)
    {
        var git = Environment.GetEnvironmentVariable(Workbench.Git.GitService.EnvironmentVariable) is { Length: > 0 } custom ? custom : "git";
        var start = new ProcessStartInfo(git) { WorkingDirectory = _folder, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new SmokeFailure("git did not start.");
        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Check(process.ExitCode == 0, $"git {string.Join(' ', arguments)} succeeds (exit code {process.ExitCode}: {error.Result.Trim()} {output.Trim()})");
    }

    private async Task<CodeEditorController> OpenAsync(string path)
    {
        var document = await shell.OpenFileAsync(path).ConfigureAwait(true) ?? throw new SmokeFailure($"{path} did not open.");
        shell.Layout.OpenDocument(document);
        await WaitAsync(() => document.Controller is not null, $"the editor for {Path.GetFileName(path)} appears").ConfigureAwait(true);
        return document.Controller!;
    }

    private static void MoveCaret(CodeEditorController editor, string marker, int delta = 0)
    {
        var offset = editor.Model.Text.Text.IndexOf(marker, StringComparison.Ordinal);
        Check(offset >= 0, $"the text contains '{marker}'");
        editor.Editor.CaretOffset = offset + delta;
        editor.Editor.TextArea.Focus();
    }

    /// <summary>Undoes this step's edits.</summary>
    private static void Restore(CodeEditorController editor, string original)
    {
        while (editor.Model.Text.Text != original && editor.Editor.CanUndo)
        {
            editor.Editor.Undo();
        }

        Check(editor.Model.Text.Text == original, "undo restores the text");
    }

    private string RunOutput() =>
        shell.Workbench.Output.Channels.FirstOrDefault(c => c.Name == "Run: Hello")?.GetText() ?? string.Empty;

    private async Task RenderedAsync()
    {
        var rendered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.RequestAnimationFrame(_ => window.RequestAnimationFrame(_ => rendered.TrySetResult()));
        await rendered.Task.ConfigureAwait(true);
    }

    // ------------------------------------------------------------------ report

    private void Screenshot(string name)
    {
        try
        {
            var scaling = window.RenderScaling;
            var size = new PixelSize(Math.Max(1, (int)(window.Bounds.Width * scaling)), Math.Max(1, (int)(window.Bounds.Height * scaling)));
            using var bitmap = new RenderTargetBitmap(size, new Vector(96 * scaling, 96 * scaling));
            bitmap.Render(window);
            Directory.CreateDirectory(shell.Paths.Logs);
            bitmap.Save(Path.Combine(shell.Paths.Logs, name), PngBitmapEncoderOptions.Default);
        }
        catch (Exception ex)
        {
            shell.Workbench.Log.AppendLine($"[smoke] Screenshot {name} failed: {ex.Message}");
        }
    }

    private void WriteReport()
    {
        Directory.CreateDirectory(shell.Paths.Logs);
        var passed = _results.Count > 0 && _results.All(r => r.Outcome == "passed");
        var os = $"{System.Runtime.InteropServices.RuntimeInformation.OSDescription} ({System.Runtime.InteropServices.RuntimeInformation.OSArchitecture})";

        using (var stream = File.Create(Path.Combine(shell.Paths.Logs, "smoke-test.json")))
        using (var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            json.WriteStartObject();
            json.WriteString("version", ShellInfo.Version);
            json.WriteString("os", os);
            json.WriteBoolean("passed", passed);
            json.WriteNumber("seconds", Math.Round(_clock.Elapsed.TotalSeconds, 1));
            json.WriteStartArray("steps");
            foreach (var r in _results)
            {
                json.WriteStartObject();
                json.WriteString("id", r.Id);
                json.WriteString("milestone", r.Milestone);
                json.WriteString("title", r.Title);
                json.WriteString("outcome", r.Outcome);
                json.WriteNumber("seconds", Math.Round(r.Seconds, 1));
                if (r.Error is not null)
                {
                    json.WriteString("error", r.Error);
                }

                json.WriteEndObject();
            }

            json.WriteEndArray();
            json.WriteEndObject();
        }

        var md = new StringBuilder();
        md.AppendLine($"## Smoke test on {os}: {(passed ? "passed" : "FAILED")}");
        md.AppendLine();
        md.AppendLine($"{ShellInfo.ProductName} {ShellInfo.Version}, {_clock.Elapsed.TotalSeconds:0} s.");
        md.AppendLine();
        md.AppendLine("| Milestone | Step | Result | Time |");
        md.AppendLine("|---|---|---|---|");
        foreach (var r in _results)
        {
            md.AppendLine($"| {r.Milestone} | {r.Title} (`{r.Id}`) | {(r.Outcome == "passed" ? "✅ passed" : r.Outcome == "skipped" ? "⏭️ skipped" : "❌ failed")} | {r.Seconds:0.0} s |");
        }

        foreach (var r in _results.Where(r => r.Error is not null))
        {
            md.AppendLine().AppendLine($"### `{r.Id}` {r.Outcome}").AppendLine().AppendLine("```").AppendLine(r.Error).AppendLine("```");
        }

        if (!passed)
        {
            // The tails of the output channels usually say why.
            foreach (var channel in shell.Workbench.Output.Channels)
            {
                var text = channel.GetText();
                if (text.Length > 0)
                {
                    md.AppendLine().AppendLine($"<details><summary>Output: {channel.Name}</summary>").AppendLine().AppendLine("```").AppendLine(Tail(text, 6000)).AppendLine("```").AppendLine("</details>");
                }
            }
        }

        File.WriteAllText(Path.Combine(shell.Paths.Logs, "smoke-test.md"), md.ToString());
    }

    private static string Tail(string text, int length) => text.Length > length ? "…" + text[^length..] : text;
}
