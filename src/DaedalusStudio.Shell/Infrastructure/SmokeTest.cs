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
/// milestone (M1–M6 on a copy of <c>samples/BuildAndRun</c>, M8 on part of <c>samples/Testing</c>, M9 on <c>samples/Packages</c>), with the hosts,
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
    private static readonly string[] s_themes = ["blue", "light", "highContrast", "dark"];

    /// <summary>The themes the M29 step photographs.</summary>
    private static readonly string[] s_chromeThemes = ["blue", "light", "dark"];

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
            Check(shell.StatusBar is { DisplayMessage: "Ready", IsEmptyState: true }, "the status bar says Ready, in its no-solution color");
            Check(window.GetVisualDescendants().OfType<StandardToolbar>().Any(t => t.IsEffectivelyVisible), "the standard toolbar");
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

        await StepAsync("editor.insights", "M12", "Inlay hints, CodeLens, Peek Definition, the Document Outline and the navigation bar", async () =>
        {
            var program = await OpenAsync(Program).ConfigureAwait(true);
            await WaitAsync(() => program.Insights.Hints.Any(h => h.Label == "name:"), "an inlay hint names Greet's parameter", 60).ConfigureAwait(true);

            MoveCaret(program, "Greeter.Greet(", "Greeter.Gr".Length);
            shell.Run("editor.action.peekDefinition");
            await WaitAsync(() => shell.ActiveEditor?.View?.Peek is { IsOpen: true } peek && peek.Viewer.Editor.Document.Text.Contains("Greet(string name)", StringComparison.Ordinal), "Peek Definition shows Greeter.Greet", 60).ConfigureAwait(true);
            shell.ActiveEditor!.View!.Peek!.Close();

            var greeter = await OpenAsync(Greeter).ConfigureAwait(true);
            var classLine = greeter.Model.Text.GetLineByOffset(greeter.Model.Text.Text.IndexOf("class Greeter", StringComparison.Ordinal)).LineNumber;
            await WaitAsync(() => greeter.Insights.EndOfLineText(classLine)?.Contains("reference", StringComparison.Ordinal) == true, "CodeLens counts Greeter's references", 60).ConfigureAwait(true);
            await WaitAsync(() => shell.ActiveEditor?.View?.NavigationBar.Types.Any(t => t.Display == "Greeter") == true, "the navigation bar lists Greeter", 30).ConfigureAwait(true);
            shell.Layout.ShowToolWindow(ViewModels.DocumentOutlineViewModel.ToolWindowId, focus: false);
            await WaitAsync(() => shell.DocumentOutline.Roots.Any(), "the Document Outline shows Greeter.cs", 30).ConfigureAwait(true);
            shell.Layout.CloseToolWindow(ViewModels.DocumentOutlineViewModel.ToolWindowId);
        }, "language.ready").ConfigureAwait(true);

        await StepAsync("shell.chrome", "M29", "The title bar, toolbar and status bar, in the Blue, Light and Dark themes", async () =>
        {
            await WaitAsync(() => shell.Toolbar is { HasSolution: true, Configuration: not null, Platform: not null }, "the toolbar shows the solution configuration", 60).ConfigureAwait(true);
            await WaitAsync(() => shell.Toolbar.StartupProject is { Name: "Hello" }, "the toolbar shows the startup project", 60).ConfigureAwait(true);
            Check(shell.Toolbar.SolutionName == "BuildAndRun", "the title bar shows the solution's name");
            Check(shell.StatusBar is { HasSolution: true, IsEmptyState: false }, "the status bar is in its solution color");
            Check(shell.Workbench.Commands.Find("workbench.action.featureSearch") is not null, "the search box's command is registered");
            var before = shell.Theme.Setting;
            await OpenAsync(Program).ConfigureAwait(true);
            foreach (var theme in s_chromeThemes)
            {
                await shell.Workbench.Settings.SetAsync(ThemeService.SettingKey, theme, SettingScope.User, CancellationToken.None).ConfigureAwait(true);
                await WaitAsync(() => shell.Theme.ActiveTheme == theme, $"the {theme} theme applies").ConfigureAwait(true);
                await RenderedAsync().ConfigureAwait(true);
                Screenshot($"smoke-test-theme-{theme}.png");
            }

            await shell.Workbench.Settings.SetAsync(ThemeService.SettingKey, before, SettingScope.User, CancellationToken.None).ConfigureAwait(true);
        }, "language.ready").ConfigureAwait(true);

        await StepAsync("editor.editing", "M12", "Snippets, Duplicate Line, Expand Selection, the Syntax Visualizer and Code Metrics", async () =>
        {
            var greeter = await OpenAsync(Greeter).ConfigureAwait(true);
            var original = greeter.Model.Text.Text;
            try
            {
                MoveCaret(greeter, "Greet(string", 2);
                await greeter.ExpandSelectionAsync().ConfigureAwait(true);
                await WaitAsync(() => greeter.Editor.SelectedText == "Greet", "Expand Selection selects the identifier", 30).ConfigureAwait(true);
                await greeter.ExpandSelectionAsync().ConfigureAwait(true);
                Check(greeter.Editor.SelectedText.Length > "Greet".Length, "Expand Selection grows to the enclosing node");

                greeter.Editor.TextArea.ClearSelection(); // with a selection, Duplicate copies the selection instead
                var lines = greeter.Model.Text.LineCount;
                var length = greeter.Model.Text.TextLength;
                var selection = greeter.Editor.SelectionLength;
                greeter.DuplicateLines();
                Check(greeter.Model.Text.LineCount > lines, $"Duplicate Line adds the selected lines again (lines {lines} → {greeter.Model.Text.LineCount}, length {length} → {greeter.Model.Text.TextLength}, selection {selection}, read-only {greeter.Model.IsReadOnly}, caret line {greeter.Model.Text.GetLineByOffset(greeter.Editor.CaretOffset).LineNumber})");

                greeter.Editor.CaretOffset = greeter.Model.Text.TextLength;
                greeter.InsertSnippet("// ${1:snippet} works$0", surround: false);
                Check(greeter.Model.Text.Text.Contains("// snippet works", StringComparison.Ordinal), "a snippet expands with its placeholder");

                MoveCaret(greeter, "Greet(string", 2);
                shell.ShowSyntaxVisualizer();
                await WaitAsync(() => shell.SyntaxVisualizer.Selected?.Item.Text == "Greet", "the Syntax Visualizer shows the identifier at the caret", 60).ConfigureAwait(true);
                shell.Layout.CloseToolWindow(ViewModels.SyntaxVisualizerViewModel.ToolWindowId);

                await shell.CalculateCodeMetricsAsync(projectOnly: false).ConfigureAwait(true);
                Check(shell.CodeMetrics.Roots.Count >= 2 && shell.CodeMetrics.Roots.All(r => r.Item.SourceLines > 0), "Code Metrics measure every project:\n" + shell.CodeMetrics.ToTable());
                shell.Layout.CloseToolWindow(ViewModels.CodeMetricsViewModel.ToolWindowId);
            }
            finally
            {
                greeter.Model.Text.Text = original;
            }
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

        // M10: the integrated terminal (ADR 0014), while BuildAndRun is open.
        await StepAsync("terminal.shell", "M10", "A terminal runs the default shell with shell integration", async () =>
        {
            var profile = shell.Terminals.FindProfile(shell.Workbench.Settings.GetString(ShellHost.TerminalDefaultProfileSetting))
                ?? throw new SmokeFailure("No shell was found for the terminal.");
            Console.Out.WriteLine($"  profile: {profile.Name} ({profile.Executable})");
            var marks = new System.Collections.Concurrent.ConcurrentQueue<DaedalusStudio.Terminal.CommandMark>();
            var session = shell.ShowTerminal(newTerminal: true, profile: profile.Id) ?? throw new SmokeFailure("The terminal did not start.");
            session.Terminal.MarkReceived += marks.Enqueue;
            await Task.Delay(1500).ConfigureAwait(true); // let the shell print its prompt
            session.Write(profile.Shell switch
            {
                "pwsh" or "powershell" => "6*7\r",
                "cmd" => "set /a 6*7\r",
                "fish" => "math 6*7\r",
                _ => "echo $((6*7))\r",
            });
            await WaitAsync(() => session.Terminal.ScreenText().Split('\n').Any(l => l.Trim() == "42"), "the shell prints 42", 60).ConfigureAwait(true);
            if (profile.Shell is "bash" or "zsh" or "pwsh")
            {
                await WaitAsync(() => marks.Any(m => m is { Kind: DaedalusStudio.Terminal.CommandMarkKind.CommandEnd, ExitCode: 0 }), "shell integration reports the command's exit code (OSC 133)").ConfigureAwait(true);
                Check(session.Terminal.WorkingDirectory is { Length: > 0 }, "shell integration reports the working directory (OSC 7)");
            }

            shell.TerminalWindow.Kill(shell.TerminalWindow.Tabs.First(t => t.Primary == session));
            await WaitAsync(() => !shell.Terminals.Sessions.Contains(session), "the terminal closes", 20).ConfigureAwait(true);
        }).ConfigureAwait(true);

        await StepAsync("run.terminal", "M10", "A console app runs in the integrated terminal", async () =>
        {
            var hello = Path.Combine(_folder, "src", "Hello", "Hello.csproj");
            Check(await shell.RunInTerminalAsync(hello).ConfigureAwait(true), "the program started in a terminal");
            var session = shell.Terminals.Sessions[^1];
            await WaitAsync(() => session.ExitCode is not null, "the program exits", 120).ConfigureAwait(true);
            Check(session.ExitCode == 0, $"it exited with 0 (got {session.ExitCode})");
            var screen = string.Join('\n', Enumerable.Range(0, session.Terminal.TotalRows).Select(i => session.Terminal.GetRow(i).GetText()));
            Check(screen.Contains("Hello from Hello", StringComparison.Ordinal), "its output is in the terminal");
            Check(screen.Contains("args: one two words", StringComparison.Ordinal), "the launch profile's arguments arrived");
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

        await StepAsync("debug.depth", "M14", "Run to Cursor, PerfTips, QuickWatch, the Threads, Modules and Exception Settings windows", async () =>
        {
            var lines = File.ReadAllLines(Program);
            var argsLine = Array.FindIndex(lines, l => l.Contains("Console.WriteLine($\"args", StringComparison.Ordinal)) + 1;
            var cwdLine = Array.FindIndex(lines, l => l.Contains("Console.WriteLine($\"cwd", StringComparison.Ordinal)) + 1;

            // Run to Cursor with no session starts debugging with a temporary breakpoint.
            await shell.RunToLineAsync(Program, argsLine).ConfigureAwait(true);
            await WaitAsync(() => shell.Debugger.State == DebugState.Stopped, "the debugger stops at the cursor's line", 300).ConfigureAwait(true);
            await WaitAsync(() => shell.ActiveEditor is { Controller: { } c } && c.CurrentDebugLine() == argsLine, $"the editor shows line {argsLine}").ConfigureAwait(true);
            Check(shell.Debugger.Breakpoints.At(Program, argsLine) is null, "the temporary breakpoint is gone after its stop");

            await shell.ShowQuickWatchAsync("args[1]").ConfigureAwait(true);
            Check(shell.QuickWatch.Roots.SingleOrDefault()?.Value == "\"two words\"", "QuickWatch evaluates args[1]: " + (shell.QuickWatch.Error ?? shell.QuickWatch.Roots.SingleOrDefault()?.Value));

            // Run to Cursor while paused, with a PerfTip at the new line.
            await shell.RunToLineAsync(Program, cwdLine).ConfigureAwait(true);
            await WaitAsync(() => shell.Debugger.State == DebugState.Stopped && shell.ActiveEditor?.Controller?.CurrentDebugLine() == cwdLine, $"Run to Cursor stops at line {cwdLine}", 60).ConfigureAwait(true);
            Check(PerfTipRenderer.Text(shell.Debugger.CurrentStop?.Elapsed) is not null, "a PerfTip shows the elapsed time");

            shell.Layout.ShowToolWindow(ThreadsViewModel.ToolWindowId);
            await shell.ThreadList.RefreshAsync().ConfigureAwait(true);
            Check(shell.ThreadList.Rows.Any(r => r.IsCurrent), "the Threads window marks the current thread");
            shell.Layout.ShowToolWindow(ModulesViewModel.ToolWindowId);
            await WaitAsync(() => shell.ModuleList.Rows.Any(r => r.Name == "Hello.dll"), "the Modules window lists Hello.dll").ConfigureAwait(true);
            shell.Run("debug.exceptionSettings");
            await WaitAsync(() => window.GetVisualDescendants().OfType<ExceptionSettingsView>().Any(), "the Exception Settings window renders").ConfigureAwait(true);
            Check(shell.ExceptionSettingsWindow.Rows.Any(r => r.Type == "System.NullReferenceException"), "it lists the common exception types");
            Screenshot("smoke-test-debug-depth.png");

            shell.Run("debug.stop");
            await WaitAsync(() => shell.Debugger.State == DebugState.Idle, "the session ends", 60).ConfigureAwait(true);
        }, "debug.session").ConfigureAwait(true);

        // M6: Git.
        await StepAsync("git.changes", "M6", "Git status, diff, stage, commit, blame and branches", async () =>
        {
            await shell.Git.RefreshAsync().ConfigureAwait(true);
            await WaitAsync(() => shell.Git.Status is { Branch: Branch } s && s.Files.Any(f => f.Path == Greeter) && s.Files.Any(f => f.Path == Program), "Git lists the edited files on main", 60).ConfigureAwait(true);
            await WaitAsync(() => shell.StatusBar.Right.Any(i => i.Item.Id == "daedalus.git.branch" && i.Text.Contains(Branch, StringComparison.Ordinal)), "the status bar shows the branch").ConfigureAwait(true);

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

        await StepAsync("git.depth", "M13", "History with its graph, stash, tags, and a conflict resolved in the merge editor", async () =>
        {
            // History: the commits of every branch, with the graph laid out.
            var history = await shell.ShowHistoryAsync().ConfigureAwait(true) ?? throw new SmokeFailure("The history didn't open.");
            await WaitAsync(() => history.ViewModel.Rows.Any(r => r.Subject == "Smoke test") && history.ViewModel.Rows.All(r => r.Graph is not null), "the history lists the smoke commit with its graph", 60).ConfigureAwait(true);

            // Stash and pop an edit.
            var original = await File.ReadAllTextAsync(Greeter).ConfigureAwait(true);
            await File.WriteAllTextAsync(Greeter, original + "// stashed\n").ConfigureAwait(true);
            await shell.Git.RefreshAsync().ConfigureAwait(true);
            await shell.Git.StashAsync("smoke", includeUntracked: false).ConfigureAwait(true);
            Check(!(await File.ReadAllTextAsync(Greeter).ConfigureAwait(true)).Contains("// stashed", StringComparison.Ordinal), "the edit is stashed");
            var stash = (await shell.Git.StashesAsync().ConfigureAwait(true)).Single();
            await shell.Git.ApplyStashAsync(stash, pop: true).ConfigureAwait(true);
            Check((await File.ReadAllTextAsync(Greeter).ConfigureAwait(true)).Contains("// stashed", StringComparison.Ordinal), "the stash pops back");
            await shell.Git.DiscardAsync([Greeter]).ConfigureAwait(true);

            // Tags.
            await shell.Git.CreateTagAsync("smoke-tag", null, "Smoke").ConfigureAwait(true);
            Check((await shell.Git.TagsAsync().ConfigureAwait(true)).Any(t => t.Name == "smoke-tag"), "the tag is created");

            // A conflict between two branches, resolved in the merge editor and continued.
            await File.WriteAllTextAsync(Greeter, original.Replace("Hello from", "Ours from", StringComparison.Ordinal)).ConfigureAwait(true);
            await shell.Git.CommitAsync("ours", amend: false, all: true).ConfigureAwait(true);
            await shell.Git.CreateBranchAtAsync("smoke/theirs", "HEAD~1", switchTo: true).ConfigureAwait(true);
            await File.WriteAllTextAsync(Greeter, original.Replace("Hello from", "Theirs from", StringComparison.Ordinal)).ConfigureAwait(true);
            await shell.Git.CommitAsync("theirs", amend: false, all: true).ConfigureAwait(true);
            await shell.Git.SwitchAsync(new DaedalusStudio.Workbench.Git.GitBranch("smoke/branch", false, false, null, string.Empty)).ConfigureAwait(true);
            try
            {
                await shell.Git.MergeAsync("smoke/theirs").ConfigureAwait(true);
            }
            catch (DaedalusStudio.Workbench.Git.GitException)
            {
                // The expected conflict.
            }

            await WaitAsync(() => shell.Git.Status is { Operation: "merge" } && shell.Git.Status.Conflicts.Any(), "the merge stops on the conflict").ConfigureAwait(true);
            _ = await shell.OpenMergeEditorAsync(Greeter).ConfigureAwait(true) ?? throw new SmokeFailure("The merge editor didn't open.");
            await WaitAsync(() => window.GetVisualDescendants().OfType<MergeEditorView>().Any(), "the merge editor renders").ConfigureAwait(true);
            var merge = window.GetVisualDescendants().OfType<MergeEditorView>().Single();
            merge.Accept("incoming");
            await merge.AcceptMergeAsync().ConfigureAwait(true);
            await shell.ContinueOperationAsync().ConfigureAwait(true);
            await WaitAsync(() => shell.Git.Status is { Operation: null, Files.Count: 0 }, "the merge is committed").ConfigureAwait(true);
            Check((await File.ReadAllTextAsync(Greeter).ConfigureAwait(true)).Contains("Theirs from", StringComparison.Ordinal), "the incoming side won");
        }, "git.changes").ConfigureAwait(true);

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

        // M9: NuGet packages, on a copy of samples/Packages and its local feed (ADR 0012).
        await StepAsync("packages.manage", "M9", "The NuGet Package Manager browses a feed, updates a package and restores it", async () =>
        {
            await shell.OpenSolutionAsync(PreparePackagesFixture()).ConfigureAwait(true);
            await WaitAsync(() => shell.Solutions.Projects.Count == 4 && shell.Solutions.Progress is { Completed: true } && shell.Solutions.RestoreState == "succeeded", "the Packages solution is open and restored", 300).ConfigureAwait(true);
            var vm = shell.OpenPackageManager(PlainProject) ?? throw new SmokeFailure("The NuGet Package Manager did not open.");
            await WaitAsync(() => vm.Sources.Contains("local"), "the package sources are listed").ConfigureAwait(true);
            vm.SelectedSource = "local";
            vm.Search = "Daedalus.Sample.Greeting";
            try
            {
                await WaitAsync(() => vm.Rows.Any(r => r is { Id: "Daedalus.Sample.Greeting", Version: "1.1.0", InstalledVersion: "1.0.0" }) && !vm.IsLoading, "the feed's package shows with its installed version", 60).ConfigureAwait(true);
            }
            catch (SmokeFailure ex)
            {
                throw new SmokeFailure($"{ex.Message} Rows: {string.Join(", ", vm.Rows.Select(r => $"{r.Id} {r.Version} (installed {r.InstalledVersion ?? "-"})"))}; error: {vm.Error ?? "none"}; loading: {vm.IsLoading}.");
            }
            vm.Selected = vm.Rows.First(r => r.Id == "Daedalus.Sample.Greeting");
            await WaitAsync(() => vm.Details is { IsLoading: false, Package: not null, HasReadme: true }, "its details and README load", 60).ConfigureAwait(true);
            vm.Details!.InstallCommand.Execute(null);
            await WaitAsync(() => vm.Details?.InstalledVersion == "1.1.0" && ReadText(PlainProject).Contains("Version=\"1.1.0\"", StringComparison.Ordinal), "Update to 1.1.0 edits the project and restores", 300).ConfigureAwait(true);
            vm.Tab = PackageTab.Installed;
            vm.Search = string.Empty; // the search box also filters the Installed tab
            await WaitAsync(() => vm.Rows.Any(r => r is { Id: "Daedalus.Sample.Text", IsTransitive: true }), "the new transitive dependency is listed", 120).ConfigureAwait(true);
        }, "projects.open").ConfigureAwait(true);

        await StepAsync("packages.pack", "M9", "Pack writes the project's NuGet package", async () =>
        {
            var created = await shell.PackAsync(PlainProject).ConfigureAwait(true);
            Check(created.Count == 1 && File.Exists(created[0]) && created[0].EndsWith("Plain.1.0.0.nupkg", StringComparison.Ordinal), "Plain.1.0.0.nupkg was created");
        }, "packages.manage").ConfigureAwait(true);

        // M9: project properties and solution management (ADR 0013), on the same solution.
        await StepAsync("projects.properties", "M9", "Property pages show provenance and edit the project; Configuration Manager; unload and reload", async () =>
        {
            var properties = shell.OpenProjectProperties(PlainProject) ?? throw new SmokeFailure("The property pages did not open.");
            await WaitAsync(() => properties.Sections.Count > 0 && !properties.IsLoading, "the Application page loads").ConfigureAwait(true);
            var items = properties.Sections.SelectMany(s => s.Items).ToDictionary(i => i.Definition.Name);
            Check(items["Nullable"].Origin.StartsWith("Set in Plain.csproj", StringComparison.Ordinal), "Nullable comes from Plain.csproj");
            items["RootNamespace"].Text = "Smoke.Plain";
            await WaitAsync(() => ReadText(PlainProject).Contains("<RootNamespace>Smoke.Plain</RootNamespace>", StringComparison.Ordinal), "RootNamespace is written to the project").ConfigureAwait(true);

            var configurations = shell.OpenConfigurationManager() ?? throw new SmokeFailure("The Configuration Manager did not open.");
            await WaitAsync(() => configurations.Rows.Count == 4, "it lists the four projects").ConfigureAwait(true);

            var multi = Path.Combine(_work, "Packages", "src", "Multi", "Multi.csproj");
            await shell.UnloadProjectAsync(multi).ConfigureAwait(true);
            await WaitAsync(() => shell.Solutions.Projects.TryGetValue(multi, out var p) && p.State == Projects.Protocol.ProjectStates.Unloaded, "Multi is unloaded").ConfigureAwait(true);
            await shell.ReloadProjectAsync(multi).ConfigureAwait(true);
            await WaitAsync(() => shell.Solutions.Projects.TryGetValue(multi, out var p) && p.State == Projects.Protocol.ProjectStates.Loaded, "Multi is loaded again", 60).ConfigureAwait(true);
        }, "packages.manage").ConfigureAwait(true);

        // M10: C# Interactive (ADR 0015), initialized with Plain (which references a package from the fixture feed).
        await StepAsync("interactive.repl", "M10", "C# Interactive runs code, references a project and a NuGet package", async () =>
        {
            var window = shell.ShowInteractive();
            await window.ExecuteAsync("var answer = 6 * 7;").ConfigureAwait(true);
            await window.ExecuteAsync("answer").ConfigureAwait(true);
            await WaitAsync(() => window.Transcript.GetText().Split('\n').Any(l => l.Trim() == "42"), "a submission prints 42", 60).ConfigureAwait(true);

            await shell.InitializeInteractiveWithProjectAsync(PlainProject).ConfigureAwait(true);
            await window.ExecuteAsync("Plain.Hello.Ada()").ConfigureAwait(true);
            await WaitAsync(() => window.Transcript.GetText().Contains("\"Hello, Ada\"", StringComparison.Ordinal), "the project's code runs: " + Tail(window.Transcript.GetText(), 1500), 120).ConfigureAwait(true);

            await window.ExecuteAsync("#r \"nuget: Daedalus.Sample.Text, 1.0.0\"").ConfigureAwait(true);
            await window.ExecuteAsync("Daedalus.Sample.Text.Text.Shout(\"hi\")").ConfigureAwait(true);
            await WaitAsync(() => window.Transcript.GetText().Contains("\"HI!\"", StringComparison.Ordinal), "a #r \"nuget:\" package runs: " + Tail(window.Transcript.GetText(), 1500), 180).ConfigureAwait(true);
        }, "packages.manage").ConfigureAwait(true);

        // M11: languages and TextMate grammars (ADR 0016).
        await StepAsync("languages.grammars", "M11", "JSON and MSBuild files are colored by their TextMate grammars", async () =>
        {
            var json = Path.Combine(_work, "smoke-settings.json");
            await File.WriteAllTextAsync(json, "{\n  \"Logging\": { \"Level\": 3, \"On\": true }\n}\n").ConfigureAwait(true);
            var editor = await OpenAsync(json).ConfigureAwait(true);
            Check(editor.Model.LanguageId == "json" && editor.GrammarScope == "source.json", $"the file is JSON with its grammar (got {editor.Model.LanguageId}, {editor.GrammarScope})");
            await WaitAsync(() => editor.LineColors(2) >= 3, "the JSON grammar colors keys, numbers and keywords").ConfigureAwait(true);
            var project = await OpenAsync(PlainProject).ConfigureAwait(true);
            Check(project.Model.LanguageId == "msbuild" && project.GrammarScope == "text.xml", "the project file is MSBuild with the XML grammar");
            await WaitAsync(() => project.LineColors(1) >= 2, "the XML grammar colors the project file").ConfigureAwait(true);
        }, "packages.manage").ConfigureAwait(true);

        await StepAsync("web.http", "M15", "The Endpoints Explorer, and a .http request to the running web app", async () =>
        {
            var (solution, http) = PrepareWebFixture();
            await shell.OpenSolutionAsync(solution).ConfigureAwait(true);
            await WaitAsync(() => shell.Solutions.Projects.Count == 1 && shell.Solutions.Progress is { Completed: true }, "the Web solution is open", 300).ConfigureAwait(true);
            await shell.ShowEndpointsAsync().ConfigureAwait(true);
            await WaitAsync(() => shell.Endpoints.Endpoints.Any(e => e.Route == "/api/todos/{id:int}") && shell.Endpoints.Endpoints.Any(e => e.Route == "/api/Orders/{id}"),
                "the Endpoints Explorer lists the minimal API and controller routes: " + (shell.Endpoints.Status ?? string.Join(", ", shell.Endpoints.Endpoints.Select(e => e.Route))), 240).ConfigureAwait(true);

            var editor = await OpenAsync(http).ConfigureAwait(true);
            Check(editor.Model.LanguageId == ShellHost.HttpLanguageId && editor.GrammarScope == "source.http", $"the .http file has its grammar (got {editor.Model.LanguageId}, {editor.GrammarScope})");
            Check(editor.HttpMargin.RequestLines.Count == 3, "the .http file shows three send glyphs");

            await shell.StartWithoutDebuggingAsync().ConfigureAwait(true);
            try
            {
                await WaitAsync(() => shell.Runs.Running.Count == 1 && shell.Runs.Running[0].Channel?.GetText().Contains("Now listening on", StringComparison.Ordinal) == true, "the web app listens", 240).ConfigureAwait(true);
                await shell.SendHttpRequestAsync(http, editor.Model.Text.Text, editor.HttpMargin.RequestLines[0]).ConfigureAwait(true);
                Check(shell.HttpResponse.Selected?.Exchange.Response?.Body == "Hello from the Web fixture",
                    "the request gets the app's answer: " + (shell.HttpResponse.Selected?.Exchange.Error ?? shell.HttpResponse.Selected?.Exchange.Response?.Body));
                await WaitAsync(() => window.GetVisualDescendants().OfType<HttpResponseView>().Any(), "the HTTP Response window renders").ConfigureAwait(true);
                Screenshot("smoke-test-web-http.png");
            }
            finally
            {
                shell.StopRunning();
            }
        }).ConfigureAwait(true);

        await StepAsync("web.hotReload", "M15", "Hot Reload applies a saved edit to the running web app (dotnet watch)", async () =>
        {
            await WaitAsync(() => shell.Runs.Running.Count == 0, "the previous run has stopped", 60).ConfigureAwait(true);
            var programFile = Path.Combine(_work, "Web", "WebApi", "Program.cs");
            var urls = (await shell.Runs.PlanAsync(Path.Combine(_work, "Web", "WebApi", "WebApi.csproj"), CancellationToken.None).ConfigureAwait(true)).Plan.Environment["ASPNETCORE_URLS"];
            await shell.StartWithHotReloadAsync().ConfigureAwait(true);
            await WaitAsync(() => shell.HotReloadProgram is not null, "the app runs under dotnet watch", 240).ConfigureAwait(true);
            var program = shell.HotReloadProgram!;
            using var client = new System.Net.Http.HttpClient { BaseAddress = new Uri(urls.Split(';')[0]), Timeout = TimeSpan.FromSeconds(10) };
            try
            {
                await WaitAsync(() => program.HotReload!.State == Workbench.Build.HotReloadState.Watching && program.Channel?.GetText().Contains("Now listening on", StringComparison.Ordinal) == true,
                    "dotnet watch waits for changes: " + Tail(program.Channel?.GetText() ?? string.Empty, 1500), 240).ConfigureAwait(true);
                var editor = await OpenAsync(programFile).ConfigureAwait(true);
                var offset = editor.Model.Text.Text.IndexOf("Hello from the Web fixture", StringComparison.Ordinal);
                editor.Model.Text.Replace(offset, "Hello from the Web fixture".Length, "Hello from Hot Reload");
                shell.Run("run.applyCodeChanges");
                var answer = string.Empty;
                await WaitAsync(() =>
                {
                    try
                    {
                        answer = client.GetStringAsync("/").GetAwaiter().GetResult();
                    }
                    catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TaskCanceledException)
                    {
                        // Restarting.
                    }

                    return answer == "Hello from Hot Reload";
                }, "the running app answers with the edited text (got '" + answer + "'): " + Tail(program.Channel?.GetText() ?? string.Empty, 1500), 120).ConfigureAwait(true);
                // dotnet watch reports the update after the app already serves it.
                await WaitAsync(() => program.HotReload!.Applied + program.HotReload.Restarts >= 1, "dotnet watch reports the update: " + Tail(program.Channel?.GetText() ?? string.Empty, 1500), 60).ConfigureAwait(true);
            }
            finally
            {
                shell.StopRunning();
            }
        }, "web.http").ConfigureAwait(true);

        await StepAsync("blazor.razor", "M16", "Razor files get C# features through the Razor generator; the Blazor app runs", async () =>
        {
            await WaitAsync(() => shell.Runs.Running.Count == 0, "the previous run has stopped", 60).ConfigureAwait(true);
            var (solution, port) = PrepareBlazorFixture();
            await shell.OpenSolutionAsync(solution).ConfigureAwait(true);
            await WaitAsync(() => shell.Solutions.Projects.Count == 1 && shell.Solutions.Progress is { Completed: true }, "the Blazor solution is open", 300).ConfigureAwait(true);
            await WaitAsync(() => shell.Languages.Status(ShellHost.CSharpLanguageId) is { State: LanguageServices.Protocol.WorkspaceStates.Ready, Projects: 1 }, "the C# service loads the Blazor project", 300).ConfigureAwait(true);
            var counter = Path.Combine(_work, "Blazor", "BlazorApp", "Components", "Pages", "Counter.razor");
            var editor = await OpenAsync(counter).ConfigureAwait(true);
            Check(editor.Model.LanguageId == ShellHost.RazorLanguageId, "Counter.razor is a Razor file");
            var client = shell.Languages.RunningClient(ShellHost.CSharpLanguageId)!;
            var at = editor.Model.PositionAt(editor.Model.Text.Text.IndexOf("@currentCount", StringComparison.Ordinal) + 3);
            string? hover = null;
            await WaitAsync(() => (hover = client.HoverAsync(editor.Model.Uri, at, CancellationToken.None).GetAwaiter().GetResult()?.Contents.Value) is not null,
                "hover on C# in the Razor file", 120).ConfigureAwait(true);
            Check(hover!.Contains("currentCount", StringComparison.Ordinal), "the hover describes the field: " + hover);
            var completion = await client.CompletionAsync(editor.Model.Uri, editor.Model.PositionAt(editor.Model.Text.Text.IndexOf("<button", StringComparison.Ordinal) + 1),
                new LanguageServices.Protocol.CompletionContext(LanguageServices.Protocol.CompletionTriggerKind.Invoked, null), CancellationToken.None).ConfigureAwait(true);
            Check(completion?.Items.Any(i => i.Label == "PageTitle") == true, "markup completion offers components");

            await shell.StartWithoutDebuggingAsync().ConfigureAwait(true);
            try
            {
                await WaitAsync(() => shell.Runs.Running.Count == 1 && shell.Runs.Running[0].Channel?.GetText().Contains("Now listening on", StringComparison.Ordinal) == true,
                    "the Blazor app listens", 300).ConfigureAwait(true);
                using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(30) };
                var page = await http.GetStringAsync(new Uri($"http://localhost:{port}/counter")).ConfigureAwait(true);
                Check(page.Contains("<h1>Counter</h1>", StringComparison.Ordinal), "the app renders the Counter page");
            }
            finally
            {
                shell.StopRunning();
            }
        }).ConfigureAwait(true);

        await StepAsync("web.languages", "M16", "Node.js and the web language servers install on demand and serve TypeScript and HTML", WebLanguagesStepAsync).ConfigureAwait(true);

        await StepAsync("dotnet.net11", "M30", "A .NET 11 solution opens with the .NET 11 SDK, builds, and runs on .NET 11", async () =>
        {
            await WaitAsync(() => shell.Runs.Running.Count == 0, "the previous run has stopped", 60).ConfigureAwait(true);
            Check(File.Exists(Path.Combine(samples, "Net11", "Net11.slnx")), $"the samples folder {samples} has Net11");
            var target = Path.Combine(_work, "Net11");
            Copy(Path.Combine(samples, "Net11"), target);
            await shell.OpenSolutionAsync(Path.Combine(target, "Net11.slnx")).ConfigureAwait(true);
            await WaitAsync(() => shell.Solutions.Projects.Count == 5 && shell.Solutions.Progress is { Completed: true }, "the .NET 11 solution is open", 300).ConfigureAwait(true);
            var sdk = Path.GetFileName(Path.TrimEndingDirectorySeparator(shell.Solutions.SdkPath ?? string.Empty));
            Check(sdk.StartsWith("11.0.", StringComparison.Ordinal), $"the build host loaded the .NET 11 SDK (it has {sdk}; {shell.Solutions.SdkWarning})");

            shell.Runs.SetStartupProject(Path.Combine(target, "src", "App", "App.csproj"));
            var before = shell.Builds.LastBuild;
            shell.Run("build.buildSolution");
            await WaitAsync(() => shell.Builds.LastBuild is { } b && b != before, "the build finishes", 420).ConfigureAwait(true);
            Check(shell.Builds.LastBuild!.Result.Succeeded, "the build succeeded:\n" + Tail(shell.Builds.Channel.GetText(), 3000));
            shell.Run("run.startWithoutDebugging");
            string Output() => shell.Workbench.Output.Channels.FirstOrDefault(c => c.Name == "Run: App")?.GetText() ?? string.Empty;
            await WaitAsync(() => Output().Contains("has exited with code 0", StringComparison.Ordinal), "App runs and exits with 0", 300).ConfigureAwait(true);
            Check(Output().Contains("runtime=11 library=net11.0", StringComparison.Ordinal), "App ran on .NET 11 with the net11.0 build of its library:\n" + Tail(Output(), 1000));
        }).ConfigureAwait(true);

        const string wasmTitle = "F5 debugs a Blazor WebAssembly app in the browser: a breakpoint in a .razor file stops";
        if (Browsers.Find(shell.Workbench.Settings.GetString(Browsers.SettingKey, "auto")) is null && Environment.GetEnvironmentVariable("CI") != "true")
        {
            Report("blazor.wasmDebug", "M16", wasmTitle, "skipped", 0, "No Chrome, Edge or Chromium is installed.");
        }
        else
        {
            await StepAsync("blazor.wasmDebug", "M16", wasmTitle, WasmDebugStepAsync).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Browser debugging (M16c, ADR 0026): F5 on samples/BlazorWasm starts the dev server and a Chromium browser through
    /// vscode-js-debug, and a breakpoint in Counter.razor's C# stops. Stopping the session stops the dev server.
    /// </summary>
    private async Task WasmDebugStepAsync()
    {
        await WaitAsync(() => shell.Runs.Running.Count == 0 && !shell.Debugger.IsActive, "the previous run has stopped", 60).ConfigureAwait(true);
        Check(File.Exists(Path.Combine(samples, "BlazorWasm", "BlazorWasm.slnx")), $"the samples folder {samples} has BlazorWasm");
        var target = Path.Combine(_work, "BlazorWasm");
        Copy(Path.Combine(samples, "BlazorWasm"), target);
        using (var socket = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0))
        {
            socket.Start();
            var port = ((System.Net.IPEndPoint)socket.LocalEndpoint).Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
            socket.Stop();
            var settings = Path.Combine(target, "BlazorWasm", "Properties", "launchSettings.json");
            File.WriteAllText(settings, File.ReadAllText(settings).Replace("5045", port, StringComparison.Ordinal));
        }

        await shell.OpenSolutionAsync(Path.Combine(target, "BlazorWasm.slnx")).ConfigureAwait(true);
        await WaitAsync(() => shell.Solutions.Projects.Count == 1 && shell.Solutions.Progress is { Completed: true }, "the WebAssembly solution is open", 300).ConfigureAwait(true);
        var counter = Path.Combine(target, "BlazorWasm", "Pages", "Counter.razor");
        var line = Array.FindIndex(File.ReadAllLines(counter), l => l.Contains("// [initialized]", StringComparison.Ordinal)) + 1;
        shell.Debugger.Breakpoints.Toggle(counter, line);
        shell.Run("debug.start");
        try
        {
            // The first session downloads vscode-js-debug (and Node.js, if M16b's step didn't).
            await WaitAsync(() => shell.Debugger.State == DebugState.Stopped, "the browser session stops at the breakpoint (Debug Console:\n" + Tail(shell.DebugLog.GetText(), 2000) + ")", 600).ConfigureAwait(true);
            await WaitAsync(() => shell.CallStack.Frames.Count > 0, "the call stack", 60).ConfigureAwait(true);
            Check(shell.CallStack.Frames[0].Name.Contains("Counter.OnInitialized", StringComparison.Ordinal), "the top frame is Counter.OnInitialized, in the browser: " + shell.CallStack.Frames[0].Name);
            Screenshot("smoke-test-blazor-wasm-debug.png");
        }
        finally
        {
            shell.Debugger.Breakpoints.Clear();
            await shell.Debugger.StopAsync().ConfigureAwait(true);
        }

        await WaitAsync(() => shell.Runs.Running.Count == 0, "the dev server stops with the session", 60).ConfigureAwait(true);
    }

    /// <summary>The web language servers (M16b): Node.js and the servers install, then serve TypeScript and HTML files.</summary>
    private async Task WebLanguagesStepAsync()
    {
        if (Workbench.Node.NodeRuntime.Platform is null)
        {
            throw new SmokeFailure("There is no Node.js build for this platform.");
        }

        await shell.InstallWebLanguagesAsync().ConfigureAwait(true);
        Check(shell.WebServers.IsInstalled, "Node.js and the web language servers are installed: " + Tail(shell.LanguageLog.GetText(), 1500));
        var folder = Path.Combine(_work, "site");
        Directory.CreateDirectory(folder);
        var script = Path.Combine(folder, "app.ts");
        await File.WriteAllTextAsync(script, "const count: number = \"three\";\nconsole.log(count);\n").ConfigureAwait(true);
        var page = Path.Combine(folder, "index.html");
        await File.WriteAllTextAsync(page, "<!DOCTYPE html>\n<html>\n<body>\n<di\n</body>\n</html>\n").ConfigureAwait(true);

        var editor = await OpenAsync(script).ConfigureAwait(true);
        await WaitAsync(() => shell.Languages.Diagnostics.For(script).Any(d => d.Code == "2322"), "TypeScript reports the type error (TS2322): " + Tail(shell.LanguageLog.GetText(), 1500), 180).ConfigureAwait(true);
        var html = await OpenAsync(page).ConfigureAwait(true);
        await WaitAsync(() => shell.Languages.RunningClient("html") is not null, "the HTML server starts", 120).ConfigureAwait(true);
        var completion = await shell.Languages.RunningClient("html")!.CompletionAsync(html.Model.Uri, html.Model.PositionAt(html.Model.Text.Text.IndexOf("<di", StringComparison.Ordinal) + 3),
            new LanguageServices.Protocol.CompletionContext(LanguageServices.Protocol.CompletionTriggerKind.Invoked, null), CancellationToken.None).ConfigureAwait(true);
        Check(completion?.Items.Any(i => i.Label == "div") == true, "HTML completion offers div");
        Check(editor.Model.LanguageId == "typescript", "app.ts is TypeScript");
    }

    /// <summary>samples/Blazor with a free port in its launch profile.</summary>
    private (string Solution, string Port) PrepareBlazorFixture()
    {
        Check(File.Exists(Path.Combine(samples, "Blazor", "Blazor.slnx")), $"the samples folder {samples} has Blazor");
        var target = Path.Combine(_work, "Blazor");
        Copy(Path.Combine(samples, "Blazor"), target);
        using var socket = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        socket.Start();
        var port = ((System.Net.IPEndPoint)socket.LocalEndpoint).Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        socket.Stop();
        var settings = Path.Combine(target, "BlazorApp", "Properties", "launchSettings.json");
        File.WriteAllText(settings, File.ReadAllText(settings).Replace("5280", port, StringComparison.Ordinal));
        return (Path.Combine(target, "Blazor.slnx"), port);
    }

    /// <summary>samples/Web with a free port in its launch profile and .http file.</summary>
    private (string Solution, string Http) PrepareWebFixture()
    {
        Check(File.Exists(Path.Combine(samples, "Web", "Web.slnx")), $"the samples folder {samples} has Web");
        var target = Path.Combine(_work, "Web");
        Copy(Path.Combine(samples, "Web"), target);
        using var socket = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        socket.Start();
        var port = ((System.Net.IPEndPoint)socket.LocalEndpoint).Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        socket.Stop();
        var http = Path.Combine(target, "WebApi", "WebApi.http");
        foreach (var file in new[] { Path.Combine(target, "WebApi", "Properties", "launchSettings.json"), http })
        {
            File.WriteAllText(file, File.ReadAllText(file).Replace("5248", port, StringComparison.Ordinal));
        }

        return (Path.Combine(target, "Web.slnx"), http);
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
    private string PlainProject => Path.Combine(_work, "Packages", "src", "Plain", "Plain.csproj");

    private string PreparePackagesFixture()
    {
        Check(File.Exists(Path.Combine(samples, "Packages", "Packages.slnx")), $"the samples folder {samples} has Packages");
        Copy(Path.Combine(samples, "Packages"), Path.Combine(_work, "Packages"));
        return Path.Combine(_work, "Packages", "Packages.slnx");
    }

    /// <summary>A file's text, or empty while another process writes it.</summary>
    private static string ReadText(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (IOException)
        {
            return string.Empty;
        }
    }

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
            Visual root = window; // up to the host, with the decorations Avalonia draws
            while (root.GetVisualParent() is { } parent)
            {
                root = parent;
            }

            var scaling = window.RenderScaling;
            var size = new PixelSize(Math.Max(1, (int)(root.Bounds.Width * scaling)), Math.Max(1, (int)(root.Bounds.Height * scaling)));
            using var bitmap = new RenderTargetBitmap(size, new Vector(96 * scaling, 96 * scaling));
            bitmap.Render(root);
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
