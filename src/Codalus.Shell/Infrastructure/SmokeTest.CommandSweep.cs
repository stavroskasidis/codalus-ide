using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Codalus.Debugging;
using Codalus.Platform;
using Codalus.Shell.Docking;
using Codalus.Shell.Editor;
using Codalus.Shell.Input;
using Codalus.Shell.ViewModels;
using Codalus.Shell.Views;
using Codalus.Workbench.Commands;
using Codalus.Workbench.Debugging;
using Codalus.Workbench.Menus;
using Codalus.Workbench.Notifications;
using Codalus.Workbench.Settings;
using MenuItem = Avalonia.Controls.MenuItem;

namespace Codalus.Shell.Infrastructure;

/// <summary>
/// The command sweep (<c>shell.commandSweep</c>): every registered command, through each way a user reaches it — its
/// menu item (the in-window menu bar on Windows and Linux, the global menu on macOS, the editor's and the Solution
/// Explorer's context menus), its key binding through the window's key handling, and the command palette — on a copy
/// of BuildAndRun that is a Git repository with a change and a local bare remote. Each menu item must be there exactly
/// when its when-clause holds, enabled exactly when the command's enablement does, with the platform's key label; each
/// run must finish without an error, an unhandled exception or a UI-thread stall over 500 ms, and whatever it opens
/// (a dialog, a picker, a tool window, a document) must open and close cleanly. Native file pickers answer Cancel (a
/// stand-in window), dialogs and pickers are cancelled, and commands that would leave the machine (exit, install,
/// publish, a browser) are skipped with their reason. The rows go to <c>command-sweep.json</c> next to
/// <c>smoke-test.json</c>; the step fails on any failure not listed in <see cref="s_sweepKnown"/>.
/// </summary>
internal sealed partial class SmokeTest
{
    /// <summary>A regular expression: the sweep runs only the commands whose ids match it (to bisect a failure).</summary>
    private const string SweepOnlyVariable = "CODALUS_SWEEP_ONLY";

    /// <summary>
    /// The longest the UI thread may stall in a command: 500 ms on a developer's machine. CI's hosted runners render in
    /// software with no GPU (Xvfb on Linux), and only a command's first run there went past 500 ms: a cold first open of
    /// its view or window (templates, styles, first layout and text shaping), 521 ms on Linux and 824 ms on macOS, while
    /// its later runs stayed under 400 ms on every OS (four CI reports of 0.4.0, about 3,700 runs). On CI a first run may
    /// stall up to 2 s and a later one up to 1 s; the benchmarks' budgets hold on the reference machine and are
    /// indicative on CI (ADR 0002).
    /// </summary>
    private static TimeSpan SweepStallLimit(bool firstRun) =>
        TimeSpan.FromMilliseconds(Environment.GetEnvironmentVariable("CI") != "true" ? 500 : firstRun ? 2000 : 1000);

    /// <summary>The commands that have run from an entry point (the next run of each is warm).</summary>
    private readonly HashSet<string> _sweepRan = new(StringComparer.Ordinal);

    /// <summary>Commands the sweep never runs, and why: they leave the machine or the app (the report's skips).</summary>
    private static readonly Dictionary<string, string> s_sweepSkips = new(StringComparer.Ordinal)
    {
        ["workbench.quit"] = "exits the app",
        ["workbench.restart"] = "restarts the app",
        ["update.restart"] = "applies a downloaded update and restarts the app (the update.restart step covers it with a stand-in)",
        ["update.check"] = "asks the release feed (api.github.com) on an installed build",
        ["plugins.checkUpdates"] = "asks the plugin sources (nuget.org) and installs updates",
        ["plugins.browse"] = "lists the Plugin Gallery from nuget.org",
        ["plugins.bisect"] = "turns plugins off and restarts the app, repeatedly",
        ["plugins.runDevelopmentInstance"] = "builds a plugin and starts a second Codalus IDE",
        ["plugins.debugDevelopmentInstance"] = "starts a second Codalus IDE and attaches the debugger to it",
        ["web.installLanguageServers"] = "downloads Node.js and the web language servers",
        ["web.trustDevCertificate"] = "changes the operating system's certificate trust store",
        ["web.manageUserSecrets"] = "writes the user's UserSecrets folder, outside the test's folders",
        ["mobile.installAndroidSdk"] = "installs the Android SDK and a JDK (0.5 GB) and accepts their licenses",
        ["ef.installTool"] = "installs dotnet-ef from nuget.org",
        ["fsharp.installLanguageService"] = "installs fsautocomplete and fantomas from nuget.org",
        ["packages.signIn"] = "signs in to a package feed (a real device-code sign-in)",
        ["containers.composeUp"] = "builds images and pulls more than mcr.microsoft.com/dotnet",
        ["containers.composeDebug"] = "builds images and pulls more than mcr.microsoft.com/dotnet",
        ["remote.reopenInContainer"] = "builds or pulls a dev container's image from any registry",
        ["remote.open"] = "installs codalus-server on another machine (the remote.session step covers --remote local)",
        ["debug.justInTime"] = "registers the Just-In-Time debugger (tests never register it for real)",
        ["debug.createDump"] = "writes a full dump of the program (6.7 GB on macOS); the debug.companion step covers dumps",
        ["debug.webPage"] = "starts a browser under the JavaScript debugger (the blazor.* steps cover it)",
        ["debug.webPageFile"] = "starts a browser under the JavaScript debugger (the blazor.* steps cover it)",
        ["ai.agents.connect"] = "opens Connect to an agent, whose test downloads the agent from npm and starts it (the ai.connect step covers the dialog with stand-ins)",
        ["ai.agents.connect.claude"] = "opens Connect to Claude, whose test downloads the adapter from npm and starts it (the ai.connect step covers the dialog with stand-ins)",
        ["ai.agents.connect.codex"] = "opens Connect to Codex, whose test downloads it from npm and starts it",
        ["ai.agents.connect.gemini"] = "opens Connect to Gemini CLI, whose test downloads it from npm and starts it",
        ["ai.agents.connect.copilot"] = "opens Connect to GitHub Copilot, whose test downloads it from npm and starts it",
        ["ai.agents.connect.pi"] = "opens Connect to pi, whose test downloads it from npm and installs pi's CLI",
        ["ai.agents.connect.opencode"] = "opens Connect to OpenCode, whose install runs npm install -g",
        ["ai.agents.connect.goose"] = "opens Connect to goose, whose install runs Homebrew",
        ["ai.agents.check"] = "starts the user's configured agent (npx downloads it) to check its sign-in",
        ["ai.agents.disconnect"] = "can start the user's agent to sign it out, and removes its entry and key",
        ["ai.agents.manage"] = "lists the configured agents, whose actions start them (Check Connection, Disconnect)",
    };

    /// <summary>Commands for plugins, CodeLens and the agent: they take arguments, and no menu, key or palette entry runs them.</summary>
    private static readonly HashSet<string> s_sweepApiCommands = new(StringComparer.Ordinal)
    {
        "editor.activePosition", "editor.action.showReferences", "fsharp.showReferences", "project.addReferenceTo", "solution.addProjectFile", "solution.getInfo",
        "welcome.nextTip", "workbench.getRecentSolutions", "workbench.openExternal", "workbench.openFile", "workbench.setSetting",
    };

    /// <summary>Commands that end the sweep's context (the solution, the editor): run last, in that order.</summary>
    private static readonly string[] s_sweepLast = ["workbench.closeAllDocuments", "workbench.closeActiveDocument", "workbench.closeSolution"];

    /// <summary>
    /// Errors a command is right to report in the sweep's context (part of the error's text, and why): the row passes
    /// and notes it. Any other error notification fails the row.
    /// </summary>
    private static readonly Dictionary<string, (string Text, string Why)> s_sweepExpectedErrors = new(StringComparer.Ordinal)
    {
        ["git.continue"] = ("Resolve the conflicts first", "the GitMerge context leaves its conflict unresolved on purpose"),
    };

    /// <summary>
    /// Failures triaged and kept for later (TODO(issue)), "command|entry" or "command|*": the rows stay failed in the
    /// report, but they don't fail the step.
    /// </summary>
    private static readonly Dictionary<string, string> s_sweepKnown = new(StringComparer.Ordinal)
    {
    };

    private sealed record SweepRow(string Command, string Entry, string Outcome, string? Reason)
    {
        public string? Location { get; init; }

        public string? Scenario { get; init; }

        public double Milliseconds { get; init; }

        public double StallMilliseconds { get; init; }

        /// <summary>How long the garbage collector paused the process during the run: a stall it explains isn't the command's code.</summary>
        public double GcPauseMilliseconds { get; init; }

        public string? Saw { get; init; }

        public string? Screenshot { get; init; }

        public bool Known { get; init; }
    }

    private enum SweepScenario
    {
        Editor,
        Selection,
        ExplorerSolution,
        ExplorerProject,
        ExplorerFolder,
        ExplorerFile,
        ExplorerOther,
        Terminal,
        Debug,
        GitMerge,
        Last,
        NoSolution,
    }

    /// <summary>
    /// Contexts the sweep doesn't set up, and why (the first whose key a never-enabled command's enablement names):
    /// other steps cover most of them, on fixtures of their own.
    /// </summary>
    private static readonly (string Key, string Reason)[] s_sweepUnreached =
    [
        ("winformsDesigner", "needs a Windows Forms designer document (Windows only; the winforms.designer steps cover it)"),
        ("explorerItemOrdered", "needs an F# project's compile order in the Solution Explorer (the fsharp.* steps cover F#)"),
        ("fsharp", "needs an F# file served by FsAutoComplete (the fsharp.* steps cover it)"),
        (".fsx", "needs an F# script (the fsharp.interactive step covers it)"),
        ("gitLfs", "needs a Git LFS repository and an LFS server"),
        ("coverageAvailable", "needs coverage results (the testing steps cover them)"),
        ("folderMode", "needs a folder opened without a solution (the projects.folder step covers it)"),
        ("http", "needs an .http file"),
        ("profilerCollecting", "needs a profiler session collecting (the profiler.* steps cover it)"),
        ("remoteConnected", "needs a remote window (the remote.session step covers it)"),
        ("hostingRepository", "needs a repository on a Git host (the git.hosting step covers it with a mock)"),
        ("buildRunning", "needs a build in progress"),
        ("debugRunning", "needs a program running under the debugger: BuildAndRun's Hello runs to its end at once"),
        ("programRunning", "needs a program running: BuildAndRun's Hello runs to its end at once"),
        ("hotReloadRunning", "needs a program running with Hot Reload (the web.hotReload step covers it)"),
        ("testsRunning", "needs a test run in progress"),
        ("debugCanDetach", "needs an attached debug session"),
        ("mobileStartup", "needs a .NET MAUI startup project (the maui.* steps cover it)"),
        ("gitRepositories", "needs several Git repositories in one solution"),
        ("!gitRepository", "needs a solution outside a Git repository"),
        ("!solutionTrusted", "needs an untrusted solution"),
        ("explorerItemExcluded", "needs a file excluded from its project (Show All Files)"),
        ("explorerItemIsProjectFile", "needs a project file selected in the folder view"),
        ("explorerProjectNonSdkStyle", "needs a non-SDK-style project (the netfx.projects step covers it)"),
        ("testExplorerSelection", "needs AI turned on and a test selected in the Test Explorer"),
        ("customEditor", "needs a custom editor or the hex viewer"),
        ("debugStopped", "needs a debug session stopped at a breakpoint"),
        ("debugActive", "needs a debug session"),
    ];

