using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using MenuItem = Avalonia.Controls.MenuItem;
using DaedalusStudio.LanguageServices.Protocol;
using DaedalusStudio.Shell.Controls;
using DaedalusStudio.Shell.Editor;
using DaedalusStudio.Shell.Infrastructure;
using DaedalusStudio.Shell.ViewModels;
using DaedalusStudio.Workbench.Menus;
using DaedalusStudio.Workbench.Plugins;
using DaedalusStudio.Workbench.StatusBar;
using DaedalusStudio.Workbench.Testing;

namespace DaedalusStudio.Shell;

/// <summary>
/// Testing (ADR 0010): the Test Explorer's model fed by test controllers (the .NET test plugin), runs from the explorer,
/// the editor and the Test menu, playlists and TRX files.
/// </summary>
public sealed partial class ShellHost
{
    public const string TestHistorySetting = "testing.historyLength";
    public const string ShowExplorerOnRunSetting = "testing.showExplorerOnRun";

    private const string TestStatusItem = "daedalus.tests";
    private WorkbenchProposedApi? _proposedApi;
    private bool _testsActivated;
    private string? _lastFailureKey;

    public TestService Tests { get; private set; } = null!;

    public TestExplorerViewModel TestExplorer { get; private set; } = null!;

    public CoverageResultsViewModel CoverageResults { get; private set; } = null!;

    TestService IEditorServices.Tests => Tests;

    private void InitializeTesting()
    {
        Tests = new TestService { HistoryLength = Workbench.Settings.GetInt32(TestHistorySetting, 10) };
        _proposedApi = new WorkbenchProposedApi(Solutions, Builds, Debugger, Languages, _ => Ui.RunAsync(SaveBeforeTestBuildAsync))
        {
            Terminals = Terminals,
            DefaultTerminalDirectory = TerminalDirectory,
            ShowTerminal = session => Ui.Post(() =>
            {
                Layout.ShowToolWindow(TerminalViewModel.ToolWindowId);
                TerminalWindow.Show(session);
            }),
        };
        Plugins.ProposedApi = _proposedApi;
        Plugins.TestSink = Tests;
        TestExplorer = new TestExplorerViewModel(this);
        CoverageResults = new CoverageResultsViewModel(this);
        Tests.CoverageReported += (_, _) => Ui.Post(() => Layout.ShowToolWindow(CoverageResultsViewModel.ToolWindowId, focus: false));
        Tests.Coverage.Changed += (_, _) => Ui.Post(() => Workbench.Context.Set("coverageAvailable", Tests.Coverage.HasCoverage));
        Solutions.ProjectChanged += (_, project) =>
        {
            if (project.IsTestProject)
            {
                Ui.Post(ActivateTestProviders);
            }
        };
        Solutions.SolutionChanged += (_, _) => Ui.Post(OnTestSolutionChanged);
        Tests.RunningChanged += (_, _) => Ui.Post(UpdateTestStatus);
        if (Diagnostics.BenchEvents.BenchEvents.Enabled)
        {
            Tests.Changed += (_, _) => Diagnostics.BenchEvents.BenchEvents.Emit(
                Diagnostics.BenchEvents.BenchEventNames.TestTree,
                [("tests", Tests.Read(roots => roots.Sum(r => r.Leaves().Count()))), ("controllers", Tests.Controllers.Count)]);
        }
        Tests.RunFinished += (_, run) => Ui.Post(() => OnTestRunFinished(run));
        Workbench.Settings.Changed += (_, e) =>
        {
            if (e.Affects(TestHistorySetting))
            {
                Tests.HistoryLength = Workbench.Settings.GetInt32(TestHistorySetting, 10);
            }
        };
    }

    /// <summary>The Test Explorer's content; opening it starts the test providers (<c>onView:testExplorer</c>).</summary>
    private TestExplorerViewModel OpenTestExplorer()
    {
        _ = Plugins.OnViewOpenedAsync(TestExplorerViewModel.ToolWindowId, CancellationToken.None);
        return TestExplorer;
    }

    private async Task SaveBeforeTestBuildAsync()
    {
        if (Workbench.Settings.GetBoolean(SaveAllBeforeBuildSetting, true))
        {
            await SaveAllAsync().ConfigureAwait(true);
        }
    }

