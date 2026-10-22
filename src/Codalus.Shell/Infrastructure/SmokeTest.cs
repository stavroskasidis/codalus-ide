using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Codalus.Debugging;
using Codalus.Platform;
using Codalus.Shell.Editor;
using Codalus.Shell.Theming;
using Codalus.Shell.ViewModels;
using Codalus.Shell.Views;
using Codalus.Workbench.Debugging;
using Codalus.Workbench.Debugging.Remote;
using Codalus.Workbench.Plugins;
using Codalus.Workbench.Settings;
using Codalus.Workbench.Localization;

namespace Codalus.Shell.Infrastructure;

/// <summary>
/// The end-to-end smoke test (<c>--smoke-test samples</c>): drives the real, windowed app through one feature of each
/// milestone (M1–M6 on a copy of <c>samples/BuildAndRun</c>, M8 on part of <c>samples/Testing</c>, M9 on <c>samples/Packages</c>), with the hosts,
/// plugins and debugger it was installed with. It is what CI runs on the published Native AOT build and on the release packages of every OS, so it catches what
/// the in-process headless tests cannot: trimming and AOT breaks, the installed folder layout, and the real windowing
/// platform. Results go to <c>smoke-test.json</c>, <c>smoke-test.md</c> and screenshots in the logs folder, and to
/// standard output; the process exits with 0 when no step failed. A step the machine can't run (no browser, outside CI)
/// is skipped, not failed; a step whose prerequisite failed is skipped too, after that failure.
/// </summary>
internal sealed partial class SmokeTest(Window window, ShellHost shell, string samples)
{
    private const string Branch = "main";
    private static readonly TimeSpan s_watchdog = TimeSpan.FromMinutes(40); // the command sweep's 25 at most, with the rest
    private static readonly string[] s_commands = ["workbench.showCommandPalette", "build.buildSolution", "debug.start", "git.commit"];
    private static readonly string[] s_themes = ["blue", "light", "highContrast", "dark"];

    /// <summary>The themes the M29 step photographs.</summary>
    private static readonly string[] s_chromeThemes = ["blue", "light", "dark"];

    private readonly List<StepResult> _results = [];
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private string _workRoot = string.Empty;
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
            exitCode = Passed ? 0 : 1;
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
        if (_workRoot.Length == 0 || !Directory.Exists(_workRoot))
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