    private static string UnreachedReason(CommandDescriptor command) =>
        (s_sweepUnreached.FirstOrDefault(u => command.Enablement.Text.Contains(u.Key, StringComparison.Ordinal)).Reason ?? "never enabled in the sweep's contexts")
        + $" (enablement: {command.Enablement.Text})";

    private readonly List<SweepRow> _sweepRows = [];
    private readonly List<string> _sweepUnhandled = [];
    private SweepScenario _sweepScenario;
    private string _sweepFolder = string.Empty;
    private string _sweepProgram = string.Empty;
    private string _sweepSolution = string.Empty;
    private CodeEditorController? _sweepEditor;
    private CodeEditorDocument? _sweepDocument;
    private string _sweepProgramText = string.Empty;
    private ContextMenu? _sweepContextMenu;
    private int _sweepScreenshots;
    private bool _sweepDebugReopened;
    private SweepStallMonitor? _sweepStall;
    private SweepContextWatch? _sweepContext;

    /// <summary>
    /// The longest the sweep waits, before an entry point, for the context to settle: the UI thread idle, no project
    /// evaluation in progress, and the keys the command reads unchanged (a command's effects land a posted job later).
    /// </summary>
    private static readonly TimeSpan s_sweepSettleLimit = TimeSpan.FromSeconds(3);

    /// <summary>Once it has settled, how long the sweep waits for a command it chose in this context to be enabled again.</summary>
    private static readonly TimeSpan s_sweepEnabledWait = TimeSpan.FromSeconds(1);

