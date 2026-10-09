using DaedalusStudio.Shell.ViewModels;
using DaedalusStudio.Workbench.Commands;
using DaedalusStudio.Workbench.Context;
using DaedalusStudio.Workbench.Menus;
using DaedalusStudio.Workbench.Settings;
using DaedalusStudio.Workbench.Testing;
using DaedalusStudio.Workbench.ToolWindows;

namespace DaedalusStudio.Shell;

/// <summary>The Test Explorer, the Test menu and their commands and key bindings (ADR 0010).</summary>
internal static partial class BuiltIns
{
    /// <summary>Visual Studio's test chords (Ctrl+R, …) and Ctrl+E, T for the Test Explorer.</summary>
    public const string TestingKeybindings = """
        [
          { "key": "primary+r a", "command": "testing.runAll" },
          { "key": "primary+r primary+a", "command": "testing.debugAll" },
          { "key": "primary+r t", "command": "testing.runAtCursor", "when": "editorTextFocus" },
          { "key": "primary+r primary+t", "command": "testing.debugAtCursor", "when": "editorTextFocus" },
          { "key": "primary+r l", "command": "testing.repeatLast" },
          { "key": "primary+r f", "command": "testing.runFailed" },
          { "key": "primary+e t", "command": "workbench.view.testExplorer" }
        ]
        """;

    public static ToolWindowDescriptor TestToolWindow() =>
        new(TestExplorerViewModel.ToolWindowId, "Test Explorer", ToolWindowLocation.Left, ToolWindowKind.BuiltIn, ContributionSource.Shell) { Icon = "beaker" };

    public static ToolWindowDescriptor CoverageToolWindow() =>
        new(CoverageResultsViewModel.ToolWindowId, "Coverage Results", ToolWindowLocation.Bottom, ToolWindowKind.BuiltIn, ContributionSource.Shell) { Icon = "coverage" };

    public static IReadOnlyList<SettingDescriptor> TestingSettings()
    {
        var shell = ContributionSource.Shell;
        return
        [
            SettingDescriptor.Create(ShellHost.TestHistorySetting, "10", SettingType.Integer, shell) with
            {
                Title = "Results Kept per Test",
                Category = "Testing",
                Description = "How many earlier results of each test the Test Explorer keeps (shown in its details).",
                Minimum = 1,
                Maximum = 100,
                Order = 1,
            },
            SettingDescriptor.Create(ShellHost.ShowExplorerOnRunSetting, "true", SettingType.Boolean, shell) with
            {
                Title = "Show Test Explorer When Running Tests",
                Category = "Testing",
                Description = "Open the Test Explorer when a test run starts from a command or the editor.",
                Order = 2,
            },
        ];
    }