    /// <summary>The first test project of a solution starts the test providers (activation event <c>onTestProjects</c>).</summary>
    private void ActivateTestProviders()
    {
        if (_testsActivated)
        {
            return;
        }

        _testsActivated = true;
        Diagnostics.BenchEvents.BenchEvents.Emit("test_providers");
        _ = Plugins.ActivateByEventAsync("onTestProjects", CancellationToken.None);
    }

    private void OnTestSolutionChanged()
    {
        if (Solutions.Solution is not { } solution)
        {
            _testsActivated = false;
            TestExplorer.SetPlaylists([]);
            return;
        }

        TestExplorer.SetPlaylists(Playlists.Load(solution.Path));
        if (Solutions.Projects.Values.Any(p => p.IsTestProject))
        {
            ActivateTestProviders();
        }
    }

    private void UpdateTestStatus()
    {
        Workbench.Context.Set("testsRunning", Tests.IsRunning);
        if (Tests.IsRunning)
        {
            Workbench.StatusBar.Set(new StatusBarItem(TestStatusItem, "Running tests…", StatusBarAlignment.Left, 40, "shell")
            {
                Tooltip = "Cancel the test run",
                Command = "testing.cancel",
            });
        }
    }

    private void OnTestRunFinished(TestRunRecord run)
    {
        var passed = run.Count(TestOutcome.Passed);
        var failed = run.Count(TestOutcome.Failed) + run.Count(TestOutcome.Errored);
        var skipped = run.Count(TestOutcome.Skipped);
        var text = string.Format(CultureInfo.CurrentCulture, "Tests: {0} passed, {1} failed, {2} skipped", passed, failed, skipped);
        Workbench.StatusBar.Set(new StatusBarItem(TestStatusItem, failed > 0 ? $"✗ {text}" : $"✓ {text}", StatusBarAlignment.Left, 40, "shell")
        {
            Tooltip = run.Name + " finished. Click to open the Test Explorer.",
            Command = BuiltIns.ViewCommandPrefix + TestExplorerViewModel.ToolWindowId,
        });
        var attachments = run.Attachments;
        if (run.Output.Length > 0 || attachments.Count > 0)
        {
            var channel = Workbench.Output.GetOrCreate("daedalus.tests", "Tests");
            channel.Clear();
            channel.Append(run.Output);
            foreach (var attachment in attachments)
            {
                // Files the run produced (coverage reports, logs): where to find them.
                var where = DocumentUris.ToPath(attachment.Uri) ?? attachment.Uri;
                channel.Append($"Attachment: {attachment.DisplayName ?? Path.GetFileName(where)}: {where}\n");
            }
        }
    }

    // ------------------------------------------------------------------ running

    /// <summary>
    /// Runs tests (<paramref name="keys"/> null: all) with the providers' default profile of <paramref name="kind"/>.
    /// Starts the providers if needed; reports problems in the status bar instead of throwing.
    /// </summary>
    public async Task RunTestsAsync(TestRunKind kind, IReadOnlyList<string>? keys)
    {
        if (Solutions.Solution is not { } solution)
        {
            StatusBar.ShowMessage("Open a solution to run its tests.", TimeSpan.FromSeconds(4));
            return;
        }

        if (!solution.Trusted)
        {
            Workbench.Notifications.Warning("Tests run only in trusted folders.");
            return;
        }

        if (Tests.IsRunning)
        {
            StatusBar.ShowMessage("Tests are already running.", TimeSpan.FromSeconds(4));
            return;
        }

        ActivateTestProviders();
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (Tests.DefaultProfiles(kind).Count == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100).ConfigureAwait(true);
        }

        if (Workbench.Settings.GetBoolean(ShowExplorerOnRunSetting, true))
        {
            Layout.ShowToolWindow(TestExplorerViewModel.ToolWindowId, focus: false);
        }

