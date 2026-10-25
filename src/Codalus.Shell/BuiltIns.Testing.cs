using Codalus.Shell.ViewModels;
using Codalus.Workbench.Commands;
using Codalus.Workbench.Context;
using Codalus.Workbench.Menus;
using Codalus.Workbench.Settings;
using Codalus.Workbench.Testing;
using Codalus.Workbench.ToolWindows;

namespace Codalus.Shell;

/// <summary>The Test Explorer, the Test menu and their commands and key bindings (ADR 0010).</summary>
internal static partial class BuiltIns
{
    /// <summary>The classic IDE's test chords (Ctrl+R, …) and Ctrl+E, T for the Test Explorer.</summary>
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
        Add("testing.importPlaylist", "Import Test Playlist (.playlist)…", shell.ImportVisualStudioPlaylistAsync, "solutionOpen");
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
}