    public static void RegisterTestingCommands(ShellHost shell)
    {
        var commands = shell.Workbench.Commands;
        void Add(string id, string title, Func<Task> handler, string? enablement = null) =>
            commands.Register(
                new CommandDescriptor(id, title, ContributionSource.Shell) { Category = "Test", Enablement = WhenClause.Parse(enablement) },
                Ui(handler));

        Add("testing.runAll", "Run All Tests", () => shell.RunTestsAsync(TestRunKind.Run, null), "solutionOpen && !testsRunning");
        Add("testing.debugAll", "Debug All Tests", () => shell.RunTestsAsync(TestRunKind.Debug, null), "solutionOpen && !testsRunning && !debugActive");
        Add("testing.coverAll", "Run All Tests with Coverage", () => shell.RunTestsAsync(TestRunKind.Coverage, null), "solutionOpen && !testsRunning");
        Add("testing.runFailed", "Run Failed Tests", () => shell.Tests.RunFailedAsync(), "solutionOpen && !testsRunning");
        Add("testing.runNotRun", "Run Tests That Have Not Run", () => shell.Tests.RunNotRunAsync(), "solutionOpen && !testsRunning");
        Add("testing.repeatLast", "Repeat Last Test Run", () => shell.Tests.RepeatLastAsync(), "solutionOpen && !testsRunning");
        Add("testing.cancel", "Cancel Test Run", () =>
        {
            shell.Tests.Cancel();
            return Task.CompletedTask;
        }, "testsRunning");
        Add("testing.runAtCursor", "Run Tests at Cursor", () => shell.RunTestsAtCursorAsync(TestRunKind.Run), "activeDocumentKind == textFile && !testsRunning");
        Add("testing.debugAtCursor", "Debug Tests at Cursor", () => shell.RunTestsAtCursorAsync(TestRunKind.Debug), "activeDocumentKind == textFile && !testsRunning && !debugActive");
        Add("testing.coverAtCursor", "Run Tests at Cursor with Coverage", () => shell.RunTestsAtCursorAsync(TestRunKind.Coverage), "activeDocumentKind == textFile && !testsRunning");
        Add("testing.refresh", "Refresh Tests", shell.RefreshTestsAsync, "solutionOpen && !testsRunning");
        Add("testing.nextFailure", "Go to Next Failed Test", () => shell.GoToFailedTestAsync(forward: true));
        Add("testing.previousFailure", "Go to Previous Failed Test", () => shell.GoToFailedTestAsync(forward: false));
        Add("testing.showInExplorer", "Show Test in Test Explorer", shell.ShowTestAtCursorInExplorer, "activeDocumentKind == textFile");
        Add("testing.exportTrx", "Export Test Results (TRX)…", shell.ExportTrxAsync, "solutionOpen");
        Add("testing.importTrx", "Import Test Results (TRX)…", shell.ImportTrxAsync, "solutionOpen");
        Add("testing.newPlaylist", "New Playlist…", () => shell.AddToPlaylistAsync(shell.TestExplorer.SelectedKeys), "solutionOpen");
        Add("testing.importPlaylist", "Import Visual Studio Playlist…", shell.ImportVisualStudioPlaylistAsync, "solutionOpen");
        Add("testing.deletePlaylist", "Delete Playlist…", shell.DeletePlaylistAsync, "solutionOpen");
        Add("testing.toggleCoverageColoring", "Toggle Coverage Coloring", () =>
        {
            shell.Tests.Coverage.IsVisible = !shell.Tests.Coverage.IsVisible;
            return Task.CompletedTask;
        }, "coverageAvailable");
        Add("testing.exportCoverage", "Export Coverage (Cobertura)…", shell.ExportCoverageAsync, "coverageAvailable");
        Add("testing.clearCoverage", "Clear Coverage", () =>
        {
            shell.Tests.Coverage.Clear();
            return Task.CompletedTask;
        }, "coverageAvailable");
    }

    public static void RegisterTestingMenus(MenuRegistry menus)
    {
        menus.AddMenuBarMenu(new MenuBarMenu("test", "_Test", 65));
        var shell = ContributionSource.Shell;
        MenuItemRule Item(string command, string group, int order, string? title = null) =>
            new(MenuLocations.MenuBar("test"), command, shell) { Group = group, Order = order, Title = title };
        MenuItemRule Context(string command, string group, int order, string? title = null, string? when = null) =>
            new(MenuLocations.EditorContext, command, shell) { Group = group, Order = order, Title = title, When = WhenClause.Parse(when) };

        menus.Add(
        [
            Item("testing.runAll", "1-run", 1),
            Item("testing.debugAll", "1-run", 2),
            Item("testing.coverAll", "1-run", 3),
            Item("testing.runFailed", "1-run", 4),
            Item("testing.runNotRun", "1-run", 5),
            Item("testing.repeatLast", "1-run", 6),
            Item("testing.cancel", "1-run", 7),
            Item(ViewCommandPrefix + TestExplorerViewModel.ToolWindowId, "2-explorer", 1, "Test Explorer"),
            Item("testing.refresh", "2-explorer", 2),
            Item("testing.nextFailure", "2-explorer", 3),
            Item("testing.previousFailure", "2-explorer", 4),
            Item("testing.newPlaylist", "3-playlists", 1),
            Item("testing.importPlaylist", "3-playlists", 2),
            Item("testing.deletePlaylist", "3-playlists", 3),
            Item("testing.exportTrx", "4-results", 1),
            Item("testing.importTrx", "4-results", 2),
            Item(ViewCommandPrefix + CoverageResultsViewModel.ToolWindowId, "5-coverage", 1, "Coverage Results"),
            Item("testing.toggleCoverageColoring", "5-coverage", 2),
            Item("testing.exportCoverage", "5-coverage", 3),
            Item("testing.clearCoverage", "5-coverage", 4),

            // The editor's context menu: navigation and refactoring first, then tests.
            Context("editor.action.revealDefinition", "1-navigate", 1),
            Context("editor.action.referenceSearch", "1-navigate", 2),
            Context("editor.action.rename", "2-refactor", 1),
            Context("editor.action.quickFix", "2-refactor", 2),
            Context("editor.action.formatDocument", "2-refactor", 3),
            Context("testing.runAtCursor", "3-tests", 10, "Run Tests"),
            Context("testing.debugAtCursor", "3-tests", 11, "Debug Tests"),
            Context("testing.coverAtCursor", "3-tests", 12, "Run Tests with Coverage"),
            Context("debug.toggleBreakpoint", "4-debug", 1),
        ]);
    }
}