        if (!await Tests.RunAsync(kind, keys).ConfigureAwait(true))
        {
            StatusBar.ShowMessage(keys is { Count: 0 } ? "There are no tests to run." : $"No test provider can {kind.ToString().ToLowerInvariant()} these tests.", TimeSpan.FromSeconds(5));
        }
    }

    /// <summary>The test item at the active editor's caret (the innermost test or class around it).</summary>
    private TestNode? TestAtCursor()
    {
        if (ActiveEditor is not { Controller: { } controller } editor)
        {
            return null;
        }

        var line = controller.Editor.TextArea.Caret.Line - 1;
        return Tests.FindAt(DocumentUris.FromPath(editor.Model.Path), line);
    }

    public Task RunTestsAtCursorAsync(TestRunKind kind)
    {
        if (TestAtCursor() is not { } node)
        {
            StatusBar.ShowMessage("There is no test at the cursor.", TimeSpan.FromSeconds(4));
            return Task.CompletedTask;
        }

        return RunTestsAsync(kind, [node.Key]);
    }

    public Task ShowTestAtCursorInExplorer()
    {
        if (TestAtCursor() is { } node)
        {
            ShowInTestExplorer(node);
        }

        return Task.CompletedTask;
    }

    private void ShowInTestExplorer(TestNode node)
    {
        Layout.ShowToolWindow(TestExplorerViewModel.ToolWindowId);
        TestExplorer.Grouping = TestGrouping.Hierarchy;
        TestExplorer.Reveal(node.Key);
    }

    public async Task RefreshTestsAsync()
    {
        ActivateTestProviders();
        await Tests.RefreshAsync().ConfigureAwait(true);
    }

    /// <summary>Opens the next (or previous) failed test's failure location, and selects it in the Test Explorer.</summary>
    public async Task GoToFailedTestAsync(bool forward)
    {
        var failed = Tests.Read(roots => roots.SelectMany(r => r.Leaves()).Where(l => l.Outcome.IsFailure()).ToList());
        if (failed.Count == 0)
        {
            StatusBar.ShowMessage("No failed tests.", TimeSpan.FromSeconds(3));
            return;
        }

        var index = failed.FindIndex(f => f.Key == _lastFailureKey);
        index = index < 0 ? (forward ? 0 : failed.Count - 1) : (index + (forward ? 1 : -1) + failed.Count) % failed.Count;
        var test = failed[index];
        _lastFailureKey = test.Key;
        ShowInTestExplorer(test);

        // The failing frame in the solution's own code, else the test's declaration.
        var frames = StackTraces.Parse(test.Result is { Messages.Count: > 0 } failure ? failure.Messages[0].StackTrace : null);
        var root = Solutions.Solution is { } solution ? Path.GetDirectoryName(solution.Path)! : null;
        if (StackTraces.FirstUserFrame(frames, f => root is not null && f.StartsWith(root, StringComparison.OrdinalIgnoreCase) && File.Exists(f)) is { File: { } file } frame && File.Exists(file))
        {
            await OpenFileAsync(file, preview: false, new Position(Math.Max(0, frame.Line - 1), 0)).ConfigureAwait(true);
        }
        else if ((test.Uri ?? test.Parent?.Uri) is { } uri && DocumentUris.ToPath(uri) is { } path)
        {
            await OpenFileAsync(path, preview: false, new Position(test.Range?.StartLine ?? test.Parent?.Range?.StartLine ?? 0, 0)).ConfigureAwait(true);
        }
    }

    // ------------------------------------------------------------------ editor

    void IEditorServices.ShowTestMenu(TestNode test, Control anchor)
    {
        var key = test.Key;
        var menu = new ContextMenu();
        MenuItem Item(string header, string icon, Action action)
        {
            var item = new MenuItem { Header = header, Icon = new Icon { Kind = icon }, IsEnabled = !Tests.IsRunning || header.StartsWith("Show", StringComparison.Ordinal) };
            item.Click += (_, _) => action();
            return item;
        }

        menu.Items.Add(Item("Run", "play", () => _ = RunTestsAsync(TestRunKind.Run, [key])));
        menu.Items.Add(Item("Debug", "debug", () => _ = RunTestsAsync(TestRunKind.Debug, [key])));
        menu.Items.Add(Item("Run with Coverage", "coverage", () => _ = RunTestsAsync(TestRunKind.Coverage, [key])));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Show in Test Explorer", "beaker", () =>
        {
            if (Tests.Find(key) is { } node)
            {
                ShowInTestExplorer(node);
            }
        }));
        menu.Open(anchor);
    }

    void IEditorServices.RunCommand(string commandId) => Run(commandId);

    void IEditorServices.ShowEditorContextMenu(CodeEditorController editor, Control anchor)
    {
        var items = Workbench.Menus.GetItems(MenuLocations.EditorContext);
        if (items.Count == 0)
        {
            return;
        }

        // Commands get the document and the caret (ADR 0010: editor/context).
        var caret = editor.Editor.TextArea.Caret;
        JsonElement[] args =
        [
            JsonSerializer.SerializeToElement(DocumentUris.FromPath(editor.Model.Path), Docking.ShellJsonContext.Default.String),
            JsonSerializer.SerializeToElement(caret.Line - 1, Docking.ShellJsonContext.Default.Int32),
            JsonSerializer.SerializeToElement(caret.Column - 1, Docking.ShellJsonContext.Default.Int32),
        ];
        var menu = new ContextMenu();
        string? group = null;
        foreach (var item in items)
        {
            if (group is not null && item.Group != group)
            {
                menu.Items.Add(new Separator());
            }

            group = item.Group;
            var header = new DockPanel();
            if (item.KeyLabel is { } keys)
            {
                var label = new TextBlock { Text = keys, Margin = new Thickness(32, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                label.Classes.Add("muted");
                DockPanel.SetDock(label, Avalonia.Controls.Dock.Right);
                header.Children.Add(label);
            }

            header.Children.Add(new TextBlock { Text = item.Title, VerticalAlignment = VerticalAlignment.Center });
            var entry = new MenuItem { Header = header, IsEnabled = item.Enabled, Command = CommandFor(item.Command, args) };
            AutomationProperties.SetName(entry, item.Title);
            menu.Items.Add(entry);
        }

        menu.Open(anchor);
    }

    // ------------------------------------------------------------------ playlists

    public async Task AddToPlaylistAsync(IReadOnlyList<string> keys)
    {
        if (Solutions.Solution is not { } solution)
        {
            return;
        }

        if (keys.Count == 0)
        {
            StatusBar.ShowMessage("Select tests in the Test Explorer first.", TimeSpan.FromSeconds(4));
            return;
        }

        var existing = Playlists.Load(solution.Path);
        var choices = existing.Select(p => new QuickPickItem(p.Name) { Detail = $"{p.Tests.Count} items", Tag = p }).Prepend(new QuickPickItem("New Playlist…")).ToList();
        var picked = existing.Count == 0 ? choices[0] : await Palette.ShowAsync(choices, "Add to playlist").ConfigureAwait(true);
        if (picked is null)
        {
            return;
        }

        Playlist playlist;
        if (picked.Tag is Playlist chosen)
        {
            playlist = chosen with { Tests = [.. chosen.Tests.Union(keys, StringComparer.Ordinal)] };
        }
        else
        {
            var name = await Dialogs.InputAsync("New Playlist", "Name of the playlist:", "Playlist " + (existing.Count + 1).ToString(CultureInfo.CurrentCulture)).ConfigureAwait(true);
            if (string.IsNullOrWhiteSpace(name))
            {
                return;
            }

            playlist = new Playlist(name.Trim(), [.. keys]);
        }

        await Playlists.SaveAsync(solution.Path, playlist, CancellationToken.None).ConfigureAwait(true);
        TestExplorer.SetPlaylists(Playlists.Load(solution.Path), playlist.Name);
    }

    public async Task RemoveFromPlaylistAsync(Playlist playlist, IReadOnlyList<string> keys)
    {
        if (Solutions.Solution is not { } solution)
        {
            return;
        }

        var updated = playlist with { Tests = [.. playlist.Tests.Except(keys, StringComparer.Ordinal)] };
        await Playlists.SaveAsync(solution.Path, updated, CancellationToken.None).ConfigureAwait(true);
        TestExplorer.SetPlaylists(Playlists.Load(solution.Path), updated.Name);
    }

    public async Task DeletePlaylistAsync()
    {
        if (Solutions.Solution is not { } solution)
        {
            return;
        }

        var existing = Playlists.Load(solution.Path);
        if (existing.Count == 0)
        {
            StatusBar.ShowMessage("This solution has no playlists.", TimeSpan.FromSeconds(4));
            return;
        }

        if (await Palette.ShowAsync([.. existing.Select(p => new QuickPickItem(p.Name) { Tag = p })], "Delete playlist").ConfigureAwait(true) is { Tag: Playlist chosen })
        {
            Playlists.Delete(solution.Path, chosen.Name);
            TestExplorer.SetPlaylists(Playlists.Load(solution.Path));
        }
    }

    public async Task ImportVisualStudioPlaylistAsync()
    {
        if (Solutions.Solution is not { } solution || Window is null)
        {
            return;
        }

        if (await Window.PickFileAsync("Import Playlist", "Visual Studio playlists", ["*.playlist"]).ConfigureAwait(true) is not { } file)
        {
            return;
        }

        try
        {
            var document = XDocument.Load(file);
            var keys = Tests.Read(roots => Playlists.ImportVisualStudio(document, roots));
            if (keys.Count == 0)
            {
                Workbench.Notifications.Warning("The playlist names no tests that the Test Explorer knows. Build the solution so the tests are discovered, then import it again.");
                return;
            }

            var playlist = new Playlist(Path.GetFileNameWithoutExtension(file), keys);
            await Playlists.SaveAsync(solution.Path, playlist, CancellationToken.None).ConfigureAwait(true);
            TestExplorer.SetPlaylists(Playlists.Load(solution.Path), playlist.Name);
        }
        catch (Exception ex) when (ex is IOException or System.Xml.XmlException or FormatException or UnauthorizedAccessException)
        {
            Workbench.Notifications.Error("The playlist could not be imported: " + ex.Message);
        }
    }

    // ------------------------------------------------------------------ TRX

    public async Task ExportTrxAsync()
    {
        if (Window is null)
        {
            return;
        }

        var leaves = Tests.Read(roots => roots.SelectMany(r => r.Leaves()).Where(l => l.Result is not null).ToList());
        if (leaves.Count == 0)
        {
            StatusBar.ShowMessage("There are no test results to export.", TimeSpan.FromSeconds(4));
            return;
        }

        var name = $"TestResults-{DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.trx";
        if (await Window.SaveFileAsync("Export Test Results", name, "Test results", ["*.trx"]).ConfigureAwait(true) is not { } file)
        {
            return;
        }

        try
        {
            var document = Tests.Read(_ => Trx.Export(leaves, "Daedalus Studio", DateTimeOffset.Now));
            await using var stream = File.Create(file);
            await document.SaveAsync(stream, SaveOptions.None, CancellationToken.None).ConfigureAwait(true);
            StatusBar.ShowMessage($"Exported {leaves.Count} results to {Path.GetFileName(file)}.", TimeSpan.FromSeconds(5));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Workbench.Notifications.Error("The results could not be exported: " + ex.Message);
        }
    }

    /// <summary>Saves the latest coverage as a Cobertura report (ADR 0011), for ReportGenerator or CI tools.</summary>
    public async Task ExportCoverageAsync()
    {
        if (Window is null)
        {
            return;
        }

        if (!Tests.Coverage.HasCoverage)
        {
            StatusBar.ShowMessage("There is no coverage to export.", TimeSpan.FromSeconds(4));
            return;
        }

        if (await Window.SaveFileAsync("Export Coverage", "coverage.cobertura.xml", "Cobertura coverage", ["*.xml"]).ConfigureAwait(true) is not { } file)
        {
            return;
        }

        try
        {
            var document = Tests.Coverage.ToCobertura();
            await using var stream = File.Create(file);
            await document.SaveAsync(stream, SaveOptions.None, CancellationToken.None).ConfigureAwait(true);
            StatusBar.ShowMessage($"Exported coverage to {Path.GetFileName(file)}.", TimeSpan.FromSeconds(5));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Workbench.Notifications.Error("The coverage could not be exported: " + ex.Message);
        }
    }

    public async Task ImportTrxAsync()
    {
        if (Window is null || await Window.PickFileAsync("Import Test Results", "Test results", ["*.trx"]).ConfigureAwait(true) is not { } file)
        {
            return;
        }

        try
        {
            var imported = Trx.Import(XDocument.Load(file), "import");
            var unmatched = Tests.ApplyImported(Path.GetFileName(file), imported, (leaf, result) => TestNames.Matches(leaf, result.FullyQualifiedName, result.DisplayName));
            Layout.ShowToolWindow(TestExplorerViewModel.ToolWindowId, focus: false);
            var message = $"Imported {imported.Count - unmatched.Count} of {imported.Count} results from {Path.GetFileName(file)}.";
            if (unmatched.Count > 0)
            {
                message += $" {unmatched.Count} name tests that are not in this solution (or not discovered yet).";
            }

            Workbench.Notifications.Info(message);
        }
        catch (Exception ex) when (ex is IOException or System.Xml.XmlException or FormatException or UnauthorizedAccessException)
        {
            Workbench.Notifications.Error("The results could not be imported: " + ex.Message);
        }
    }
}