    private async Task CommandSweepStepAsync()
    {
        var started = Stopwatch.StartNew();
        PrepareSweepFixture();
        var realWindow = shell.Window;
        shell.Window = new SweepWindow(realWindow, shell.Workbench.Log);
        var helpUrl = shell.OpenHelpUrl;
        shell.OpenHelpUrl = uri => shell.Workbench.Log.AppendLine($"[sweep] help page {uri} (not opened)");
        var (externalUrl, revealPath) = (shell.OpenExternalUrl, shell.RevealPath);
        shell.OpenExternalUrl = uri => shell.Workbench.Log.AppendLine($"[sweep] the browser or file manager would open {uri}");
        shell.RevealPath = path => shell.Workbench.Log.AppendLine($"[sweep] the file manager would show {path}");
        var scheme = shell.Workbench.Settings.GetString(BuiltIns.KeyboardSchemeSetting, "default");
        await shell.Workbench.Settings.SetAsync(BuiltIns.KeyboardSchemeSetting, "default", SettingScope.User, CancellationToken.None).ConfigureAwait(true);
        Dispatcher.UIThread.UnhandledException += OnSweepUnhandled;
        TaskScheduler.UnobservedTaskException += OnSweepUnobserved;
        MenuBase.OpenedEvent.AddClassHandler<ContextMenu>((menu, _) => _sweepContextMenu = menu);
        _sweepStall = new SweepStallMonitor();
        _sweepContext = new SweepContextWatch(shell.Workbench.Context);
        try
        {
            shell.Trust.Trust(_sweepFolder);
            await shell.OpenSolutionAsync(_sweepSolution).ConfigureAwait(true);
            await WaitAsync(() => shell.Solutions.Projects.Count == 2 && shell.Solutions.Progress is { Completed: true }, "the sweep's solution is evaluated", 240).ConfigureAwait(true);
            // Exactly its two projects: a design-time build of the step before's solution (dotnet.older) that ended after the
            // switch once brought one of its projects back into this workspace (macOS; LanguageService forgets closed ones).
            await WaitAsync(() => shell.Languages.Status(ShellHost.CSharpLanguageId) is { State: "ready", Projects: 2 }, () => $"the C# workspace is ready with the sweep's two projects ({shell.Languages.Status(ShellHost.CSharpLanguageId)})", 360).ConfigureAwait(true);
            await WaitAsync(() => shell.HasRepository && shell.CurrentBranch is not null, "the Git repository is found", 60).ConfigureAwait(true);

            var commands = shell.Workbench.Commands.Descriptors
                .Where(d => !s_sweepApiCommands.Contains(d.Id))
                .OrderBy(d => Array.IndexOf(s_sweepLast, d.Id))
                .ThenBy(d => d.Id, StringComparer.Ordinal)
                .ToList();
            if (Environment.GetEnvironmentVariable(SweepOnlyVariable) is { Length: > 0 } only)
            {
                // To bisect a failure: only the matching commands run (each context is still set up).
                var filter = new Regex(only, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
                commands.RemoveAll(d => !filter.IsMatch(d.Id));
                Console.Out.WriteLine($"  [sweep] {SweepOnlyVariable}: {commands.Count.ToString(CultureInfo.InvariantCulture)} commands match '{only}'");
            }

            foreach (var api in shell.Workbench.Commands.Descriptors.Where(d => s_sweepApiCommands.Contains(d.Id)))
            {
                Sweep(api.Id, "*", "skip", "an API command (plugins, CodeLens, the start page): it takes arguments, and no menu, key or palette entry runs it");
            }

            foreach (var skipped in commands.Where(d => SkipReason(d.Id) is not null).ToList())
            {
                Sweep(skipped.Id, "*", "skip", SkipReason(skipped.Id));
                commands.Remove(skipped);
            }

            var pending = commands.Where(d => !s_sweepLast.Contains(d.Id)).ToList();
            foreach (var scenario in Enum.GetValues<SweepScenario>().Where(s => s is not (SweepScenario.Last or SweepScenario.NoSolution)))
            {
                if (pending.Count == 0)
                {
                    break;
                }

                if (!await EnterScenarioAsync(scenario).ConfigureAwait(true))
                {
                    continue;
                }

                // A command whose only menus are this scenario's context menu runs here first; then whatever is enabled.
                foreach (var command in pending.Where(d => HomeOf(d.Id) == scenario).Concat(pending.Where(d => HomeOf(d.Id) != scenario)).ToList())
                {
                    if (!command.Enablement.Evaluate(shell.Workbench.Context) || (HomeOf(command.Id) is { } home && home != scenario && LaterScenario(home, scenario)))
                    {
                        continue;
                    }

                    pending.Remove(command);
                    await SweepCommandAsync(command).ConfigureAwait(true);
                    if (!await EnsureScenarioAsync().ConfigureAwait(true))
                    {
                        break;
                    }
                }
            }

            // The commands that end the context, then no solution at all.
            foreach (var id in s_sweepLast)
            {
                if (commands.FirstOrDefault(d => d.Id == id) is { } command)
                {
                    await EnterScenarioAsync(SweepScenario.Last).ConfigureAwait(true);
                    if (command.Enablement.Evaluate(shell.Workbench.Context))
                    {
                        await SweepCommandAsync(command).ConfigureAwait(true);
                    }
                    else
                    {
                        pending.Add(command);
                    }
                }
            }

            if (pending.Count > 0 && await EnterScenarioAsync(SweepScenario.NoSolution).ConfigureAwait(true))
            {
                foreach (var command in pending.Where(d => d.Enablement.Evaluate(shell.Workbench.Context)).ToList())
                {
                    pending.Remove(command);
                    await SweepCommandAsync(command).ConfigureAwait(true);
                }
            }

            foreach (var command in pending)
            {
                Sweep(command.Id, "*", "skip", UnreachedReason(command));
            }
        }
        finally
        {
            _sweepStall.Stop();
            _sweepContext.Stop();
            Dispatcher.UIThread.UnhandledException -= OnSweepUnhandled;
            TaskScheduler.UnobservedTaskException -= OnSweepUnobserved;
            shell.Window = realWindow;
            shell.OpenHelpUrl = helpUrl;
            (shell.OpenExternalUrl, shell.RevealPath) = (externalUrl, revealPath);
            await shell.Workbench.Settings.SetAsync(BuiltIns.KeyboardSchemeSetting, scheme, SettingScope.User, CancellationToken.None).ConfigureAwait(true);
            WriteSweepReport(started.Elapsed);
        }

        var failed = _sweepRows.Where(r => r is { Outcome: "fail", Known: false }).ToList();
        Check(failed.Count == 0, $"every command runs from each of its entry points ({failed.Count} failed: {string.Join("; ", failed.Take(15).Select(r => $"{r.Command} [{r.Entry}] {r.Reason}"))})");
    }

    private static string? SkipReason(string id) =>
        s_sweepSkips.TryGetValue(id, out var reason) ? reason
        : id.StartsWith("externalTools.run.", StringComparison.Ordinal) ? "runs a user-defined external tool"
        : null;

    // ------------------------------------------------------------------ fixture

    /// <summary>
    /// Copies samples/BuildAndRun into its own folder (never the samples in place), makes it a Git repository with a
    /// commit, a local bare remote (origin, so fetch, pull and push stay on this machine) and an uncommitted change.
    /// </summary>
    private void PrepareSweepFixture()
    {
        var root = Path.Combine(Work, "CommandSweep");
        _sweepFolder = Path.Combine(root, "BuildAndRun");
        Copy(Path.Combine(samples, "BuildAndRun"), _sweepFolder);
        File.Copy(Path.Combine(samples, "Directory.Build.props"), Path.Combine(root, "Directory.Build.props"), overwrite: true);
        File.Copy(Path.Combine(samples, "Directory.Packages.props"), Path.Combine(root, "Directory.Packages.props"), overwrite: true);
        if (File.Exists(Path.Combine(samples, "global.json")))
        {
            File.Copy(Path.Combine(samples, "global.json"), Path.Combine(root, "global.json"), overwrite: true);
        }

        File.WriteAllText(Path.Combine(_sweepFolder, ".gitignore"), "bin/\nobj/\n");
        var remote = Path.Combine(root, "remote.git");
        GitIn(root, "init", "-q", "--bare", "-b", Branch, remote);
        GitIn(_sweepFolder, "init", "-q", "-b", Branch);
        GitIn(_sweepFolder, "config", "user.name", "Smoke Test");
        GitIn(_sweepFolder, "config", "user.email", "smoke@example.com");
        GitIn(_sweepFolder, "config", "commit.gpgsign", "false");
        GitIn(_sweepFolder, "config", "core.autocrlf", "false");
        File.WriteAllText(Path.Combine(_sweepFolder, "notes.txt"), "a line both branches change\n");

        // The Solution Explorer's folder and file for the commands that rename, cut, exclude or delete what's selected.
        Directory.CreateDirectory(Path.GetDirectoryName(SweepExplorerFile)!);
        File.WriteAllText(SweepExplorerFile, "namespace Hello;\n\ninternal static class Sweep\n{\n}\n");
        GitIn(_sweepFolder, "add", ".");
        GitIn(_sweepFolder, "commit", "-q", "-m", "initial");
        GitIn(_sweepFolder, "remote", "add", "origin", remote);
        GitIn(_sweepFolder, "push", "-q", "-u", "origin", Branch);

        // A branch whose merge conflicts, for the commands of a merge in progress (the GitMerge context).
        GitIn(_sweepFolder, "checkout", "-q", "-b", SweepConflictBranch);
        File.WriteAllText(Path.Combine(_sweepFolder, "notes.txt"), "the other branch's line\n");
        GitIn(_sweepFolder, "commit", "-q", "-am", "the other side");
        GitIn(_sweepFolder, "checkout", "-q", Branch);
        File.WriteAllText(Path.Combine(_sweepFolder, "notes.txt"), "this branch's line\n");
        GitIn(_sweepFolder, "commit", "-q", "-am", "this side");
        _sweepProgram = Path.Combine(_sweepFolder, "src", "Hello", "Program.cs");
        _sweepSolution = Path.Combine(_sweepFolder, "BuildAndRun.slnx");

        // An uncommitted change, for the Git commands that act on one.
        var greeter = Path.Combine(_sweepFolder, "src", "Greetings", "Greeter.cs");
        File.AppendAllText(greeter, "\n// a change for the command sweep\n");
    }

    private const string SweepConflictBranch = "sweep-conflict";

    private string SweepExplorerFile => Path.Combine(_sweepFolder, "src", "Hello", "SweepFolder", "Sweep.cs");

    /// <summary>Merges the conflicting branch: git stops with the conflict in notes.txt (its exit code says so).</summary>
    private void GitMergeConflict()
    {
        // git won't merge with changes staged (a command before may have staged some): unstaged, they stay in the files.
        GitIn(_sweepFolder, "reset", "-q");
        var git = Environment.GetEnvironmentVariable(Workbench.Git.GitService.EnvironmentVariable) is { Length: > 0 } custom ? custom : "git";
        var start = new ProcessStartInfo(git) { WorkingDirectory = _sweepFolder, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { "merge", "--no-edit", SweepConflictBranch })
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new SmokeFailure("git did not start.");
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        Check(File.Exists(Path.Combine(_sweepFolder, ".git", "MERGE_HEAD")), $"git merge stops with a conflict (git said: {output.Trim()})");
    }

    /// <summary>Ends a merge the GitMerge context left in progress, so the other contexts see a plain repository.</summary>
    private async Task AbortSweepMergeAsync()
    {
        if (File.Exists(Path.Combine(_sweepFolder, ".git", "MERGE_HEAD")))
        {
            GitIn(_sweepFolder, "merge", "--abort");
            await shell.Git.RefreshAsync().ConfigureAwait(true);
        }
    }

    // ------------------------------------------------------------------ scenarios

    /// <summary>The scenario whose context menu is a command's only menu: it runs there first.</summary>
    private SweepScenario? HomeOf(string id)
    {
        var locations = shell.Workbench.Menus.Rules.Where(r => r.Command == id && r.Submenu is null && r.Location != MenuLocations.CommandPalette).ToList();
        if (locations.Count == 0 || locations.Any(r => r.Location.StartsWith(MenuLocations.MenuBarPrefix, StringComparison.Ordinal)))
        {
            return null;
        }

        if (locations.Any(r => r.Location.StartsWith(MenuLocations.EditorContext, StringComparison.Ordinal)))
        {
            return SweepScenario.Editor;
        }

        if (locations.FirstOrDefault(r => r.Location.StartsWith(MenuLocations.SolutionExplorerContext, StringComparison.Ordinal)) is { } explorer)
        {
            foreach (var (scenario, kind) in s_explorerKinds)
            {
                var scope = shell.Workbench.Context.With(new Dictionary<string, object?>(StringComparer.Ordinal) { ["explorerItemKind"] = kind, ["focusedView"] = SolutionExplorerViewModel.ToolWindowId });
                if (explorer.When.Evaluate(scope))
                {
                    return scenario;
                }
            }

            return SweepScenario.ExplorerOther;
        }

        return null;
    }

    private static readonly (SweepScenario Scenario, string Kind)[] s_explorerKinds =
    [
        (SweepScenario.ExplorerSolution, "solution"), (SweepScenario.ExplorerProject, "project"),
        (SweepScenario.ExplorerFolder, "folder"), (SweepScenario.ExplorerFile, "file"),
    ];

    /// <summary>Whether <paramref name="home"/> comes after <paramref name="current"/>: its command waits for it.</summary>
    private static bool LaterScenario(SweepScenario home, SweepScenario current) => home > current;

    private async Task<bool> EnterScenarioAsync(SweepScenario scenario)
    {
        if (_sweepScenario != scenario || _sweepRows.Count == 0)
        {
            Console.Out.WriteLine($"  [sweep] scenario {scenario}");
        }

        SweepProgress($"(entering {scenario})");

        _sweepScenario = scenario;
        try
        {
            switch (scenario)
            {
                case SweepScenario.Editor or SweepScenario.Selection or SweepScenario.Last:
                    await StopSweepDebuggingAsync().ConfigureAwait(true);
                    await AbortSweepMergeAsync().ConfigureAwait(true);
                    await FocusSweepEditorAsync(select: scenario == SweepScenario.Selection).ConfigureAwait(true);
                    return true;

                case SweepScenario.ExplorerSolution or SweepScenario.ExplorerProject or SweepScenario.ExplorerFolder or SweepScenario.ExplorerFile or SweepScenario.ExplorerOther:
                    return await FocusExplorerAsync(scenario).ConfigureAwait(true);

                case SweepScenario.Terminal:
                    shell.Run(BuiltIns.ViewCommandPrefix + "terminal");
                    await WaitAsync(() => shell.Terminals.Sessions.Count > 0, "a terminal opens", 30).ConfigureAwait(true);
                    await WaitAsync(() => Equals(shell.Workbench.Context.GetValue("focusedView"), "terminal"), "the terminal has the focus", 10).ConfigureAwait(true);
                    return true;

                case SweepScenario.Debug:
                    if (DebugAdapterLocator.Resolve() is null)
                    {
                        Console.Out.WriteLine("  [sweep] no netcoredbg: the debug scenario is skipped");
                        return false;
                    }

                    if (shell.Debugger.State == DebugState.Stopped)
                    {
                        // Still stopped after a command: Start Debugging now would be F5's Continue, and the program would end.
                        await FocusSweepEditorAsync(select: false).ConfigureAwait(true);
                        return true;
                    }

                    if (await StartSweepDebuggingAsync().ConfigureAwait(true) is { } problem)
                    {
                        if (_sweepDebugReopened)
                        {
                            Sweep("(scenario)", "Debug", "fail", $"Start Debugging stopped at no breakpoint again: {problem}");
                            return false;
                        }

                        // Once more on a fresh solution (new hosts): a pass then means the commands before left debugging broken.
                        _sweepDebugReopened = true;
                        Screenshot("smoke-test-sweep-scenario-debug.png");
                        Console.Out.WriteLine("  [sweep] the debugger didn't stop: reopening the solution to try again");
                        await StopSweepDebuggingAsync().ConfigureAwait(true);
                        await shell.CloseSolutionAsync().ConfigureAwait(true);
                        await shell.OpenSolutionAsync(_sweepSolution).ConfigureAwait(true);
                        await WaitAsync(() => shell.Solutions.Projects.Count == 2 && shell.Solutions.Progress is { Completed: true }, "the sweep's solution is evaluated again", 240).ConfigureAwait(true);
                        var again = await StartSweepDebuggingAsync().ConfigureAwait(true);
                        Sweep("(scenario)", "Debug", "fail", again is null
                            ? $"Start Debugging didn't stop at a breakpoint after the commands before it, and did once the solution was reopened: {problem}"
                            : $"Start Debugging doesn't stop at a breakpoint, even on a reopened solution: {again}");
                        if (again is not null)
                        {
                            return false;
                        }
                    }

                    return true;

                case SweepScenario.GitMerge:
                    await StopSweepDebuggingAsync().ConfigureAwait(true);
                    if (!Equals(shell.Workbench.Context.GetValue("gitMerging"), true))
                    {
                        GitMergeConflict();
                        await shell.Git.RefreshAsync().ConfigureAwait(true);
                        await WaitAsync(() => Equals(shell.Workbench.Context.GetValue("gitMerging"), true), "the merge with a conflict is in progress", 30).ConfigureAwait(true);
                    }

                    await FocusSweepEditorAsync(select: false).ConfigureAwait(true);
                    return true;

                case SweepScenario.NoSolution:
                    await StopSweepDebuggingAsync().ConfigureAwait(true);
                    if (shell.Solutions.Solution is not null)
                    {
                        await shell.CloseSolutionAsync().ConfigureAwait(true);
                    }

                    return shell.Solutions.Solution is null;
            }
        }
        catch (SmokeFailure ex)
        {
            Sweep("(scenario)", scenario.ToString(), "fail", $"the sweep couldn't set up its {scenario} context: {ex.Message}");
            return false;
        }

        return false;
    }

    /// <summary>Starts debugging Hello with a fresh breakpoint on its args line; null once it stops there, else what happened.</summary>
    private async Task<string?> StartSweepDebuggingAsync()
    {
        await FocusSweepEditorAsync(select: false).ConfigureAwait(true);

        // A fresh, enabled breakpoint: the commands before may have disabled, moved or removed it.
        var line = Array.FindIndex(File.ReadAllLines(_sweepProgram), l => l.Contains("Console.WriteLine($\"args", StringComparison.Ordinal)) + 1;
        shell.Debugger.Breakpoints.Clear();
        shell.Debugger.Breakpoints.Toggle(_sweepProgram, line);
        var sawActive = false;
        var seen = new List<string>();
        shell.Run("debug.start");
        var clock = Stopwatch.StartNew();
        while (shell.Debugger.State != DebugState.Stopped && !(sawActive && !shell.Debugger.IsActive) && clock.Elapsed < TimeSpan.FromSeconds(180))
        {
            sawActive |= shell.Debugger.IsActive;
            CloseSweepOverlays(seen); // a dialog (save, build errors) would hold the start
            await Task.Delay(50).ConfigureAwait(true);
        }

        if (shell.Debugger.State == DebugState.Stopped)
        {
            // The context follows the state on the UI thread: until it does, the commands' enablement is the running
            // program's (Pause enabled, then disabled before its click: #145's Linux run).
            await WaitAsync(() => Equals(shell.Workbench.Context.GetValue("debugStopped"), true) && !Equals(shell.Workbench.Context.GetValue("debugRunning"), true), "the context says the program is stopped", 10).ConfigureAwait(true);
            await FocusSweepEditorAsync(select: false).ConfigureAwait(true);
            return null;
        }

        var build = shell.Workbench.Output.Channels.FirstOrDefault(c => c.Name == "Build")?.GetText() ?? string.Empty;
        var breakpoints = string.Join(", ", shell.Debugger.Breakpoints.All.Select(b =>
            $"{Path.GetFileName(b.Path)}:{b.Line}{(b.Enabled ? string.Empty : " disabled")}{(b.Temporary ? " temporary" : string.Empty)} {(shell.Debugger.Breakpoints.StateOf(b) is { } state ? $"verified={state.Verified} {state.Message}" : "no state")}"));
        var startup = shell.Runs.MultipleStartup ? "multiple: " + string.Join(", ", shell.Runs.StartupTargets.Select(t => $"{Path.GetFileNameWithoutExtension(t.Project)} {t.Action}")) : Path.GetFileName(shell.Runs.StartupProject);
        return $"the debugger didn't stop at line {line} ({(sawActive ? "the program ran to its end" : "no session started")}; startup: {startup}; breakpoints: {breakpoints}; saw: {string.Join(" | ", seen)}; "
               + $"status: {shell.StatusBar.DisplayMessage}; Debug output: {Tail(shell.DebugLog.GetText(), 1500)}; Build output: {Tail(build, 500)})";
    }

    /// <summary>After a command: the scenario's context is back, or set up again (a command closed the editor or the solution).</summary>
    private async Task<bool> EnsureScenarioAsync()
    {
        // What the command started lands first: set up on the tree as it was, the scenario selected a folder that the
        // command's Exclude From Project then took away while the palette ran it again (0.4.0's release run).
        await SettleSweepAsync().ConfigureAwait(true);
        if (_sweepScenario is SweepScenario.NoSolution)
        {
            return true;
        }

        if (shell.Solutions.Solution?.Path is not { } open || !string.Equals(Path.GetFullPath(open), Path.GetFullPath(_sweepSolution), StringComparison.OrdinalIgnoreCase))
        {
            Console.Out.WriteLine("  [sweep] the solution closed: opening it again");
            await shell.OpenSolutionAsync(_sweepSolution).ConfigureAwait(true);
            await WaitAsync(() => shell.Solutions.Projects.Count == 2 && shell.Solutions.Progress is { Completed: true }, "the sweep's solution is evaluated again", 240).ConfigureAwait(true);
        }

        if (_sweepScenario == SweepScenario.Debug && shell.Debugger.State != DebugState.Stopped)
        {
            await StopSweepDebuggingAsync().ConfigureAwait(true);
        }

        return await EnterScenarioAsync(_sweepScenario).ConfigureAwait(true);
    }

    private async Task FocusSweepEditorAsync(bool select)
    {
        var document = await shell.OpenFileAsync(_sweepProgram).ConfigureAwait(true) ?? throw new SmokeFailure("Program.cs did not open.");
        shell.Layout.OpenDocument(document, focus: true);
        await WaitAsync(() => document.Controller is not null, "the editor for Program.cs appears").ConfigureAwait(true);
        _sweepDocument = document;
        _sweepEditor = document.Controller!;
        if (_sweepProgramText.Length == 0)
        {
            _sweepProgramText = _sweepEditor.Model.Text.Text;
        }

        MoveCaret(_sweepEditor, "Greeter.Greet(", "Greeter.Gr".Length);
        if (select)
        {
            var offset = _sweepEditor.Model.Text.Text.IndexOf("Greeter.Greet(", StringComparison.Ordinal);
            _sweepEditor.Editor.Select(offset, "Greeter".Length);
        }

        // The focus can move once more as a debug session stops or a window closes: ask again until it stays.
        for (var attempt = 1; ; attempt++)
        {
            window.Activate();
            _sweepEditor.Editor.TextArea.Focus();
            try
            {
                await WaitAsync(() => Equals(shell.Workbench.Context.GetValue("editorTextFocus"), true), "the editor has the focus", 3).ConfigureAwait(true);
                break;
            }
            catch (SmokeFailure) when (attempt < 4)
            {
                shell.Layout.OpenDocument(document, focus: true);
                await RenderedAsync().ConfigureAwait(true);
            }
        }
        if (select)
        {
            await WaitAsync(() => Equals(shell.Workbench.Context.GetValue("editorHasSelection"), true), "the editor has a selection", 10).ConfigureAwait(true);
        }
    }

    private async Task<bool> FocusExplorerAsync(SweepScenario scenario)
    {
        shell.Layout.ShowToolWindow(SolutionExplorerViewModel.ToolWindowId, focus: true);
        var kind = s_explorerKinds.FirstOrDefault(k => k.Scenario == scenario).Kind;

        // The folder and the file are the fixture's own (SweepFolder/Sweep.cs): commands rename, cut, exclude and delete them.
        ExplorerRowViewModel? Row() => kind switch
        {
            null => shell.Explorer.Rows.FirstOrDefault(r => SolutionExplorerViewModel.KindKey(r.Node.Kind) is not ("solution" or "project" or "folder" or "file")),
            "folder" => shell.Explorer.Rows.FirstOrDefault(r => SolutionExplorerViewModel.KindKey(r.Node.Kind) == kind && r.Name == "SweepFolder"),
            "file" => shell.Explorer.Rows.FirstOrDefault(r => SolutionExplorerViewModel.KindKey(r.Node.Kind) == kind && r.Name == "Sweep.cs"),
            _ => shell.Explorer.Rows.FirstOrDefault(r => SolutionExplorerViewModel.KindKey(r.Node.Kind) == kind),
        };

        // Revealing expands the tree as the project's items arrive; a command before may have removed them (from git again).
        var deadline = Stopwatch.StartNew();
        var restored = false;
        while (Row() is null && deadline.Elapsed < TimeSpan.FromSeconds(30))
        {
            shell.Explorer.Reveal("f:" + SweepExplorerFile);
            await Task.Delay(100).ConfigureAwait(true);
            if (!restored && deadline.Elapsed > TimeSpan.FromSeconds(5) && kind is "folder" or "file")
            {
                restored = true;
                Console.Out.WriteLine("  [sweep] SweepFolder is gone: restoring src/Hello from git");
                GitIn(_sweepFolder, "checkout", "--", Path.Combine("src", "Hello"));
            }
        }

        var row = Row();
        if (row is null)
        {
            Console.Out.WriteLine($"  [sweep] no Solution Explorer row for {scenario}");
            return false;
        }

        shell.Explorer.Selected = row;
        if (window.GetVisualDescendants().OfType<SolutionExplorerView>().FirstOrDefault()?.FindControl<ListBox>("List") is { } list)
        {
            list.Focus();
        }

        await WaitAsync(() => Equals(shell.Workbench.Context.GetValue("focusedView"), SolutionExplorerViewModel.ToolWindowId), "the Solution Explorer has the focus", 10).ConfigureAwait(true);
        return true;
    }

    private async Task StopSweepDebuggingAsync()
    {
        if (shell.Debugger.IsActive)
        {
            try
            {
                await shell.Debugger.StopAsync().WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is TimeoutException or InvalidOperationException)
            {
                Console.Out.WriteLine($"  [sweep] the debug session didn't stop: {ex.Message}");
            }
        }
    }

    // ------------------------------------------------------------------ one command

    private async Task SweepCommandAsync(CommandDescriptor command)
    {
        // The keys its enablement reads (and the focus, which key bindings read): the context has settled when they stay.
        var chosen = SweepContextWatch.Read(command.Enablement, shell.Workbench.Context);
        _sweepContext!.Watch(chosen.Select(p => p.Key).Concat(["focusedView", "editorTextFocus"]));
        var waited = false;
        async Task<string?> ReadyAsync()
        {
            var problem = await SweepReadyAsync(command, chosen, wait: !waited).ConfigureAwait(true);
            waited |= problem is not null;
            return problem;
        }

        // The menu: its items in the menus this context shows (checked), and one of them clicked.
        var ready = await ReadyAsync().ConfigureAwait(true);
        var menuRules = shell.Workbench.Menus.Rules.Where(r => r.Command == command.Id && r.Submenu is null && r.Location != MenuLocations.CommandPalette).ToList();
        var shown = menuRules
            .Where(r => SweepableLocation(r.Location, _sweepScenario) && MenuVisible(r))
            .OrderBy(r => r.Location.StartsWith(MenuLocations.MenuBarPrefix, StringComparison.Ordinal) ? 0 : 1)
            .ToList();
        if (menuRules.Count == 0)
        {
            Sweep(command.Id, "menu", "skip", "no menu item");
        }
        else if (shown.Count > 0)
        {
            // The other menus that show it here, and those whose when-clause hides it here: checked, not clicked.
            foreach (var other in menuRules.Where(r => r != shown[0] && SweepableLocation(r.Location, _sweepScenario)))
            {
                var visible = MenuVisible(other);
                string? problem;
                try
                {
                    problem = await ClickMenuAsync(command, other, click: false, visible).ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    problem = $"the sweep's menu check failed: {ex.Message}";
                }

                Sweep(command.Id, "menu", problem is null ? "pass" : "fail", problem ?? (visible ? "checked (another menu item was clicked)" : $"checked hidden (when {other.When.Text})"), other.Location,
                    known: s_sweepKnown.ContainsKey($"{command.Id}|menu"));
            }

            var rule = shown[0];
            await SweepRunAsync(command, "menu", rule.Location, () => ClickMenuAsync(command, rule, click: true, visible: true), ready).ConfigureAwait(true);
            await EnsureScenarioAsync().ConfigureAwait(true);
        }
        else
        {
            var where = string.Join(", ", menuRules.Select(r => $"{r.Location}{(r.When.Text == "true" ? string.Empty : $" when {r.When.Text}")}"));
            Sweep(command.Id, "menu", "skip", $"no menu item of it shows in the {_sweepScenario} context ({where})");
        }

        // The key binding the window would dispatch here.
        var rules = shell.Workbench.Keybindings.EffectiveRules.Where(r => r.Command == command.Id).ToList();
        ready = rules.Count > 0 ? await ReadyAsync().ConfigureAwait(true) : null;
        var applicable = rules.LastOrDefault(r => r.When.Evaluate(shell.Workbench.Context));
        if (rules.Count == 0)
        {
            Sweep(command.Id, "key", "skip", menuRules.Any(r => r.Keys is not null)
                ? $"the focused control handles its keys ({menuRules.First(r => r.Keys is not null).Keys}); no key binding"
                : "no key binding");
        }
        else if (applicable is null)
        {
            Sweep(command.Id, "key", "skip", $"no key binding of it applies in the {_sweepScenario} context ({string.Join(", ", rules.Select(r => $"{r.Keys} when {r.When.Text}"))})");
        }
        else
        {
            await SweepRunAsync(command, "key", applicable.Keys!.ToLabel(shell.Workbench.Platform), () => PressKeysAsync(applicable.Keys!), ready).ConfigureAwait(true);
            await EnsureScenarioAsync().ConfigureAwait(true);
        }

        // The palette.
        ready = command.ShowInPalette ? await ReadyAsync().ConfigureAwait(true) : null;
        if (!command.ShowInPalette)
        {
            Sweep(command.Id, "palette", "skip", "hidden from the palette");
        }
        else if (!shell.Workbench.Menus.IsVisibleInPalette(command.Id, shell.Workbench.Context))
        {
            Sweep(command.Id, "palette", "skip", "the palette hides it in this context (commandPalette when-clause)");
        }
        else if (ready is not null)
        {
            Sweep(command.Id, "palette", "skip", $"not enabled after its other entry points ran (enablement: {command.Enablement.Text}; {ready})");
        }
        else
        {
            await SweepRunAsync(command, "palette", null, () => PickFromPaletteAsync(command)).ConfigureAwait(true);
            await EnsureScenarioAsync().ConfigureAwait(true);
        }
    }

    /// <summary>Whether a menu item shows here: its when-clause, and those of the submenus it sits in, hold.</summary>
    private bool MenuVisible(MenuItemRule rule)
    {
        var context = shell.Workbench.Context;
        if (!rule.When.Evaluate(context))
        {
            return false;
        }

        for (var location = rule.Location; shell.Workbench.Menus.Rules.FirstOrDefault(r => r.Submenu == location) is { } parent; location = parent.Location)
        {
            if (!parent.When.Evaluate(context))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The menus the sweep opens in a context: the menu bar always, a context menu where its view has the focus.</summary>
    private static bool SweepableLocation(string location, SweepScenario scenario) =>
        location.StartsWith(MenuLocations.MenuBarPrefix, StringComparison.Ordinal)
        || (location.StartsWith(MenuLocations.EditorContext, StringComparison.Ordinal) && scenario is SweepScenario.Editor or SweepScenario.Selection or SweepScenario.Debug or SweepScenario.Last)
        || (location.StartsWith(MenuLocations.SolutionExplorerContext, StringComparison.Ordinal) && IsExplorer(scenario));

    private static bool IsExplorer(SweepScenario scenario) =>
        scenario is SweepScenario.ExplorerSolution or SweepScenario.ExplorerProject or SweepScenario.ExplorerFolder or SweepScenario.ExplorerFile or SweepScenario.ExplorerOther;

    /// <summary>Waits until what the last entry point started has reached the context, at most a few seconds.</summary>
    private async Task SettleSweepAsync()
    {
        if (!await _sweepContext!.SettleAsync(() => shell.Solutions.Progress is { Completed: false }, s_sweepSettleLimit).ConfigureAwait(true))
        {
            SweepProgress($"(the context didn't settle within {s_sweepSettleLimit.TotalSeconds:0} s)");
        }
    }

    /// <summary>
    /// Before an entry point: once the context has settled, the command is enabled, as when the sweep chose it in this
    /// context (waiting a moment for it with <paramref name="wait"/>). Null then; otherwise which keys its enablement reads
    /// changed since the sweep chose it (<paramref name="chosen"/>).
    /// </summary>
    private async Task<string?> SweepReadyAsync(CommandDescriptor command, IReadOnlyList<KeyValuePair<string, object?>> chosen, bool wait)
    {
        await SettleSweepAsync().ConfigureAwait(true);
        var clock = Stopwatch.StartNew();
        while (!command.Enablement.Evaluate(shell.Workbench.Context))
        {
            if (!wait || clock.Elapsed >= s_sweepEnabledWait)
            {
                return $"not enabled once the context settled; {SweepContextWatch.Differences(command.Enablement, chosen, shell.Workbench.Context)}";
            }

            await Task.Delay(50).ConfigureAwait(true);
            await SettleSweepAsync().ConfigureAwait(true);
        }

        return null;
    }

    /// <summary>
    /// Runs one entry point and records its row: the run, what it opened (closed again), errors and stalls.
    /// <paramref name="notReady"/>: why the command wasn't enabled before it ran (<see cref="SweepReadyAsync"/>), added to a failure.
    /// </summary>
    private async Task SweepRunAsync(CommandDescriptor command, string entry, string? location, Func<Task<string?>> trigger, string? notReady = null)
    {
        var executed = new TaskCompletionSource<CommandExecutedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        var others = new List<string>();
        var errors = new List<string>();
        var saw = new List<string>();
        var before = SweepBaseline.Capture(shell, window);
        var status = shell.StatusBar.DisplayMessage;
        SweepProgress($"{command.Id} [{entry}{(location is null ? string.Empty : " " + location)}] in {_sweepScenario}");

        void OnExecuted(object? sender, CommandExecutedEventArgs e)
        {
            if (e.CommandId == command.Id)
            {
                executed.TrySetResult(e);
            }
            else
            {
                lock (others)
                {
                    others.Add(e.CommandId);
                }
            }
        }

        void OnNotification(object? sender, Notification n)
        {
            lock (saw)
            {
                if (n.Severity == NotificationSeverity.Error)
                {
                    errors.Add(n.Message);
                }
                else
                {
                    saw.Add($"{n.Severity.ToString().ToLowerInvariant()}: {n.Message}");
                }
            }

            // A command may wait for its notification to be answered or closed (a plugin's ShowNotificationAsync does):
            // closing it answers none, as the user's ✕ does.
            Dispatcher.UIThread.Post(() => shell.Workbench.Notifications.Dismiss(n));
        }

        lock (_sweepUnhandled)
        {
            _sweepUnhandled.Clear();
        }

        shell.Workbench.Commands.Executed += OnExecuted;
        shell.Workbench.Notifications.Added += OnNotification;
        var clock = Stopwatch.StartNew();
        var enabled = SweepContextWatch.Read(command.Enablement, shell.Workbench.Context);
        _sweepStall!.Reset();
        var gcBefore = GC.GetTotalPauseDuration();
        string? problem = null;
        try
        {
            problem = await trigger().ConfigureAwait(true);
            var limit = SweepTimeout(command.Id);
            while (problem is null && !executed.Task.IsCompleted && clock.Elapsed < limit)
            {
                CloseSweepOverlays(saw);
                if (shell.StatusBar.DisplayMessage != status && shell.StatusBar.DisplayMessage?.Contains("is not available right now", StringComparison.Ordinal) == true)
                {
                    // Which keys changed between the trigger and the run says what took it away (a recurrence explains itself).
                    problem = $"it wasn't enabled when it ran ({shell.StatusBar.DisplayMessage}; {SweepContextWatch.Differences(command.Enablement, enabled, shell.Workbench.Context)})";
                    break;
                }

                lock (saw)
                {
                    if (errors.Count > 0 && clock.Elapsed > TimeSpan.FromSeconds(2))
                    {
                        break; // the command failed before (or without) running its handler
                    }
                }

                await Task.Delay(30).ConfigureAwait(true);
            }

            if (problem is null && !executed.Task.IsCompleted && errors.Count == 0)
            {
                lock (others)
                {
                    problem = entry == "key" && others.Count > 0
                        ? $"the key ran {string.Join(", ", others.Distinct())} instead"
                        : $"it didn't finish within {limit.TotalSeconds:0} s{(others.Count > 0 ? $" (it ran {string.Join(", ", others.Distinct())})" : string.Empty)}";
                }
            }

            // Close whatever it opened, and give late dialogs a moment.
            for (var i = 0; i < 5; i++)
            {
                CloseSweepOverlays(saw);
                await Task.Delay(20).ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            problem = $"the sweep's {entry} failed: {ex.Message}";
        }
        finally
        {
            shell.Workbench.Commands.Executed -= OnExecuted;
            shell.Workbench.Notifications.Added -= OnNotification;
        }

        var milliseconds = clock.Elapsed.TotalMilliseconds;
        var stall = _sweepStall.MaxMilliseconds;
        var gcPause = (GC.GetTotalPauseDuration() - gcBefore).TotalMilliseconds;
        if (problem is null && executed.Task.IsCompleted && executed.Task.Result.Error is { } error)
        {
            problem = $"the command threw {error.GetType().Name}: {error.Message}";
        }

        lock (saw)
        {
            if (s_sweepExpectedErrors.TryGetValue(command.Id, out var expectedError))
            {
                foreach (var expected in errors.Where(e => e.Contains(expectedError.Text, StringComparison.Ordinal)).ToList())
                {
                    errors.Remove(expected);
                    saw.Add($"expected error ({expectedError.Why}): {expected}");
                }
            }

            if (problem is null && errors.Count > 0)
            {
                problem = "error notification: " + string.Join(" | ", errors);
            }
        }

        lock (_sweepUnhandled)
        {
            if (problem is null && _sweepUnhandled.Count > 0)
            {
                problem = "unhandled exception: " + string.Join(" | ", _sweepUnhandled);
            }
        }

        var firstRun = _sweepRan.Add(command.Id);
        var stallLimit = SweepStallLimit(firstRun);
        if (problem is null && stall > stallLimit.TotalMilliseconds)
        {
            problem = $"the UI thread stalled for {stall:0} ms (over {stallLimit.TotalMilliseconds:0} ms for its {(firstRun ? "first" : "a later")} run; the GC paused {gcPause:0} ms of the run; it ran {_sweepStall.JobOfMax ?? "unsampled"})";
        }

        var unclosed = await CleanUpSweepAsync(before, saw).ConfigureAwait(true);
        if (problem is null && unclosed is not null)
        {
            problem = unclosed;
        }

        if (problem is not null && notReady is not null)
        {
            problem += $" (before it ran: {notReady})";
        }

        string? screenshot = null;
        if (problem is not null && _sweepScreenshots++ < 80)
        {
            screenshot = $"smoke-test-sweep-{Slug(command.Id)}-{entry}.png";
            Screenshot(screenshot);
        }

        var known = s_sweepKnown.ContainsKey($"{command.Id}|{entry}") || s_sweepKnown.ContainsKey($"{command.Id}|*");
        lock (saw)
        {
            Sweep(command.Id, entry, problem is null ? "pass" : "fail", problem, location, milliseconds, stall, saw.Count > 0 ? string.Join(" | ", saw.Distinct()) : null, screenshot, known, gcPause);
        }
    }

    private static TimeSpan SweepTimeout(string id) =>
        id.StartsWith("build.", StringComparison.Ordinal) || id.StartsWith("run.", StringComparison.Ordinal) || id.StartsWith("debug.", StringComparison.Ordinal)
        || id.StartsWith("testing.", StringComparison.Ordinal) || id.StartsWith("profiler.", StringComparison.Ordinal) || id.StartsWith("packages.", StringComparison.Ordinal)
        || id.StartsWith("project.", StringComparison.Ordinal) || id.StartsWith("ef.", StringComparison.Ordinal)
            ? TimeSpan.FromSeconds(180)
            : TimeSpan.FromSeconds(20);

    // ------------------------------------------------------------------ entry points

    /// <summary>
    /// Checks the command's item in the menu bar (in-window or native) or a context menu — there exactly when its
    /// when-clause holds (<paramref name="visible"/>), enabled as the command, with the binding's keys — and clicks it.
    /// </summary>
    private async Task<string?> ClickMenuAsync(CommandDescriptor command, MenuItemRule rule, bool click, bool visible)
    {
        var context = shell.Workbench.Context;
        var expectedEnabled = command.Enablement.Evaluate(context);
        var binding = shell.Workbench.Keybindings.GetBinding(command.Id) ?? (rule.Keys is { } hint ? Codalus.Platform.KeySequence.Parse(hint, shell.Workbench.Platform) : null);
        var expected = new SweepMenuExpectation(command, rule, expectedEnabled, binding?.ToLabel(shell.Workbench.Platform), binding, click, visible);

        if (rule.Location.StartsWith(MenuLocations.MenuBarPrefix, StringComparison.Ordinal))
        {
            return shell.Workbench.Platform == PlatformKind.MacOS
                ? await ClickNativeMenuAsync(expected).ConfigureAwait(true)
                : await ClickMenuBarAsync(expected).ConfigureAwait(true);
        }

        return await ClickContextMenuAsync(expected).ConfigureAwait(true);
    }

    private sealed record SweepMenuExpectation(CommandDescriptor Command, MenuItemRule Rule, bool Enabled, string? Label, Codalus.Platform.KeySequence? Binding, bool Click, bool Visible)
    {
        /// <summary>What's wrong with the rendered entry (null when it's as expected), before any click.</summary>
        public string? Problem(string where, bool found, bool enabled, string? keys)
        {
            if (!Visible)
            {
                return found ? $"{where} shows {Command.Title}, but its when-clause doesn't hold here ({Rule.When.Text})" : null;
            }

            if (!found)
            {
                return $"{where} has no {Command.Title} item";
            }

            if (enabled != Enabled)
            {
                return $"the menu item is {(enabled ? "enabled" : "disabled")}, but the command is {(Enabled ? "enabled" : "disabled")} ({Command.Enablement.Text})";
            }

            if ((keys ?? string.Empty) != (Label ?? string.Empty))
            {
                return $"the menu item shows the keys '{keys}', the binding is '{Label}'";
            }

            return Click && !Enabled ? "disabled in this context (not clicked)" : null;
        }
    }

    /// <summary>The locations from a menu bar menu down to <paramref name="location"/>, with their titles.</summary>
    private List<(string Location, string Title)> MenuPath(string location)
    {
        var path = new List<(string, string)>();
        var current = location;
        while (shell.Workbench.Menus.Rules.FirstOrDefault(r => r.Submenu == current) is { } parent)
        {
            path.Insert(0, (current, parent.Title ?? current));
            current = parent.Location;
        }

        var top = shell.Workbench.Menus.MenuBar.FirstOrDefault(m => MenuLocations.MenuBar(m.Id) == current);
        path.Insert(0, (current, (top?.Title ?? current).Replace("_", string.Empty, StringComparison.Ordinal)));
        return path;
    }

    private async Task<string?> ClickMenuBarAsync(SweepMenuExpectation expected)
    {
        var bar = window.GetVisualDescendants().OfType<Menu>().FirstOrDefault(m => m.Name == "MenuBar") ?? throw new SmokeFailure("no menu bar");
        var path = MenuPath(expected.Rule.Location);
        var where = string.Join(" ▸ ", path.Select(p => p.Title));
        var top = bar.Items.OfType<MenuItem>().FirstOrDefault(i => AutomationProperties.GetName(i) == path[0].Title);
        if (top is null)
        {
            return expected.Problem(where, found: false, enabled: false, keys: null);
        }

        var opened = new List<MenuItem> { top };
        try
        {
            top.Open();
            await RenderedAsync().ConfigureAwait(true);
            var items = top.Items;
            foreach (var (_, title) in path.Skip(1))
            {
                var sub = items.OfType<MenuItem>().FirstOrDefault(i => AutomationProperties.GetName(i) == title && i.ItemCount > 0);
                if (sub is null)
                {
                    return expected.Problem(where, found: false, enabled: false, keys: null);
                }

                sub.Open();
                opened.Add(sub);
                await RenderedAsync().ConfigureAwait(true);
                items = sub.Items;
            }

            var entry = FindRendered(items.OfType<MenuItem>().ToList(), expected.Rule, expected.Command);
            if (expected.Problem(where, entry is not null, entry?.IsEnabled ?? false, entry is null ? null : AutomationProperties.GetAcceleratorKey(entry)) is { } problem)
            {
                return problem;
            }

            if (expected.Click && entry is not null)
            {
                entry.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            }

            return null;
        }
        finally
        {
            foreach (var item in Enumerable.Reverse(opened))
            {
                item.Close();
            }
        }
    }

    /// <summary>
    /// The rendered entry of <paramref name="rule"/>: the location's items in order, separators left out; by its title,
    /// since a menu may show one command twice under when-clauses of its own (Start with Hot Reload, Delete and Remove).
    /// </summary>
    private MenuItem? FindRendered(List<MenuItem> rendered, MenuItemRule rule, CommandDescriptor command)
    {
        var title = MenuTitle(rule, command);
        var model = shell.Workbench.Menus.GetItems(rule.Location).ToList();
        var index = model.FindIndex(m => m.Command == command.Id && m.Submenu is null && m.Title == title);
        return rendered.Count == model.Count && index >= 0 ? rendered[index] : rendered.FirstOrDefault(i => AutomationProperties.GetName(i) == title);
    }

    private string MenuTitle(MenuItemRule rule, CommandDescriptor command) =>
        rule.Title ?? shell.Workbench.Commands.Find(command.Id)?.Title ?? command.Title;

    private async Task<string?> ClickNativeMenuAsync(SweepMenuExpectation expected)
    {
        // macOS refills a menu as it opens (NativeMenu.Opening, which only the platform raises); the menus' Changed
        // event rebuilds them all from the current context, the same items.
        shell.Workbench.Menus.Customizations = shell.Workbench.Menus.Customizations;
        await RenderedAsync().ConfigureAwait(true);
        var native = NativeMenu.GetMenu(window) ?? throw new SmokeFailure("the window has no native menu");
        var path = MenuPath(expected.Rule.Location);
        var where = "the global menu's " + string.Join(" ▸ ", path.Select(p => p.Title));
        var items = native.Items;
        foreach (var (_, header) in path)
        {
            if (items.OfType<NativeMenuItem>().FirstOrDefault(i => i.Header == header && i.Menu is not null) is not { Menu: { } menu })
            {
                return expected.Problem(where, found: false, enabled: false, keys: null);
            }

            items = menu.Items;
        }

        var title = MenuTitle(expected.Rule, expected.Command);
        var model = shell.Workbench.Menus.GetItems(expected.Rule.Location).ToList();
        var rendered = items.OfType<NativeMenuItem>().ToList();
        var index = model.FindIndex(m => m.Command == expected.Command.Id && m.Submenu is null && m.Title == title);
        var entry = rendered.Count == model.Count && index >= 0 ? rendered[index] : rendered.FirstOrDefault(i => i.Header == title);

        // The native menu shows single-chord gestures only (MenuBuilder.FillNative).
        var gesture = expected.Binding is { Chords.Count: 1 } ? KeyMapper.ToGesture(expected.Binding.Chords[0])?.ToString() : null;
        var shown = entry?.Gesture?.ToString();
        if (expected.Problem(where, entry is not null, entry?.IsEnabled ?? false, shown == gesture ? expected.Label : shown) is { } problem)
        {
            return problem;
        }

        if (expected.Click && entry is not null)
        {
            ((INativeMenuItemExporterEventsImplBridge)entry).RaiseClicked(); // what macOS does on a click
        }

        return null;
    }

    /// <summary>Opens the editor's or the Solution Explorer's context menu as a right-click does, and checks (and clicks) the item.</summary>
    private async Task<string?> ClickContextMenuAsync(SweepMenuExpectation expected)
    {
        _sweepContextMenu = null;
        Control target;
        if (expected.Rule.Location.StartsWith(MenuLocations.EditorContext, StringComparison.Ordinal))
        {
            target = _sweepEditor?.Editor.TextArea ?? throw new SmokeFailure("no editor for its context menu");
        }
        else
        {
            target = window.GetVisualDescendants().OfType<SolutionExplorerView>().FirstOrDefault()?.FindControl<ListBox>("List")
                     ?? throw new SmokeFailure("no Solution Explorer for its context menu");
        }

        target.RaiseEvent(new ContextRequestedEventArgs { RoutedEvent = Control.ContextRequestedEvent, Source = target });
        await WaitAsync(() => _sweepContextMenu is { IsOpen: true }, "the context menu opens", 10).ConfigureAwait(true);
        var menu = _sweepContextMenu!;
        try
        {
            var path = new List<string>();
            var current = expected.Rule.Location;
            while (current.Count(c => c == '/') > 1 && shell.Workbench.Menus.Rules.FirstOrDefault(r => r.Submenu == current) is { } parent)
            {
                path.Insert(0, parent.Title ?? current);
                current = parent.Location;
            }

            var where = $"the context menu ({expected.Rule.Location})";
            var items = menu.Items;
            foreach (var title in path)
            {
                var sub = items.OfType<MenuItem>().FirstOrDefault(i => AutomationProperties.GetName(i) == title && i.ItemCount > 0);
                if (sub is null)
                {
                    return expected.Problem(where, found: false, enabled: false, keys: null);
                }

                sub.Open();
                await RenderedAsync().ConfigureAwait(true);
                items = sub.Items;
            }

            var entry = FindRendered(items.OfType<MenuItem>().ToList(), expected.Rule, expected.Command);
            if (expected.Problem(where, entry is not null, entry?.IsEnabled ?? false, entry is null ? null : AutomationProperties.GetAcceleratorKey(entry)) is { } problem)
            {
                return problem;
            }

            if (expected.Click && entry is not null)
            {
                entry.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            }

            return null;
        }
        finally
        {
            menu.Close();
        }
    }

    /// <summary>Presses the binding's chords on the focused control, through the window's key handling.</summary>
    private async Task<string?> PressKeysAsync(Codalus.Platform.KeySequence keys)
    {
        foreach (var chord in keys.Chords)
        {
            if (KeyMapper.ToGesture(chord) is not { } gesture)
            {
                return $"the binding {keys} has no key gesture on this platform";
            }

            var target = window.FocusManager?.GetFocusedElement() as Avalonia.Interactivity.Interactive ?? window;
            target.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = gesture.Key, KeyModifiers = gesture.KeyModifiers, Source = target });
            target.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyUpEvent, Key = gesture.Key, KeyModifiers = gesture.KeyModifiers, Source = target });
            await Task.Yield();
        }

        return null;
    }