        for (var attempt = 0; attempt < 20 && Directory.Exists(_workRoot); attempt++)
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(_workRoot, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal); // git writes its object files read-only
                }

                Directory.Delete(_workRoot, recursive: true);
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

        // The steps read programs' output in the IDE: Windows' default console windows (a classic IDE's) would keep it
        // from them and open on the desktop.
        await shell.Workbench.Settings.SetAsync(ShellHost.RunConsoleSetting, "outputWindow", SettingScope.User, CancellationToken.None).ConfigureAwait(true);

        // The steps' terminals (here and through the remote server) keep no shell history in the real home.
        Environment.SetEnvironmentVariable("HISTFILE", "/dev/null");
        await shell.Workbench.Settings.SetAsync(ShellHost.DebugConsoleSetting, "internalConsole", SettingScope.User, CancellationToken.None).ConfigureAwait(true);

        // M1: shell and plugin foundation.
        await StepAsync("shell.window", "M1", "Main window, status bar and default commands", () =>
        {
            Check(window.IsVisible && window.Bounds.Width > 0, "the main window is visible");
            Check(shell.StatusBar.Right.Any(i => i.Item.Id == "codalus.notifications"), "the notifications status item");
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
            shell.Workbench.Notifications.Info(Localizer.Get("msg.smokeTest.smokeTestNotification", "Smoke test notification"));
            await WaitAsync(() => shell.Notifications.Count > 0, "the notification arrives").ConfigureAwait(true);
            shell.Run("workbench.clearNotifications");
            await WaitAsync(() => shell.Notifications.Count == 0, "notifications clear").ConfigureAwait(true);
        }).ConfigureAwait(true);

        await StepAsync("plugins.host", "M1", "Built-in plugins load and run in the plugin host", async () =>
        {
            var builtIn = shell.Plugins.Plugins.Where(p => p.IsBuiltIn).ToList();
            Check(builtIn.Any(p => p.Id == "codalus.welcome") && builtIn.Any(p => p.Id == "codalus.templates"), $"the built-in plugins are discovered (found: {string.Join(", ", builtIn.Select(p => p.Id))})");
            var broken = builtIn.Where(p => p.State is PluginState.Invalid or PluginState.Incompatible or PluginState.Failed).ToList();
            Check(broken.Count == 0, $"no built-in plugin is broken ({string.Join(", ", broken.Select(p => $"{p.Id}: {p.State}"))})");

            // The Start page is a plugin tree view: its items come from the out-of-process plugin host. A plugin command
            // shows it (the layout may have been reset since startup).
            await WaitAsync(() => shell.Plugins.Plugins.Any(p => p is { Id: "codalus.welcome", State: PluginState.Active }), "the Welcome plugin activates at startup", 90).ConfigureAwait(true);
            await shell.RunAsync("welcome.show", []).ConfigureAwait(true);
            await WaitAsync(() => shell.Layout.IsVisible("welcome.view"), "welcome.show shows the Start page", 60).ConfigureAwait(true);
            var tree = (TreeToolViewModel)shell.Layout.OpenToolWindows.Single(t => t.Id == "welcome.view").Content!;
            await WaitAsync(() => tree.Roots.Count > 0, "the Start page has items").ConfigureAwait(true);
        }).ConfigureAwait(true);

        // M49: the shipped hosts' runtime requirement, resolved by the check as hostfxr resolves it: the plugin host ran
        // above, so the check must agree that its runtime is installed (a check that misjudged would block every host).
        await StepAsync("setup.hostRuntime", "M49", "The IDE's .NET runtime: what the shipped hosts ask for is found under their root, as their start shows", () =>
        {
            var requirements = Codalus.Workbench.Setup.IdeRuntime.Requirements(AppContext.BaseDirectory, Environment.GetEnvironmentVariable);
            Check(requirements.Any(r => r.Name == Codalus.Rpc.FrameworkReference.NetCoreApp), $"the hosts' runtimeconfig.json files name Microsoft.NETCore.App (found: {string.Join(", ", requirements)})");
            var row = shell.EnvironmentCheck.HostRuntimeStatus();
            Check(row.State == Codalus.Workbench.Setup.ToolState.Found, $"the runtime row is Found ({row.State}: {row.Detail})");
            var host = PluginHostLocator.Resolve();
            Check(host is not null && Codalus.Rpc.HostRuntimeCheck.Check(host) is null or { IsSatisfied: true }, "the plugin host's own check passes");
            return Task.CompletedTask;
        }, "plugins.host").ConfigureAwait(true);

        // M46: the first-run setup, as at a first start, to the start window (the website's screenshot of its appearance step).
        await StepAsync("shell.setup", "M46", "The first-run setup: a theme family previewed live, a keyboard scheme, VS Code's settings imported and the tools checked; Done opens the start window with them applied", SetupStepAsync).ConfigureAwait(true);

        // M38: the start window, with the recent list the run has so far (the website's screenshot).
        await StepAsync("shell.startWindow", "M38", "The start window lists recent solutions and closes on Continue without code", async () =>
        {
            shell.Recent.Add(Path.Combine(samples, "BuildAndRun", "BuildAndRun.slnx"));
            shell.ShowStartWindow();
            await WaitAsync(() => shell.IsStartWindowOpen && shell.StartWindowModel is { IsEmpty: false }, "the start window opens with its recent list").ConfigureAwait(true);
            // Its own frames, not the main window's: the screenshot renders the start window once it has drawn itself.
            var start = Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
                ? desktop.Windows.OfType<Views.StartWindow>().FirstOrDefault()
                : null;
            await RenderedAsync(start).ConfigureAwait(true);
            Screenshot("start-window.png", start);
            shell.StartWindowModel!.ContinueCommand.Execute(null);
            await WaitAsync(() => !shell.IsStartWindowOpen, "Continue without code closes it").ConfigureAwait(true);
        }).ConfigureAwait(true);

        // M23: personalization in the shipped app; the screenshots are the website's.
        await StepAsync("shell.personalization", "M23", "Fonts and Colors, Customize, and a profile switched to and back", async () =>
        {
            shell.Run("workbench.openFontsAndColors");
            await WaitAsync(() => shell.Layout.ActiveDocument is FontsAndColorsDocument { ViewModel.Items.Count: > 0 }, "Fonts and Colors lists the colors").ConfigureAwait(true);
            await RenderedAsync().ConfigureAwait(true);
            Screenshot("personalization-fonts-and-colors.png");
            shell.Run("workbench.closeActiveDocument");

            shell.Run("workbench.customize");
            await WaitAsync(() => shell.Layout.ActiveDocument is CustomizeDocument { ViewModel.Items.Count: > 0 }, "Customize lists the standard toolbar").ConfigureAwait(true);
            await RenderedAsync().ConfigureAwait(true);
            Screenshot("personalization-customize.png");
            shell.Run("workbench.closeActiveDocument");

            var fontSize = shell.Workbench.Settings.GetValue("editor.fontSize").GetRawText();
            await shell.NewProfileAsync("Smoke", copyActive: false).ConfigureAwait(true);
            Check(shell.Profiles.Active == "Smoke" && Equals(shell.Workbench.Context.GetValue("profile"), "Smoke"), "a new profile is active");
            await shell.SwitchProfileAsync(Workbench.Settings.ProfileStore.DefaultName).ConfigureAwait(true);
            await shell.DeleteProfileAsync("Smoke", confirm: false).ConfigureAwait(true);
            Check(shell.Profiles.Active == Workbench.Settings.ProfileStore.DefaultName && !shell.Profiles.Exists("Smoke"), "back on Default, the profile deleted");
            Check(shell.Workbench.Settings.GetValue("editor.fontSize").GetRawText() == fontSize, "Default's settings came back");
        }).ConfigureAwait(true);

        // M34d: the UI gallery's empty states, before a solution opens.
        await StepAsync("shell.galleryEmpty", "M34d", "The gallery: every tool window empty, Code Search, the WCF Test Client and the setup's steps (M34g), in Classic's four themes and Harbor's three (M34h)", GalleryEmptyStepAsync).ConfigureAwait(true);

        // M2: project system (build host, MSBuild evaluation, Solution Explorer).
        await StepAsync("projects.open", "M2", "A trusted solution opens and evaluates", async () =>
        {
            PrepareFixture();
            shell.Trust.Trust(Work);
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
            Check(shell.StatusBar.Right.Any(i => i.Item.Id == "codalus.editor.language" && i.Text == "C#"), "the status bar shows C#");
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

        await StepAsync("editor.findInFiles", "M3", "Find and Replace: Find in Files from the floating window into Find Results", async () =>
        {
            await OpenAsync(Program).ConfigureAwait(true);
            shell.Run("workbench.action.findInFiles");
            await WaitAsync(() => shell.FindReplace.IsOpen, "Find and Replace opens", 10).ConfigureAwait(true);
            shell.FindReplace.Query = "Greet";
            shell.FindReplace.LookIn = ViewModels.FindScopes.EntireSolution;
            await Task.Delay(300).ConfigureAwait(true);
            Screenshot("smoke-test-find-and-replace.png");
            await shell.FindReplace.FindAllAsync().ConfigureAwait(true);
            Check(!shell.FindReplace.IsOpen, "Find All closes the window");
            await WaitAsync(() => shell.FindResults.Rows.Any(r => !r.IsHeader && r.Path == Program && r.Highlights is { Count: > 0 }), "Find Results lists Program.cs with the match highlighted", 30).ConfigureAwait(true);
            await shell.FindResults.GoToAsync(1).ConfigureAwait(true);
            await WaitAsync(() => shell.ActiveEditor?.Controller?.Editor.SelectedText.Equals("Greet", StringComparison.OrdinalIgnoreCase) == true, "Next opens the first match selected", 30).ConfigureAwait(true);
            await Task.Delay(300).ConfigureAwait(true);
            Screenshot("smoke-test-find-results.png");
            shell.ActiveEditor?.Controller?.Editor.TextArea.ClearSelection(); // the next steps start from a caret, not the match
            if (shell.FindResults.ReferenceSets.FirstOrDefault() is { } references)
            {
                shell.FindResults.CurrentSet = references; // back to the references, as the theme screenshots (the website's) show them
            }
        }, "language.ready").ConfigureAwait(true);

        await StepAsync("navigation.codeSearch", "M47", "Code Search: the index saved next to the solution, a type found with its container and project, the editable preview on its file, prefixes, Enter, and Feature Search", CodeSearchStepAsync, "language.ready").ConfigureAwait(true);

        await StepAsync("editor.intelliSense", "0.5.0", "IntelliSense: signature help in the editor's colors with the overloads' chevrons, completion below it with kind icons, filters and the description, and Quick Info with a diagnostic's link and Fix", IntelliSenseStepAsync, "language.ready").ConfigureAwait(true);

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
            await WaitAsync(() => shell.DocumentOutline.Roots.Count > 0, "the Document Outline shows Greeter.cs", 30).ConfigureAwait(true);
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

        await StepAsync("shell.themeFamilies", "M45", "Harbor, Graphite, Daylight and Ember, each Light, Dark and High contrast, over the open solution; Harbor follows the system; the family picker in Options", ThemeFamiliesStepAsync, "language.ready").ConfigureAwait(true);

        // M34a: the menus as trees with icons, Ctrl+Space through the keyboard, output that follows.
        await UxPassStepsAsync().ConfigureAwait(true);

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
        await StepAsync("build.solution", "M4", "Build Solution succeeds, shown in the status bar as a task with a progress bar", async () =>
        {
            var before = shell.Builds.LastBuild;
            shell.Run("build.buildSolution");

            // While it runs, the status bar shows it as a task with a progress bar and cancel (M22a); the site shows it.
            // Another task (a project reloading after the rename's save) may hold the status bar first: wait for the build's.
            bool Building() => shell.StatusBar.HasProgress && shell.StatusBar.DisplayMessage.StartsWith("Building ", StringComparison.Ordinal);
            await WaitAsync(() => Building() || (shell.Builds.LastBuild is { } done && done != before), "the build shows in the status bar", 60).ConfigureAwait(true);
            if (Building())
            {
                Check(shell.StatusBar.DisplayMessage.StartsWith("Building ", StringComparison.Ordinal) && shell.StatusBar.CanCancelProgress, $"the status bar says what's building and offers cancel ({shell.StatusBar.DisplayMessage})");
                Screenshot("smoke-test-progress.png");
            }

            await WaitAsync(() => BuildDone(before), "the build finishes", 420).ConfigureAwait(true);
            Check(shell.Builds.LastBuild!.Result.Succeeded, "the build succeeded:\n" + Tail(shell.Builds.Channel.GetText(), 3000));
            await WaitAsync(() => !shell.StatusBar.HasProgress, "the status bar is ready again", 30).ConfigureAwait(true);
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
            var marks = new System.Collections.Concurrent.ConcurrentQueue<Codalus.Terminal.CommandMark>();
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
                await WaitAsync(() => marks.Any(m => m is { Kind: Codalus.Terminal.CommandMarkKind.CommandEnd, ExitCode: 0 }), "shell integration reports the command's exit code (OSC 133)").ConfigureAwait(true);
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

        // M26b: build depth (ADR 0034, 0035).
        await StepAsync("build.depth", "M26", "The fast up-to-date check, a binary log in the viewer, Build Timings, Batch Build, and the ResX and T4 custom tools", BuildDepthStepAsync, "build.solution").ConfigureAwait(true);

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

            // The stop refreshes the window too: a refresh that starts later wins, and the earlier one leaves the rows to it.
            shell.Layout.ShowToolWindow(ThreadsViewModel.ToolWindowId);
            await shell.ThreadList.RefreshAsync().ConfigureAwait(true);
            await WaitAsync(() => shell.ThreadList.Rows.Any(r => r.IsCurrent), "the Threads window marks the current thread", 30).ConfigureAwait(true);
            shell.Layout.ShowToolWindow(ModulesViewModel.ToolWindowId);
            await WaitAsync(() => shell.ModuleList.Rows.Any(r => r.Name == "Hello.dll"), "the Modules window lists Hello.dll").ConfigureAwait(true);
            shell.Run("debug.exceptionSettings");
            await WaitAsync(() => window.GetVisualDescendants().OfType<ExceptionSettingsView>().Any(), "the Exception Settings window renders").ConfigureAwait(true);
            Check(shell.ExceptionSettingsWindow.Rows.Any(r => r.Type == "System.NullReferenceException"), "it lists the common exception types");
            Screenshot("smoke-test-debug-depth.png");

            shell.Run("debug.stop");
            await WaitAsync(() => shell.Debugger.State == DebugState.Idle, "the session ends", 60).ConfigureAwait(true);
        }, "debug.session").ConfigureAwait(true);

        await StepAsync("debug.companion", "M14", "Parallel Stacks, the grid visualizer, a pinned data tip, and a dump written and opened by the debug companion", async () =>
        {
            var lines = File.ReadAllLines(Program);
            var argsLine = Array.FindIndex(lines, l => l.Contains("Console.WriteLine($\"args", StringComparison.Ordinal)) + 1;
            var cwdLine = Array.FindIndex(lines, l => l.Contains("Console.WriteLine($\"cwd", StringComparison.Ordinal)) + 1;
            await shell.RunToLineAsync(Program, argsLine).ConfigureAwait(true);
            await WaitAsync(() => shell.Debugger.State == DebugState.Stopped && shell.ActiveEditor?.Controller?.CurrentDebugLine() == argsLine, $"the debugger stops at line {argsLine}", 300).ConfigureAwait(true);

            // Parallel Stacks: the main thread's frames, marked as the active thread's.
            shell.Layout.ShowToolWindow(ParallelStacksViewModel.ToolWindowId);
            await shell.ParallelStacksWindow.RefreshAsync().ConfigureAwait(true);
            Check(Flatten(shell.ParallelStacksWindow.Roots).Any(n => n.HasCurrentThread && n.Frames.Any(f => f.Contains("Main", StringComparison.Ordinal))), "Parallel Stacks shows the active thread in Main");

            // The grid visualizer on args.
            await shell.Locals.RefreshAsync().ConfigureAwait(true);
            var args = shell.Locals.Roots.SingleOrDefault(r => r.Name == "args") ?? throw new SmokeFailure("Locals has no args.");
            await shell.VisualizeAsync(args, VisualizerKind.Grid).ConfigureAwait(true);
            await WaitAsync(() => shell.ActiveEditor?.Model is { ReadOnlyLabel: "Grid Visualizer" } model && model.Text.Text.Contains("two words", StringComparison.Ordinal), "the Grid Visualizer lists args").ConfigureAwait(true);

            // A pinned data tip, evaluated at the next pause.
            var pin = shell.Debugger.PinnedTips.Pin(Program, cwdLine, "args[1]");
            shell.Debugger.PinnedTips.SetComment(pin, "the second argument");
            await shell.RunToLineAsync(Program, cwdLine).ConfigureAwait(true);
            await WaitAsync(() => shell.Debugger.State == DebugState.Stopped && pin.Value == "\"two words\"", "the pinned data tip shows args[1] at the next pause", 60).ConfigureAwait(true);
            var editor = await OpenAsync(Program).ConfigureAwait(true);
            await WaitAsync(() => editor.PinLayer.Pins.TryGetValue(cwdLine, out var control) && control.IsVisible, "the pinned data tip floats over its line").ConfigureAwait(true);
            Screenshot("smoke-test-debug-companion.png");

            // A full dump of the paused program, opened in the dump view.
            var pid = shell.Debugger.ProcessId ?? throw new SmokeFailure("The session has no process id.");
            var dump = Path.Combine(Path.GetTempPath(), $"codalus-smoke-{pid.ToString(CultureInfo.InvariantCulture)}.dmp");
            Codalus.Debugging.Companion.CreateDumpResult created;
            try
            {
                created = await shell.Companion.CreateDumpAsync(pid, dump, "full", CancellationToken.None).ConfigureAwait(true);
            }
            finally
            {
                // Ended even when the dump fails, so the steps after this one start without a session.
                shell.Debugger.PinnedTips.Clear();
                shell.Run("debug.stop");
                await WaitAsync(() => shell.Debugger.State == DebugState.Idle, "the session ends", 60).ConfigureAwait(true);
            }

            Check(File.Exists(created.Path) && created.Size > 0, $"the companion writes a dump ({created.Size} bytes)");
            try
            {
                var document = await shell.OpenDumpAsync(created.Path).ConfigureAwait(true) ?? throw new SmokeFailure("The dump didn't open (the notification says why).");
                await WaitAsync(() => window.GetVisualDescendants().OfType<DumpView>().Any(), "the dump view renders").ConfigureAwait(true);
                var main = document.ViewModel.Summary.Threads.SelectMany(t => t.Frames).FirstOrDefault(f => f.Method.Contains("Main", StringComparison.Ordinal));
                Check(main is not null, "the dump's threads include Main: " + string.Join(", ", document.ViewModel.Summary.Threads.SelectMany(t => t.Frames).Take(8).Select(f => f.Method)));
                Check(main!.File is { } file && file.EndsWith("Program.cs", StringComparison.OrdinalIgnoreCase) && main.Line == cwdLine, $"Main's frame maps to Program.cs line {cwdLine} (got {main.File}:{main.Line})");

                await document.ViewModel.LoadHeapAsync().ConfigureAwait(true);
                Check(document.ViewModel.Heap.Any(r => r.Name == "System.String"), "the heap lists System.String");
                Screenshot("smoke-test-dump.png");
                shell.Layout.CloseDocument(document);
                await document.ViewModel.CloseAsync().ConfigureAwait(true);
            }
            finally
            {
                // Best effort: a failure above may leave the dump open, and must not be hidden by this.
                try
                {
                    File.Delete(created.Path);
                }
                catch (IOException ex)
                {
                    shell.Workbench.Log.AppendLine($"[smoke] Could not delete the dump: {ex.Message}");
                }
            }
        }, "debug.session").ConfigureAwait(true);

        // M6: Git.
        await StepAsync("git.changes", "M6", "Git status, diff, stage, commit, blame and branches", async () =>
        {
            await shell.Git.RefreshAsync().ConfigureAwait(true);
            await WaitAsync(() => shell.Git.Status is { Branch: Branch } s && s.Files.Any(f => f.Path == Greeter) && s.Files.Any(f => f.Path == Program), "Git lists the edited files on main", 60).ConfigureAwait(true);
            await WaitAsync(() => shell.StatusBar.Right.Any(i => i.Item.Id == "codalus.git.branch" && i.Text.Contains(Branch, StringComparison.Ordinal)), "the status bar shows the branch").ConfigureAwait(true);

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
            await shell.Git.SwitchAsync(new Codalus.Workbench.Git.GitBranch("smoke/branch", false, false, null, string.Empty)).ConfigureAwait(true);
            try
            {
                await shell.Git.MergeAsync("smoke/theirs").ConfigureAwait(true);
            }
            catch (Codalus.Workbench.Git.GitException)
            {
                // The expected conflict.
            }

            await WaitAsync(() => shell.Git.Status is { Operation: "merge" } && shell.Git.Status.Conflicts.Any(), "the merge stops on the conflict").ConfigureAwait(true);
            _ = await shell.OpenMergeEditorAsync(Greeter).ConfigureAwait(true) ?? throw new SmokeFailure("The merge editor didn't open.");
            await WaitAsync(() => window.GetVisualDescendants().OfType<MergeEditorView>().Any(), "the merge editor renders").ConfigureAwait(true);
            var merge = window.GetVisualDescendants().OfType<MergeEditorView>().Single();
            var mergeDocument = shell.Layout.Documents.OfType<MergeEditorDocument>().Single();

            // The redesigned editor (2026-10-19): the file's name as the tab, the branches in the headers, the merge
            // base's text in the result instead of markers, a check box beside each block, and the count.
            Check(mergeDocument.Title == "Greeter.cs", "the tab is the file's name");
            Check(merge.ViewModel.IncomingTitle == "Incoming: smoke/theirs" && merge.ViewModel.CurrentTitle == "Current: smoke/branch", "the headers name the branches");
            Check(merge.ResultText.Contains("Hello from", StringComparison.Ordinal) && !merge.ResultText.Contains("<<<<<<<", StringComparison.Ordinal), "the result shows the merge base, not git's markers");
            Check(merge.SummaryText == "1 Conflict (1 Remaining)" && !merge.CanAcceptMerge, "the toolbar counts the conflict");
            await RenderedAsync().ConfigureAwait(true);
            Screenshot("smoke-test-merge-editor.png");

            var incomingBox = merge.CheckBoxesFor(MergeSide.Incoming).Single();
            incomingBox.IsChecked = true;
            incomingBox.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
            Check(merge.ResultText.Contains("Theirs from", StringComparison.Ordinal) && merge.SummaryText == "1 Conflict (0 Remaining)" && merge.CanAcceptMerge, "the incoming block's check box takes it");
            Check(mergeDocument.Title == "Greeter.cs*" && merge.HeaderFor(MergeSide.Incoming).IsChecked == true, "the tab shows the change, and the header's box follows");
            await RenderedAsync().ConfigureAwait(true);
            Screenshot("smoke-test-merge-editor-resolved.png");
            await merge.AcceptMergeAsync().ConfigureAwait(true);
            await shell.ContinueOperationAsync().ConfigureAwait(true);
            await WaitAsync(() => shell.Git.Status is { Operation: null, Files.Count: 0 }, "the merge is committed").ConfigureAwait(true);
            Check((await File.ReadAllTextAsync(Greeter).ConfigureAwait(true)).Contains("Theirs from", StringComparison.Ordinal), "the incoming side won");
        }, "git.changes").ConfigureAwait(true);

        await StepAsync("git.hosting", "M13b", "GitHub on a mock server: device sign-in, pull requests, checkout, review comments, checks, # completion, askpass and worktrees", HostingStepAsync, "git.changes").ConfigureAwait(true);

        await StepAsync("ai.chat", "M28a", "AI Chat with an OpenAI-compatible mock server: off until turned on, a question about the selection streamed, its code applied after the preview", AiChatStepAsync).ConfigureAwait(true);

        // M8: testing, on part of samples/Testing (xUnit on VSTest and on Microsoft.Testing.Platform).
        await StepAsync("testing.discover", "M8", "The Test Explorer lists tests from source; the solution closed for it keeps its session in .codalus/state", async () =>
        {
            var solution = PrepareTestingFixture();
            var previousState = shell.SolutionStateDirectory;
            await shell.OpenSolutionAsync(solution).ConfigureAwait(true);
            if (previousState is not null)
            {
                // ADR 0070: closing a solution writes its session next to it, git-ignored by the state folder's own .gitignore.
                await shell.SolutionState.FlushAsync().ConfigureAwait(true);
                Check(File.Exists(Path.Combine(previousState, "session.json")), $"the closed solution's session is in {previousState}");
                Check(File.Exists(Path.Combine(Path.GetDirectoryName(previousState)!, ".gitignore")), "the state folder ignores itself in Git");
            }

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

        // M34d: the UI gallery with data (the Testing solution after its runs), the documents, the chrome and the error states.
        await StepAsync("shell.gallery", "M34d", "The gallery: every tool window with data, the documents and the window's tabs, the error states, Code Search's results, the color theme cards and the WCF references page (M34g), in Classic's four themes and Harbor's three (M34h)", GalleryDataStepAsync, "testing.run").ConfigureAwait(true);

        // M9: NuGet packages, on a copy of samples/Packages and its local feed (ADR 0012).
        await StepAsync("packages.manage", "M9", "The NuGet Package Manager browses a feed, updates a package and restores it", async () =>
        {
            await shell.OpenSolutionAsync(PreparePackagesFixture()).ConfigureAwait(true);
            await WaitAsync(() => shell.Solutions.Projects.Count == 4 && shell.Solutions.Progress is { Completed: true } && shell.Solutions.RestoreState == "succeeded", "the Packages solution is open and restored", 300).ConfigureAwait(true);
            var vm = shell.OpenPackageManager(PlainProject) ?? throw new SmokeFailure("The NuGet Package Manager did not open.");
            await WaitAsync(() => vm.Sources.Contains("local"), "the package sources are listed").ConfigureAwait(true);
            vm.SelectedSource = "local";
            vm.Search = "Codalus.Sample.Greeting";
            try
            {
                await WaitAsync(() => vm.Rows.Any(r => r is { Id: "Codalus.Sample.Greeting", Version: "1.1.0", InstalledVersion: "1.0.0" }) && !vm.IsLoading, "the feed's package shows with its installed version", 60).ConfigureAwait(true);
            }
            catch (SmokeFailure ex)
            {
                throw new SmokeFailure($"{ex.Message} Rows: {string.Join(", ", vm.Rows.Select(r => $"{r.Id} {r.Version} (installed {r.InstalledVersion ?? "-"})"))}; error: {vm.Error ?? "none"}; loading: {vm.IsLoading}.");
            }
            vm.Selected = vm.Rows.First(r => r.Id == "Codalus.Sample.Greeting");
            await WaitAsync(() => vm.Details is { IsLoading: false, Package: not null, HasReadme: true }, "its details and README load", 60).ConfigureAwait(true);
            vm.Details!.InstallCommand.Execute(null);
            await WaitAsync(() => vm.Details?.InstalledVersion == "1.1.0" && ReadText(PlainProject).Contains("Version=\"1.1.0\"", StringComparison.Ordinal), "Update to 1.1.0 edits the project and restores", 300).ConfigureAwait(true);
            vm.Tab = PackageTab.Installed;
            vm.Search = string.Empty; // the search box also filters the Installed tab
            await WaitAsync(() => vm.Rows.Any(r => r is { Id: "Codalus.Sample.Text", IsTransitive: true }), "the new transitive dependency is listed", 120).ConfigureAwait(true);
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

            var multi = Path.Combine(Work, "Packages", "src", "Multi", "Multi.csproj");
            await shell.UnloadProjectAsync(multi).ConfigureAwait(true);
            await WaitAsync(() => shell.Solutions.Projects.TryGetValue(multi, out var p) && p.State == Projects.Protocol.ProjectStates.Unloaded, "Multi is unloaded").ConfigureAwait(true);
            await shell.ReloadProjectAsync(multi).ConfigureAwait(true);
            await WaitAsync(() => shell.Solutions.Projects.TryGetValue(multi, out var p) && p.State == Projects.Protocol.ProjectStates.Loaded, "Multi is loaded again", 60).ConfigureAwait(true);
        }, "packages.manage").ConfigureAwait(true);

        // M26a: project items, Show All Files, Add New Item and External Tools (ADR 0033), on the same solution.
        await StepAsync("projects.items", "M26", "The Properties window sets Copy to Output; Show All Files with Exclude and Include; Add New Item; an External Tool", async () =>
        {
            var plainDir = Path.GetDirectoryName(PlainProject)!;
            var notes = Path.Combine(plainDir, "notes.txt");
            await File.WriteAllTextAsync(notes, "Smoke test notes.\n").ConfigureAwait(true);
            await WaitAsync(() => shell.Solutions.Projects.TryGetValue(PlainProject, out var p) && p.Items.Any(i => i.Path == notes), "notes.txt becomes an item", 60).ConfigureAwait(true);
            shell.Layout.ShowToolWindow(PropertiesWindowViewModel.ToolWindowId, focus: false);
            PropertyRowViewModel? Row(string name) => shell.PropertiesWindow.Categories.SelectMany(c => c.Rows).FirstOrDefault(r => r.Name == name);

            // The Solution Explorer gets the new file once it has refreshed the project: reveal it until it is selected.
            await WaitAsync(() =>
            {
                if (shell.Explorer.Selected?.Node.Path != notes)
                {
                    shell.Explorer.Reveal("f:" + notes);
                }

                return !shell.PropertiesWindow.IsLoading && Row("Copy to Output Directory") is not null && shell.PropertiesWindow.Title.StartsWith("notes.txt", StringComparison.Ordinal);
            }, "the Properties window shows notes.txt", 60).ConfigureAwait(true);
            Check(Row("Build Action")?.Value == "None", "notes.txt is a None item");
            Row("Copy to Output Directory")!.SelectedOption = PropertiesWindowViewModel.CopyToOutputOptions.Single(o => o.Value == "PreserveNewest");
            await WaitAsync(() => ReadText(PlainProject).Contains("<None Update=\"notes.txt\">", StringComparison.Ordinal), "an Update item is written for the globbed file", 60).ConfigureAwait(true);
            await WaitAsync(() => !shell.PropertiesWindow.IsLoading && Row("Copy to Output Directory")?.IsSet == true, "the Properties window shows the new value", 60).ConfigureAwait(true);
            await RenderedAsync().ConfigureAwait(true);
            Screenshot("smoke-test-properties.png");

            shell.Explorer.ShowAllFiles = true;
            await shell.ProjectItems.ExcludeAsync(PlainProject, [notes], CancellationToken.None).ConfigureAwait(true);
            Check(ReadText(PlainProject).Contains("<None Remove=\"notes.txt\" />", StringComparison.Ordinal), "Exclude From Project writes a Remove item");
            await WaitAsync(() => shell.Explorer.Rows.Any(r => r.Node.Path == notes && r.IsExcluded), "Show All Files shows the excluded file", 60).ConfigureAwait(true);
            shell.Explorer.Reveal("f:" + notes);
            await RenderedAsync().ConfigureAwait(true);
            Screenshot("smoke-test-show-all-files.png");
            await shell.IncludeInProjectAsync(PlainProject, [notes]).ConfigureAwait(true);
            Check(!ReadText(PlainProject).Contains("Remove=\"notes.txt\"", StringComparison.Ordinal), "Include In Project removes it");
            shell.Explorer.ShowAllFiles = false;

            var add = shell.OpenAddNewItem(PlainProject) ?? throw new SmokeFailure("Add New Item did not open.");
            await WaitAsync(() => !add.IsLoading, "Add New Item lists its templates", 120).ConfigureAwait(true);
            add.Selected = add.Templates.First(t => t.Template.ShortName == "class");
            add.Name = "SmokeItem";
            var created = await add.AddAsync().ConfigureAwait(true);
            Check(created is not null && ReadText(created).Contains("class SmokeItem", StringComparison.Ordinal), $"Add New Item creates SmokeItem.cs ({add.Error})");

            await shell.SaveExternalToolsAsync([new Workbench.ExternalTools.ExternalTool("smokesdk", "Smoke SDK Version", "dotnet", "--version", "$(SolutionDir)")]).ConfigureAwait(true);
            shell.Run("externalTools.run.smokesdk");
            var tools = shell.Workbench.Output.GetOrCreate("codalus.externalTools");
            await WaitAsync(() => tools.GetText().Contains("exited with code 0", StringComparison.Ordinal), "the external tool runs in the Output window", 60).ConfigureAwait(true);
            await shell.SaveExternalToolsAsync([]).ConfigureAwait(true);
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

            await window.ExecuteAsync("#r \"nuget: Codalus.Sample.Text, 1.0.0\"").ConfigureAwait(true);
            await window.ExecuteAsync("Codalus.Sample.Text.Text.Shout(\"hi\")").ConfigureAwait(true);
            await WaitAsync(() => window.Transcript.GetText().Contains("\"HI!\"", StringComparison.Ordinal), "a #r \"nuget:\" package runs: " + Tail(window.Transcript.GetText(), 1500), 180).ConfigureAwait(true);
        }, "packages.manage").ConfigureAwait(true);

        // M11: languages and TextMate grammars (ADR 0016).
        await StepAsync("languages.grammars", "M11", "JSON and MSBuild files are colored by their TextMate grammars", async () =>
        {
            var json = Path.Combine(Work, "smoke-settings.json");
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
            var programFile = Path.Combine(Work, "Web", "WebApi", "Program.cs");
            var urls = (await shell.Runs.PlanAsync(Path.Combine(Work, "Web", "WebApi", "WebApi.csproj"), CancellationToken.None).ConfigureAwait(true)).Plan.Environment["ASPNETCORE_URLS"];
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
            var counter = Path.Combine(Work, "Blazor", "BlazorApp", "Components", "Pages", "Counter.razor");
            var editor = await OpenAsync(counter).ConfigureAwait(true);
            Check(editor.Model.LanguageId == ShellHost.RazorLanguageId, "Counter.razor is a Razor file");
            var client = shell.Languages.RunningClient(ShellHost.CSharpLanguageId)!;
            var at = editor.Model.PositionAt(editor.Model.Text.Text.IndexOf("@currentCount", StringComparison.Ordinal) + 3);
            var hover = await WaitForAsync(async ct => (await client.HoverAsync(editor.Model.Uri, at, ct).ConfigureAwait(true))?.Contents.Value,
                "hover on C# in the Razor file", 120).ConfigureAwait(true);
            Check(hover!.Contains("currentCount", StringComparison.Ordinal), "the hover describes the field: " + hover);
            // The components come with the project's compilation, which may still be finishing after the hover answered
            // (the macOS runners are slower): ask again until they're there.
            var components = await WaitForAsync(async ct =>
            {
                var completion = await client.CompletionAsync(editor.Model.Uri, editor.Model.PositionAt(editor.Model.Text.Text.IndexOf("<button", StringComparison.Ordinal) + 1),
                    new LanguageServices.Protocol.CompletionContext(LanguageServices.Protocol.CompletionTriggerKind.Invoked, null), ct).ConfigureAwait(true);
                return completion?.Items.Any(i => i.Label == "PageTitle") == true ? "found" : null;
            }, "markup completion offers components", 60).ConfigureAwait(true);
            Check(components == "found", "markup completion offers components");

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
        await StepAsync("web.razorHtml", "M16", "HTML and CSS inside a Razor file come from the HTML server", RazorHtmlStepAsync, "web.languages").ConfigureAwait(true);
        await StepAsync("web.npm", "M16", "The Task Runner Explorer restores npm packages, runs a script, and a build runs its Before Build script", NpmStepAsync).ConfigureAwait(true);
        await StepAsync("web.eslint", "M16", "The project's ESLint lints a script and its Prettier formats it", EslintStepAsync, "web.languages").ConfigureAwait(true);

        await StepAsync("dotnet.net11", "M30", "A .NET 11 solution opens with the .NET 11 SDK, builds, and runs on .NET 11", async () =>
        {
            await WaitAsync(() => shell.Runs.Running.Count == 0, "the previous run has stopped", 60).ConfigureAwait(true);
            Check(File.Exists(Path.Combine(samples, "Net11", "Net11.slnx")), $"the samples folder {samples} has Net11");
            var target = Path.Combine(Work, "Net11");
            Copy(Path.Combine(samples, "Net11"), target);
            await shell.OpenSolutionAsync(Path.Combine(target, "Net11.slnx")).ConfigureAwait(true);
            await WaitAsync(() => shell.Solutions.Projects.Count == 5 && shell.Solutions.Progress is { Completed: true }, "the .NET 11 solution is open", 300).ConfigureAwait(true);
            var sdk = Path.GetFileName(Path.TrimEndingDirectorySeparator(shell.Solutions.SdkPath ?? string.Empty));
            Check(sdk.StartsWith("11.0.", StringComparison.Ordinal), $"the build host loaded the .NET 11 SDK (it has {sdk}; {shell.Solutions.SdkWarning})");

            shell.Runs.SetStartupProject(Path.Combine(target, "src", "App", "App.csproj"));
            var before = shell.Builds.LastBuild;
            shell.Run("build.buildSolution");
            await WaitAsync(() => BuildDone(before), "the build finishes", 420).ConfigureAwait(true);
            Check(shell.Builds.LastBuild!.Result.Succeeded, "the build succeeded:\n" + Tail(shell.Builds.Channel.GetText(), 3000));
            shell.Run("run.startWithoutDebugging");
            string Output() => shell.Workbench.Output.Channels.FirstOrDefault(c => c.Name == "Run: App")?.GetText() ?? string.Empty;
            await WaitAsync(() => Output().Contains("has exited with code 0", StringComparison.Ordinal), () => "App runs and exits with 0; its output:\n" + Tail(Output(), 2000) + "\nThe build's:\n" + Tail(shell.Builds.Channel.GetText(), 1500), 300).ConfigureAwait(true);
            Check(Output().Contains("runtime=11 library=net11.0", StringComparison.Ordinal), "App ran on .NET 11 with the net11.0 build of its library:\n" + Tail(Output(), 1000));
        }).ConfigureAwait(true);

        // M14c: debugger sessions (ADR 0039), on samples/Debugging's Sessions.slnx.
        await StepAsync("debug.compound", "M14c", "Two startup projects from the Startup Projects page debug in one session: each stops at its breakpoint, and the Processes window lists both", DebugCompoundStepAsync, "debug.session").ConfigureAwait(true);
        // Not on macOS, where debugged programs use the Debug Console (netcoredbg can't attach to a held program there).
        if (!OperatingSystem.IsMacOS())
        {
            await StepAsync("debug.terminal", "M14c", "A console app debugged in the integrated terminal reads input, stops at a breakpoint, and runs on after Detach All", DebugTerminalStepAsync, "debug.compound").ConfigureAwait(true);
        }
        await StepAsync("debug.roslyn", "M14c", "F5 on a source generator's DebugRoslynComponent profile runs the compiler on its target and stops in the generator", DebugRoslynStepAsync, "debug.compound").ConfigureAwait(true);
        if (await RemoteStandInAsync().ConfigureAwait(true) is { } remote)
        {
            await StepAsync("debug.remote", "M14c", $"Attach to a program on another machine ({remote.Display}, a local stand-in) with netcoredbg copied there", () => DebugRemoteStepAsync(remote), "debug.compound").ConfigureAwait(true);
        }
        else
        {
            Report("debug.remote", "M14c", "Attach to a program on another machine", "skipped", 0, $"No stand-in: on Windows a WSL distribution with the .NET {Environment.Version.Major} runtime; elsewhere sh.");
        }
        // M26a: folder mode (ADR 0033), on a copy of samples/ProjectSystem/FolderOnly.
        await StepAsync("projects.folder", "M26", "Open Folder: the folder view, Load All Projects, a task and a launch configuration", async () =>
        {
            await WaitAsync(() => shell.Runs.Running.Count == 0, "the previous run has stopped", 60).ConfigureAwait(true);
            var source = Path.Combine(samples, "ProjectSystem", "FolderOnly");
            Check(Directory.Exists(source), $"the samples folder {samples} has ProjectSystem/FolderOnly");
            var parent = Path.Combine(Work, "FolderMode");
            Directory.CreateDirectory(parent);
            File.Copy(Path.Combine(samples, "ProjectSystem", "Directory.Build.props"), Path.Combine(parent, "Directory.Build.props"), overwrite: true);
            var folder = Path.Combine(parent, "FolderOnly");
            Copy(source, folder);
            shell.Trust.Trust(folder);
            await shell.OpenFolderWorkspaceAsync(folder).ConfigureAwait(true);
            await WaitAsync(() => shell.Solutions.Solution is { IsFolder: true } && shell.Explorer.IsFolderView && shell.Explorer.Rows.Any(r => r.Name == "README.md"), "the folder view lists the folder", 120).ConfigureAwait(true);
            shell.Explorer.Reveal("f:" + Path.Combine(folder, "src", "Tool", "Tool.csproj"));
            await RenderedAsync().ConfigureAwait(true);
            Screenshot("smoke-test-folder-view.png");

            await shell.LoadAllFolderProjectsAsync().ConfigureAwait(true);
            await WaitAsync(() => shell.Solutions.Projects.Count == 2 && shell.Solutions.Projects.Values.All(p => p.State == Projects.Protocol.ProjectStates.Loaded), "both projects of the folder load", 240).ConfigureAwait(true);
            Check(await shell.RunTaskAsync("sdk version").ConfigureAwait(true) == 0, "the task 'sdk version' runs:\n" + Tail(shell.Workbench.Output.GetOrCreate("codalus.tasks").GetText(), 1000));
            shell.Run("run.startWithoutDebugging");
            string Output() => shell.Workbench.Output.Channels.FirstOrDefault(c => c.Name == "Run: Tool")?.GetText() ?? string.Empty;
            await WaitAsync(() => Output().Contains("FROM LAUNCH.JSON!", StringComparison.Ordinal), "the launch configuration 'Tool' runs with its arguments", 300).ConfigureAwait(true);
            await RenderedAsync().ConfigureAwait(true);
            Screenshot("smoke-test-folder.png");
        }).ConfigureAwait(true);

        await StepAsync("data.efMigrations", "M27", "EF Core: the Entity Framework window lists a context and its migrations; Update Database shows the SQL, then applies it; Add Migration; the model diagram; reverse engineering", EfStepAsync).ConfigureAwait(true);

        await StepAsync("web.scaffolding", "M15", "Add New Scaffolded Item runs dotnet-aspnet-codegenerator (an API controller over a DbContext); a gRPC service reference generates its client at build time", ScaffoldingStepAsync).ConfigureAwait(true);
        await StepAsync("wcf.testClient", "M42", "samples/Wcf's CoreWCF service runs; the WCF Test Client finds its metadata and calls operations over BasicHttpBinding and WSHttpBinding", WcfStepAsync).ConfigureAwait(true);
        await StepAsync("data.explorer", "M27", "The Database Explorer: a SQLite connection's tables, data edited and committed, a query with results, completion and a plan (the data host)", DataExplorerStepAsync).ConfigureAwait(true);

        // M17a: containers (ADR 0042), where an engine runs Linux containers (GitHub's Linux runner; Podman here).
        const string containerTitle = "A web app runs in a container through its \"Container (Dockerfile)\" profile (fast mode), answers, serves its wwwroot, and the Containers window shows it";
        var engine = shell.ContainerEngine;
        var (engineOs, engineError) = await engine.ServerOsAsync(CancellationToken.None).ConfigureAwait(true);
        if (engineOs != "linux" && !(Environment.GetEnvironmentVariable("CI") == "true" && OperatingSystem.IsLinux()))
        {
            Report("containers.run", "M17", containerTitle, "skipped", 0, engineOs is null ? $"No container engine answers ({engine.Name}: {engineError})." : $"{engine.Name} runs {engineOs} containers.");
        }
        else
        {
            await StepAsync("containers.run", "M17", containerTitle, ContainerRunStepAsync).ConfigureAwait(true);
        }

        // M17b: Aspire (ADR 0043), with the fixture's own Aspire packages and orchestrator.
        const string aspireTitle = "Ctrl+F5 on an Aspire AppHost runs its two projects through the IDE execution protocol; the Aspire window shows the resources and the dashboard's address";
        if (engineOs is null && !(Environment.GetEnvironmentVariable("CI") == "true" && OperatingSystem.IsLinux()))
        {
            // Aspire 13.1's orchestrator wants a container runtime (checkpoint m17b).
            Report("aspire.run", "M17", aspireTitle, "skipped", 0, $"Aspire's orchestrator needs a container runtime, and none answers ({engine.Name}: {engineError}).");
        }
        else
        {
            await StepAsync("aspire.run", "M17", aspireTitle, AspireRunStepAsync).ConfigureAwait(true);
        }

        // M17c: publishing (ADR 0042), through the build host.
        await StepAsync("publish.folder", "M17", "The Publish page saves a folder profile and publishes a single-file app for this machine's runtime", async () =>
        {
            await WaitAsync(() => shell.Runs.Running.Count == 0, "the previous run has stopped", 60).ConfigureAwait(true);
            var target = Path.Combine(Work, "Containers");
            if (!Directory.Exists(target))
            {
                Copy(Path.Combine(samples, "Containers"), target);
            }

            await shell.OpenSolutionAsync(Path.Combine(target, "Containers.slnx")).ConfigureAwait(true);
            await WaitAsync(() => shell.Solutions.Projects.Count == 2 && shell.Solutions.Progress is { Completed: true }, "the containers solution is open", 300).ConfigureAwait(true);
            var web = Path.Combine(target, "Web", "Web.csproj");
            var page = shell.OpenPublish(web);
            Check(page is not null, "the Publish page opens");
            page!.RuntimeIdentifier = Workbench.Publishing.PublishProfiles.CurrentRuntimeIdentifier();
            page.SingleFile = true;
            await RenderedAsync().ConfigureAwait(true);
            Screenshot("smoke-test-publish.png");
            await page.PublishAsync().ConfigureAwait(true);
            var output = Workbench.Publishing.PublishProfiles.OutputFolder(web, page.Profile!);
            Check(File.Exists(Path.Combine(output, OperatingSystem.IsWindows() ? "Web.exe" : "Web")), "the single-file app is published: " + page.Status + "\n" + Tail(shell.Builds.Channel.GetText(), 2000));
        }).ConfigureAwait(true);

        // M18a: profiling (ADR 0044 and 0045), in the diagnostics host.
        await StepAsync("profiler.cpu", "M18", "The Performance Profiler launches the startup project with CPU Usage and .NET Counters; the report shows the hot function, the flame graph and hot lines (the diagnostics host)", ProfilerStepAsync).ConfigureAwait(true);

        // M18b: the other profiler tools (ADR 0046) and BenchmarkDotNet.
        await StepAsync("profiler.tools", "M18", "The Performance Profiler records .NET Async, threads and contention, exceptions and allocations of an async program; the report shows each", ProfilerToolsStepAsync).ConfigureAwait(true);
        await StepAsync("profiler.memory", "M18", "Memory Usage takes two heap snapshots of a leaking program; the report's diff shows the leaked type growing, with its paths to roots", ProfilerMemoryStepAsync).ConfigureAwait(true);
        await StepAsync("benchmarks.run", "M18", "A benchmark's gutter glyph runs it with BenchmarkDotNet; the Benchmarks window shows its result", BenchmarksStepAsync).ConfigureAwait(true);

        // M19a: Avalonia XAML (ADR 0047 and 0048): the C# host's XAML service and the previewer.
        await StepAsync("xaml.preview", "M19", "An .axaml file completes from the C# host; the XAML Previewer builds the Avalonia app, renders the file, follows an edit and selects in the XAML what is clicked", XamlPreviewStepAsync).ConfigureAwait(true);
        await StepAsync("maui.devices", "M20a", "The .NET MAUI app is a mobile project with a device picker, its .xaml completes binding paths from the C# host, and the Devices window lists the Android toolchain", MauiStepAsync).ConfigureAwait(true);

        // M40a: WinForms and WPF (ADR 0071, D31: Windows only).
        const string desktopTitle = "The WPF app opens, its XAML completes binding paths from d:DataContext, and it builds and runs";
        if (OperatingSystem.IsWindows())
        {
            await StepAsync("windows.desktop", "M40a", desktopTitle, WindowsDesktopStepAsync).ConfigureAwait(true);
        }
        else
        {
            Report("windows.desktop", "M40a", desktopTitle, "skipped", 0, "WinForms and WPF apps run on Windows only (D31); their editing is covered by the tests on every OS.");
        }

        // M40b: the WPF previewer (ADR 0072, D31: Windows only).
        const string wpfPreviewTitle = "The XAML Previewer renders the WPF app's MainWindow.xaml at its design size with its design-time data, follows an edit and selects in the XAML what is clicked";
        if (OperatingSystem.IsWindows())
        {
            await StepAsync("wpf.preview", "M40b", wpfPreviewTitle, WpfPreviewStepAsync, "windows.desktop").ConfigureAwait(true);
        }
        else
        {
            Report("wpf.preview", "M40b", wpfPreviewTitle, "skipped", 0, "The WPF previewer runs on Windows only (D31); off Windows it says so, which the tests cover on every OS.");
        }

        // M41a: the Windows Forms designer (ADR 0073, D31: Windows only).
        const string winFormsDesignerTitle = "The WinForms app's OrderForm opens in the Windows Forms designer; a Button from the Toolbox and its Text set in the Properties window become code in OrderForm.Designer.cs";
        if (OperatingSystem.IsWindows())
        {
            await StepAsync("winforms.designer", "M41a", winFormsDesignerTitle, WinFormsDesignerStepAsync, "windows.desktop").ConfigureAwait(true);
        }
        else
        {
            Report("winforms.designer", "M41a", winFormsDesignerTitle, "skipped", 0, "The Windows Forms designer runs on Windows only (D31); off Windows a form opens as code, which the tests cover on every OS.");
        }

        // M41b: the designer's depth (ADR 0073): a menu edited through Type Here, a ToolTip extender property.
        const string winFormsDepthTitle = "InventoryForm opens in the designer; a menu item typed into its menu strip and a ToolTip set on a control become code (the item, AddRange, SetToolTip), and undo takes them back";
        if (OperatingSystem.IsWindows())
        {
            await StepAsync("winforms.designerDepth", "M41b", winFormsDepthTitle, WinFormsDesignerDepthStepAsync, "windows.desktop").ConfigureAwait(true);
        }
        else
        {
            Report("winforms.designerDepth", "M41b", winFormsDepthTitle, "skipped", 0, "The Windows Forms designer runs on Windows only (D31); the shell's side runs against a fake designer host in the tests on every OS.");
        }

        // 0.6 (#66): the Toolbox's Enter and a drop through the real host; the designer runs on a copy of the build output,
        // so the app builds with a form open, and the form loads on the new build.
        const string winFormsToolboxTitle = "OrderForm in the designer: a TextBox added from the Toolbox with Enter and a CheckBox dropped on the form become code; the app builds while the designer is open (no MSB3027) and the designer loads the form on the new build";
        if (OperatingSystem.IsWindows())
        {
            await StepAsync("winforms.toolboxAndBuild", "0.6", winFormsToolboxTitle, WinFormsToolboxAndBuildStepAsync, "windows.desktop").ConfigureAwait(true);
        }
        else
        {
            Report("winforms.toolboxAndBuild", "0.6", winFormsToolboxTitle, "skipped", 0, "The Windows Forms designer runs on Windows only (D31); the copy, the Toolbox's drag, double-click and Enter, and the reload after a build are tested on every OS against a fake designer host.");
        }

        // M44: WinUI 3 and MAUI's Windows target (ADR 0074, D31: Windows only).
        const string winUITitle = "The WinUI 3 app opens, its XAML completes x:Bind paths from the page's class, and it builds and runs unpackaged";
        if (OperatingSystem.IsWindows())
        {
            await StepAsync("maui.windows", "M44", winUITitle, WinUIStepAsync).ConfigureAwait(true);
        }
        else
        {
            Report("maui.windows", "M44", winUITitle, "skipped", 0, "WinUI 3 apps build and run on Windows only (D31); their XAML editing and the Windows-only gates are covered by the tests on every OS.");
        }

        // M43a: .NET Framework projects (ADR 0075, D31): they open and build on every OS, and run on Windows only.
        await StepAsync("netfx.projects", "M43a", OperatingSystem.IsWindows()
            ? "The .NET Framework fixture opens (the old-style project restored from packages.config), the net48 console app builds and runs on the desktop CLR"
            : "The .NET Framework fixture opens (the old-style project restored from packages.config), the net48 console app builds, and starting it says Windows only", NetFrameworkStepAsync).ConfigureAwait(true);

        await StepAsync("vb.editing", "M35", "A Visual Basic file is served by the C# host: it completes a C# library's members, and Enter after If … Then adds End If", VisualBasicStepAsync, "projects.open").ConfigureAwait(true);
        await StepAsync("fsharp.editing", "M36", "F# is served by FsAutoComplete (installed with dotnet tool install): Quick Info across files, and Solution Explorer in compile order with the signature file nested", FSharpStepAsync, "projects.open").ConfigureAwait(true);
        await StepAsync("fsharp.interactive", "M36", "A selection in an F# script runs in F# Interactive (dotnet fsi in a terminal), which shows the values defined", FSharpInteractiveStepAsync, "projects.open").ConfigureAwait(true);

        await StepAsync("update.restart", "M32", "A downloaded update offers Restart to update, and What's New shows its notes", async () =>
        {
            // The UI on a stand-in backend: this published build isn't an install (eng/test-update.sh updates real ones).
            var offer = new Workbench.Updates.UpdateOffer("99.0.0", "stable", false, "- Release notes the smoke test wrote.", null);
            shell.UseUpdates(new Workbench.Updates.InstallInfo(ShellInfo.Version, Workbench.Updates.InstallKind.SelfUpdating), new StandInUpdates(offer));
            await shell.CheckForUpdatesAsync().ConfigureAwait(true);
            await WaitAsync(() => shell.Workbench.StatusBar.GetItems(Workbench.StatusBar.StatusBarAlignment.Right)
                .Any(i => i.Id == ShellHost.UpdateStatusItem && i.Text.Contains("Restart to update (99.0.0)", StringComparison.Ordinal)), "the Restart to update item").ConfigureAwait(true);
            var notes = shell.ShowWhatsNew();
            Check(notes is not null && notes.Source.Text.Text.Contains("Release notes the smoke test wrote.", StringComparison.Ordinal), "What's New shows the release's notes");
            await Task.Delay(1000).ConfigureAwait(true); // rendered
            Screenshot("smoke-test-update.png");
            shell.Layout.CloseDocument(notes!);
            shell.UseUpdates(new Workbench.Updates.InstallInfo(ShellInfo.Version, Workbench.Updates.InstallKind.Development), new StandInUpdates(offer));
            shell.Workbench.StatusBar.Remove(ShellHost.UpdateStatusItem);
        }).ConfigureAwait(true);


        const string wasmTitle = "F5 debugs a Blazor WebAssembly app in the browser: a breakpoint in a .razor file stops";
        const string compoundTitle = "F5 on a Blazor Web App debugs the server and the page together: a .razor breakpoint stops in each";
        if (Browsers.Find(shell.Workbench.Settings.GetString(Browsers.SettingKey, "auto")) is null)
        {
            // Skipped here, a failure in CI (which must prove it), at once rather than after the steps' timeouts.
            var (outcome, reason) = Environment.GetEnvironmentVariable("CI") == "true"
                ? ("failed", "No Chrome, Edge or Chromium is installed, and CI must run browser debugging.")
                : ("skipped", "No Chrome, Edge or Chromium is installed.");
            Report("blazor.wasmDebug", "M16", wasmTitle, outcome, 0, reason);
            Report("blazor.compoundDebug", "M16", compoundTitle, outcome, 0, reason);
        }
        else
        {
            await StepAsync("blazor.wasmDebug", "M16", wasmTitle, WasmDebugStepAsync).ConfigureAwait(true);
            await StepAsync("blazor.compoundDebug", "M16", compoundTitle, CompoundDebugStepAsync).ConfigureAwait(true);
        }

        // M21: a remote window on the local stand-in server (the shipped remote/server); it disconnects at its end.
        await StepAsync("remote.session", "M21", "A remote window on a local server: the solution loads in its build host, a file is completed and saved through it, the program runs in its terminal, a new port is forwarded, and a dropped link reconnects", RemoteStepAsync, "build.solution").ConfigureAwait(true);

        // Last: it opens a solution of 38 projects, and nothing after it depends on what it leaves behind.
        await StepAsync("dotnet.older", "M33", "Projects of .NET Core 2.0 to .NET 10 open and build; Hot Reload says why it needs .NET 6", OlderFrameworksStepAsync).ConfigureAwait(true);

        // The command sweep: every command through its menu item, key binding and the palette, on its own fixture.
        await StepAsync("shell.commandSweep", "Sweep", "Every command runs from its menu item, key binding and the command palette without an error, an unhandled exception or a stalled UI, and what it opens closes cleanly", CommandSweepStepAsync, limit: TimeSpan.FromMinutes(25)).ConfigureAwait(true);
    }

    /// <summary>
    /// Remote development (M21, ADR 0050) on the local stand-in: the window connects to the shipped server, opens a copy
    /// of BuildAndRun through it, completes and saves a file, builds, runs the program in the server's terminal and sees
    /// a new listening port forwarded; its link drops and reconnects (M21b) with the session intact; then it
    /// disconnects, local again. Every wait is bounded.
    /// </summary>
    private async Task RemoteStepAsync()
    {
        await WaitAsync(() => shell.Runs.Running.Count == 0 && !shell.Debugger.IsActive, "the previous run has stopped", 60).ConfigureAwait(true);
        if (shell.Solutions.IsOpen)
        {
            await shell.CloseSolutionAsync().ConfigureAwait(true);
        }

        var folder = Path.Combine(Work, "RemoteBuildAndRun");
        Copy(Path.Combine(samples, "BuildAndRun"), folder);
        string RemoteLog() => Tail(shell.Workbench.Output.Channels.FirstOrDefault(c => c.Name == "Remote")?.GetText() ?? string.Empty, 3000);
        var spec = Workbench.Remote.RemoteTargetSpec.Parse("local")!;
        var connect = shell.ConnectRemoteAsync(spec, null, shell.DefaultRemoteOptions(spec) with { Resumable = true });
        await WaitAsync(() => connect.IsCompleted, "the window connects to the local server", 120).ConfigureAwait(true);
        Check(await connect.ConfigureAwait(true) && shell.Remote is not null, "the window connected to the local server:\n" + RemoteLog());
        var remote = shell.Remote!;
        try
        {
            Console.Out.WriteLine($"  server {remote.Connection.Server.ServerVersion} ({remote.Connection.Server.ServerFolder}), .NET {remote.Connection.Server.DotnetVersion}, paths {(remote.Map.IsIdentity ? "as they are" : "under " + remote.Map.LocalRoot)}");
            var local = remote.Map.ToLocal(folder);
            shell.Trust.Trust(Path.GetDirectoryName(local)!);
            await shell.OpenRemoteAsync(local).ConfigureAwait(true);
            await WaitAsync(() => shell.Solutions.Projects.Count == 2 && shell.Solutions.Progress is { Completed: true }, "both projects are evaluated by the build host on the server", 300).ConfigureAwait(true);
            Check(shell.Workbench.StatusBar.GetItems(Workbench.StatusBar.StatusBarAlignment.Left).Any(i => i.Id == ShellHost.RemoteStatusItem && i.Text.Contains(remote.Display, StringComparison.Ordinal)), "the status bar shows the remote connection");

            var program = Path.Combine(local, "src", "Hello", "Program.cs");
            var editor = await shell.OpenFileAsync(program).ConfigureAwait(true) ?? throw new SmokeFailure("Program.cs didn't open through the server.");
            Check(editor.Model.Text.Text.Contains("Greeter.Greet", StringComparison.Ordinal), "the editor read Program.cs through the server");
            await WaitAsync(() => shell.Languages.Status(ShellHost.CSharpLanguageId) is { State: "ready", Projects: 2 }, "the C# host on the server is ready with 2 projects", 360).ConfigureAwait(true);
            var client = shell.Languages.RunningClient(ShellHost.CSharpLanguageId) ?? throw new SmokeFailure("The C# host isn't running.");
            var text = editor.Model.Text.Text;
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60)))
            {
                var completion = await client.CompletionAsync(editor.Model.Uri, editor.Model.PositionAt(text.IndexOf("Console.WriteLine", StringComparison.Ordinal) + "Console.".Length), null, timeout.Token).ConfigureAwait(true);
                Check(completion.Items.Any(i => i.Label == "WriteLine"), $"member completion from the C# host on the server lists WriteLine ({completion.Items.Length} items: {string.Join(", ", completion.Items.Take(12).Select(i => i.Label))}; document {editor.Model.Uri}).\nC#:\n"
                    + Tail(shell.Workbench.Output.Channels.FirstOrDefault(c => c.Name == "C#")?.GetText() ?? string.Empty, 3000) + "\nRemote:\n" + RemoteLog());
            }

            editor.Model.Text.Insert(0, "// edited in a remote window\n");
            await shell.SaveAllAsync().ConfigureAwait(true);
            Check(File.ReadAllText(Path.Combine(folder, "src", "Hello", "Program.cs")).StartsWith("// edited in a remote window", StringComparison.Ordinal), "the save reached the file through the server");

            var before = shell.Builds.LastBuild;
            shell.Run("build.buildSolution");
            await WaitAsync(() => BuildDone(before), "the build on the server finishes", 420).ConfigureAwait(true);
            Check(shell.Builds.LastBuild!.Result.Succeeded, "the build on the server succeeded:\n" + Tail(shell.Builds.Channel.GetText(), 3000));

            var sessions = shell.Terminals.Sessions.Count;
            shell.SetStartupProject(Path.Combine(local, "src", "Hello", "Hello.csproj"));
            await shell.StartWithoutDebuggingAsync().ConfigureAwait(true);
            await WaitAsync(() => shell.Terminals.Sessions.Count > sessions && shell.Terminals.Sessions[^1].ExitCode is not null, "the program runs to its end in the server's terminal", 180).ConfigureAwait(true);
            var run = shell.Terminals.Sessions[^1];
            var screen = string.Join('\n', Enumerable.Range(0, run.Terminal.TotalRows).Select(i => run.Terminal.GetRow(i).GetText()));
            Check(run.ExitCode == 0 && screen.Contains("Hello from Hello", StringComparison.Ordinal), $"it printed its greeting and exited with 0 (exit code {run.ExitCode}):\n{Tail(screen, 1500)}");

            using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            listener.Start();
            var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            await WaitAsync(() => remote.Ports.Forwarded.Any(p => p.RemotePort == port), $"port {port} is detected and forwarded", 60).ConfigureAwait(true);

            // The network drops (M21b, ADR 0054): the server and its hosts go on, the window reconnects, and an edit
            // saved meanwhile reaches the file afterwards.
            Check(remote.Connection.IsResumable, "the server is resumable (started through its relay)");
            var dropped = System.Diagnostics.Stopwatch.StartNew();
            remote.Connection.DropLink();
            editor.Model.Text.Insert(0, "// saved across a reconnect\n");
            var save = shell.SaveAllAsync();
            await WaitAsync(() => remote.Connection.Reconnections == 1 && remote.Connection.IsLinked, "the window reconnects to the server", 60).ConfigureAwait(true);
            Console.Out.WriteLine($"  reconnected in {dropped.Elapsed.TotalSeconds:0.0} s");
            Check(dropped.Elapsed < TimeSpan.FromSeconds(5), $"reconnecting took {dropped.Elapsed.TotalSeconds:0.0} s, within the 5 s budget:\n" + RemoteLog());
            await save.ConfigureAwait(true);
            Check(File.ReadAllText(Path.Combine(folder, "src", "Hello", "Program.cs")).StartsWith("// saved across a reconnect", StringComparison.Ordinal), "the save reached the file after the reconnect");
            Check(shell.Workbench.StatusBar.GetItems(Workbench.StatusBar.StatusBarAlignment.Left).Any(i => i.Id == ShellHost.RemoteStatusItem && i.Text.StartsWith("$(link)", StringComparison.Ordinal)), "the status bar shows the connection again");
            Check(shell.Languages.RunningClient(ShellHost.CSharpLanguageId) == client, "the C# host on the server went on through the reconnect");
        }
        finally
        {
            await shell.DisconnectRemoteAsync().ConfigureAwait(true);
        }

        Check(shell.Remote is null && !shell.Solutions.IsOpen, "the window is local again");
    }

    /// <summary>
    /// Older .NET (M33, ADR 0030) on samples/TargetFrameworks, with the newest SDK as a user has it: the eleven
    /// frameworks evaluate, .NET Core 2.1's framework is its package, the solution builds, Hot Reload is disabled for a
    /// netcoreapp3.1 startup project with its reason, and the net10.0 app runs. The netcoreapp2.1 app runs too when
    /// this machine has the .NET Core 2.1 runtime (the full matrix is Codalus.TargetFrameworks.Tests).
    /// </summary>
    private async Task OlderFrameworksStepAsync()
    {
        await WaitAsync(() => shell.Runs.Running.Count == 0, "the previous run has stopped", 60).ConfigureAwait(true);
        var source = Path.Combine(samples, "TargetFrameworks");
        Check(File.Exists(Path.Combine(source, "TargetFrameworks.sln")), $"the samples folder {samples} has TargetFrameworks");
        var target = Path.Combine(Work, "TargetFrameworks");
        Copy(source, target);
        await shell.OpenSolutionAsync(Path.Combine(target, "TargetFrameworks.sln")).ConfigureAwait(true);
        await WaitAsync(() => shell.Solutions.Projects.Count == 38 && shell.Solutions.Progress is { Completed: true } && shell.Solutions.RestoreState is "succeeded" or "failed",
            "the 38 projects are open and restored", 600).ConfigureAwait(true);
        Check(shell.Solutions.RestoreState == "succeeded", "the restore succeeded");
        // The design-time builds after the restore resolve it: wait for them rather than look once.
        await WaitAsync(() => shell.Solutions.Projects.Values.Single(p => p.Name == "Core21.Lib").References
            .Any(r => r is { Kind: Codalus.Projects.Protocol.ReferenceKinds.Framework, Name: "Microsoft.NETCore.App", Resolved: true }), "netcoreapp2.1's framework is the Microsoft.NETCore.App package", 120).ConfigureAwait(true);

        var core31 = shell.Solutions.Projects.Values.Single(p => p.Name == "Core31.App").Path;
        shell.Runs.SetStartupProject(core31);
        await WaitAsync(() => shell.Workbench.Context.GetValue("hotReloadSupported") is false, "Start with Hot Reload is disabled for Core31.App", 30).ConfigureAwait(true);
        Check(shell.HotReloadUnsupportedReason() == "Hot Reload needs .NET 6 or later: Core31.App targets netcoreapp3.1.", "the reason names the framework: " + shell.HotReloadUnsupportedReason());

        var before = shell.Builds.LastBuild;
        shell.Run("build.buildSolution");
        await WaitAsync(() => BuildDone(before), "the build finishes", 600).ConfigureAwait(true);
        Check(shell.Builds.LastBuild!.Result.Succeeded, "the build succeeded:\n" + Tail(shell.Builds.Channel.GetText(), 3000));

        var apps = new List<string> { "Net100.App" };
        if (HasRuntime("2.1."))
        {
            apps.Add("Core21.App");
        }

        foreach (var name in apps)
        {
            shell.Runs.SetStartupProject(shell.Solutions.Projects.Values.Single(p => p.Name == name).Path);
            shell.Run("run.startWithoutDebugging");
            string Output() => shell.Workbench.Output.Channels.FirstOrDefault(c => c.Name == "Run: " + name)?.GetText() ?? string.Empty;
            await WaitAsync(() => Output().Contains("has exited with code", StringComparison.Ordinal), () => $"{name} runs and exits (its output:\n{Tail(Output(), 1000)})", 300).ConfigureAwait(true);
            Check(Output().Contains("has exited with code 0", StringComparison.Ordinal) && Output().Contains("Hello, ", StringComparison.Ordinal), $"{name} ran:\n" + Tail(Output(), 1000));
        }

        Console.Out.WriteLine($"  ran: {string.Join(", ", apps)}" + (apps.Count == 1 ? " (no .NET Core 2.1 runtime here)" : string.Empty));
    }

    /// <summary>
    /// A build after <paramref name="before"/> finished, and the shell knows it: the service records the build before the
    /// UI clears <c>buildRunning</c>, and until then the commands that start a build or a run are disabled.
    /// </summary>
    private bool BuildDone(Workbench.Build.BuildCompleted? before) =>
        shell.Builds.LastBuild is { } build && build != before && shell.Workbench.Context.GetValue("buildRunning") is not true;

    /// <summary>Whether <c>dotnet --list-runtimes</c> lists a Microsoft.NETCore.App version starting with <paramref name="prefix"/>.</summary>
    private static bool HasRuntime(string prefix)
    {
        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("dotnet", "--list-runtimes") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true });
            var output = process!.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return output.Split('\n').Any(l => l.StartsWith("Microsoft.NETCore.App " + prefix, StringComparison.Ordinal));
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    // ------------------------------------------------------------------ M14c: debugger sessions

    private string Sessions => Path.Combine(Work, "Debugging");

    private string SessionSource(string project, string file) => Path.Combine(Sessions, project, file);

    private static int MarkerLine(string file, string marker) =>
        Array.FindIndex(File.ReadAllLines(file), l => l.Contains($"// [{marker}]", StringComparison.Ordinal)) + 1;

    /// <summary>Multiple startup projects (M14c): Partner and Debuggee from the Startup Projects page, in one session.</summary>
    private async Task DebugCompoundStepAsync()
    {
        await WaitAsync(() => shell.Runs.Running.Count == 0 && !shell.Debugger.IsActive, "the previous programs have stopped", 60).ConfigureAwait(true);
        Check(File.Exists(Path.Combine(samples, "Debugging", "Sessions.slnx")), $"the samples folder {samples} has Debugging/Sessions.slnx");
        Copy(Path.Combine(samples, "Debugging"), Sessions);
        await shell.OpenSolutionAsync(Path.Combine(Sessions, "Sessions.slnx")).ConfigureAwait(true);
        await WaitAsync(() => shell.Solutions.Projects.Count == 4 && shell.Solutions.Progress is { Completed: true }, "the Sessions solution is open", 300).ConfigureAwait(true);

        var page = shell.OpenStartupProjects() ?? throw new SmokeFailure("The Startup Projects page did not open.");
        await WaitAsync(() => window.GetVisualDescendants().OfType<StartupProjectsView>().Any() && page.Rows.Count == 4, "the Startup Projects page lists the four projects", 120).ConfigureAwait(true);
        page.IsMultiple = true;
        page.Rows.Single(r => r.Name == "Partner").Action = Workbench.Build.StartupAction.Start;
        page.Rows.Single(r => r.Name == "Debuggee").Action = Workbench.Build.StartupAction.Start;
        page.Apply();
        Check(shell.Runs.StartupTargets.Count == 2, "two projects start");
        await Task.Delay(500).ConfigureAwait(true);
        Screenshot("smoke-test-startup-projects.png");

        var debuggee = SessionSource("Debuggee", "Program.cs");
        var partner = SessionSource("Partner", "Program.cs");
        shell.Debugger.Breakpoints.Clear();
        shell.Debugger.Breakpoints.Toggle(debuggee, MarkerLine(debuggee, "print"));
        shell.Debugger.Breakpoints.Toggle(partner, MarkerLine(partner, "partner"));
        shell.Run("debug.start");
        string Stopped() => Path.GetFileName(Path.GetDirectoryName(shell.Debugger.SelectedFrame?.Source?.Path) ?? string.Empty);
        await WaitAsync(() => shell.Debugger.State == DebugState.Stopped, "the first program stops at its breakpoint", 420).ConfigureAwait(true);
        var first = Stopped();
        shell.Layout.ShowToolWindow(ProcessesViewModel.ToolWindowId);
        await WaitAsync(() => shell.ProcessesWindow.Rows.Count == 2, "the Processes window lists both programs").ConfigureAwait(true);
        await Task.Delay(1000).ConfigureAwait(true);
        Screenshot("smoke-test-debug-compound.png");

        await shell.Debugger.ContinueAsync().ConfigureAwait(true);
        await WaitAsync(() => shell.Debugger.State == DebugState.Stopped && Stopped() != first, $"the other program stops at its breakpoint (the first was {first})", 120).ConfigureAwait(true);
        Check(new[] { first, Stopped() }.Order().SequenceEqual(["Debuggee", "Partner"]), "Debuggee and Partner both stopped");
        shell.Debugger.Breakpoints.Clear();
        await shell.Debugger.ContinueAsync().ConfigureAwait(true);
        await WaitAsync(() => shell.Debugger.State == DebugState.Idle, "both programs end, and the session with them", 120).ConfigureAwait(true);
        var log = shell.DebugLog.GetText();
        Check(log.Contains("partner says partner", StringComparison.Ordinal) && log.Contains("total=15", StringComparison.Ordinal), "both programs' output is in the Debug Console:\n" + Tail(log, 1500));
        shell.SetStartupProject(Path.Combine(Sessions, "Debuggee", "Debuggee.csproj"));
    }

    /// <summary>A console app in the integrated terminal (M14c): input, a breakpoint, and Detach All leaving it running.</summary>
    private async Task DebugTerminalStepAsync()
    {
        var properties = Path.Combine(Sessions, "Debuggee", "Properties");
        Directory.CreateDirectory(properties);
        await File.WriteAllTextAsync(Path.Combine(properties, "launchSettings.json"), """
            { "profiles": { "Read": { "commandName": "Project", "commandLineArgs": "--read" } } }
            """).ConfigureAwait(true);
        await shell.Workbench.Settings.SetAsync(ShellHost.DebugConsoleSetting, "integratedTerminal", SettingScope.User, CancellationToken.None).ConfigureAwait(true);
        try
        {
            shell.SetStartupProject(Path.Combine(Sessions, "Debuggee", "Debuggee.csproj"));
            var program = SessionSource("Debuggee", "Program.cs");
            shell.Debugger.Breakpoints.Clear();
            shell.Debugger.Breakpoints.Toggle(program, MarkerLine(program, "loop"));
            var before = shell.DebugTerminal.Last;
            shell.Run("debug.start");
            await WaitAsync(() => shell.DebugTerminal.Last is { } last && last != before, "the program starts in a terminal", 300).ConfigureAwait(true);
            var session = shell.DebugTerminal.Last!;
            await WaitAsync(() => shell.Debugger.Processes is [{ Kind: DebugProcess.Terminal, Id: not null }], "the debugger attaches to it", 120).ConfigureAwait(true);
            session.Write("typed in the terminal\r");
            await WaitAsync(() => shell.Debugger.State == DebugState.Stopped, "it stops at the breakpoint", 120).ConfigureAwait(true);
            string Screen()
            {
                lock (session.Terminal.SyncRoot)
                {
                    return string.Join('\n', Enumerable.Range(0, session.Terminal.TotalRows).Select(i => session.Terminal.GetRow(i).GetText()));
                }
            }

            await WaitAsync(() => Screen().Contains("echo: typed in the terminal", StringComparison.Ordinal), "the program read the line typed in the terminal", 30).ConfigureAwait(true);
            await WaitAsync(() => Equals(shell.Workbench.Context.GetValue("debugCanDetach"), true), "Detach All applies").ConfigureAwait(true);
            await Task.Delay(1000).ConfigureAwait(true);
            Screenshot("smoke-test-debug-terminal.png");

            shell.Run("debug.detachAll");
            await WaitAsync(() => shell.Debugger.State == DebugState.Idle, "the session ends", 60).ConfigureAwait(true);
            await WaitAsync(() => session.ExitCode is not null, "the program runs on to its end after Detach All", 120).ConfigureAwait(true);
            Check(session.ExitCode == 15 || OperatingSystem.IsMacOS(), $"it exited with 15 (got {session.ExitCode}):\n{Screen()}");
            Check(Screen().Contains("done", StringComparison.Ordinal), "its output after the detach is in the terminal");
        }
        finally
        {
            await shell.Workbench.Settings.SetAsync(ShellHost.DebugConsoleSetting, "internalConsole", SettingScope.User, CancellationToken.None).ConfigureAwait(true);
        }
    }

    /// <summary>The DebugRoslynComponent profile (M14c): the compiler runs on GeneratorTarget, and the generator stops.</summary>
    private async Task DebugRoslynStepAsync()
    {
        shell.SetStartupProject(Path.Combine(Sessions, "Generator", "Generator.csproj"));
        var generator = SessionSource("Generator", "HelloGenerator.cs");
        shell.Debugger.Breakpoints.Clear();
        shell.Debugger.Breakpoints.Toggle(generator, MarkerLine(generator, "generate"));
        shell.Run("debug.start");
        await WaitAsync(() => shell.Debugger.State == DebugState.Stopped, "the generator stops at its breakpoint", 420).ConfigureAwait(true);
        Check(string.Equals(shell.Debugger.SelectedFrame?.Source?.Path, generator, OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase), $"in HelloGenerator.cs (got {shell.Debugger.SelectedFrame?.Source?.Path})");
        await WaitAsync(() => shell.Locals.Roots.Any(r => r.Name == "list" && r.Value.Contains("Customer", StringComparison.Ordinal)), "Locals shows the class names the generator got").ConfigureAwait(true);
        await Task.Delay(1000).ConfigureAwait(true);
        Screenshot("smoke-test-debug-roslyn.png");
        shell.Debugger.Breakpoints.Clear();
        shell.Run("debug.stop");
        await WaitAsync(() => shell.Debugger.State == DebugState.Idle, "the session ends", 60).ConfigureAwait(true);
    }

    /// <summary>
    /// A machine to attach to without leaving this one: on Windows a WSL distribution with .NET, elsewhere SSH through a
    /// stand-in <c>ssh</c> that runs the command here. netcoredbg goes to a temporary folder there.
    /// </summary>
    private async Task<RemoteTarget?> RemoteStandInAsync()
    {
        const string home = "/tmp/codalus-smoke/netcoredbg";
        if (OperatingSystem.IsWindows())
        {
            foreach (var distribution in await RemoteTargets.WslDistributionsAsync(CancellationToken.None).ConfigureAwait(true))
            {
                var target = new WslTarget(distribution) { Home = home };
                // With the .NET the debuggee targets: a distribution with an older .NET only timed the step out.
                if (!distribution.StartsWith("docker-", StringComparison.OrdinalIgnoreCase) && !distribution.StartsWith("podman-", StringComparison.OrdinalIgnoreCase)
                    && (await RemoteShell.RunAsync(target, "exec sh -lc 'dotnet --list-runtimes'", CancellationToken.None).ConfigureAwait(true)).Output
                        .Contains($"Microsoft.NETCore.App {Environment.Version.Major}.", StringComparison.Ordinal))
                {
                    return target;
                }
            }

            return null;
        }

        var ssh = Path.Combine(Work, "stand-in-ssh");
        await File.WriteAllTextAsync(ssh, "#!/bin/sh\n# A stand-in for ssh: skip the options and the destination, run the command here.\nwhile [ $# -gt 0 ]; do\n  case \"$1\" in\n    -T) shift ;;\n    -o|-p|-i|-l) shift 2 ;;\n    *) break ;;\n  esac\ndone\nshift\nexec sh -c \"$*\"\n").ConfigureAwait(true);
        File.SetUnixFileMode(ssh, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return new SshTarget("stand-in.invalid", "smoke", null, ssh) { Home = home };
    }

    /// <summary>Remote attach (M14c): the Debuggee waits on the target, the debugger attaches there, stops, and detaches.</summary>
    private async Task DebugRemoteStepAsync(RemoteTarget target)
    {
        var dll = Path.Combine(Sessions, "Debuggee", "bin", "Debug", "net11.0", "Debuggee.dll");
        Check(File.Exists(dll), "the Debuggee was built by debug.compound");
        var signal = "/tmp/codalus-smoke-" + Guid.NewGuid().ToString("N")[..8];
        Process program;
        if (target is WslTarget wsl)
        {
            var dotnet = (await RemoteShell.RunAsync(target, "exec sh -lc 'command -v dotnet'", CancellationToken.None).ConfigureAwait(true)).Output.Trim();
            program = Process.Start(new ProcessStartInfo("wsl.exe", ["-d", wsl.Distribution, "--exec", dotnet, "exec", Workbench.Build.WslLaunch.ToLinuxPath(dll), "--wait-for", signal])
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
            }) ?? throw new SmokeFailure("The program didn't start in WSL.");
        }
        else
        {
            program = Process.Start(new ProcessStartInfo("dotnet", ["exec", dll, "--wait-for", signal]) { UseShellExecute = false, RedirectStandardOutput = true })
                      ?? throw new SmokeFailure("The program didn't start.");
        }

        try
        {
            RemoteProcess? remote = null;
            await WaitAsync(() =>
            {
                remote = RemoteShell.ProcessesAsync(target, CancellationToken.None).GetAwaiter().GetResult().FirstOrDefault(p => p.IsDotnet && p.CommandLine.Contains(signal, StringComparison.Ordinal));
                return remote is not null;
            }, "the program runs on the target", 60).ConfigureAwait(true);

            var source = SessionSource("Debuggee", "Program.cs");
            shell.Debugger.Breakpoints.Clear();
            shell.Debugger.Breakpoints.Toggle(source, MarkerLine(source, "done"));
            var attaching = shell.AttachToRemoteAsync(target, null, remote!.Id);
            while (!attaching.IsCompleted)
            {
                // The first attach on a target asks to copy netcoredbg there.
                if (shell.Dialogs.IsOpen && shell.Dialogs.Title == "Copy the Debugger")
                {
                    shell.Dialogs.Accept();
                }

                await Task.Delay(100).ConfigureAwait(true);
            }

            Check(await attaching.ConfigureAwait(true), "the debugger attached on the target:\n" + Tail(shell.DebugLog.GetText(), 1500));
            Check(shell.Debugger.Processes is [{ Target: { } where }] && where == target.Display, $"the Processes window shows the target ({target.Display})");
            await RemoteShell.RunAsync(target, $"touch {signal}", CancellationToken.None).ConfigureAwait(true);
            await WaitAsync(() => shell.Debugger.State == DebugState.Stopped, "it stops at the breakpoint", 120).ConfigureAwait(true);
            Check(string.Equals(shell.Debugger.SelectedFrame?.Source?.Path, source, OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase), $"the frame maps to this machine's Program.cs (got {shell.Debugger.SelectedFrame?.Source?.Path})");
            shell.Layout.ShowToolWindow(ProcessesViewModel.ToolWindowId);
            await Task.Delay(1000).ConfigureAwait(true);
            Screenshot("smoke-test-debug-remote.png");
            shell.Debugger.Breakpoints.Clear();
            shell.Run("debug.detachAll");
            await WaitAsync(() => shell.Debugger.State == DebugState.Idle, "the session ends", 60).ConfigureAwait(true);
            await WaitAsync(() => program.HasExited, "the program runs on to its end after Detach All", 60).ConfigureAwait(true);
        }
        finally
        {
            if (!program.HasExited)
            {
                program.Kill(entireProcessTree: true);
            }

            program.Dispose();
            await RemoteShell.RunAsync(target, $"rm -f {signal}", CancellationToken.None).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// EF Core (M27a, ADR 0036) on samples/Data and SQLite: the fixture's local dotnet-ef is restored, the Entity
    /// Framework window finds BloggingContext (Roslyn) and its pending migration (dotnet ef), Update Database shows the
    /// SQL before it applies it, a model change becomes a migration, the model view draws the entities, and the
    /// inventory database is reverse engineered.
    /// </summary>
    private async Task EfStepAsync()
    {
        await WaitAsync(() => shell.Runs.Running.Count == 0 && !shell.Debugger.IsActive, "the previous run has stopped", 60).ConfigureAwait(true);
        Check(File.Exists(Path.Combine(samples, "Data", "Data.slnx")), $"the samples folder {samples} has Data");
        var target = Path.Combine(Work, "Data");
        Copy(Path.Combine(samples, "Data"), target);
        shell.Trust.Trust(target); // projects.open trusts the work folder; CODALUS_SMOKE_ONLY=data.efMigrations skips it
        await shell.OpenSolutionAsync(Path.Combine(target, "Data.slnx")).ConfigureAwait(true);
        await WaitAsync(() => shell.Solutions.Projects.Count == 1 && shell.Solutions.Progress is { Completed: true }, "the Data solution is open", 300).ConfigureAwait(true);
        var restore = await shell.Packages.RunToolCommandAsync(new Projects.Protocol.ToolCommandParams(Projects.Protocol.ToolActions.Restore), CancellationToken.None).ConfigureAwait(true);
        Check(restore.Succeeded, "dotnet tool restore gets the fixture's dotnet-ef:\n" + Tail(restore.Output, 1500));
        await WaitAsync(
            () => shell.Languages.Status(ShellHost.CSharpLanguageId) is { State: "ready", Projects: 1 },
            () => $"the C# workspace is ready with Blogging alone (C# {shell.Languages.Status(ShellHost.CSharpLanguageId)?.ToString() ?? "not running"})",
            300).ConfigureAwait(true);

        await shell.ShowEntityFrameworkAsync().ConfigureAwait(true);
        var ef = shell.EfExplorer;
        string Log() => Tail(shell.EntityFramework.Channel.GetText(), 2500) + "\n" + ef.Message;
        await WaitAsync(
            () => ef.Context is { Name: "BloggingContext", Provider: "Microsoft.EntityFrameworkCore.Sqlite" },
            () => $"the window finds BloggingContext on SQLite (contexts: {string.Join(", ", ef.Contexts.Select(c => $"{c.Name} on {c.Provider ?? "no provider"}"))}; "
                + $"restore {shell.Solutions.RestoreState ?? "not started"}; C# {shell.Languages.Status(ShellHost.CSharpLanguageId)?.State ?? "not running"}):\n" + Log(),
            120).ConfigureAwait(true);
        await WaitAsync(() => ef.Migrations.Count == 1 && !ef.IsBusy, "the window lists InitialCreate", 420).ConfigureAwait(true);
        Check(ef.Migrations[0].State == "Pending", "InitialCreate is pending: " + Log());

        var update = shell.UpdateDatabaseAsync(null);
        await WaitAsync(() => shell.Dialogs.IsOpen || update.IsCompleted, "Update Database shows the SQL", 240).ConfigureAwait(true);
        Check(shell.Dialogs.Details.Any(l => l.Text.Contains("CREATE TABLE \"Blogs\"", StringComparison.Ordinal)), "the SQL creates the Blogs table: " + Log());
        Check(!File.Exists(Path.Combine(target, "Blogging", "blogging.db")), "nothing is applied before Apply");
        await RenderedAsync().ConfigureAwait(true);
        Screenshot("smoke-test-ef-update.png");
        shell.Dialogs.Buttons.Single(b => b.Label == "Apply").Command.Execute(null);
        await WaitAsync(() => update.IsCompleted, "the migration is applied", 240).ConfigureAwait(true);
        Check(update.Result && File.Exists(Path.Combine(target, "Blogging", "blogging.db")), "Update Database created the SQLite database: " + Log());
        await WaitAsync(() => ef.Migrations.Count == 1 && ef.Migrations[0].Applied == true && !ef.IsBusy, "InitialCreate shows as applied", 240).ConfigureAwait(true);

        var context = Path.Combine(target, "Blogging", "Data", "BloggingContext.cs");
        File.WriteAllText(context, File.ReadAllText(context).Replace("public DateTime Published { get; set; }", "public DateTime Published { get; set; }\n\n    public int Rating { get; set; }", StringComparison.Ordinal));
        shell.EntityFramework.Invalidate();
        await shell.AddMigrationAsync("AddRating").ConfigureAwait(true);
        await WaitAsync(() => ef.Migrations.Count == 2 && !ef.IsBusy, "AddRating is listed", 300).ConfigureAwait(true);
        Check(ef.Migrations[1].State == "Pending", "AddRating is pending: " + Log());

        var model = await shell.ShowEfModelAsync().ConfigureAwait(true);
        Check(model is not null, "the model view opens");
        await WaitAsync(() => model!.ViewModel.Model is not null || model.ViewModel.Message?.StartsWith("Building", StringComparison.Ordinal) == false, "the model is read", 300).ConfigureAwait(true);
        Check(model!.ViewModel.Model?.Entities.Length == 5, "the model has 5 entity types: " + model.ViewModel.Message + "\n" + Log());
        await WaitAsync(() => window.GetVisualDescendants().OfType<EfModelView>().Any(v => v.Diagram.Boxes.Count == 5), "the diagram draws them").ConfigureAwait(true);
        await RenderedAsync().ConfigureAwait(true);
        Screenshot("smoke-test-ef-model.png");

        var scaffold = shell.OpenReverseEngineer();
        Check(scaffold is not null, "Reverse Engineer opens");
        var form = scaffold!.ViewModel;
        form.ConnectionString = "Data Source=" + Path.Combine(target, "Inventory", "inventory.db");
        form.Provider = "Microsoft.EntityFrameworkCore.Sqlite";
        form.OutputDir = "Inventory";
        form.ContextName = "InventoryContext";
        await form.RunAsync().ConfigureAwait(true);
        Check(form.Error is null && File.Exists(Path.Combine(target, "Blogging", "Inventory", "Product.cs")), "the inventory database is reverse engineered: " + form.Error + "\n" + Log());
        shell.Layout.CloseDocument(model);
        shell.Layout.CloseDocument(scaffold);
    }

    /// <summary>
    /// Scaffolding and service references (M15c, ADR 0041) on samples/Scaffolding: an API controller through the
    /// fixture's local dotnet-aspnet-codegenerator, and a gRPC reference whose client the build generates.
    /// </summary>
    private async Task ScaffoldingStepAsync()
    {
        await WaitAsync(() => shell.Runs.Running.Count == 0 && !shell.Debugger.IsActive, "the previous run has stopped", 60).ConfigureAwait(true);
        Check(File.Exists(Path.Combine(samples, "Scaffolding", "Scaffolding.slnx")), $"the samples folder {samples} has Scaffolding");
        var target = Path.Combine(Work, "Scaffolding");
        Copy(Path.Combine(samples, "Scaffolding"), target);
        string Log() => Tail(shell.Scaffolding.Channel.GetText(), 2500);

        // Every wait is bounded, and says what it was doing: on a cold machine each of these downloads packages.
        async Task<T> Bounded<T>(Task<T> task, string what, int seconds)
        {
            var deadline = Stopwatch.GetTimestamp() + (seconds * Stopwatch.Frequency);
            while (!task.IsCompleted)
            {
                if (Stopwatch.GetTimestamp() > deadline)
                {
                    throw new SmokeFailure($"Timed out after {seconds} s waiting for: {what}. The Scaffolding output:\n{Log()}\nThe build output:\n{Tail(shell.Builds.Channel.GetText(), 1500)}");
                }

                await Task.Delay(100).ConfigureAwait(true);
            }

            return await task.ConfigureAwait(true);
        }

        await shell.OpenSolutionAsync(Path.Combine(target, "Scaffolding.slnx")).ConfigureAwait(true);
        await WaitAsync(() => shell.Solutions.Projects.Count == 2 && shell.Solutions.Progress is { Completed: true }, "the Scaffolding solution is open", 300).ConfigureAwait(true);
        var restore = await Bounded(shell.Packages.RunToolCommandAsync(new Projects.Protocol.ToolCommandParams(Projects.Protocol.ToolActions.Restore), CancellationToken.None), "dotnet tool restore of dotnet-aspnet-codegenerator", 240).ConfigureAwait(true);
        Check(restore.Succeeded, "dotnet tool restore gets the fixture's dotnet-aspnet-codegenerator:\n" + Tail(restore.Output, 1500));

        // The project restored and built first, as a developer's would be: the generator then only builds incrementally.
        var shop = Path.Combine(target, "Shop", "Shop.csproj");
        Check(await Bounded(shell.BuildAsync(Projects.Protocol.BuildTargets.Build, shop), "the Shop project's restore and build", 420).ConfigureAwait(true), "Shop builds:\n" + Tail(shell.Builds.Channel.GetText(), 3000));

        var form = shell.OpenAddScaffoldedItem(shop, Path.Combine(target, "Shop", "Controllers"));
        Check(form is not null, "Add New Scaffolded Item opens");
        form!.Select("api.ef");
        await WaitAsync(() => form.Status is not null || form.HasError, "the code generator's state is known", 120).ConfigureAwait(true);
        Check(form.Status is not null && !form.NeedsTool, "the code generator can run: " + (form.Error ?? form.ToolMessage));
        ((ScaffoldTextField)form.Field("model")!).Text = "Product";
        ((ScaffoldTextField)form.Field("dbContext")!).Text = "ShopContext";
        await RenderedAsync().ConfigureAwait(true);
        Screenshot("smoke-test-scaffold.png");
        await Bounded(form.AddAsync().ContinueWith(_ => true, TaskScheduler.Default), "dotnet aspnet-codegenerator controller", 300).ConfigureAwait(true);
        var controller = Path.Combine(target, "Shop", "Controllers", "ProductsController.cs");
        Check(form.Error is null && File.Exists(controller), "the API controller is scaffolded: " + form.Error + "\n" + Log());
        Check(File.ReadAllText(controller).Contains("ShopContext", StringComparison.Ordinal), "the controller uses the context");

        var client = Path.Combine(target, "Client", "Client.csproj");
        var references = shell.OpenServiceReferences(client);
        Check(references is not null, "Service References opens");
        await WaitAsync(() => !references!.IsBusy && references.Packages.Length > 0, "the references are listed", 120).ConfigureAwait(true);
        references!.KindLabel = "gRPC";
        references.Source = Path.Combine(target, "contracts", "greet.proto");
        references.GrpcServices = Projects.Protocol.GrpcServiceKinds.Client;
        await Bounded(references.RunAsync("add").ContinueWith(_ => true, TaskScheduler.Default), "adding the gRPC reference (package versions and the project edit)", 240).ConfigureAwait(true);
        Check(references.Error is null && references.References.Count == 1, "the gRPC reference is added: " + references.Error);
        await RenderedAsync().ConfigureAwait(true);
        Screenshot("smoke-test-service-references.png");
        File.WriteAllText(Path.Combine(target, "Client", "Program.cs"), "var client = new Contracts.Greet.Greeter.GreeterClient(Grpc.Net.Client.GrpcChannel.ForAddress(\"http://localhost:5000\"));\nConsole.WriteLine(client.GetType().Name);\n");
        Check(await Bounded(shell.BuildAsync(Projects.Protocol.BuildTargets.Build, client), "the Client project's restore and build with Grpc.Tools", 420).ConfigureAwait(true), "the client builds with its generated gRPC client:\n" + Tail(shell.Builds.Channel.GetText(), 3000));
        Check(Directory.GetFiles(Path.Combine(target, "Client", "obj"), "GreetGrpc.cs", SearchOption.AllDirectories).Length > 0, "Grpc.Tools generated GreetGrpc.cs");
        foreach (var document in shell.Layout.Documents.OfType<SolutionManagementDocument>().ToList())
        {
            shell.Layout.CloseDocument(document);
        }
    }
    /// <summary>
    /// WCF (M42, ADR 0069): samples/Wcf's CoreWCF service started with Run; the WCF Test Client finds its metadata from
    /// the running program, reads it, and calls Add over BasicHttpBinding (SOAP 1.1) and Greet, with a data contract,
    /// over WSHttpBinding (SOAP 1.2 with WS-Addressing). The screenshot is the website's.
    /// </summary>
    private async Task WcfStepAsync()
    {
        await WaitAsync(() => shell.Runs.Running.Count == 0 && !shell.Debugger.IsActive, "the previous run has stopped", 60).ConfigureAwait(true);
        Check(File.Exists(Path.Combine(samples, "Wcf", "Wcf.slnx")), $"the samples folder {samples} has Wcf");
        var target = Path.Combine(Work, "Wcf");
        Copy(Path.Combine(samples, "Wcf"), target);
        shell.Trust.Trust(target); // projects.open trusts the work folder; CODALUS_SMOKE_ONLY=wcf.testClient skips it
        await shell.OpenSolutionAsync(Path.Combine(target, "Wcf.slnx")).ConfigureAwait(true);
        await WaitAsync(() => shell.Solutions.Projects.Count == 2 && shell.Solutions.Progress is { Completed: true }, "the Wcf solution is open", 300).ConfigureAwait(true);
        var service = Path.Combine(target, "CalculatorService", "CalculatorService.csproj");
        shell.SetStartupProject(service);
        await shell.StartWithoutDebuggingAsync().ConfigureAwait(true);
        try
        {
            // The program's channel stays after it exits, so a service that stops at start says why.
            string Output() => shell.Workbench.Output.Channels.FirstOrDefault(c => c.Name == "Run: CalculatorService")?.GetText() ?? string.Empty;
            await WaitAsync(() => Output().Contains("Now listening on", StringComparison.Ordinal) || (Output().Length > 0 && shell.Runs.Running.Count == 0), "the CoreWCF service starts", 420).ConfigureAwait(true);
            Check(Output().Contains("Now listening on", StringComparison.Ordinal), "the CoreWCF service listens:\n" + Tail(Output(), 2000) + "\nThe build output:\n" + Tail(shell.Builds.Channel.GetText(), 1500));

            var client = shell.OpenWcfTestClient();
            string? Result() => client.ResponseTree.Count > 0 && client.ResponseTree[0].Children.Count > 0 ? client.ResponseTree[0].Children[0].Label : null;
            var found = await shell.DiscoverWcfServicesAsync().ConfigureAwait(true);
            Check(found.Count > 0 && found[0].MetadataUrl.EndsWith("/CalculatorService.svc?wsdl", StringComparison.Ordinal), "the running service's metadata is found: " + string.Join(", ", found.Select(d => d.MetadataUrl)) + "\n" + Tail(Output(), 1000));
            client.Address = found[0].MetadataUrl;
            await client.LoadAsync().ConfigureAwait(true);
            Check(client.Error is null && client.Endpoints.Count == 2, "the metadata is read: " + client.Error);

            client.Select("Add", Workbench.Wcf.SoapVersion.Soap11);
            client.Row("a")!.Value = "2";
            client.Row("b")!.Value = "3.5";
            await client.SendAsync().ConfigureAwait(true);
            Check(client.Error is null && Result() == "AddResult = 5.5", "Add answers over BasicHttpBinding: " + (client.Error ?? client.ResponseText));

            client.Select("Greet", Workbench.Wcf.SoapVersion.Soap12);
            client.Row("person.Name")!.Value = "Ada";
            client.Row("person.Age")!.Value = "36";
            client.Row("person.Mood")!.Value = "Happy";
            client.Row("person.Tags.string")!.AddCommand.Execute(null);
            client.Rows[^1].Value = "math";
            await client.SendAsync().ConfigureAwait(true);
            Check(client.Error is null && Result() == "GreetResult = Hello Ada, 36 and happy (math)!", "Greet answers over WSHttpBinding: " + (client.Error ?? client.ResponseText));
            await WaitAsync(() => window.GetVisualDescendants().OfType<WcfTestClientView>().Any(), "the WCF Test Client renders").ConfigureAwait(true);
            await RenderedAsync().ConfigureAwait(true);
            Screenshot("smoke-test-wcf-test-client.png");
            await GalleryWcfResponseAsync(client).ConfigureAwait(true); // M34g: a response and a fault in the gallery
        }
        finally
        {
            shell.StopRunning();
            foreach (var document in shell.Layout.Documents.OfType<SolutionManagementDocument>().ToList())
            {
                shell.Layout.CloseDocument(document);
            }
        }
    }

    /// <summary>
    /// The database explorer (M27b, ADR 0037 and 0038) on a copy of samples/Data's inventory database: a connection
    /// added in the form, the tree, a table's data edited and committed, a query editor with schema completion,
    /// results and an estimated plan, all through the published data host.
    /// </summary>
    /// <summary>
    /// Containers (M17a): samples/Containers' web app with its container profile: Ctrl+F5 builds the Dockerfile's base
    /// stage, mounts the build output and the static web assets, and starts the app through exec; it answers on its
    /// published port and serves wwwroot; the Containers window lists the container with its environment; stopping the
    /// program removes the container.
    /// </summary>
    private async Task ContainerRunStepAsync()
    {
        await WaitAsync(() => shell.Runs.Running.Count == 0, "the previous run has stopped", 60).ConfigureAwait(true);
        var target = Path.Combine(Work, "Containers");
        Copy(Path.Combine(samples, "Containers"), target);
        await shell.OpenSolutionAsync(Path.Combine(target, "Containers.slnx")).ConfigureAwait(true);
        await WaitAsync(() => shell.Solutions.Projects.Count == 2 && shell.Solutions.Progress is { Completed: true }, "the containers solution is open", 300).ConfigureAwait(true);
        var web = Path.Combine(target, "Web", "Web.csproj");
        var name = Workbench.Containers.DockerSupport.ContainerName(web);
        var engine = shell.ContainerEngine;
        shell.Runs.SetStartupProject(web);
        shell.Runs.SetProfile(web, Workbench.Containers.DockerSupport.ContainerProfileName);
        try
        {
            await shell.StartWithoutDebuggingAsync().ConfigureAwait(true);
            string Output() => shell.Workbench.Output.Channels.FirstOrDefault(c => c.Name == "Run: Web")?.GetText() ?? string.Empty;
            await WaitAsync(() => Output().Contains("Now listening on", StringComparison.Ordinal), "the app listens in its container:\n" + Tail(shell.ContainerLog.GetText(), 2000), 600).ConfigureAwait(true);
            var port = await engine.HostPortAsync(name, "8080/tcp", CancellationToken.None).ConfigureAwait(true);
            Check(port is not null, "the container publishes port 8080");
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            var answer = await http.GetStringAsync(new Uri($"http://127.0.0.1:{port}/")).ConfigureAwait(true);
            Check(answer.StartsWith("Hello from ", StringComparison.Ordinal), "the app answers: " + answer);
            var asset = await http.GetStringAsync(new Uri($"http://127.0.0.1:{port}/hello.txt")).ConfigureAwait(true);
            Check(asset.Trim() == "Hello from wwwroot", "the app serves wwwroot, a static web asset of the project folder: " + asset);

            await shell.ShowContainersAsync().ConfigureAwait(true);
            var row = shell.ContainersWindow.Containers.FirstOrDefault(c => c.Name == name);
            Check(row is { IsOurs: true }, "the Containers window lists the container first");
            shell.ContainersWindow.Selected = row;
            await WaitAsync(() => shell.ContainersWindow.Environment.Any(e => e.Name == "ASPNETCORE_HTTP_PORTS"), "the container's environment is shown", 60).ConfigureAwait(true);
            shell.ContainersWindow.Tab = 1;
            await RenderedAsync().ConfigureAwait(true);
            Screenshot("smoke-test-containers.png");

            shell.StopRunning();
            await WaitAsync(() => engine.InspectAsync(name, CancellationToken.None).GetAwaiter().GetResult() is null, "stopping the app removes its container", 60).ConfigureAwait(true);
        }
        finally
        {
            shell.StopRunning();
            await engine.RemoveAsync(name, CancellationToken.None).ConfigureAwait(true);
            await engine.RunAsync(["rmi", name + ":dev"], CancellationToken.None).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Aspire (M17b): samples/Containers/Aspire's AppHost with Ctrl+F5. Its orchestrator asks the IDE for the api and
    /// the frontend, which run as the IDE's programs; the resource service lists them running and the Redis container
    /// waiting for its explicit start; the frontend answers through the api; stopping the AppHost ends them.
    /// </summary>
    private async Task AspireRunStepAsync()
    {
        await WaitAsync(() => shell.Runs.Running.Count == 0, "the previous run has stopped", 60).ConfigureAwait(true);
        var target = Path.Combine(Work, "Containers");
        if (!Directory.Exists(target))
        {
            Copy(Path.Combine(samples, "Containers"), target);
        }

        await shell.OpenSolutionAsync(Path.Combine(target, "Aspire.slnx")).ConfigureAwait(true);
        await WaitAsync(() => shell.Solutions.Projects.Count == 3 && shell.Solutions.Progress is { Completed: true }, "the Aspire solution is open", 300).ConfigureAwait(true);
        var appHost = Path.Combine(target, "Aspire", "AppHost", "AppHost.csproj");
        Check(shell.IsAspireHost(appHost), "the AppHost is recognized (IsAspireHost)");
        shell.Runs.SetStartupProject(appHost);
        try
        {
            await shell.StartWithoutDebuggingAsync().ConfigureAwait(true);
            var window = shell.AspireWindow;
            await WaitAsync(
                () => window.Resources.Count(r => r.Resource.Type == "Project" && r.Resource.State == "Running") == 2 && window.Resources.Any(r => r.Name == "cache"),
                () => "the api and the frontend run, and the cache is listed:\n" + Tail(shell.AspireLog.GetText(), 2000),
                420).ConfigureAwait(true);
            await WaitAsync(() => window.HasDashboard, "the dashboard's address is known", 60).ConfigureAwait(true);
            var url = window.Resources.Single(r => r.Name == "frontend").Resource.Urls.First(u => !u.IsInternal).Url;
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            var answer = await http.GetStringAsync(new Uri(url)).ConfigureAwait(true);
            Check(answer.StartsWith("Frontend: Sunny, from the api", StringComparison.Ordinal), "the frontend answers through the api: " + answer);
            window.Selected = window.Resources.Single(r => r.Name == "api");
            await WaitAsync(() => window.Logs.Contains("Now listening on", StringComparison.Ordinal), "the api's console output is shown", 60).ConfigureAwait(true);
            shell.ShowAspire();
            await RenderedAsync().ConfigureAwait(true);
            Screenshot("smoke-test-aspire.png");
        }
        finally
        {
            shell.StopRunning();
            await WaitAsync(() => !shell.AspireWindow.IsRunning && shell.Runs.Running.Count == 0, "stopping the AppHost ends its projects", 60).ConfigureAwait(true);
        }
    }

    private async Task MauiStepAsync()
    {
        await WaitAsync(() => shell.Runs.Running.Count == 0, "the previous run has stopped", 60).ConfigureAwait(true);
        var target = Path.Combine(Work, "Maui");
        if (!Directory.Exists(target))
        {
            Copy(Path.Combine(samples, "Maui"), target);
        }

        await shell.OpenSolutionAsync(Path.Combine(target, "Maui.slnx")).ConfigureAwait(true);
        await WaitAsync(() => shell.Solutions.Projects.Count == 2 && shell.Solutions.Progress is { Completed: true }, "the MAUI solution is open (its packages restored)", 600).ConfigureAwait(true);
        var app = Path.Combine(target, "TodoApp", "TodoApp.csproj");
        Check(shell.Solutions.Projects.TryGetValue(app, out var info) && info.UsesMaui, "TodoApp is a .NET MAUI project");
        shell.SetStartupProject(app);
        await WaitAsync(() => shell.Toolbar.HasDevice, "the toolbar shows the device picker").ConfigureAwait(true);

        var path = Path.Combine(target, "MauiViews", "Views", "TodoPage.xaml");
        var editor = await OpenAsync(path).ConfigureAwait(true);
        Check(editor.Model.LanguageId == ShellHost.XamlLanguageId, "the file is XAML for the C# host: " + editor.Model.LanguageId);
        MoveCaret(editor, "Text=\"{Binding NewTitle}\"", "Text=\"{Binding ".Length);
        var asked = Stopwatch.StartNew();
        editor.ShowCompletion();
        await WaitAsync(() =>
        {
            if (editor.CompletionWindow is { } w && w.CompletionList.CompletionData.Any(d => d.Text == "AddCommand"))
            {
                return true;
            }

            if (asked.Elapsed > TimeSpan.FromSeconds(10))
            {
                editor.CompletionWindow?.Close();
                MoveCaret(editor, "Text=\"{Binding NewTitle}\"", "Text=\"{Binding ".Length);
                editor.ShowCompletion();
                asked.Restart();
            }

            return false;
        }, "the binding path completes the view model's members (x:DataType)", 360).ConfigureAwait(true);
        editor.CompletionWindow!.Close();

        shell.ShowDevices();
        await WaitAsync(() => shell.DevicesWindow.Toolchain.Length > 0, "the Devices window shows the Android toolchain").ConfigureAwait(true);
        await Task.Delay(500).ConfigureAwait(true);
        Screenshot("maui-devices.png");
        await SoftWaitAsync(() => shell.Solutions.RestoreState is "succeeded" or "failed", 300).ConfigureAwait(true);
        DismissRestoreFailureWithoutMauiWorkloads();
    }

    /// <summary>
    /// Without the MAUI workloads (the CI runners and most machines have none), restoring the MAUI solution fails on
    /// TodoApp (NETSDK1147) and MauiViews is restored on its own, as SolutionSession.RestoreAsync does: the failure is
    /// expected here, and its error notification would stay in every later picture. It is dismissed, and printed to the
    /// run's log, only when the restore's output names the missing workload; any other restore failure stays.
    /// </summary>
    private void DismissRestoreFailureWithoutMauiWorkloads()
    {
        if (shell.Solutions.RestoreState != "failed")
        {
            return;
        }

        var restore = shell.Workbench.Output.Channels.FirstOrDefault(c => c.Id == "codalus.restore")?.GetText() ?? string.Empty;
        var failures = shell.Workbench.Notifications.Items.Where(n => n.Message.StartsWith("Restore failed", StringComparison.Ordinal)).ToList();
        if (!restore.Contains("NETSDK1147", StringComparison.Ordinal))
        {
            Console.Out.WriteLine("  (the MAUI solution's restore failed, not for want of a workload: " + Tail(restore, 1500) + ")");
            return;
        }

        foreach (var failure in failures)
        {
            Console.Out.WriteLine($"  (expected without the MAUI workloads, dismissed: {failure.Message})");
            shell.Workbench.Notifications.Dismiss(failure);
        }
    }

    private async Task WindowsDesktopStepAsync()
    {
        await WaitAsync(() => shell.Runs.Running.Count == 0, "the previous run has stopped", 60).ConfigureAwait(true);
        var target = Path.Combine(Work, "WindowsDesktop");
        if (!Directory.Exists(target))
        {
            Copy(Path.Combine(samples, "WindowsDesktop"), target);
        }

        await shell.OpenSolutionAsync(Path.Combine(target, "WindowsDesktop.slnx")).ConfigureAwait(true);
        await WaitAsync(() => shell.Solutions.Projects.Count == 4 && shell.Solutions.Progress is { Completed: true }, "the WindowsDesktop solution is open (its packages restored)", 600).ConfigureAwait(true);
        var app = Path.Combine(target, "WpfApp", "WpfApp.csproj");
        Check(shell.Solutions.Projects.TryGetValue(app, out var info) && info.UsesWpf, "WpfApp is a WPF project");

        var path = Path.Combine(target, "WpfApp", "MainWindow.xaml");
        var editor = await OpenAsync(path).ConfigureAwait(true);
        Check(editor.Model.LanguageId == ShellHost.XamlLanguageId, "the file is XAML for the C# host: " + editor.Model.LanguageId);
        MoveCaret(editor, "Text=\"{Binding NewTitle,", "Text=\"{Binding ".Length);
        var asked = Stopwatch.StartNew();
        editor.ShowCompletion();
        await WaitAsync(() =>
        {
            if (editor.CompletionWindow is { } w && w.CompletionList.CompletionData.Any(d => d.Text == "AddCommand"))
            {
                return true;
            }

            if (asked.Elapsed > TimeSpan.FromSeconds(10))
            {
                editor.CompletionWindow?.Close();
                MoveCaret(editor, "Text=\"{Binding NewTitle,", "Text=\"{Binding ".Length);
                editor.ShowCompletion();
                asked.Restart();
            }

            return false;
        }, "the binding path completes the view model's members (d:DataContext)", 360).ConfigureAwait(true);
        await Task.Delay(500).ConfigureAwait(true);
        Screenshot("wpf-xaml.png");
        editor.CompletionWindow!.Close();

        // Start Without Debugging builds the app and runs it: a WPF window on the desktop, until it's stopped.
        shell.SetStartupProject(app);
        await shell.StartWithoutDebuggingAsync().ConfigureAwait(true);
        await WaitAsync(() => shell.Runs.Running.Any(p => p.Name == "WpfApp" && !p.HasExited), "the WPF app is running: " + Tail(shell.Builds.Channel.GetText(), 2000), 300).ConfigureAwait(true);
        await Task.Delay(3000).ConfigureAwait(true);
        Check(shell.Runs.Running.Any(p => p.Name == "WpfApp" && !p.HasExited), "the WPF app is still running (its window is up)");
        shell.StopRunning();
        await WaitAsync(() => shell.Runs.Running.All(p => p.HasExited), "the WPF app stops", 60).ConfigureAwait(true);
    }

    /// <summary>
    /// .NET Framework in the shipped app (M43a, ADR 0075): samples/NetFramework opens, the old-style project gets its
    /// packages.config package and reference assemblies, the project that needs Visual Studio's targets says why it
    /// can't build (<c>netfx-projects.png</c>), and the SDK-style net48 console app builds. On Windows Start Without
    /// Debugging runs its .exe on the desktop CLR and its output comes back, and F5 debugs it there to a breakpoint (M43b,
    /// ADR 0076: <c>netfx-debugging.png</c>); elsewhere starting it says Windows only.
    /// </summary>
    private async Task NetFrameworkStepAsync()
    {
        await WaitAsync(() => shell.Runs.Running.All(p => p.HasExited), "the previous run has stopped", 60).ConfigureAwait(true);
        var target = Path.Combine(Work, "NetFramework");
        if (!Directory.Exists(target))
        {
            Copy(Path.Combine(samples, "NetFramework"), target);
        }

        await shell.OpenSolutionAsync(Path.Combine(target, "NetFramework.slnx")).ConfigureAwait(true);
        var legacy = Path.Combine(target, "LegacyApp", "LegacyApp.csproj");
        var web = Path.Combine(target, "LegacyWebApp", "LegacyWebApp.csproj");
        var app = Path.Combine(target, "ConsoleApp", "ConsoleApp.csproj");
        await WaitAsync(
            () => shell.Solutions.Projects.Count == 8 && shell.Solutions.Progress is { Completed: true }
                  && shell.Solutions.Projects.TryGetValue(legacy, out var info) && info.References.Any(r => r.Kind == Codalus.Projects.Protocol.ReferenceKinds.Package && r.Resolved),
            "the NetFramework solution is open, LegacyApp's packages.config package restored", 600).ConfigureAwait(true);
        Check(shell.Solutions.Projects[legacy].IsNonSdkStyle, "LegacyApp is a non-SDK-style project");
        Check(shell.Solutions.Projects[web].CannotBuildReason?.Contains("imports proprietary MSBuild targets", StringComparison.Ordinal) == true, "LegacyWebApp says why it can't build");
        shell.Explorer.Reveal("p:" + legacy);
        await Task.Delay(1000).ConfigureAwait(true);
        Screenshot("netfx-projects.png");
        await GalleryNetFrameworkAsync(legacy, web).ConfigureAwait(true); // M34g: the .NET Framework project UI in the gallery

        Check(await shell.BuildAsync(Codalus.Projects.Protocol.BuildTargets.Build, app).ConfigureAwait(true), "ConsoleApp (net48) builds:\n" + Tail(shell.Builds.Channel.GetText(), 3000));
        Check(File.Exists(Path.Combine(target, "ConsoleApp", "bin", "Debug", "net48", "ConsoleApp.exe")), "ConsoleApp.exe was built");
        shell.SetStartupProject(app);
        if (!OperatingSystem.IsWindows())
        {
            await shell.StartWithoutDebuggingAsync().ConfigureAwait(true);
            Check(shell.Workbench.Notifications.Items.Any(n => n.Message.Contains("ConsoleApp targets .NET Framework (net48): it runs, debugs and tests only on Windows", StringComparison.Ordinal)), "starting it says Windows only");
            return;
        }

        // A program that ends leaves Runs.Running at once; its Run channel says how it ended, after its output.
        string Output() => shell.Workbench.Output.Channels.FirstOrDefault(c => c.Name == "Run: ConsoleApp")?.GetText() ?? string.Empty;
        shell.Workbench.Output.Channels.FirstOrDefault(c => c.Name == "Run: ConsoleApp")?.Clear(); // an earlier step's ConsoleApp
        await shell.StartWithoutDebuggingAsync().ConfigureAwait(true);
        await WaitAsync(() => Output().Contains("has exited with code", StringComparison.Ordinal), () => "ConsoleApp ran: " + Tail(Output() + shell.Builds.Channel.GetText(), 2000), 300).ConfigureAwait(true);
        Check(Output().Contains("has exited with code 0 (0x0)", StringComparison.Ordinal), "it exits with code 0: " + Output());
        Check(Output().Contains("Runtime: .NET Framework 4.", StringComparison.Ordinal), "its output names the desktop CLR: " + Output());
        Check(Output().Contains("Hello from .NET Framework, Codalus!", StringComparison.Ordinal), "its output comes back");

        // F5: the desktop-CLR debugger (codalus-debughost --netfx-dap) stops at a breakpoint in Main and evaluates.
        var source = Path.Combine(target, "ConsoleApp", "Program.cs");
        var line = Array.FindIndex(File.ReadAllLines(source), l => l.Contains("Console.WriteLine(greeter.Greet())", StringComparison.Ordinal)) + 1;
        shell.Debugger.Breakpoints.Set(new Codalus.Workbench.Debugging.BreakpointSpec(source, line));
        await shell.StartDebuggingAsync().ConfigureAwait(true);
        await WaitAsync(() => shell.Debugger.State == Codalus.Workbench.Debugging.DebugState.Stopped, () => "F5 stops ConsoleApp (net48) at its breakpoint: " + shell.DebugLog.GetText(), 300).ConfigureAwait(true);
        Check(shell.Debugger.SelectedFrame is { Name: "ConsoleApp.Program.Main" } frame && frame.Line == line, $"it stopped in Main on line {line}: {shell.Debugger.SelectedFrame?.Name} {shell.Debugger.SelectedFrame?.Line}");
        var greeting = await shell.Debugger.EvaluateAsync("greeter.Greet()", "watch", CancellationToken.None).ConfigureAwait(true);
        Check(greeting.Result == "\"Hello from .NET Framework, Codalus!\"", "greeter.Greet() evaluates through func-eval: " + greeting.Result);
        await Task.Delay(1000).ConfigureAwait(true);
        Screenshot("netfx-debugging.png");
        await GalleryNetFrameworkDebuggingAsync().ConfigureAwait(true); // M34g: the Call Stack and Modules of a desktop-CLR session
        shell.Debugger.Breakpoints.Clear();
        await shell.Debugger.StopAsync().ConfigureAwait(true);
        await WaitAsync(() => shell.Debugger.State == Codalus.Workbench.Debugging.DebugState.Idle, "the debug session ends", 60).ConfigureAwait(true);
    }

    /// <summary>
    /// WinUI 3 in the shipped app (M44, Windows): the WinUI fixture opens, MainPage.xaml completes an x:Bind path from
    /// the page's class (<c>winui-xaml.png</c>), and Start Without Debugging builds the unpackaged app and runs it until
    /// it's stopped.
    /// </summary>
    private async Task WinUIStepAsync()
    {
        await WaitAsync(() => shell.Runs.Running.Count == 0, "the previous run has stopped", 60).ConfigureAwait(true);
        var target = Path.Combine(Work, "WindowsDesktop");
        if (!Directory.Exists(target))
        {
            Copy(Path.Combine(samples, "WindowsDesktop"), target);
        }

        await shell.OpenSolutionAsync(Path.Combine(target, "WinUIApp.slnx")).ConfigureAwait(true);
        await WaitAsync(() => shell.Solutions.Projects.Count == 1 && shell.Solutions.Progress is { Completed: true }, "the WinUI solution is open (its packages restored)", 600).ConfigureAwait(true);
        var app = Path.Combine(target, "WinUIApp", "WinUIApp.csproj");
        Check(shell.Solutions.Projects.TryGetValue(app, out var info) && info.UsesWinUI && info.WindowsPackageType == "None", "WinUIApp is an unpackaged WinUI 3 project: " + info?.WindowsPackageType);

        var path = Path.Combine(target, "WinUIApp", "MainPage.xaml");
        var editor = await OpenAsync(path).ConfigureAwait(true);
        Check(editor.Model.LanguageId == ShellHost.XamlLanguageId, "the file is XAML for the C# host: " + editor.Model.LanguageId);
        const string Marker = "{x:Bind ViewModel.NewTitle,";
        MoveCaret(editor, Marker, "{x:Bind ViewModel.".Length);
        var asked = Stopwatch.StartNew();
        editor.ShowCompletion();
        await WaitAsync(() =>
        {
            if (editor.CompletionWindow is { } w && w.CompletionList.CompletionData.Any(d => d.Text == "IsTyping"))
            {
                return true;
            }

            if (asked.Elapsed > TimeSpan.FromSeconds(10))
            {
                editor.CompletionWindow?.Close();
                MoveCaret(editor, Marker, "{x:Bind ViewModel.".Length);
                editor.ShowCompletion();
                asked.Restart();
            }

            return false;
        }, "the x:Bind path completes the view model's members (from the page's class)", 360).ConfigureAwait(true);
        await Task.Delay(500).ConfigureAwait(true);
        Screenshot("winui-xaml.png");
        editor.CompletionWindow!.Close();

        // Start Without Debugging builds the app and runs it: a WinUI window on the desktop, until it's stopped.
        shell.SetStartupProject(app);
        var before = shell.Builds.LastBuild;
        await shell.StartWithoutDebuggingAsync().ConfigureAwait(true);
        // A failed build starts nothing: it fails the step at once (the first Windows run waited out the 600 s and the job's limit).
        bool BuildFailed() => BuildDone(before) && shell.Builds.LastBuild is { Result.Succeeded: false };
        await WaitAsync(() => shell.Runs.Running.Any(p => p.Name == "WinUIApp" && !p.HasExited) || BuildFailed(), () => "the WinUI app is running: " + Tail(shell.Builds.Channel.GetText(), 2000), 600).ConfigureAwait(true);
        Check(!BuildFailed(), "the WinUI app built:\n" + Tail(shell.Builds.Channel.GetText(), 3000));
        await Task.Delay(3000).ConfigureAwait(true);
        Check(shell.Runs.Running.Any(p => p.Name == "WinUIApp" && !p.HasExited), "the WinUI app is still running (its window is up)");
        shell.StopRunning();
        await WaitAsync(() => shell.Runs.Running.All(p => p.HasExited), "the WinUI app stops", 60).ConfigureAwait(true);
    }

    private async Task VisualBasicStepAsync()
    {
        await WaitAsync(() => shell.Runs.Running.Count == 0, "the previous run has stopped", 60).ConfigureAwait(true);
        var target = Path.Combine(Work, "VisualBasic");
        if (!Directory.Exists(target))
        {
            Copy(Path.Combine(samples, "VisualBasic"), target);
        }

        await shell.OpenSolutionAsync(Path.Combine(target, "VisualBasic.slnx")).ConfigureAwait(true);
        await WaitAsync(() => shell.Solutions.Projects.Count == 4 && shell.Solutions.Progress is { Completed: true }, "the Visual Basic solution is open (its packages restored)", 600).ConfigureAwait(true);
        await WaitAsync(() => shell.Languages.Status(ShellHost.CSharpLanguageId) is { State: LanguageServices.Protocol.WorkspaceStates.Ready, Projects: 4 }, "the C# host has loaded the VB and C# projects", 300).ConfigureAwait(true);

        var path = Path.Combine(target, "VbConsole", "Program.vb");
        var editor = await OpenAsync(path).ConfigureAwait(true);
        Check(editor.Model.LanguageId == ShellHost.VisualBasicLanguageId, "the file is Visual Basic: " + editor.Model.LanguageId);

        // formatter. completes PriceFormatter's members: a C# class in another project.
        var loop = editor.Model.Text.Text.IndexOf("Console.WriteLine(formatter.Format(", StringComparison.Ordinal);
        var dot = loop + "Console.WriteLine(formatter.".Length;
        var asked = Stopwatch.StartNew();
        editor.Editor.CaretOffset = dot;
        editor.ShowCompletion();
        await WaitAsync(() =>
        {
            if (editor.CompletionWindow is { } w && w.CompletionList.CompletionData.Any(d => d.Text == "Currency"))
            {
                return true;
            }

            if (asked.Elapsed > TimeSpan.FromSeconds(10))
            {
                editor.CompletionWindow?.Close();
                editor.Editor.CaretOffset = dot;
                editor.ShowCompletion();
                asked.Restart();
            }

            return false;
        }, "formatter. completes the C# library's members", 300).ConfigureAwait(true);
        await Task.Delay(500).ConfigureAwait(true);
        Screenshot("visual-basic.png");
        editor.CompletionWindow!.Close();

        // Enter after an If … Then adds End If (our pretty listing, through on-type formatting).
        var main = editor.Model.Text.Text.IndexOf("Dim inventory As New Inventory()", StringComparison.Ordinal);
        var original = editor.Model.Text.Text;
        try
        {
            editor.Model.Text.Insert(main, "If args.Length > 0 Then\n        ");
            editor.Editor.CaretOffset = main + "If args.Length > 0 Then".Length;
            editor.Editor.TextArea.PerformTextInput("\n");
            await WaitAsync(() => editor.Model.Text.Text.Contains("End If", StringComparison.Ordinal), "End If follows the If block").ConfigureAwait(true);
        }
        finally
        {
            editor.Model.Text.Text = original;
        }
    }

    private async Task FSharpStepAsync()
    {
        await WaitAsync(() => shell.Runs.Running.Count == 0, "the previous run has stopped", 60).ConfigureAwait(true);
        var target = Path.Combine(Work, "FSharp");
        if (!Directory.Exists(target))
        {
            Copy(Path.Combine(samples, "FSharp"), target);
        }

        await shell.InstallFSharpAsync().ConfigureAwait(true);
        Check(shell.FSharp.IsInstalled, "FsAutoComplete and Fantomas are installed:\n" + Tail(shell.LanguageLog.GetText(), 2000));
        await shell.OpenSolutionAsync(Path.Combine(target, "FSharp.slnx")).ConfigureAwait(true);
        await WaitAsync(() => shell.Solutions.Projects.Count == 5 && shell.Solutions.Progress is { Completed: true }, "the F# solution is open", 600).ConfigureAwait(true);

        var library = Path.Combine(target, "FsLibrary", "FsLibrary.fsproj");
        var tree = Workbench.Projects.SolutionTree.Contents(library, shell.Solutions.Projects[library]);
        Check(tree.Select(n => n.Name).SequenceEqual(["Shapes.fs", "Geometry.fs"]) && tree[0].Children.Single().Name == "Shapes.fsi",
            "Solution Explorer shows the compile order with Shapes.fsi under Shapes.fs: " + string.Join(", ", tree.Select(n => n.Name)));

        // Quick Info on Shapes.area, from the file before Geometry.fs in the compile order.
        var path = Path.Combine(target, "FsLibrary", "Geometry.fs");
        var editor = await OpenAsync(path).ConfigureAwait(true);
        Check(editor.Model.LanguageId == ShellHost.FSharpLanguageId, "the file is F#: " + editor.Model.LanguageId);
        var uri = LanguageServices.Protocol.DocumentUris.FromPath(path);
        var offset = editor.Model.Text.Text.IndexOf("List.sumBy area", StringComparison.Ordinal) + "List.sumBy ar".Length;
        var line = editor.Model.Text.GetLineByOffset(offset);
        // FsAutoComplete loads the solution's projects first: ask again until it answers.
        var client = await shell.Languages.ClientAsync(ShellHost.FSharpLanguageId).ConfigureAwait(true);
        Check(client is not null, "FsAutoComplete starts:\n" + Tail(shell.LanguageLog.GetText(), 2000));
        string? info = null;
        var asked = Stopwatch.StartNew();
        while (info?.Contains("val area", StringComparison.Ordinal) != true && asked.Elapsed < TimeSpan.FromSeconds(300))
        {
            try
            {
                client = await shell.Languages.ClientAsync(ShellHost.FSharpLanguageId).ConfigureAwait(true) ?? client;
                info = (await client!.HoverAsync(uri, new LanguageServices.Protocol.Position(line.LineNumber - 1, offset - line.Offset), CancellationToken.None).ConfigureAwait(true))?.Contents.Value;
            }
            catch (Rpc.JsonRpcRemoteException)
            {
                // not loaded yet
            }
            catch (Exception ex) when (ex is Rpc.JsonRpcConnectionLostException or ObjectDisposedException)
            {
                // FsAutoComplete starts again once a project it loaded is restored
            }

            if (info?.Contains("val area", StringComparison.Ordinal) != true)
            {
                await Task.Delay(1000).ConfigureAwait(true);
            }
        }

        Check(info?.Contains("val area", StringComparison.Ordinal) == true, "Quick Info on area shows its signature: " + info);
        shell.Explorer.Reveal("f:" + path);
        await Task.Delay(1000).ConfigureAwait(true);
        Screenshot("fsharp.png");
    }

    private async Task FSharpInteractiveStepAsync()
    {
        var target = Path.Combine(Work, "FSharp");
        if (!Directory.Exists(target))
        {
            Copy(Path.Combine(samples, "FSharp"), target);
        }

        var editor = await OpenAsync(Path.Combine(target, "Scripts", "Explore.fsx")).ConfigureAwait(true);
        var start = editor.Model.Text.Text.IndexOf("let square", StringComparison.Ordinal);
        var end = editor.Model.Text.Text.IndexOf("// [fsi-end]", StringComparison.Ordinal);
        editor.Editor.Select(start, end - start);
        shell.SendToFSharpInteractive();
        var session = shell.FSharpInteractive;
        Check(session is not null, "F# Interactive started");
        string Screen() => string.Join('\n', Enumerable.Range(0, session!.Terminal.TotalRows).Select(i => session.Terminal.GetRow(i).GetText()));
        await WaitAsync(() => Screen().Contains("val squares: int list = [1; 4; 9]", StringComparison.Ordinal), "F# Interactive shows val squares", 180).ConfigureAwait(true);
        await Task.Delay(500).ConfigureAwait(true);
        Screenshot("fsharp-interactive.png");
        shell.Terminals.Close(session!);
    }

    /// <summary>
    /// The Windows Forms designer in the shipped app (M41a): the solution windows.desktop opened, OrderForm in the
    /// designer, a Button from the Toolbox, its Text from the Properties window, and the designer file's new code.
    /// </summary>
    private async Task WinFormsDesignerStepAsync()
    {
        await WaitAsync(() => shell.Runs.Running.Count == 0, "the previous run has stopped", 60).ConfigureAwait(true);
        var app = Path.Combine(Work, "WindowsDesktop", "WinFormsApp");
        Check(shell.Solutions.Projects.Values.Any(p => p.UsesWindowsForms), "the WindowsDesktop solution is still open");
        await WaitAsync(() => shell.Languages.Status(ShellHost.CSharpLanguageId) is { State: LanguageServices.Protocol.WorkspaceStates.Ready }, "the C# host has loaded the solution", 300).ConfigureAwait(true);

        var started = Stopwatch.StartNew();
        await shell.OpenFileAsync(Path.Combine(app, "OrderForm.cs")).ConfigureAwait(true);
        var designer = shell.Layout.Documents.OfType<ViewModels.WinFormsDesignerDocument>().FirstOrDefault();
        Check(designer is not null, "OrderForm.cs opens in the designer");
        await WaitAsync(() => designer!.FrameCount > 0, "the designer builds the app, loads the form and draws it: " + designer!.Status, 600).ConfigureAwait(true);
        Check(designer!.Problems is null, "every statement applied: " + designer.Problems);
        Check(designer.Components.Any(c => c.Name == "placeOrderButton" && c.Parent == "buttonPanel"), "the form's controls are on the surface, nested");
        Console.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  first frame (build included) in {started.Elapsed.TotalSeconds:0.0} s, {designer.FrameWidth}×{designer.FrameHeight}"));

        var code = designer.DesignerCode!;
        var original = code.Text.Text;
        shell.Layout.ShowToolWindow(ViewModels.ToolboxViewModel.ToolWindowId, focus: false);
        var button = shell.Toolbox.Items.FirstOrDefault(i => i.Type == "System.Windows.Forms.Button");
        Check(button is not null && shell.Toolbox.IsAvailable, "the Toolbox has the Button");
        shell.Toolbox.Armed = button;
        var root = designer.Root!;
        // On the form's free space left of the counter (M34h: dropped at 290, 300 it landed on the counter control and the
        // gallery showed its selection around nothing, beside the counter's own button).
        await designer.CreateAsync(button!.Type, root.ClientX + 48, root.ClientY + 300).WaitAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(true);
        Check(code.Text.Text.Contains("button1 = new Button();", StringComparison.Ordinal) && code.Text.Text.Contains("Controls.Add(button1);", StringComparison.Ordinal),
            "the new button is in OrderForm.Designer.cs: " + designer.Status);

        shell.Layout.ShowToolWindow(ViewModels.PropertiesWindowViewModel.ToolWindowId, focus: false);
        await shell.PropertiesWindow.RefreshAsync().ConfigureAwait(true);
        // The property's own row: (DataBindings), first in the window, has a Text row too (M41b).
        var text = shell.PropertiesWindow.Categories.Where(c => c.Title != "(DataBindings)").SelectMany(c => c.Rows).FirstOrDefault(r => r.Name == "Text");
        Check(text is { Value: "button1" }, "the Properties window shows the new button's Text: " + text?.Value);
        text!.Text = "Smoke test";
        await WaitAsync(() => code.Text.Text.Contains("button1.Text = \"Smoke test\";", StringComparison.Ordinal), "the Text set in the Properties window is in the code", 60).ConfigureAwait(true);
        var added = code.Text.Text.Split('\n').Length - original.Split('\n').Length;
        // The lines that weren't there before, in the message: the first Windows run counted 14 with nothing else to go on.
        var remaining = original.Split('\n').GroupBy(l => l, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var newLines = new List<string>();
        foreach (var line in code.Text.Text.Split('\n'))
        {
            if (remaining.TryGetValue(line, out var count) && count > 0)
            {
                remaining[line] = count - 1;
            }
            else
            {
                newLines.Add(line);
            }
        }

        Check(added == 12, $"the diff is the button's field, creation, section and Controls.Add: {added} lines:\n{string.Join('\n', newLines)}");
        await RenderedAsync().ConfigureAwait(true);
        Screenshot("winforms-designer.png");
        await GalleryWinFormsAsync(designer).ConfigureAwait(true); // M34g: the designer, the Toolbox and Properties in the gallery

        while (code.Text.UndoStack.CanUndo)
        {
            code.Text.UndoStack.Undo();
        }

        Check(code.Text.Text == original, "undo takes the designer file back to the original");
        shell.Layout.CloseDocument(designer);
        shell.Layout.CloseToolWindow(ViewModels.ToolboxViewModel.ToolWindowId);
    }

    /// <summary>
    /// The Toolbox and building with a form open in the shipped app (0.6, #66): OrderForm opened; Enter on the Toolbox's
    /// TextBox (the form selected) adds textBox1 at the form's default place through the real host's classic creation,
    /// with the designer active again and nothing armed; a CheckBox dropped on the form (the drop handler's own method: a
    /// real drag can't be scripted, and the Avalonia drop is tested headless); the app built through Build Project (which
    /// saves the designer file first), with neither MSB3027 nor MSB3026 (before, the designer host held the app's assembly
    /// and the build's copy step retried ten times and failed), then the form loaded on a new host with both controls;
    /// the designer file put back as it was.
    /// </summary>
    private async Task WinFormsToolboxAndBuildStepAsync()
    {
        await WaitAsync(() => shell.Runs.Running.Count == 0 && !shell.Builds.IsBuilding, "the previous run and build have stopped", 120).ConfigureAwait(true);
        var app = Path.Combine(Work, "WindowsDesktop", "WinFormsApp");
        var project = Path.Combine(app, "WinFormsApp.csproj");
        var form = Path.Combine(app, "OrderForm.cs");
        Check(shell.Solutions.Projects.Values.Any(p => p.UsesWindowsForms), "the WindowsDesktop solution is still open");
        await shell.OpenFileAsync(form).ConfigureAwait(true);
        var designer = shell.Layout.Documents.OfType<ViewModels.WinFormsDesignerDocument>().FirstOrDefault(d => d.CodePath == form);
        Check(designer is not null, "OrderForm.cs opens in the designer");
        await WaitAsync(() => designer!.FrameCount > 0 && designer.Host is not null, () => "the designer loads the form and draws it: " + designer!.Status, 600).ConfigureAwait(true);
        var code = designer!.DesignerCode!;
        var original = await File.ReadAllTextAsync(designer.DesignerPath).ConfigureAwait(true);
        var root = designer.Root!;

        // Enter on the Toolbox's TextBox, the form selected: textBox1 at the form's default place, selected, the designer active.
        shell.Layout.ShowToolWindow(ViewModels.ToolboxViewModel.ToolWindowId, focus: false);
        await WaitAsync(() => window.GetVisualDescendants().OfType<ToolboxView>().Any(v => v.IsEffectivelyVisible), "the Toolbox shows", 30).ConfigureAwait(true);
        var toolbox = window.GetVisualDescendants().OfType<ToolboxView>().First(v => v.IsEffectivelyVisible);
        Button? textItem = null;
        await WaitAsync(() => (textItem = toolbox.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.DataContext is ViewModels.ToolboxItemViewModel { Type: "System.Windows.Forms.TextBox" })) is not null,
            "the Toolbox lists the TextBox", 30).ConfigureAwait(true);
        var item = textItem!;
        item.BringIntoView();
        designer.SetSelection([root.Name], notify: true);
        item.Focus();
        await RenderedAsync().ConfigureAwait(true);
        item.RaiseEvent(new Avalonia.Input.KeyEventArgs { RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent, Key = Avalonia.Input.Key.Enter, Source = item });
        item.RaiseEvent(new Avalonia.Input.KeyEventArgs { RoutedEvent = Avalonia.Input.InputElement.KeyUpEvent, Key = Avalonia.Input.Key.Enter, Source = item });
        await WaitAsync(() => code.Text.Text.Contains("textBox1 = new TextBox();", StringComparison.Ordinal) && designer.Selection is ["textBox1"] && designer.Find("textBox1") is not null,
            () => "Enter on the Toolbox's TextBox adds textBox1: " + designer.Status, 60).ConfigureAwait(true);
        var textBox = designer.Find("textBox1")!;
        Check(code.Text.Text.Contains("Controls.Add(textBox1);", StringComparison.Ordinal) && textBox.Parent == root.Name, "textBox1 is on the form: " + textBox.Parent);
        Check(textBox.X == root.ClientX && textBox.Y == root.ClientY, $"textBox1 is at the form's default place, its client area's corner: {textBox.X}, {textBox.Y}, not {root.ClientX}, {root.ClientY}");
        Check(shell.Layout.ActiveDocument == designer && shell.Toolbox.Armed is null, "the designer is active again and nothing is armed");

        // A CheckBox dropped on the form's free space.
        var surface = window.GetVisualDescendants().OfType<DesignerSurface>().First(s => s.IsEffectivelyVisible);
        await surface.DropToolboxItemAsync("System.Windows.Forms.CheckBox", new Point(root.ClientX + 48, root.ClientY + 300)).WaitAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(true);
        Check(code.Text.Text.Contains("checkBox1 = new CheckBox();", StringComparison.Ordinal), "the dropped check box is in OrderForm.Designer.cs: " + designer.Status);
        shell.Layout.CloseToolWindow(ViewModels.ToolboxViewModel.ToolWindowId);

        var host = designer.Host;
        var frames = designer.FrameCount;
        var before = shell.Builds.LastBuild;
        var built = await shell.BuildProjectAsync(Codalus.Projects.Protocol.BuildTargets.Build, project).WaitAsync(TimeSpan.FromMinutes(10)).ConfigureAwait(true);
        await WaitAsync(() => BuildDone(before), "the build finishes", 60).ConfigureAwait(true);
        var output = shell.Builds.Channel.GetText();
        Check(built, "the app builds with OrderForm open in the designer:\n" + Tail(output, 3000));
        Check(!output.Contains("MSB3027", StringComparison.Ordinal) && !output.Contains("MSB3026", StringComparison.Ordinal), "no build step waited for or failed on a file the designer holds:\n" + Tail(output, 3000));

        // The build replaced the app's assembly: the designer loads the form on a new host, with the saved check box.
        await WaitAsync(() => designer.Host != host && designer.FrameCount > frames && !designer.IsRestarting,
            () => "the designer loads the form on the new build: " + designer.Status + "\n" + Tail(shell.WinFormsDesignerLog.GetText(), 2000), 180).ConfigureAwait(true);
        Check(designer.Problems is null, "every statement applied after the restart: " + designer.Problems);
        Check(designer.Components.Any(c => c.Name == "checkBox1") && designer.Components.Any(c => c.Name == "textBox1"), "the text box and the check box are on the form after the restart");
        foreach (var line in shell.WinFormsDesignerLog.GetText().Split('\n').Where(l => l.StartsWith("Copied the build output", StringComparison.Ordinal) || l.StartsWith("A build replaced", StringComparison.Ordinal)).TakeLast(3))
        {
            Console.Out.WriteLine("  " + line.TrimEnd());
        }

        // The designer file as it was, saved (the build saved the check box).
        while (code.Text.UndoStack.CanUndo)
        {
            code.Text.UndoStack.Undo();
        }

        await shell.SaveAllAsync().ConfigureAwait(true);
        Check(await File.ReadAllTextAsync(designer.DesignerPath).ConfigureAwait(true) == original, "undo and save take OrderForm.Designer.cs back to the original");
        shell.Layout.CloseDocument(designer);
    }

    /// <summary>
    /// The designer's depth in the shipped app (M41b): InventoryForm opened, "&amp;Edit" typed into its menu strip's Type Here
    /// (editToolStripMenuItem and the menu's AddRange), "ToolTip on inventoryToolTip" set on the list view (SetToolTip),
    /// a frame drawn with the new item, and both undone.
    /// </summary>
    private async Task WinFormsDesignerDepthStepAsync()
    {
        var app = Path.Combine(Work, "WindowsDesktop", "WinFormsApp");
        await shell.OpenFileAsync(Path.Combine(app, "InventoryForm.cs")).ConfigureAwait(true);
        var designer = shell.Layout.Documents.OfType<ViewModels.WinFormsDesignerDocument>().FirstOrDefault(d => d.CodePath.EndsWith("InventoryForm.cs", StringComparison.Ordinal));
        Check(designer is not null, "InventoryForm.cs opens in the designer");
        await WaitAsync(() => designer!.FrameCount > 0, "the designer loads InventoryForm and draws it: " + designer!.Status, 600).ConfigureAwait(true);
        Check(designer!.Problems is null, "every statement applied: " + designer.Problems);
        Check(designer.Tray.Any(t => t.Name == "inventoryToolTip") && designer.Tray.Any(t => t.Name == "itemBindingSource"), "the component tray has the form's components");
        var code = designer.DesignerCode!;
        var original = code.Text.Text;

        // Type Here on the menu strip.
        designer.SetSelection(["mainMenuStrip"], notify: true);
        Check(designer.StripTypeHere is { CanType: true }, "the menu strip shows Type Here");
        await designer.AddItemAsync(designer.StripTypeHere!, "&Edit").WaitAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(true);
        Check(code.Text.Text.Contains("editToolStripMenuItem = new ToolStripMenuItem();", StringComparison.Ordinal)
            && code.Text.Text.Contains("mainMenuStrip.Items.AddRange(new ToolStripItem[] { fileToolStripMenuItem, helpToolStripMenuItem, editToolStripMenuItem });", StringComparison.Ordinal)
            && code.Text.Text.Contains("editToolStripMenuItem.Text = \"&Edit\";", StringComparison.Ordinal),
            "the typed item is in InventoryForm.Designer.cs: " + designer.Status);
        Check(designer.Components.Any(c => c is { Name: "editToolStripMenuItem", IsItem: true, Owner: "mainMenuStrip" }), "the new item is on the surface, in the menu strip");

        // An extender property in the Properties window.
        designer.SetSelection(["itemsListView"], notify: true);
        shell.Layout.ShowToolWindow(ViewModels.PropertiesWindowViewModel.ToolWindowId, focus: false);
        await shell.PropertiesWindow.RefreshAsync().ConfigureAwait(true);
        var tip = shell.PropertiesWindow.Categories.SelectMany(c => c.Rows).FirstOrDefault(r => r.Name == "ToolTip on inventoryToolTip");
        Check(tip is not null, "the Properties window shows ToolTip on inventoryToolTip");
        tip!.Text = "The items in stock.";
        await WaitAsync(() => code.Text.Text.Contains("inventoryToolTip.SetToolTip(itemsListView, \"The items in stock.\");", StringComparison.Ordinal), "the ToolTip is a SetToolTip call in the code", 60).ConfigureAwait(true);
        await RenderedAsync().ConfigureAwait(true);
        Screenshot("winforms-designer-depth.png");

        for (var i = 0; i < 10 && code.Text.Text != original; i++)
        {
            designer.Undo();
        }

        Check(code.Text.Text == original, "undo takes the designer file back to the original");
        shell.Layout.CloseDocument(designer);
    }

    /// <summary>The WPF previewer in the shipped app (M40b): the solution windows.desktop opened, its MainWindow.xaml previewed.</summary>
    private async Task WpfPreviewStepAsync()
    {
        await WaitAsync(() => shell.Runs.Running.Count == 0, "the previous run has stopped", 60).ConfigureAwait(true);
        var path = Path.Combine(Work, "WindowsDesktop", "WpfApp", "MainWindow.xaml");
        Check(shell.Solutions.Projects.Values.Any(p => p.UsesWpf), "the WindowsDesktop solution is still open");
        var editor = await OpenAsync(path).ConfigureAwait(true);
        var original = editor.Model.Text.Text;

        var started = Stopwatch.StartNew();
        var preview = shell.XamlPreview;
        // FrameCount counts every frame the tool window has shown, the Avalonia ones of xaml.preview included (Clear keeps
        // it), so the WPF file's first frame is one past what was counted before it opened.
        var shown = preview.FrameCount;
        shell.ShowXamlPreview();
        await WaitAsync(() => (preview.FrameCount > shown && preview.Frame is not null) || preview.HasError, "the WPF previewer builds the app and shows its first frame: " + preview.Status, 600).ConfigureAwait(true);
        Check(preview.FrameCount > shown && preview.Frame is not null, "the preview rendered: " + preview.Error + "\n" + Tail(shell.PreviewerLog.GetText(), 2000));
        Check(preview.Frame is { } frame && frame.Width / frame.Scale == 480 && frame.Height / frame.Scale == 360, $"the window renders at its design size (d:DesignWidth/Height): {preview.Frame?.Width}×{preview.Frame?.Height}");
        Check(!preview.SupportsThemes, "WPF has no theme variants: the picker is hidden");
        Console.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  first frame (build included) in {started.Elapsed.TotalSeconds:0.0} s, {preview.ImageWidth}×{preview.ImageHeight}"));

        var session = shell.Previewer.Session;
        Check(session is not null, "the WPF previewer runs");
        Workbench.Previewer.PreviewPathStep[] remaining = [new("MainWindow", 0), new("Grid", 0), new("StackPanel", 0), new("TextBlock", 0)];
        var text = await session!.BoundsAsync(remaining, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(true);
        Check(text is { Width: > 0 }, "the design-time view model (d:DesignInstance) shows its remaining count");

        var before = preview.Frame!;
        var edit = Stopwatch.StartNew();
        var add = editor.Model.Text.Text.IndexOf("Content=\"Add\"", StringComparison.Ordinal);
        Check(add >= 0, "the button's content is in the XAML");
        editor.Model.Text.Replace(add, "Content=\"Add\"".Length, "Content=\"Add a to-do\"");
        await WaitAsync(() => preview.Frame is { } shown && shown != before && !shown.Data.AsSpan().SequenceEqual(before.Data), "the edit shows in the preview", 60).ConfigureAwait(true);
        Console.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  edit to the frame showing it: {edit.Elapsed.TotalMilliseconds:0} ms (the editor waits 150 ms after the last keystroke)"));

        Workbench.Previewer.PreviewPathStep[] button = [new("MainWindow", 0), new("Grid", 0), new("DockPanel", 0), new("Button", 0)];
        var bounds = await session.BoundsAsync(button, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(true);
        Check(bounds is { Width: > 0 }, "the WPF previewer finds the button");
        await shell.SelectFromPreviewAsync(bounds!.Value.X + bounds.Value.Width / 2, bounds.Value.Y + bounds.Value.Height / 2).WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(true);
        Check(editor.Editor.SelectedText.StartsWith("<Button DockPanel.Dock=\"Right\"", StringComparison.Ordinal), "a click selects the button in the XAML: " + editor.Editor.SelectedText);
        await RenderedAsync().ConfigureAwait(true);
        Screenshot("wpf-previewer.png");
        await GalleryWpfPreviewAsync().ConfigureAwait(true); // M34g: the WPF preview in the gallery
        shell.Layout.CloseToolWindow(ViewModels.XamlPreviewViewModel.ToolWindowId);
        Restore(editor, original);
        await shell.Previewer.InvalidateAsync().WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(true);
    }

    private async Task XamlPreviewStepAsync()
    {
        await WaitAsync(() => shell.Runs.Running.Count == 0, "the previous run has stopped", 60).ConfigureAwait(true);
        var target = Path.Combine(Work, "Avalonia");
        if (!Directory.Exists(target))
        {
            Copy(Path.Combine(samples, "Avalonia"), target);
        }

        await shell.OpenSolutionAsync(Path.Combine(target, "Avalonia.slnx")).ConfigureAwait(true);
        await WaitAsync(() => shell.Solutions.Projects.Count == 2 && shell.Solutions.Progress is { Completed: true }, "the Avalonia solution is open (its packages restored)", 600).ConfigureAwait(true);
        await WaitAsync(() => shell.Languages.Status(ShellHost.CSharpLanguageId) is { State: LanguageServices.Protocol.WorkspaceStates.Ready, Projects: 2 }, "the C# host has loaded both projects", 300).ConfigureAwait(true);

        var path = Path.Combine(target, "AvaloniaApp", "Views", "MainWindow.axaml");
        var editor = await OpenAsync(path).ConfigureAwait(true);
        Check(editor.Model.LanguageId == ShellHost.AxamlLanguageId, "the file is Avalonia XAML: " + editor.Model.LanguageId);
        var original = editor.Model.Text.Text;
        MoveCaret(editor, "Text=\"{Binding Greeting}\"", "Text=\"{Binding ".Length);
        // Asked again every 10 s: on a slow runner the first answers come before the project's compilation is ready.
        var asked = Stopwatch.StartNew();
        editor.ShowCompletion();
        await WaitAsync(() =>
        {
            if (editor.CompletionWindow is { } w && w.CompletionList.CompletionData.Any(d => d.Text == "Greeting"))
            {
                return true;
            }

            if (asked.Elapsed > TimeSpan.FromSeconds(10))
            {
                editor.CompletionWindow?.Close();
                MoveCaret(editor, "Text=\"{Binding Greeting}\"", "Text=\"{Binding ".Length);
                editor.ShowCompletion();
                asked.Restart();
            }

            return false;
        }, "the binding path completes the view model's properties", 360).ConfigureAwait(true);
        editor.CompletionWindow!.Close();

        var started = Stopwatch.StartNew();
        shell.ShowXamlPreview();
        var preview = shell.XamlPreview;
        await WaitAsync(() => preview.FrameCount > 0 || preview.HasError, "the previewer builds the app and shows its first frame: " + preview.Status, 600).ConfigureAwait(true);
        Check(preview.FrameCount > 0, "the preview rendered: " + preview.Error + "\n" + Tail(shell.PreviewerLog.GetText(), 2000));
        Console.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  first frame (build included) in {started.Elapsed.TotalSeconds:0.0} s, {preview.ImageWidth}×{preview.ImageHeight}"));

        var before = preview.Frame!;
        var edit = Stopwatch.StartNew();
        var text = editor.Model.Text.Text.IndexOf("Content=\"Increment\"", StringComparison.Ordinal);
        Check(text >= 0, "the button's content is in the XAML");
        editor.Model.Text.Replace(text, "Content=\"Increment\"".Length, "Content=\"Smoke test\"");
        await WaitAsync(() => preview.Frame is { } shown && shown != before && !shown.Data.AsSpan().SequenceEqual(before.Data), "the edit shows in the preview", 60).ConfigureAwait(true);
        Console.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  edit to the frame showing it: {edit.Elapsed.TotalMilliseconds:0} ms (the editor waits 150 ms after the last keystroke)"));

        var session = shell.Previewer.Session;
        Check(session is not null, "the previewer runs");
        Workbench.Previewer.PreviewPathStep[] button = [new("MainWindow", 0), new("DockPanel", 0), new("StackPanel", 0), new("Button", 0)];
        var bounds = await session!.BoundsAsync(button, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(true);
        Check(bounds is { Width: > 0 }, "the agent finds the button in the preview");
        await shell.SelectFromPreviewAsync(bounds!.Value.X + bounds.Value.Width / 2, bounds.Value.Y + bounds.Value.Height / 2).WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(true);
        Check(editor.Editor.SelectedText.StartsWith("<Button x:Name=\"IncrementButton\"", StringComparison.Ordinal), "a click selects the button in the XAML: " + editor.Editor.SelectedText);
        await RenderedAsync().ConfigureAwait(true);
        Screenshot("smoke-test-xaml-preview.png");
        shell.Layout.CloseToolWindow(ViewModels.XamlPreviewViewModel.ToolWindowId);
        Restore(editor, original);
        await shell.Previewer.InvalidateAsync().WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(true);
    }
    private async Task ProfilerStepAsync()
    {
        await WaitAsync(() => shell.Runs.Running.Count == 0, "the previous run has stopped", 60).ConfigureAwait(true);
        var target = Path.Combine(Work, "Diagnostics");
        if (!Directory.Exists(target))
        {
            Copy(Path.Combine(samples, "Diagnostics"), target);
        }

        await shell.OpenSolutionAsync(Path.Combine(target, "Diagnostics.slnx")).ConfigureAwait(true);
        await WaitAsync(() => shell.Solutions.Projects.Count == 6 && shell.Solutions.Progress is { Completed: true }, "the Diagnostics solution is open", 300).ConfigureAwait(true);
        shell.SetStartupProject(Path.Combine(target, "CpuBound", "CpuBound.csproj"));
        var hub = shell.OpenProfiler();
        var run = await hub.StartAsync().ConfigureAwait(true);
        Check(run is not null, "profiling starts: " + hub.Status);
        await WaitAsync(() => !hub.IsCollecting, "CpuBound runs its 3 seconds and the collection stops", 300).ConfigureAwait(true);

        ProfilingReportViewModel? Report() => shell.Layout.Documents.OfType<SolutionManagementDocument>().Select(d => d.ViewModel).OfType<ProfilingReportViewModel>().FirstOrDefault();
        await WaitAsync(() => Report() is { IsLoading: false, Total: > 0 } r && (r.HotFiles.Count > 0 || r.HasError), "the report shows the trace with its source lines", 300).ConfigureAwait(true);
        var report = Report()!;
        Check(!report.HasError, "the trace was read: " + report.Error);
        // The hot function is the app's hottest: on a busy machine (a CI runner) the samples land on the runtime's safe
        // points, so Stopwatch, Sleep and wait frames of the other threads can rank above it overall.
        var hottest = report.Functions.FirstOrDefault(f => f.Name.StartsWith("CpuBound.", StringComparison.Ordinal));
        Check(hottest?.Name.Contains("Primes.IsPrime", StringComparison.Ordinal) == true, "the hot function is the app's hottest: " + string.Join(", ", report.Functions.Take(8).Select(f => f.Name)));
        Check(report.CounterSummaries.Count > 0, "the counters were recorded");
        Console.Out.WriteLine($"  {report.Summary}");

        // The flame graph: drawn in the window; zoom steps timed (the budget is 50 ms a frame).
        report.SelectedTab = 3;
        await RenderedAsync().ConfigureAwait(true);
        var graph = window.GetVisualDescendants().OfType<Controls.FlameGraph>().FirstOrDefault();
        Check(graph is { Frames.Count: > 0 }, "the flame graph has frames");
        var clock = Stopwatch.StartNew();
        for (var i = 0; i < 10; i++)
        {
            graph!.Zoom(1.15, 0.3);
            await RenderedAsync().ConfigureAwait(true);
        }

        Console.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  flame graph: {graph!.Frames!.Count} frames, {clock.Elapsed.TotalMilliseconds / 10:0.0} ms per zoom step (two rendered frames each)"));
        graph.ResetView();
        await RenderedAsync().ConfigureAwait(true);
        Screenshot("smoke-test-profiler.png");

        // Hot lines in the editor's gutter.
        var editor = await OpenAsync(Path.Combine(target, "CpuBound", "Program.cs")).ConfigureAwait(true);
        await WaitAsync(() => editor.HotLineMargin.Lines.Count > 0, "Program.cs shows its hot lines in the gutter", 60).ConfigureAwait(true);
    }

    /// <summary>The Diagnostics solution open (the profiler.cpu step opened it), with a startup project and its arguments.</summary>
    private async Task<string> DiagnosticsStartupAsync(string project, string arguments)
    {
        await WaitAsync(() => shell.Runs.Running.Count == 0 && shell.ActiveProfiling is null && !shell.Builds.IsBuilding, $"the previous run, collection and build have stopped (collecting: {shell.ActiveProfiling?.Target}, building: {shell.Builds.IsBuilding})", 120).ConfigureAwait(true);
        var target = Path.Combine(Work, "Diagnostics");
        if (!Directory.Exists(target))
        {
            Copy(Path.Combine(samples, "Diagnostics"), target);
        }

        // Still open from profiler.cpu (its path is the real one, which differs from target's under macOS's /private).
        if (!shell.Solutions.Projects.Values.Any(p => p.Name == "CpuBound"))
        {
            await shell.OpenSolutionAsync(Path.Combine(target, "Diagnostics.slnx")).ConfigureAwait(true);
        }

        await WaitAsync(() => shell.Solutions.Projects.Count == 6 && shell.Solutions.Progress is { Completed: true }, "the Diagnostics solution is open", 300).ConfigureAwait(true);
        Directory.CreateDirectory(Path.Combine(target, project, "Properties"));
        File.WriteAllText(Path.Combine(target, project, "Properties", "launchSettings.json"), $$"""{ "profiles": { "{{project}}": { "commandName": "Project", "commandLineArgs": "{{arguments}}" } } }""");
        var path = shell.Solutions.Projects.Values.First(p => p.Name == project).Path;
        shell.SetStartupProject(path);
        Check(shell.Runs.StartupProject is { } startup && Workbench.Projects.SolutionService.PathComparer.Equals(startup, path), $"{project} is the startup project: {shell.Runs.StartupProject}");
        return target;
    }

    private PerformanceProfilerViewModel ProfilerWith(params string[] tools)
    {
        var hub = shell.OpenProfiler();
        foreach (var tool in hub.Tools)
        {
            hub.Use(tool.Id, tools.Contains(tool.Id));
        }

        return hub;
    }

    private string LastNotifications() => string.Join(" | ", shell.Workbench.Notifications.Items.TakeLast(3).Select(n => n.Message));

    private ProfilingReportViewModel? LatestReport() =>
        shell.Layout.Documents.OfType<SolutionManagementDocument>().Select(d => d.ViewModel).OfType<ProfilingReportViewModel>().LastOrDefault();

    private async Task ProfilerToolsStepAsync()
    {
        await DiagnosticsStartupAsync("AsyncHeavy", "3").ConfigureAwait(true);
        var before = LatestReport();
        var hub = ProfilerWith(Workbench.Diagnostics.ProfilerTools.Cpu, Workbench.Diagnostics.ProfilerTools.Allocations, Workbench.Diagnostics.ProfilerTools.Async, Workbench.Diagnostics.ProfilerTools.Threads, Workbench.Diagnostics.ProfilerTools.Exceptions);
        var run = await hub.StartAsync().ConfigureAwait(true);
        Check(run is not null, "profiling starts: " + hub.Status + " " + LastNotifications());
        await WaitAsync(() => !hub.IsCollecting, "AsyncHeavy runs its 3 seconds and the collection stops", 300).ConfigureAwait(true);
        await WaitAsync(() => LatestReport() is { } r && r != before && (r.ToolsLoaded || r.HasError), "the report loads every tool", 300).ConfigureAwait(true);
        var report = LatestReport()!;
        Check(!report.HasError, "the trace was read: " + report.Error);
        Check(report.Tasks.Count > 0, ".NET Async lists the tasks");
        Check(report.ThreadRows.Count > 0 && report.ContentionTree.Count > 0, "Threads shows the threads and the lock contention");
        Check(report.ExceptionGroups.Count > 0 && report.ExceptionGroups[0].Type == "System.InvalidOperationException" && report.ExceptionGroups[0].ThrowSite.Contains("Worker.Validate", StringComparison.Ordinal), "Exceptions groups the fixture's exception at its throw site: " + report.ExceptionGroups.FirstOrDefault()?.AccessibleName);
        Check(report.AllocationTypes.Count > 0, "Allocations lists the allocated types");
        Check(report.EventCounts.Count > 0 && report.Events.Count > 0, "the events viewer lists the trace's events");
        Console.Out.WriteLine($"  {report.Tasks.Count} tasks, {report.ThreadRows.Count} threads, {report.ExceptionGroups.Count} exception groups, {report.AllocationTypes.Count} allocated types, {report.EventCounts.Count} kinds of events");
        // The flame graph again, once the solution has settled (profiler.cpu times it right after the solution opens).
        report.SelectedTab = 3;
        await RenderedAsync().ConfigureAwait(true);
        if (window.GetVisualDescendants().OfType<Controls.FlameGraph>().FirstOrDefault() is { Frames.Count: > 0 } graph)
        {
            var clock = Stopwatch.StartNew();
            for (var i = 0; i < 10; i++)
            {
                graph.Zoom(1.15, 0.3);
                await RenderedAsync().ConfigureAwait(true);
            }

            Console.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  flame graph: {graph.Frames!.Count} frames, {clock.Elapsed.TotalMilliseconds / 10:0.0} ms per zoom step"));
        }

        report.SelectedTab = ProfilingReportViewModel.ThreadsTab;
        await RenderedAsync().ConfigureAwait(true);
        Screenshot("smoke-test-profiler-threads.png");
    }

    private async Task ProfilerMemoryStepAsync()
    {
        await DiagnosticsStartupAsync("Leaky", "0").ConfigureAwait(true);
        var before = LatestReport();
        var hub = ProfilerWith(Workbench.Diagnostics.ProfilerTools.Memory);
        var run = await hub.StartAsync().ConfigureAwait(true);
        Check(run is not null, "profiling starts: " + hub.Status + " " + LastNotifications());
        try
        {
            await Task.Delay(1000).ConfigureAwait(true);
            var clock = Stopwatch.StartNew();
            Check(await hub.TakeSnapshotAsync().ConfigureAwait(true) is not null, "the first snapshot is taken: " + hub.Status);
            Console.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  heap snapshot in {clock.ElapsedMilliseconds} ms"));
            await Task.Delay(2000).ConfigureAwait(true);
            Check(await hub.TakeSnapshotAsync().ConfigureAwait(true) is not null, "the second snapshot is taken: " + hub.Status);
        }
        finally
        {
            await hub.StopAsync().ConfigureAwait(true);
        }

        await WaitAsync(() => !hub.IsCollecting, "the collection stops", 120).ConfigureAwait(true);
        await WaitAsync(() => LatestReport() is { } r && r != before && (r.HeapTypes.Count > 0 || r.HeapSummary?.Contains("could not", StringComparison.Ordinal) == true), "the report reads the snapshots", 300).ConfigureAwait(true);
        var report = LatestReport()!;
        var order = report.HeapTypes.FirstOrDefault(t => t.Name == "Order");
        Check(order is { Type.CountDelta: > 0 }, "the diff shows Order growing: " + report.HeapSummary);
        report.SelectedHeapType = order;
        await WaitAsync(() => report.Referrers.Count > 0, "the paths to roots of Order", 60).ConfigureAwait(true);
        Console.Out.WriteLine($"  {report.HeapSummary}; Order {order!.CountDiff}, held by {report.Referrers[0].Name}");
        await RenderedAsync().ConfigureAwait(true);
        Screenshot("smoke-test-profiler-memory.png");
    }

    private async Task BenchmarksStepAsync()
    {
        var target = await DiagnosticsStartupAsync("CpuBound", "1").ConfigureAwait(true);
        await WaitAsync(() => shell.Benchmarks.All.Count >= 4, "the solution's benchmarks are found", 60).ConfigureAwait(true);
        var file = Path.Combine(target, "Benchmarks", "StringBenchmarks.cs");
        var editor = await OpenAsync(file).ConfigureAwait(true);
        await WaitAsync(() => editor.BenchmarkMargin.Glyphs.Count > 0, "StringBenchmarks.cs shows benchmark glyphs", 30).ConfigureAwait(true);
        var concat = editor.BenchmarkMargin.Glyphs.FirstOrDefault(g => g.Method == "Concat");
        Check(concat is not null, "Concat has a glyph");
        shell.BenchmarksWindow.Job = "Dry";
        shell.BenchmarksWindow.InProcess = true;
        var clock = Stopwatch.StartNew();
        await shell.RunBenchmarkAsync(file, concat!).ConfigureAwait(true);
        await WaitAsync(() => !shell.Benchmarks.IsRunning, "BenchmarkDotNet finishes", 600).ConfigureAwait(true);
        var result = shell.BenchmarksWindow.Results.FirstOrDefault();
        Check(result is { Result.Mean: > 0 }, "the result is shown: " + shell.BenchmarksWindow.Status + "\n" + Tail(shell.BenchmarkLog.GetText(), 2000));
        Console.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {result!.Method}: {result.Mean}, allocated {result.Allocated}, in {clock.Elapsed.TotalSeconds:0.0} s"));
        shell.ShowBenchmarks();
        await RenderedAsync().ConfigureAwait(true);
        Screenshot("smoke-test-benchmarks.png");
    }

    private async Task DataExplorerStepAsync()
    {
        var database = Path.Combine(Work, "smoke-inventory.db");
        File.Copy(Path.Combine(samples, "Data", "Inventory", "inventory.db"), database, overwrite: true);
        shell.ShowDatabaseExplorer();
        var form = shell.OpenConnectionForm().ViewModel;
        form.EngineName = "SQLite";
        form.File = database;
        await form.TestAsync().ConfigureAwait(true);
        Check(form.Result?.StartsWith("Connected: SQLite", StringComparison.Ordinal) == true, "Test Connection reaches the database: " + form.Result + "\n" + Tail(shell.DataLog.GetText(), 1500));
        await form.SaveAsync().ConfigureAwait(true);
        var connection = shell.DbExplorer.Roots.FirstOrDefault(r => r.Label == "smoke-inventory.db");
        Check(connection is not null, "the connection is in the Database Explorer");

        async Task<DbNodeViewModel> Child(DbNodeViewModel node, string label)
        {
            node.IsExpanded = true;
            await WaitAsync(() => node.Children.Any(c => c.Label == label), $"{node.Label} shows {label}", 60).ConfigureAwait(true);
            return node.Children.First(c => c.Label == label);
        }

        var products = await Child(await Child(connection!, "Tables").ConfigureAwait(true), "Products").ConfigureAwait(true);
        shell.DbExplorer.Selected = products;
        var document = await shell.ViewTableDataAsync(products).ConfigureAwait(true);
        Check(document is not null, "View Data opens the table");
        var table = document!.ViewModel;
        await WaitAsync(() => table.Grid is { Rows.Count: 5 } && !table.IsBusy, "the table's rows are shown", 60).ConfigureAwait(true);
        var name = Array.FindIndex(table.Grid!.Columns, c => c.Name == "Name");
        table.Grid.Rows[1].Set(name, "Chang (smoke)");
        await RenderedAsync().ConfigureAwait(true);
        Screenshot("smoke-test-data-table.png");
        Check(await table.CommitAsync().ConfigureAwait(true), "the edit commits: " + table.Message);
        Check(table.Grid!.Rows[1].Values[name] == "Chang (smoke)", "the committed value is read back");

        var editor = await shell.OpenQueryAsync(products).ConfigureAwait(true);
        Check(editor is not null, "New Query opens an editor");
        await WaitAsync(() => editor!.Controller is not null, "the query editor appears").ConfigureAwait(true);
        editor!.Model.Text.Text = "SELECT p.Name, c.Name AS Category FROM Products p JOIN Categories c ON c.CategoryId = p.CategoryId ORDER BY p.ProductId;";
        var client = await shell.Languages.ClientAsync(ShellHost.SqlLanguageId).ConfigureAwait(true);
        Check(client is not null, "the SQL language service starts");
        var found = false;
        for (var attempt = 0; attempt < 120 && !found; attempt++)
        {
            var completion = await client!.CompletionAsync(editor.Model.Uri, new LanguageServices.Protocol.Position(0, "SELECT p.".Length), null, CancellationToken.None).ConfigureAwait(true);
            found = completion.Items.Any(i => i.Label == "Sku");
            if (!found)
            {
                await Task.Delay(500).ConfigureAwait(true);
            }
        }

        Check(found, "SQL completion offers the alias's columns from the schema");
        var result = await shell.ExecuteQueryAsync().ConfigureAwait(true);
        Check(result?.ResultSets is [{ Rows.Length: 5 }], "the query returns the 5 products: " + shell.QueryResults.Messages);
        await RenderedAsync().ConfigureAwait(true);
        Screenshot("smoke-test-data-query.png");
        await shell.ExecuteQueryAsync(plan: Data.Protocol.PlanKinds.Estimated).ConfigureAwait(true);
        Check(shell.QueryResults.HasPlan && shell.QueryResults.Plan.Count > 0, "the estimated plan is shown: " + shell.QueryResults.Messages);
        await RenderedAsync().ConfigureAwait(true);
        Screenshot("smoke-test-data-plan.png");
        shell.Layout.CloseDocument(document);
        await shell.Data.DisconnectAsync(connection!.ConnectionId!).ConfigureAwait(true);
    }

    /// <summary>
    /// HTML and CSS in Razor (M16d, ADR 0027): at a markup position of Counter.razor (samples/Blazor, opened by
    /// blazor.razor) the HTML server completes elements, through the file's HTML.
    /// </summary>
    private async Task RazorHtmlStepAsync()
    {
        var counter = Path.Combine(Work, "Blazor", "BlazorApp", "Components", "Pages", "Counter.razor");
        var editor = await OpenAsync(counter).ConfigureAwait(true);
        var heading = editor.Model.Text.Text.IndexOf("<h1>Counter</h1>", StringComparison.Ordinal) + "<h1>Counter</h1>".Length;
        editor.Model.Text.Insert(heading, "\n<blockq");
        var offset = heading + "\n<blockq".Length;
        var embedded = await ((Editor.IEditorServices)shell).EmbeddedAsync(editor.Model, offset).ConfigureAwait(true);
        Check(embedded is not null, "the HTML server serves the Razor file's markup");
        var completion = await embedded!.Client.CompletionAsync(embedded.Uri, editor.Model.PositionAt(offset),
            new LanguageServices.Protocol.CompletionContext(LanguageServices.Protocol.CompletionTriggerKind.Invoked, null), CancellationToken.None).ConfigureAwait(true);
        Check(completion?.Items.Any(i => i.Label == "blockquote") == true, "HTML completion in Razor offers blockquote");
        editor.Model.Text.Remove(heading, "\n<blockq".Length);
    }

    /// <summary>
    /// Build depth (M26b, ADR 0034 and 0035) on BuildAndRun: Build Solution again is skipped by the fast up-to-date check;
    /// Build with Binary Log opens the log in the viewer and its search finds CoreCompile; Build Timings shows the
    /// projects; Batch Build builds Hello's Release configuration; the ResX and T4 custom tools (the shipped T4 host)
    /// generate code in Greetings, which then builds. The project file and the new files are put back afterwards.
    /// </summary>
    private async Task BuildDepthStepAsync()
    {
        await WaitAsync(() => !shell.Builds.IsBuilding && shell.Runs.Running.Count == 0, "the previous build and run have finished", 120).ConfigureAwait(true);
        var before = shell.Builds.LastBuild;
        shell.Run("build.buildSolution");
        await WaitAsync(() => BuildDone(before), "the build finishes", 300).ConfigureAwait(true);
        Check(shell.Builds.LastBuild!.Result is { Succeeded: true, UpToDate: true }, "Build Solution right after a build is up to date:\n" + Tail(shell.Builds.Channel.GetText(), 1500));

        // A binary log, read by the build host and browsed in the viewer.
        shell.Run("build.buildWithBinaryLog");
        BinaryLogViewModel? Log() => shell.Layout.Documents.OfType<PageDocument>().Select(d => d.ViewModel).OfType<BinaryLogViewModel>().FirstOrDefault();
        await WaitAsync(() => Log() is { Roots.Count: 1 } log && log.Roots[0].Children.Count > 1, "Build with Binary Log opens the log in the viewer", 300).ConfigureAwait(true);
        var binlog = Log()!;
        Check(binlog.Summary?.Contains("Build succeeded", StringComparison.Ordinal) == true, $"the viewer summarizes the build ({binlog.Summary}{binlog.Error})");
        binlog.Query = "CoreCompile";
        await binlog.SearchAsync().ConfigureAwait(true);
        Check(binlog.Results.Any(r => r.Kind == "Target"), "searching the log finds the CoreCompile target");
        await binlog.RevealAsync(binlog.Results.First(r => r.Kind == "Target").Result).ConfigureAwait(true);
        await RenderedAsync().ConfigureAwait(true);
        Screenshot("smoke-test-binary-log.png");

        var timings = shell.OpenBuildTimings();
        await WaitAsync(() => timings.Projects.Count > 0, "Build Timings shows the last build's projects", 60).ConfigureAwait(true);
        Check(timings.CriticalPath.Any(p => p.StartsWith("Hello", StringComparison.Ordinal)), "the critical path ends with Hello: " + timings.CriticalPathText);
        await RenderedAsync().ConfigureAwait(true);
        Screenshot("smoke-test-build-timings.png");

        // Batch Build: Hello's Release configuration.
        var batch = shell.OpenBatchBuild();
        await WaitAsync(() => batch.Rows.Any(r => r is { Name: "Hello", Configuration: "Release" }), "Batch Build lists the project configurations", 60).ConfigureAwait(true);
        foreach (var row in batch.Rows)
        {
            row.IsChecked = row is { Name: "Hello", Configuration: "Release" };
        }

        await batch.RunAsync(Projects.Protocol.BuildTargets.Build).ConfigureAwait(true);
        var release = batch.Rows.First(r => r is { Name: "Hello", Configuration: "Release" });
        Check(release.Status?.StartsWith("succeeded", StringComparison.Ordinal) == true, $"Batch Build built Hello|Release ({release.Status}):\n" + Tail(shell.Builds.Channel.GetText(), 1500));
        await RenderedAsync().ConfigureAwait(true);
        Screenshot("smoke-test-batch-build.png");

        // Custom tools: a .resx and a text template in Greetings (the T4 host shipped next to the app).
        var greetings = Path.Combine(_folder, "src", "Greetings", "Greetings.csproj");
        var greetingsDir = Path.GetDirectoryName(greetings)!;
        var original = await File.ReadAllTextAsync(greetings).ConfigureAwait(true);
        var resx = Path.Combine(greetingsDir, "SmokeStrings.resx");
        var template = Path.Combine(greetingsDir, "SmokeNumbers.tt");
        string[] created = [resx, template, Path.Combine(greetingsDir, "SmokeStrings.Designer.cs"), Path.Combine(greetingsDir, "SmokeNumbers.cs")];
        try
        {
            await File.WriteAllTextAsync(resx, "<root>\n  <data name=\"SmokeGreeting\" xml:space=\"preserve\">\n    <value>Hello from a resource</value>\n  </data>\n</root>\n").ConfigureAwait(true);
            await File.WriteAllTextAsync(template, "<#@ template language=\"C#\" #>\n<#@ output extension=\".cs\" #>\nnamespace Greetings;\n\npublic static class SmokeNumbers\n{\n<# for (var i = 1; i <= 3; i++) { #>\n    public const int N<#= i #> = <#= i * i #>;\n<# } #>\n}\n").ConfigureAwait(true);
            await WaitAsync(() => shell.Solutions.Projects.TryGetValue(greetings, out var p) && p.Items.Any(i => i.Path == resx) && p.Items.Any(i => i.Path == template), "the new files become items", 60).ConfigureAwait(true);
            await shell.ProjectItems.SetAsync(greetings, resx, null, new Dictionary<string, string?> { [Projects.Protocol.ItemMetadataNames.Generator] = "ResXFileCodeGenerator" }, CancellationToken.None).ConfigureAwait(true);
            await shell.ProjectItems.SetAsync(greetings, template, null, new Dictionary<string, string?> { [Projects.Protocol.ItemMetadataNames.Generator] = "TextTemplatingFileGenerator" }, CancellationToken.None).ConfigureAwait(true);
            var strings = await shell.RunCustomToolAsync(resx).ConfigureAwait(true);
            Check(strings?.Succeeded == true && ReadText(created[2]).Contains("internal static string SmokeGreeting", StringComparison.Ordinal), "the ResX generator writes SmokeStrings.Designer.cs");
            var resources = shell.OpenResx(resx);
            await resources.LoadAsync().ConfigureAwait(true);
            Check(resources.Rows.Any(r => r is { Name: "SmokeGreeting", Value: "Hello from a resource" }), "the resource editor lists SmokeStrings.resx's strings");
            shell.Layout.CloseDocument(resources);
            var numbers = await shell.RunCustomToolAsync(template).ConfigureAwait(true);
            Check(numbers?.Succeeded == true && ReadText(created[3]).Contains("public const int N3 = 9;", StringComparison.Ordinal),
                "the T4 host transforms SmokeNumbers.tt: " + string.Join("; ", numbers?.Diagnostics.Select(d => d.Message) ?? ["no result"]));
            Check(ReadText(greetings).Contains("<DependentUpon>SmokeNumbers.tt</DependentUpon>", StringComparison.Ordinal), "the generated file is nested under its template");
            Check(await shell.BuildAsync(Projects.Protocol.BuildTargets.Build, greetings).ConfigureAwait(true), "Greetings builds with the generated code:\n" + Tail(shell.Builds.Channel.GetText(), 1500));
        }
        finally
        {
            foreach (var file in created.Where(File.Exists))
            {
                File.Delete(file);
            }

            // The deletions make the build host evaluate Greetings again, and on Windows MSBuild holds the project file
            // open while it reads it: a writer gets a sharing violation (#108's run). Left changed, the file would fail
            // git.changes, which needs a clean tree.
            await WriteBackAsync(greetings, original).ConfigureAwait(true);
        }
    }

    /// <summary>Writes a file back that the build host may be reading (MSBuild shares a project file only for reading): tried again for 10 seconds.</summary>
    private static async Task WriteBackAsync(string path, string text)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await File.WriteAllTextAsync(path, text).ConfigureAwait(true);
                return;
            }
            catch (IOException) when (attempt < 40)
            {
                await Task.Delay(250).ConfigureAwait(true);
            }
        }
    }

    /// <summary>
    /// npm (M16d, ADR 0027): samples/WebScripts' package restores (a local dependency), a script runs in a terminal, and
    /// Build runs the script bound to Before Build.
    /// </summary>
    private async Task NpmStepAsync()
    {
        await WaitAsync(() => shell.Runs.Running.Count == 0 && !shell.Debugger.IsActive, "the previous run has stopped", 60).ConfigureAwait(true);
        Check(File.Exists(Path.Combine(samples, "WebScripts", "WebScripts.slnx")), $"the samples folder {samples} has WebScripts");
        var target = Path.Combine(Work, "WebScripts");
        Copy(Path.Combine(samples, "WebScripts"), target);
        await shell.OpenSolutionAsync(Path.Combine(target, "WebScripts.slnx")).ConfigureAwait(true);
        await WaitAsync(() => shell.Solutions.Projects.Count == 1 && shell.Solutions.Progress is { Completed: true }, "the WebScripts solution is open", 300).ConfigureAwait(true);
        var project = Path.Combine(target, "WebScripts");
        shell.Run("npm.taskRunner");
        await WaitAsync(() => shell.TaskRunner.Rows.Any(r => r.Label == "greet"), "the Task Runner Explorer lists the scripts", 60).ConfigureAwait(true);
        Check(await shell.RestoreNpmAsync(Path.Combine(project, "package.json")).ConfigureAwait(true), "npm install succeeded: " + Tail(shell.NpmLog.GetText(), 1500));
        Check(Directory.Exists(Path.Combine(project, "node_modules", "local-greeting")), "the local dependency is installed");

        await shell.RunNpmScriptAsync(Path.Combine(project, "package.json"), "greet").ConfigureAwait(true);
        var session = shell.Terminals.Sessions.Last(s => s.Name == "npm: greet");
        string Text() => string.Join("\n", Enumerable.Range(0, session.Terminal.TotalRows).Select(i => session.Terminal.GetRow(i).GetText()));
        await WaitAsync(() => Text().Contains("Hello, npm!", StringComparison.Ordinal), "the script prints its greeting in the terminal", 120).ConfigureAwait(true);

        var before = shell.Builds.LastBuild;
        shell.Run("build.buildSolution");
        await WaitAsync(() => BuildDone(before), "the build finishes", 300).ConfigureAwait(true);
        Check(File.Exists(Path.Combine(project, "stamp.txt")), "the Before Build script ran: " + Tail(shell.NpmLog.GetText(), 1500));
    }

    /// <summary>
    /// ESLint and Prettier (M16d, ADR 0027): samples/WebLint gets its tools (npm ci), then ESLint reports src/app.js's
    /// problems next to TypeScript, and Format Document runs Prettier.
    /// </summary>
    private async Task EslintStepAsync()
    {
        Check(File.Exists(Path.Combine(samples, "WebLint", "package-lock.json")), $"the samples folder {samples} has WebLint");
        var target = Path.Combine(Work, "WebLint");
        Copy(Path.Combine(samples, "WebLint"), target);
        var npm = Workbench.Node.Npm.UserNpm() ?? Workbench.Node.Npm.BuiltIn(shell.Node);
        var log = new System.Text.StringBuilder();
        var code = await Workbench.Node.Npm.RunAsync(npm, ["ci", "--no-audit", "--no-fund"], target, line => log.AppendLine(line), CancellationToken.None).ConfigureAwait(true);
        Check(code == 0, "npm ci installed ESLint and Prettier:\n" + Tail(log.ToString(), 1500));

        var app = Path.Combine(target, "src", "app.js");
        var editor = await OpenAsync(app).ConfigureAwait(true);
        await WaitAsync(() => shell.Languages.Diagnostics.For(app) is var d && d.Any(x => x.Code == "no-unused-vars") && d.Any(x => x.Code == "eqeqeq"),
            "ESLint reports no-unused-vars and eqeqeq: " + Tail(shell.LanguageLog.GetText(), 1500), 180).ConfigureAwait(true);
        shell.Run("editor.action.formatDocument");
        await WaitAsync(() => editor.Model.Text.Text.Contains("let sum = 0;", StringComparison.Ordinal), "Prettier formats the file: " + Tail(shell.LanguageLog.GetText(), 1500), 60).ConfigureAwait(true);
    }

    /// <summary>
    /// Compound debugging (M16d, ADR 0027): F5 on samples/BlazorHosted stops in Counter.razor on the server (netcoredbg,
    /// prerendering), then in the browser (js-debug and the WebAssembly debugging proxy). Stopping ends both.
    /// </summary>
    private async Task CompoundDebugStepAsync()
    {
        await WaitAsync(() => shell.Runs.Running.Count == 0 && !shell.Debugger.IsActive, "the previous session has stopped", 60).ConfigureAwait(true);
        Check(File.Exists(Path.Combine(samples, "BlazorHosted", "BlazorHosted.slnx")), $"the samples folder {samples} has BlazorHosted");
        var target = Path.Combine(Work, "BlazorHosted");
        Copy(Path.Combine(samples, "BlazorHosted"), target);
        using (var socket = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0))
        {
            socket.Start();
            var port = ((System.Net.IPEndPoint)socket.LocalEndpoint).Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
            socket.Stop();
            var settings = Path.Combine(target, "BlazorHosted", "Properties", "launchSettings.json");
            File.WriteAllText(settings, File.ReadAllText(settings).Replace("5274", port, StringComparison.Ordinal));
        }

        await shell.OpenSolutionAsync(Path.Combine(target, "BlazorHosted.slnx")).ConfigureAwait(true);
        await WaitAsync(() => shell.Solutions.Projects.Count == 2 && shell.Solutions.Progress is { Completed: true }, "the Blazor Web App is open", 300).ConfigureAwait(true);
        shell.Runs.SetStartupProject(Path.Combine(target, "BlazorHosted", "BlazorHosted.csproj"));
        var counter = Path.Combine(target, "BlazorHosted.Client", "Pages", "Counter.razor");
        var line = Array.FindIndex(File.ReadAllLines(counter), l => l.Contains("// [initialized]", StringComparison.Ordinal)) + 1;
        shell.Debugger.Breakpoints.Toggle(counter, line);
        // The page's ticks: WebAssembly breakpoints can bind after the page's OnInitialized has run.
        shell.Debugger.Breakpoints.Toggle(counter, Array.FindIndex(File.ReadAllLines(counter), l => l.Contains("// [tick]", StringComparison.Ordinal)) + 1);
        string Thread() => shell.Debugger.Threads.FirstOrDefault(t => t.Id == shell.Debugger.CurrentStop?.ThreadId)?.Name ?? string.Empty;
        shell.Run("debug.start");
        try
        {
            await WaitAsync(() => shell.Debugger.State == DebugState.Stopped && shell.Debugger.Frames.Count > 0, () => "the server stops at the breakpoint (Debug Console:\n" + Tail(shell.DebugLog.GetText(), 2000) + ")", 600).ConfigureAwait(true);
            Check(shell.Debugger.IsCompound && !Thread().StartsWith("Browser:", StringComparison.Ordinal), "the first stop is the server's: " + Thread());
            var first = shell.Debugger.CurrentStop;
            await shell.Debugger.ContinueAsync().ConfigureAwait(true);
            await WaitAsync(() => shell.Debugger.State == DebugState.Stopped && shell.Debugger.CurrentStop != first && shell.Debugger.Frames.Count > 0,
                () => "the page stops at the breakpoint in WebAssembly (Debug Console:\n" + Tail(shell.DebugLog.GetText(), 2000) + ")", 300).ConfigureAwait(true);
            Check(Thread().StartsWith("Browser:", StringComparison.Ordinal), "the second stop is the browser's: " + Thread());
            Screenshot("smoke-test-blazor-compound-debug.png");
        }
        finally
        {
            shell.Debugger.Breakpoints.Clear();
            await shell.Debugger.StopAsync().ConfigureAwait(true);
        }

        await WaitAsync(() => !shell.Debugger.IsActive, "the session ends", 60).ConfigureAwait(true);
    }

    /// <summary>
    /// Browser debugging (M16c, ADR 0026): F5 on samples/BlazorWasm starts the dev server and a Chromium browser through
    /// vscode-js-debug, and a breakpoint in Counter.razor's C# stops. Stopping the session stops the dev server.
    /// </summary>
    private async Task WasmDebugStepAsync()
    {
        await WaitAsync(() => shell.Runs.Running.Count == 0 && !shell.Debugger.IsActive, "the previous run has stopped", 60).ConfigureAwait(true);
        Check(File.Exists(Path.Combine(samples, "BlazorWasm", "BlazorWasm.slnx")), $"the samples folder {samples} has BlazorWasm");
        var target = Path.Combine(Work, "BlazorWasm");
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
        // The page's ticks: WebAssembly breakpoints can bind after the page's OnInitialized has run.
        shell.Debugger.Breakpoints.Toggle(counter, Array.FindIndex(File.ReadAllLines(counter), l => l.Contains("// [tick]", StringComparison.Ordinal)) + 1);
        var buildBefore = shell.Builds.LastBuild;
        bool BuildFailed() => shell.Builds.LastBuild is { Result.Succeeded: false } build && build != buildBefore;
        shell.Run("debug.start");
        try
        {
            // The first session downloads vscode-js-debug (and Node.js, if M16b's step didn't). A failed build ends the wait.
            // The dev server's debugging proxy can stall on a loaded runner before the page's .NET runtime is reported
            // (no Mono.runtimeReady, none of the app's sources: the page runs, no breakpoint binds; seen once on the
            // linux-arm64 installed app). A user starts debugging again; so does this step, once, and that session must stop.
            try
            {
                await WaitAsync(() => shell.Debugger.State == DebugState.Stopped || BuildFailed(), "the browser session stops at the breakpoint", 300).ConfigureAwait(true);
            }
            catch (SmokeFailure) when (!BuildFailed())
            {
                shell.Workbench.Log.AppendLine("[smoke] blazor.wasmDebug: the page didn't stop in 300 s; debugging starts again. Debug Console:\n" + Tail(shell.DebugLog.GetText(), 2000));
                await shell.Debugger.StopAsync().ConfigureAwait(true);
                await WaitAsync(() => shell.Runs.Running.Count == 0 && shell.Debugger.State == DebugState.Idle, "the first session and its dev server stop", 60).ConfigureAwait(true);
                shell.Run("debug.start");
                await WaitAsync(() => shell.Debugger.State == DebugState.Stopped || BuildFailed(), () => "the browser session stops at the breakpoint, on the second start (Debug Console:\n" + Tail(shell.DebugLog.GetText(), 2000) + ")", 300).ConfigureAwait(true);
            }

            Check(!BuildFailed(), "the build before debugging succeeded:\n" + Tail(shell.Builds.Channel.GetText(), 3000));
            await WaitAsync(() => shell.CallStack.Frames.Count > 0, "the call stack", 60).ConfigureAwait(true);
            Check(shell.CallStack.Frames[0].Name is var top && (top.Contains("Counter.OnInitialized", StringComparison.Ordinal) || top.Contains("Counter.Tick", StringComparison.Ordinal)),
                "the top frame is Counter.OnInitialized or Counter.Tick, in the browser: " + shell.CallStack.Frames[0].Name);
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
        var folder = Path.Combine(Work, "site");
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
        var target = Path.Combine(Work, "Blazor");
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
        var target = Path.Combine(Work, "Web");
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

    /// <summary>
    /// M13b against <see cref="Tests.MockGitHub"/> (nothing reaches GitHub): the fixture's origin is on the mock (fetched
    /// over git's dumb HTTP protocol from a bare repository, pushed to it directly); a device-code sign-in whose token goes
    /// to the OS credential store under this profile's own service; the pull request checked out with its review comment
    /// in the editor and its checks; <c>#</c> completion; askpass for an HTTPS remote; a worktree; then sign-out.
    /// </summary>
    /// <summary>
    /// AI assistance (M28a, ADR 0062) against <see cref="Tests.MockModelServers"/>: the OpenAI-compatible plugin stays
    /// unloaded while AI is off, then a question about a selection streams into AI Chat and its code is applied.
    /// </summary>
    private async Task AiChatStepAsync()
    {
        const string Reply = "This adds two numbers. A shorter version:\n\n```csharp\nstatic int Add(int a, int b) => a + b;\n```\n\nThe expression body returns the same value.";
        const string AgentQuestion = "Does Greeter handle an empty name?";
        const string AgentReply = "### Empty names\n\n`Greeter.Greet` formats whatever it gets, so an empty name gives `Hello, !`. Check it first:\n\n```csharp\npublic static string Greet(string name) =>\n    string.IsNullOrWhiteSpace(name) ? \"Hello!\" : $\"Hello, {name}!\";\n```\n\n- The callers in `Program.cs` keep working.\n- Add a test for the empty name.";

        // M34h: a second question in agent mode reads a file, so AI Chat shows a step (and the gallery photographs it).
        await using var server = Tests.MockModelServers.Script(request =>
        {
            using var body = JsonDocument.Parse(request.Body);
            var last = body.RootElement.GetProperty("messages").EnumerateArray().Last();
            return last.GetProperty("role").GetString() == "tool" ? new Tests.MockTurn(AgentReply)
                : last.GetProperty("content").GetString()?.EndsWith(AgentQuestion, StringComparison.Ordinal) == true ? new Tests.MockTurn("Reading Greeter first.", "read_file", """{"path":"src/Greetings/Greeter.cs"}""")
                : new Tests.MockTurn(Reply);
        });
        var settings = shell.Workbench.Settings;
        Check(shell.Ai.State == Workbench.Ai.AiState.Off, "AI assistance is off by default");
        Check(shell.Plugins.Plugins.Any(p => p.Id == "codalus.openai" && p.State == PluginState.Ready), "the OpenAI-compatible plugin is installed and not loaded");
        try
        {
            await settings.SetAsync("ai.openai.endpoint", server.BaseUrl + "/v1", SettingScope.User, CancellationToken.None).ConfigureAwait(true);
            await settings.SetAsync(Workbench.Ai.AiService.EnabledSetting, true, SettingScope.User, CancellationToken.None).ConfigureAwait(true);
            await settings.SetAsync(Workbench.Ai.AiService.ProviderSetting, "openai", SettingScope.User, CancellationToken.None).ConfigureAwait(true);
            await settings.SetAsync(Workbench.Ai.AiService.ModelSetting, "gpt-test", SettingScope.User, CancellationToken.None).ConfigureAwait(true);
            await shell.Secrets.StoreAsync("codalus.openai", "apiKey:openai", Tests.MockModelServers.Key, CancellationToken.None).ConfigureAwait(true);

            var path = Path.Combine(Work, "Calculator.cs");
            await File.WriteAllTextAsync(path, "namespace Samples;\n\npublic static class Calculator\n{\n    static int Add(int a, int b)\n    {\n        return a + b;\n    }\n}\n").ConfigureAwait(true);
            var editor = await OpenAsync(path).ConfigureAwait(true);
            var method = "    static int Add(int a, int b)\n    {\n        return a + b;\n    }\n";
            editor.Editor.Select(editor.Model.Text.Text.IndexOf(method, StringComparison.Ordinal), method.Length);

            shell.ShowAiChat();
            await WaitAsync(() => window.GetVisualDescendants().OfType<AiChatView>().Any(), "AI Chat opens").ConfigureAwait(true);
            shell.AiChat.Input = "What does this do? Can it be shorter?";
            await shell.AiChat.SendAsync().ConfigureAwait(true);
            var turns = shell.AiChat.Chat.Turns;
            var turn = turns.Count > 0 ? turns[^1] : throw new SmokeFailure("The question wasn't asked.");
            await WaitAsync(() => !turn.IsStreaming, "the answer streams in", 60).ConfigureAwait(true);
            Check(turn.Error is null && turn.Answer == Reply, $"the answer arrived ({turn.Error ?? turn.Answer})");
            Check(AiChatViewModel.ContextSummary(turn).Contains("Calculator.cs", StringComparison.Ordinal), "the selection went with the question");
            await WaitAsync(() => window.GetVisualDescendants().OfType<Button>().Any(b => Avalonia.Automation.AutomationProperties.GetName(b) == "Apply the code after a preview"), "the code block offers Apply").ConfigureAwait(true);
            Check(shell.StatusBar.Right.Any(i => i.Item.Id == "codalus.ai"), "the status bar shows AI is on");
            await RenderedAsync().ConfigureAwait(true);
            Screenshot("ai-chat.png");

            var applying = shell.AiChat.ApplyAsync(turn, Workbench.Ai.AiCodeBlocks.Parse(turn.Answer)[0]);
            await WaitAsync(() => shell.Dialogs.IsOpen && shell.Dialogs.HasDetails, "the preview shows the change").ConfigureAwait(true);
            shell.Dialogs.Accept();
            await applying.ConfigureAwait(true);
            Check(editor.Model.Text.Text.Contains("    static int Add(int a, int b) => a + b;\n}", StringComparison.Ordinal), "Apply replaced the selection");
            Check(!shell.Workbench.Output.GetOrCreate(Workbench.Ai.AiService.OutputChannelId).GetText().Contains("return a + b", StringComparison.Ordinal), "the request log has no content");

            // Agent mode in the trusted solution (projects.open): a step that read a file, as the step template shows it.
            if (shell.Solutions.Solution is { Trusted: true } && File.Exists(Greeter))
            {
                shell.AiChat.IsAgent = true;
                shell.AiChat.Input = AgentQuestion;
                try
                {
                    await shell.AiChat.SendAsync().ConfigureAwait(true);
                }
                finally
                {
                    shell.AiChat.IsAgent = false;
                }

                var agentTurn = shell.AiChat.Chat.Turns[^1];
                await WaitAsync(() => !agentTurn.IsStreaming, "the agent's answer streams in", 60).ConfigureAwait(true);
                Check(agentTurn.IsAgent && agentTurn.Error is null && agentTurn.Steps is [{ Tool: "read_file", Outcome: not null, IsError: false }], $"the agent read Greeter.cs ({agentTurn.Error ?? string.Join(", ", agentTurn.Steps)})");
                await WaitAsync(() => window.GetVisualDescendants().OfType<AiStepView>().Any(s => s.DataContext is AiStepItem { Title: "read_file", Status: AiStepStatus.Completed }), "AI Chat shows the agent's step").ConfigureAwait(true);
                await RenderedAsync().ConfigureAwait(true);
                Screenshot("ai-chat-agent.png");
            }
        }
        finally
        {
            await settings.SetAsync(Workbench.Ai.AiService.EnabledSetting, (JsonElement?)null, SettingScope.User, CancellationToken.None).ConfigureAwait(true);
            await settings.SetAsync(Workbench.Ai.AiService.ProviderSetting, (JsonElement?)null, SettingScope.User, CancellationToken.None).ConfigureAwait(true);

            // The key goes too, like the hosting step's token: on macOS a key left in the Keychain makes the next run
            // (another build, so another code signature) ask the user for access, which an unattended run can't answer.
            await shell.Secrets.DeleteAsync("codalus.openai", "apiKey:openai", CancellationToken.None).ConfigureAwait(true);
        }
    }

    private async Task HostingStepAsync()
    {
        await using var github = new Tests.MockGitHub("codalus-samples", "BuildAndRun");
        var bare = Path.Combine(Work, "BuildAndRun.git");
        Directory.CreateDirectory(bare);
        GitIn(bare, "init", "-q", "--bare", "-b", Branch);
        Git("remote", "add", "origin", github.RemoteUrl);
        Git("config", "remote.origin.pushurl", bare);
        Git("config", "credential.helper", string.Empty);
        Git("push", "-q", "origin", "HEAD:refs/heads/main");
        Git("switch", "-q", "-c", "smoke/pull-request");
        await File.AppendAllTextAsync(Path.Combine(_folder, "README.md"), "\nChanged by the smoke test's pull request.\n").ConfigureAwait(true);
        Git("commit", "-q", "-am", "Smoke pull request");
        Git("push", "-q", "origin", "smoke/pull-request");
        Git("switch", "-q", "smoke/branch");
        Git("branch", "-q", "-D", "smoke/pull-request");
        Git("update-ref", "-d", "refs/remotes/origin/smoke/pull-request");
        GitIn(bare, "update-server-info");
        github.GitDirectory = bare;
        github.PullRequests.Add(new Tests.MockPullRequest(1, "Smoke pull request", "bo", "smoke/pull-request", "main") { Body = "Changes the **README**.", Reviewers = ["ada"] });
        github.Threads[1] = [new Tests.MockReviewThread("src/Greetings/Greeter.cs", 3, [("bo", "Should this be configurable?"), ("ada", "Later.")])];
        github.CheckRuns.Add(new Tests.MockCheckRun("build", "completed", "success"));
        github.Issues.Add(new Tests.MockIssue(7, "Greet in French", "ada"));
        github.PendingPolls = int.MaxValue;
        await shell.Workbench.Settings.SetAsync("github.serverUrl", github.ServerUrl, SettingScope.User, CancellationToken.None).ConfigureAwait(true);
        await shell.Workbench.Settings.SetAsync("github.oauthClientId", Tests.MockGitHub.ClientId, SettingScope.User, CancellationToken.None).ConfigureAwait(true);
        await shell.Git.RefreshAsync().ConfigureAwait(true);
        Check(shell.Plugins.Plugins.Any(p => p.Id == "codalus.github" && p.State == PluginState.Ready), "the GitHub plugin is installed and not loaded before sign-in");

        // Sign in with a device code: the code shows until the host approves it.
        shell.ShowAccounts("github");
        var row = shell.Accounts.Row("github") ?? throw new SmokeFailure("The Accounts page has no GitHub row.");
        await WaitAsync(() => row.HasCode, "the device code shows", 60).ConfigureAwait(true);
        await RenderedAsync().ConfigureAwait(true);
        Screenshot("smoke-test-hosting-signin.png");
        github.PendingPolls = 0;
        await WaitAsync(() => row.IsSignedIn && shell.Match is not null, "the sign-in completes and the repository's host is found", 60).ConfigureAwait(true);
        Console.Out.WriteLine($"  (the token is in: {shell.Secrets.StoreName}, service '{shell.Secrets.ServiceName}')");
        Check(shell.Secrets.IsPersistent || !(OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()), $"the token is in the OS credential store ({shell.Secrets.StoreName})");

        // The pull request: listed with its review thread, checked out over HTTP from the mock.
        shell.Layout.ShowToolWindow(PullRequestsViewModel.ToolWindowId);
        await WaitAsync(() => shell.PullRequests.Rows.Count == 1, "the Pull Requests window lists the pull request", 60).ConfigureAwait(true);
        shell.PullRequests.Selected = shell.PullRequests.Rows[0];
        await WaitAsync(() => shell.PullRequests.Threads.Count == 1, "the pull request's review thread shows", 30).ConfigureAwait(true);
        await shell.CheckOutAsync(shell.PullRequests.Rows[0].PullRequest).ConfigureAwait(true);
        await WaitAsync(() => shell.Git.Status is { Branch: "smoke/pull-request", Upstream: "origin/smoke/pull-request" }, "the pull request is checked out", 60).ConfigureAwait(true);
        await WaitAsync(() => shell.ReviewThreads.Count == 1 && shell.GitChanges.CiState == "success", "the branch's review comment and checks", 60).ConfigureAwait(true);
        var greeter = await OpenAsync(Greeter).ConfigureAwait(true);
        await WaitAsync(() => greeter.Insights.ReviewLines.Any(r => r.Line == 3), "the editor shows the review comment at line 3").ConfigureAwait(true);
        Check(shell.StatusBar.Right.Any(i => i.Item.Id == "codalus.hosting.ci" && i.Text.Contains("Checks passed", StringComparison.Ordinal)), "the status bar shows the checks");
        var before = shell.Theme.Setting;
        foreach (var theme in s_chromeThemes)
        {
            await shell.Workbench.Settings.SetAsync(ThemeService.SettingKey, theme, SettingScope.User, CancellationToken.None).ConfigureAwait(true);
            await WaitAsync(() => shell.Theme.ActiveTheme == theme, $"the {theme} theme applies").ConfigureAwait(true);
            await RenderedAsync().ConfigureAwait(true);
            Screenshot($"smoke-test-hosting-{theme}.png");
        }

        await shell.Workbench.Settings.SetAsync(ThemeService.SettingKey, before, SettingScope.User, CancellationToken.None).ConfigureAwait(true);

        // # in the commit message completes the issue.
        var completions = await shell.GitChanges.CompleteAsync("Fixes #7", 8, CancellationToken.None).ConfigureAwait(true);
        Check(completions.Any(c => c.Reference == "#7" && c.Title == "Greet in French"), "# completes the issue");

        await AskpassCheckAsync(bare).ConfigureAwait(true);

        // A worktree, listed in the Worktrees window, then removed.
        var worktree = Path.Combine(Work, "BuildAndRun-worktree");
        await shell.Git.AddWorktreeAsync(worktree, "smoke/worktree", null).ConfigureAwait(true);
        shell.Layout.ShowToolWindow(WorktreesViewModel.ToolWindowId);
        await shell.Worktrees.ReloadAsync().ConfigureAwait(true);
        await WaitAsync(() => shell.Worktrees.Rows.Count == 2, "the Worktrees window lists the new worktree").ConfigureAwait(true);
        await RenderedAsync().ConfigureAwait(true);
        Screenshot("smoke-test-worktrees.png");
        await shell.Git.RemoveWorktreeAsync(worktree, force: true).ConfigureAwait(true);

        // Sign out: the token leaves the credential store; back to the branch the later steps expect.
        row.SignOutCommand.Execute(null);
        await WaitAsync(() => !row.IsSignedIn && shell.Hosting.Accounts.Count == 0 && shell.Match is null, "signing out forgets the account").ConfigureAwait(true);
        await shell.Git.SwitchAsync(new Workbench.Git.GitBranch("smoke/branch", false, false, null, string.Empty)).ConfigureAwait(true);
    }

    /// <summary>
    /// Askpass in the shipped app: git, with no credential helper, asks this executable for HTTPS credentials; the
    /// shell's dialog answers it, and git authenticates (a mock server serving the bare repository with Basic auth).
    /// </summary>
    private async Task AskpassCheckAsync(string bare)
    {
        Check(shell.Askpass is not null, "the askpass server runs (the codalus executable was found)");
        var expected = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("smoke:secret"));
        await using var server = new Tests.MockHttpServer(request =>
            request.Header("Authorization") != expected
                ? new Tests.MockHttpResponse(401, [], "text/plain") { Headers = new Dictionary<string, string> { ["WWW-Authenticate"] = "Basic realm=\"smoke\"" } }
                : request.Path == "/private.git/info/refs" ? Tests.MockHttpResponse.Text(File.ReadAllText(Path.Combine(bare, "info", "refs")))
                : request.Path.StartsWith("/private.git/", StringComparison.Ordinal) ? Tests.MockHttpResponse.File(Path.Combine(bare, request.Path["/private.git/".Length..]))
                : Tests.MockHttpResponse.NotFound());
        var listing = Task.Run(() => shell.Git.RunAsync(_folder, ["-c", "credential.helper=", "ls-remote", server.BaseUrl + "/private.git"], CancellationToken.None, check: false));
        await WaitAsync(() => shell.Dialogs.IsOpen && shell.Dialogs.Title == "Git: Sign In", "askpass asks for the user name", 60).ConfigureAwait(true);
        shell.Dialogs.InputText = "smoke";
        shell.Dialogs.Accept();
        await WaitAsync(() => shell.Dialogs.IsOpen && shell.Dialogs.Title == "Git: Password" && shell.Dialogs.PasswordChar != '\0', "askpass asks for the password, masked", 60).ConfigureAwait(true);
        await RenderedAsync().ConfigureAwait(true);
        Screenshot("smoke-test-askpass.png");
        shell.Dialogs.InputText = "secret";
        shell.Dialogs.Accept();
        var (code, output, error) = await listing.WaitAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(true);
        Check(code == 0 && output.Contains("refs/heads/main", StringComparison.Ordinal), $"git authenticated with what askpass gave it (exit code {code}: {error.Trim()})");
    }

    private Workbench.Testing.TestNode? TestClass(string project) =>
        shell.Tests.Read(roots => roots.FirstOrDefault(r => r.Label == project)?.SelfAndDescendants().FirstOrDefault(n => n.Label == "CalculatorTests"));

    private Workbench.Testing.TestOutcome Outcome(Workbench.Testing.TestNode cls, string method) =>
        shell.Tests.Read(_ => cls.Children.FirstOrDefault(c => c.Label == method)?.Outcome ?? Workbench.Testing.TestOutcome.None);

    // ------------------------------------------------------------------ steps

    /// <param name="limit">The step's own time limit (the command sweep's); <see cref="s_stepLimit"/> otherwise.</param>
    private async Task StepAsync(string id, string milestone, string title, Func<Task> body, string? requires = null, TimeSpan? limit = null)
    {
        if (requires is not null && _results.FirstOrDefault(r => r.Id == requires) is { Outcome: not "passed" })
        {
            Report(id, milestone, title, "skipped", 0, $"Needs '{requires}', which did not pass.");
            return;
        }

        // A known gap of one platform, skipped with its reason in the report (never passed): CODALUS_SMOKE_SKIP is
        // "<id>[,<id>…]=<reason>" (no workflow sets it at the moment; it's for a platform's known gap).
        if (Environment.GetEnvironmentVariable("CODALUS_SMOKE_SKIP") is { Length: > 0 } skip && skip.Split('=', 2) is [var ids, var because]
            && ids.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Contains(id, StringComparer.Ordinal))
        {
            Report(id, milestone, title, "skipped", 0, because.Trim());
            return;
        }

        // Diagnosing one step: CODALUS_SMOKE_ONLY=<id>[,<id>…] runs those (steps that need an earlier one's state
        // need it listed too).
        if (Environment.GetEnvironmentVariable("CODALUS_SMOKE_ONLY") is { Length: > 0 } only
            && !only.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Contains(id, StringComparer.Ordinal))
        {
            Report(id, milestone, title, "skipped", 0, "Not in CODALUS_SMOKE_ONLY.");
            return;
        }

        Console.Out.WriteLine($"[{_clock.Elapsed:mm\\:ss}] {milestone} {id}: {title}");
        Console.Out.Flush();
        var start = Stopwatch.GetTimestamp();
        Task? running = null;
        try
        {
            // A step that hangs (a request without a timeout) fails on its own instead of using up the run.
            running = body();
            await running.WaitAsync(limit ?? s_stepLimit).ConfigureAwait(true);
            Report(id, milestone, title, "passed", Stopwatch.GetElapsedTime(start).TotalSeconds, null);
        }
        catch (Exception ex)
        {
            // Only the step limit is reported as such: a TimeoutException from the step itself (a timed wait in the
            // code it drives) is reported with its stack, like any other exception.
            var error = ex is SmokeFailure ? ex.Message
                : ex is TimeoutException && running is { IsCompleted: false } ? $"The step didn't finish within {(limit ?? s_stepLimit).TotalMinutes:0} minutes."
                : ex.ToString();
            Report(id, milestone, title, "failed", Stopwatch.GetElapsedTime(start).TotalSeconds, error);
            Screenshot($"smoke-test-{id}.png", keepToasts: true); // the toasts may say why
            if (!_logsShown)
            {
                // The first failure prints the hosts' logs, so CI's log says why even when the run is cut short.
                _logsShown = true;
                foreach (var channel in shell.Workbench.Output.Channels.Where(c => c.Name is "Language Services" or "Build Host" or "Log"))
                {
                    Console.Out.WriteLine($"  --- {channel.Name} (the end) ---");
                    Console.Out.WriteLine(Tail(channel.GetText(), 4000));
                }

                Console.Out.WriteLine("  --- Language services ---");
                foreach (var line in shell.Languages.Describe())
                {
                    Console.Out.WriteLine("  " + line);
                }

                PrintHostDiagnostics();
                Console.Out.Flush();
            }

            // A failed step leaves no debug session behind: the next one would find it instead of starting its own.
            if (shell.Debugger.IsActive)
            {
                try
                {
                    await shell.Debugger.StopAsync().WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(true);
                }
                catch (Exception stop)
                {
                    Console.Out.WriteLine($"  (the debug session didn't stop: {stop.Message})");
                }
            }
        }
        finally
        {
            WriteReport();
        }
    }

    /// <summary>
    /// On Linux and macOS: the busiest processes, and the C# host's managed stacks when <c>dotnet-stack</c> is installed
    /// (CI installs it), so a host that stopped answering says where it is.
    /// </summary>
    private void PrintHostDiagnostics()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        static string Run(string program, params string[] arguments)
        {
            try
            {
                var start = new ProcessStartInfo(program) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
                foreach (var argument in arguments)
                {
                    start.ArgumentList.Add(argument);
                }

                using var process = Process.Start(start)!;
                var output = process.StandardOutput.ReadToEndAsync();
                var error = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(TimeSpan.FromSeconds(60)))
                {
                    // The tool alone (ps and dotnet-stack start nothing): ending a process tree lists the system's
                    // processes, which is what blocks when ps does (the macOS runner, blazor.wasmDebug).
                    process.Kill();
                    return "(timed out)";
                }

                return output.GetAwaiter().GetResult() + error.GetAwaiter().GetResult();
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
            {
                return $"({program}: {ex.Message})";
            }
        }

        Console.Out.WriteLine("  --- Processes (by CPU) ---");
        Console.Out.WriteLine(string.Join('\n', (OperatingSystem.IsMacOS() ? Run("ps", "-axro", "pid,ppid,etime,pcpu,rss,command") : Run("ps", "-eo", "pid,ppid,etime,pcpu,rss,args", "--sort=-pcpu")).Split('\n').Take(15).Select(l => l.Length > 200 ? l[..200] : l)));
        var tool = new[] { Environment.GetEnvironmentVariable("HOME") is { } home ? Path.Combine(home, ".dotnet", "tools", "dotnet-stack") : null }
            .OfType<string>().FirstOrDefault(File.Exists);
        if (tool is not null && shell.Languages.HostProcessId(ShellHost.CSharpLanguageId) is { } pid)
        {
            Console.Out.WriteLine($"  --- The C# host's stacks (process {pid}) ---");
            Console.Out.WriteLine(Tail(Run(tool, "report", "-p", pid.ToString(System.Globalization.CultureInfo.InvariantCulture)), 20000));
        }
    }

    /// <summary>The longest a step may take (the first browser and Node.js downloads included).</summary>
    private static readonly TimeSpan s_stepLimit = TimeSpan.FromMinutes(12);

    private bool _logsShown;

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

    private static Task WaitAsync(Func<bool> condition, string what, int seconds = 30) => WaitAsync(condition, () => what, seconds);

    /// <summary>As above, with <paramref name="what"/> read when the wait times out (a log's tail at that moment).</summary>
    private static async Task WaitAsync(Func<bool> condition, Func<string> what, int seconds = 30)
    {
        var deadline = Stopwatch.GetTimestamp() + (seconds * Stopwatch.Frequency);
        while (!condition())
        {
            if (Stopwatch.GetTimestamp() > deadline)
            {
                throw new SmokeFailure($"Timed out after {seconds} s waiting for: {what()}.");
            }

            await Task.Delay(50).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Asks <paramref name="probe"/> until it answers non-null, each try cut off after 10 s, without ever blocking the
    /// UI thread: a host that stops answering fails the step instead of hanging the app.
    /// </summary>
    private static async Task<T> WaitForAsync<T>(Func<CancellationToken, Task<T?>> probe, string what, int seconds = 30) where T : class
    {
        var deadline = Stopwatch.GetTimestamp() + (seconds * Stopwatch.Frequency);
        while (true)
        {
            using var attempt = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                if (await probe(attempt.Token).ConfigureAwait(true) is { } value)
                {
                    return value;
                }
            }
            catch (OperationCanceledException) when (attempt.IsCancellationRequested)
            {
                // This try took too long; the deadline decides.
            }

            if (Stopwatch.GetTimestamp() > deadline)
            {
                throw new SmokeFailure($"Timed out after {seconds} s waiting for: {what}.");
            }

            await Task.Delay(250).ConfigureAwait(true);
        }
    }

    // ------------------------------------------------------------------ fixture and editor helpers

    /// <summary>
    /// The temporary folder every step copies its fixtures into, made on first use with the samples' build files (so a
    /// fixture never inherits the repository's), whichever steps CODALUS_SMOKE_ONLY keeps.
    /// </summary>
    private string Work
    {
        get
        {
            if (_workRoot.Length == 0)
            {
                var folder = Path.Combine(RealPath.Resolve(Path.GetTempPath()), "codalus-smoke-" + Guid.NewGuid().ToString("N")[..8]);
                Directory.CreateDirectory(folder);
                File.Copy(Path.Combine(samples, "Directory.Build.props"), Path.Combine(folder, "Directory.Build.props"));
                File.Copy(Path.Combine(samples, "Directory.Packages.props"), Path.Combine(folder, "Directory.Packages.props"));
                _workRoot = folder;
            }

            return _workRoot;
        }
    }

    /// <summary>Copies samples/BuildAndRun (standalone, as under samples/) to a temporary folder and makes it a git repository.</summary>
    private void PrepareFixture()
    {
        Check(File.Exists(Path.Combine(samples, "BuildAndRun", "BuildAndRun.slnx")), $"the samples folder {samples} has BuildAndRun");
        _folder = Path.Combine(Work, "BuildAndRun");
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
    private string PlainProject => Path.Combine(Work, "Packages", "src", "Plain", "Plain.csproj");

    private string PreparePackagesFixture()
    {
        Check(File.Exists(Path.Combine(samples, "Packages", "Packages.slnx")), $"the samples folder {samples} has Packages");
        Copy(Path.Combine(samples, "Packages"), Path.Combine(Work, "Packages"));
        return Path.Combine(Work, "Packages", "Packages.slnx");
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
        _testing = Path.Combine(Work, "Testing");
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

    private void Git(params string[] arguments) => GitIn(_folder, arguments);

    private static void GitIn(string folder, params string[] arguments)
    {
        var git = Environment.GetEnvironmentVariable(Workbench.Git.GitService.EnvironmentVariable) is { Length: > 0 } custom ? custom : "git";
        var start = new ProcessStartInfo(git) { WorkingDirectory = folder, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
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

    private static IEnumerable<ParallelStackNodeViewModel> Flatten(IEnumerable<ParallelStackNodeViewModel> nodes) =>
        nodes.SelectMany(n => Flatten(n.Children).Prepend(n));

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

    /// <summary>Two frames of <paramref name="of"/> (the main window by default) have been rendered.</summary>
    private async Task RenderedAsync(Avalonia.Controls.TopLevel? of = null)
    {
        var target = of ?? window;
        var rendered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        target.RequestAnimationFrame(_ => target.RequestAnimationFrame(_ => rendered.TrySetResult()));

        // macOS can stop drawing a window it considers hidden (behind others, App Nap): the screenshot renders it
        // itself, so a few seconds without frames go on rather than wait forever.
        await rendered.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext | ConfigureAwaitOptions.SuppressThrowing);
        if (!rendered.Task.IsCompleted)
        {
            shell.Workbench.Log.AppendLine("[smoke] No frame in 5 s: going on without one.");
        }
    }

    // ------------------------------------------------------------------ report

    /// <summary>
    /// Saves the window (or another of the shell's) as <paramref name="name"/> in the logs. The toasts are hidden first
    /// (see <see cref="HideToasts"/>) unless <paramref name="keepToasts"/>: a failed step's picture keeps them.
    /// </summary>
    private void Screenshot(string name, Avalonia.Controls.TopLevel? of = null, bool keepToasts = false)
    {
        if (!keepToasts)
        {
            HideToasts(name);
        }

        try
        {
            var target = of ?? window;
            Visual root = target; // up to the host, with the decorations Avalonia draws
            while (root.GetVisualParent() is { } parent)
            {
                root = parent;
            }

            var scaling = target.RenderScaling;
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

    /// <summary>
    /// The pictures the website and the gallery use show their feature, not a toast an earlier step left at the bottom
    /// right: the toasts are hidden (the Escape of a toast, so each stays in the Notifications window and nothing is
    /// answered for it), and each hidden one is printed to the run's log, so a real error is still seen there. The
    /// steps' own checks of notifications are unaffected.
    /// </summary>
    private void HideToasts(string picture)
    {
        if (shell.Notifications.Toasts.Count == 0)
        {
            return;
        }

        foreach (var toast in shell.Notifications.Toasts)
        {
            Console.Out.WriteLine($"  (a toast hidden for {picture}: {toast.Model.Severity}: {toast.Message})");
        }

        shell.Notifications.HideToasts();
        window.UpdateLayout();
    }

    /// <summary>Whether the run passed: steps ran, and none failed.</summary>
    private bool Passed => _results.Any(r => r.Outcome == "passed") && _results.All(r => r.Outcome != "failed");

    private void WriteReport()
    {
        Directory.CreateDirectory(shell.Paths.Logs);
        var passed = Passed;
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

    /// <summary>The update step's backend: one offer, a download that is done at once, nothing applied.</summary>
    private sealed class StandInUpdates(Workbench.Updates.UpdateOffer offer) : Workbench.Updates.IUpdateBackend
    {
        public Task<Workbench.Updates.UpdateOffer?> CheckAsync(string channel, bool allowDowngrade, CancellationToken cancellationToken) =>
            Task.FromResult<Workbench.Updates.UpdateOffer?>(offer);

        public Task DownloadAsync(Workbench.Updates.UpdateOffer offer, IProgress<int> progress, CancellationToken cancellationToken)
        {
            progress.Report(100);
            return Task.CompletedTask;
        }

        public void ApplyAfterExit(Workbench.Updates.UpdateOffer offer, bool restart, IReadOnlyList<string> arguments)
        {
        }
    }
}