    /// <summary>Opens the palette, types the command's title and picks its row.</summary>
    private async Task<string?> PickFromPaletteAsync(CommandDescriptor command)
    {
        shell.ShowCommandPalette();
        await WaitAsync(() => shell.Palette.IsOpen, "the command palette opens", 10).ConfigureAwait(true);
        shell.Palette.Query = command.DisplayTitle;
        var deadline = Stopwatch.StartNew();
        QuickPickRow? row = null;
        while (deadline.Elapsed < TimeSpan.FromSeconds(3) && (row = shell.Palette.Rows.FirstOrDefault(r => Equals(r.Item.Tag, command.Id))) is null)
        {
            await Task.Delay(30).ConfigureAwait(true);
        }

        if (row is null)
        {
            shell.Palette.Cancel();
            return $"the palette doesn't list it under '{command.DisplayTitle}'";
        }

        shell.Palette.Accept(row);
        return null;
    }

    // ------------------------------------------------------------------ what a command opens

    /// <summary>Cancels the dialogs, pickers and other windows a command opened, noting each.</summary>
    private void CloseSweepOverlays(List<string> saw)
    {
        void Note(string what)
        {
            lock (saw)
            {
                saw.Add(what);
            }
        }

        if (shell.Dialogs.IsOpen)
        {
            Note($"dialog: {shell.Dialogs.Title}");
            shell.Dialogs.Cancel();
        }

        // Report a Problem and Suggest a Feature (M51) only open their dialog: the browser opens on its button, never pressed here.
        if (shell.Feedback.IsOpen)
        {
            Note($"dialog: {shell.Feedback.Title}");
            shell.Feedback.Cancel();
        }

        if (shell.Palette.IsOpen)
        {
            Note("picker");
            shell.Palette.Cancel();
        }

        if (shell.FindReplace.IsOpen)
        {
            Note("Find and Replace");
            shell.FindReplace.Close();
        }

        if (shell.CodeSearch.IsOpen)
        {
            Note("Code Search");
            shell.CodeSearch.Close();
        }

        if (shell.Switcher.IsOpen)
        {
            Note("document switcher");
            shell.Switcher.Close();
        }

        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            foreach (var other in desktop.Windows.Where(w => w != window && w.IsVisible).ToList())
            {
                Note($"window: {other.Title ?? other.GetType().Name}");
                other.Close();
            }
        }

        if (_sweepContextMenu is { IsOpen: true } menu)
        {
            menu.Close();
        }

        if (shell.Window is SweepWindow stand && stand.TakeRequests() is { Count: > 0 } requests)
        {
            foreach (var request in requests)
            {
                Note(request);
            }
        }
    }

    /// <summary>
    /// Puts back what the command changed: tool windows and documents it opened, the editor's text, running programs,
    /// full screen. Returns a problem when something it opened doesn't close.
    /// </summary>
    private async Task<string?> CleanUpSweepAsync(SweepBaseline before, List<string> saw)
    {
        string? problem = null;
        foreach (var id in shell.Layout.OpenToolWindows.Select(t => t.Id).Except(before.ToolWindows).ToList())
        {
            lock (saw)
            {
                saw.Add($"tool window: {id}");
            }

            shell.Layout.CloseToolWindow(id);
            if (shell.Layout.IsVisible(id))
            {
                problem ??= $"the tool window {id} it opened didn't close";
            }
        }

        foreach (var document in shell.Layout.Documents.Except(before.Documents).ToList())
        {
            lock (saw)
            {
                saw.Add($"document: {document.Title}");
            }

            if (document is CodeEditorDocument { Controller: { } controller } && controller.Model.IsModified)
            {
                while (controller.Editor.CanUndo)
                {
                    controller.Editor.Undo();
                }
            }

            shell.Layout.CloseDocument(document);
            for (var i = 0; i < 10 && shell.Layout.Documents.Contains(document); i++)
            {
                CloseSweepOverlays(saw);
                await Task.Delay(30).ConfigureAwait(true);
            }

            if (shell.Layout.Documents.Contains(document))
            {
                problem ??= $"the document {document.Title} it opened didn't close";
            }
        }

        // Program.cs as it was (an edit command changed it; a save wrote it).
        if (_sweepEditor is { } editor && _sweepDocument is { } open && shell.Layout.Documents.Contains(open) && _sweepProgramText.Length > 0 && editor.Model.Text.Text != _sweepProgramText)
        {
            editor.Model.Text.Text = _sweepProgramText;
            await shell.SaveAllAsync().ConfigureAwait(true);
        }

        if (shell.Runs.Running.Count > before.Running)
        {
            lock (saw)
            {
                saw.Add("a program ran");
            }

            // The next command starts with it ended (its tree's end can take seconds, without holding the UI thread).
            await shell.StopRunningAsync().ConfigureAwait(true);
            for (var i = 0; i < 100 && shell.Runs.Running.Count > before.Running; i++)
            {
                await Task.Delay(50).ConfigureAwait(true);
            }
        }

        if (_sweepScenario != SweepScenario.Debug && shell.Debugger.IsActive)
        {
            lock (saw)
            {
                saw.Add("a debug session");
            }

            await StopSweepDebuggingAsync().ConfigureAwait(true);
        }

        if (shell.Window?.IsFullScreen == true)
        {
            shell.Window.IsFullScreen = false;
        }

        // Set as Startup Project chose another one: the next commands (Start Debugging) need Hello's.
        if (before.StartupProject is { } startup && shell.Runs.StartupProject != startup)
        {
            lock (saw)
            {
                saw.Add($"startup project: {Path.GetFileNameWithoutExtension(shell.Runs.StartupProject)}");
            }

            shell.SetStartupProject(startup);
        }

        if (shell.Runs.MultipleStartup != before.MultipleStartup || !shell.Runs.StartupEntries.SequenceEqual(before.StartupEntries))
        {
            lock (saw)
            {
                saw.Add($"startup projects: {(shell.Runs.MultipleStartup ? "multiple" : "single")}");
            }

            shell.Runs.SetStartupProjects(before.MultipleStartup, before.StartupEntries, before.StartupProject);
        }

        await RestoreSweepSettingsAsync(before.UserSettings, saw).ConfigureAwait(true);

        CloseSweepOverlays(saw);
        return problem;
    }

    private sealed record SweepBaseline(HashSet<string> ToolWindows, HashSet<ShellDocument> Documents, int Running, string? StartupProject, string? UserSettings,
        bool MultipleStartup, IReadOnlyList<Workbench.Build.StartupEntry> StartupEntries)
    {
        public static SweepBaseline Capture(ShellHost shell, Window window) =>
            new([.. shell.Layout.OpenToolWindows.Select(t => t.Id)], [.. shell.Layout.Documents], shell.Runs.Running.Count, shell.Runs.StartupProject,
                File.Exists(shell.Paths.SettingsFile) ? File.ReadAllText(shell.Paths.SettingsFile) : null, shell.Runs.MultipleStartup, shell.Runs.StartupEntries);
    }

    /// <summary>Puts back the user settings a command changed (a toggle, a theme): the next commands run with the sweep's.</summary>
    private async Task RestoreSweepSettingsAsync(string? before, List<string> saw)
    {
        var after = File.Exists(shell.Paths.SettingsFile) ? File.ReadAllText(shell.Paths.SettingsFile) : null;
        if (after == before)
        {
            return;
        }

        static Dictionary<string, JsonElement> Read(string? text)
        {
            try
            {
                using var document = JsonDocument.Parse(text ?? "{}", new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                return document.RootElement.ValueKind == JsonValueKind.Object
                    ? document.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal)
                    : [];
            }
            catch (JsonException)
            {
                return [];
            }
        }

        var old = Read(before);
        var now = Read(after);
        foreach (var key in old.Keys.Union(now.Keys).Where(k => !old.TryGetValue(k, out var a) || !now.TryGetValue(k, out var b) || a.GetRawText() != b.GetRawText()).ToList())
        {
            lock (saw)
            {
                saw.Add($"setting: {key}");
            }

            await shell.Workbench.Settings.SetAsync(key, old.TryGetValue(key, out var value) ? value : null, SettingScope.User, CancellationToken.None).ConfigureAwait(true);
        }
    }

    private void OnSweepUnhandled(object? sender, DispatcherUnhandledExceptionEventArgs e)
    {
        if (SafeClipboard.IsBusy(e.Exception))
        {
            return; // another program holds the clipboard: the app says so on the status bar (App.OnUnhandledException)
        }

        lock (_sweepUnhandled)
        {
            var frame = e.Exception.StackTrace?.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Contains("Codalus.", StringComparison.Ordinal));
            _sweepUnhandled.Add($"{e.Exception.GetType().Name}: {e.Exception.Message}{(frame is null ? string.Empty : " " + frame)}");
        }
    }

    private void OnSweepUnobserved(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        if (SafeClipboard.IsBusy(e.Exception))
        {
            return; // the editor's Copy with a busy clipboard: the app says so (App.OnUnobservedTaskException)
        }

        lock (_sweepUnhandled)
        {
            // Found when the task is collected: it may be an earlier command's.
            var inner = e.Exception.InnerException ?? e.Exception;
            var frame = inner.StackTrace?.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Contains("Codalus.", StringComparison.Ordinal));
            _sweepUnhandled.Add($"unobserved (from this or an earlier command) {inner.GetType().Name}: {inner.Message}{(frame is null ? string.Empty : " " + frame)}");
        }
    }

    // ------------------------------------------------------------------ report

    private void Sweep(string command, string entry, string outcome, string? reason, string? location = null, double milliseconds = 0, double stall = 0, string? saw = null, string? screenshot = null, bool known = false, double gcPause = 0)
    {
        var row = new SweepRow(command, entry, outcome, reason)
        {
            GcPauseMilliseconds = Math.Round(gcPause),
            Location = location,
            Scenario = _sweepScenario.ToString(),
            Milliseconds = Math.Round(milliseconds),
            StallMilliseconds = Math.Round(stall),
            Saw = saw,
            Screenshot = screenshot,
            Known = known,
        };
        _sweepRows.Add(row);
        if (outcome == "fail")
        {
            Console.Out.WriteLine($"    FAIL {command} [{entry}{(location is null ? string.Empty : " " + location)}]: {reason}{(known ? " (known)" : string.Empty)}");
        }
    }

    /// <summary>
    /// A line in <c>command-sweep-progress.log</c> before each run: appended and closed at once, so after a crash its last
    /// line names what was running.
    /// </summary>
    private void SweepProgress(string what)
    {
        try
        {
            Directory.CreateDirectory(shell.Paths.Logs);
            File.AppendAllText(Path.Combine(shell.Paths.Logs, "command-sweep-progress.log"), $"{DateTime.Now:HH:mm:ss.fff} {what}{Environment.NewLine}");
        }
        catch (IOException)
        {
        }
    }

    private void WriteSweepReport(TimeSpan elapsed)
    {
        Directory.CreateDirectory(shell.Paths.Logs);
        var platform = shell.Workbench.Platform.ToString();
        using var stream = File.Create(Path.Combine(shell.Paths.Logs, "command-sweep.json"));
        using var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        json.WriteStartObject();
        json.WriteString("version", ShellInfo.Version);
        json.WriteString("platform", platform);
        json.WriteString("os", $"{System.Runtime.InteropServices.RuntimeInformation.OSDescription} ({System.Runtime.InteropServices.RuntimeInformation.OSArchitecture})");
        json.WriteNumber("seconds", Math.Round(elapsed.TotalSeconds, 1));
        json.WriteNumber("commands", _sweepRows.Select(r => r.Command).Distinct().Count());
        json.WriteStartObject("totals");
        foreach (var group in _sweepRows.GroupBy(r => r.Entry).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            json.WriteStartObject(group.Key);
            json.WriteNumber("pass", group.Count(r => r.Outcome == "pass"));
            json.WriteNumber("fail", group.Count(r => r.Outcome == "fail"));
            json.WriteNumber("skip", group.Count(r => r.Outcome == "skip"));
            json.WriteEndObject();
        }

        json.WriteEndObject();
        json.WriteStartArray("rows");
        foreach (var r in _sweepRows)
        {
            json.WriteStartObject();
            json.WriteString("command", r.Command);
            json.WriteString("entry", r.Entry);
            json.WriteString("platform", platform);
            json.WriteString("outcome", r.Outcome);
            Write("reason", r.Reason);
            Write("location", r.Location);
            Write("scenario", r.Scenario);
            json.WriteNumber("ms", r.Milliseconds);
            json.WriteNumber("stallMs", r.StallMilliseconds);
            if (r.GcPauseMilliseconds > 0)
            {
                json.WriteNumber("gcPauseMs", r.GcPauseMilliseconds);
            }

            Write("saw", r.Saw);
            Write("screenshot", r.Screenshot);
            if (r.Known)
            {
                json.WriteBoolean("known", true);
            }

            json.WriteEndObject();
        }

        json.WriteEndArray();
        json.WriteEndObject();

        void Write(string name, string? value)
        {
            if (value is not null)
            {
                json.WriteString(name, value);
            }
        }

        var counts = _sweepRows.GroupBy(r => r.Outcome).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        Console.Out.WriteLine($"  [sweep] {_sweepRows.Select(r => r.Command).Distinct().Count()} commands, {counts.GetValueOrDefault("pass")} passed, {counts.GetValueOrDefault("fail")} failed, {counts.GetValueOrDefault("skip")} skipped, in {elapsed.TotalSeconds:0} s");
    }

    // ------------------------------------------------------------------ stand-ins

    /// <summary>
    /// The main window, but native file pickers answer Cancel and closing the window is refused: the sweep runs every
    /// command that asks for a file or folder without a dialog it can't answer, and none that would close the app.
    /// </summary>
    private sealed class SweepWindow(IShellWindow? inner, Workbench.Output.OutputChannel log) : IShellWindow
    {
        private readonly List<string> _requests = [];

        public List<string> TakeRequests()
        {
            lock (_requests)
            {
                var taken = _requests.ToList();
                _requests.Clear();
                return taken;
            }
        }

        private void Note(string what)
        {
            lock (_requests)
            {
                _requests.Add(what);
            }

            log.AppendLine($"[sweep] {what} (answered Cancel)");
        }

        public Avalonia.Input.Platform.IClipboard? Clipboard => inner?.Clipboard;

        public bool IsFullScreen
        {
            get => inner?.IsFullScreen ?? false;
            set
            {
                if (inner is not null)
                {
                    inner.IsFullScreen = value;
                }
            }
        }

        public void FocusPane(bool backwards) => inner?.FocusPane(backwards);

        public void RequestClose() => Note("close the window");

        public void BringToFront() => inner?.BringToFront();

        public Task<string?> PickSolutionAsync()
        {
            Note("file picker: a solution");
            return Task.FromResult<string?>(null);
        }

        public Task<string?> PickProjectAsync()
        {
            Note("file picker: a project");
            return Task.FromResult<string?>(null);
        }

        public Task<string?> PickFileAsync(string title, string filterName, string[] patterns)
        {
            Note($"file picker: {title}");
            return Task.FromResult<string?>(null);
        }

        public Task<string?> SaveFileAsync(string title, string suggestedName, string filterName, string[] patterns)
        {
            Note($"save picker: {title}");
            return Task.FromResult<string?>(null);
        }

        public Task<IReadOnlyList<string>> PickFilesAsync(string title)
        {
            Note($"file picker: {title}");
            return Task.FromResult<IReadOnlyList<string>>([]);
        }

        public Task<string?> PickFolderAsync(string title)
        {
            Note($"folder picker: {title}");
            return Task.FromResult<string?>(null);
        }
    }

    /// <summary>
    /// Measures how long the UI thread takes to answer, every 20 ms, from a thread of its own: its longest wait is the
    /// stall. The answer's time is read on the UI thread as it runs the probe, and the monitor waits on its own thread:
    /// an await on the thread pool timed the pool's queue too, so a pool busy with blocking work (ending a process tree,
    /// waiting for a program's output) read as a stalled UI thread. HotReloadRestartTests saw 897 ms that way under load,
    /// while a monitor on its own thread saw 57 ms in the same run. While it waits, it notes every 100 ms which posted job
    /// the UI thread is running (<see cref="Ui.RunningJob"/>), to name what the longest wait was spent on.
    /// </summary>
    internal sealed class SweepStallMonitor
    {
        private readonly Lock _gate = new();
        private volatile bool _stopped;
        private long _maxTicks;
        private TimeSpan _maxGcPause;
        private string? _maxJob;
        private int _probes;

        public SweepStallMonitor()
        {
            Ui.Watch();
            new Thread(Loop) { IsBackground = true, Name = "Sweep stall monitor" }.Start();
        }

        public double MaxMilliseconds
        {
            get
            {
                lock (_gate)
                {
                    return _maxTicks * 1000.0 / Stopwatch.Frequency;
                }
            }
        }

        /// <summary>How long the garbage collector paused the process during the longest wait: a part of it that isn't the UI's code.</summary>
        public double GcPauseOfMaxMilliseconds
        {
            get
            {
                lock (_gate)
                {
                    return _maxGcPause.TotalMilliseconds;
                }
            }
        }

        /// <summary>
        /// What the UI thread ran during the longest wait: the posted job it was in at most of the samples, by the file and
        /// line that posted it, or none (a layout, rendering, input, or code not posted through <see cref="Ui.Post"/>,
        /// such as a command's own); null for a wait too short to sample.
        /// </summary>
        public string? JobOfMax
        {
            get
            {
                lock (_gate)
                {
                    return _maxJob;
                }
            }
        }

        /// <summary>The probes the UI thread answered since the last <see cref="Reset"/>: none means the monitor saw nothing, not a quick UI thread.</summary>
        public int Probes
        {
            get
            {
                lock (_gate)
                {
                    return _probes;
                }
            }
        }

        public void Reset()
        {
            lock (_gate)
            {
                _probes = 0;
                _maxTicks = 0;
                _maxGcPause = TimeSpan.Zero;
                _maxJob = null;
            }
        }

        private void Loop()
        {
            while (!_stopped)
            {
                var start = Stopwatch.GetTimestamp();
                var gcBefore = GC.GetTotalPauseDuration();
                long answered = 0;
                Task probe;
                try
                {
                    probe = Dispatcher.UIThread.InvokeAsync(() => Interlocked.Exchange(ref answered, Stopwatch.GetTimestamp()), DispatcherPriority.Send).GetTask();
                }
                catch (InvalidOperationException)
                {
                    return; // the dispatcher has shut down
                }

                Dictionary<string, int>? jobs = null;
                var samples = 0;
                try
                {
                    while (!probe.Wait(100))
                    {
                        if (_stopped)
                        {
                            return;
                        }

                        jobs ??= new(StringComparer.Ordinal);
                        var job = Ui.RunningJob is { } posted ? "the job posted at " + posted : "no posted job";
                        jobs[job] = jobs.GetValueOrDefault(job) + 1;
                        samples++;
                    }
                }
                catch (AggregateException)
                {
                    return; // the dispatcher has shut down
                }

                var ticks = Interlocked.Read(ref answered) - start;
                var gcPause = GC.GetTotalPauseDuration() - gcBefore;
                lock (_gate)
                {
                    _probes++;
                    if (ticks > _maxTicks)
                    {
                        _maxTicks = ticks;
                        _maxGcPause = gcPause;
                        _maxJob = jobs?.MaxBy(j => j.Value) is { } most ? $"{most.Key} ({most.Value} of {samples} samples)" : null;
                    }
                }

                Thread.Sleep(20);
            }
        }

        public void Stop()
        {
            if (!_stopped)
            {
                _stopped = true;
                Ui.Unwatch();
            }
        }
    }
}
