using System.Text.Json;
using Codalus.Projects.Protocol;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Testing;

namespace Codalus.Shell;

/// <summary>
/// Remote test environments (M21b, ADR 0055): <c>testenvironments.json</c> lists WSL, Docker and SSH environments, the
/// Test Explorer picks one, and runs build here, run there (<see cref="EnvironmentTestRunner"/>), and come back as TRX
/// results on the tree.
/// </summary>
public sealed partial class ShellHost
{
    private static string SolutionFolder(SolutionInfo solution) => solution.IsFolder ? solution.Path : Path.GetDirectoryName(solution.Path)!;

    /// <summary>Reads the open solution's <c>testenvironments.json</c> into the Test Explorer's environment list.</summary>
    internal void LoadTestEnvironments()
    {
        IReadOnlyList<TestEnvironment> environments = [];
        if (Solutions.Solution is { } solution && TestEnvironments.Find(SolutionFolder(solution)) is { } file)
        {
            try
            {
                environments = TestEnvironments.Load(file);
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                Workbench.Output.GetOrCreate("codalus.tests", "Tests").AppendLine($"{file}: {ex.Message}");
            }
        }

        TestExplorer.SetEnvironments(environments);
    }

    /// <summary>
    /// Runs tests (<paramref name="keys"/> null: all) in <paramref name="environment"/>: the test projects they belong to
    /// are built here, run there, and their results applied to the tree.
    /// </summary>
    internal async Task RunTestsInEnvironmentAsync(TestEnvironment environment, TestRunKind kind, IReadOnlyList<string>? keys)
    {
        if (kind != TestRunKind.Run)
        {
            Workbench.Notifications.Info($"Tests in {environment.Name} only run: debugging and coverage there aren't supported yet. Pick Local to {(kind == TestRunKind.Debug ? "debug" : "collect coverage")}.");
            return;
        }

        if (environment.Problem is { } problem)
        {
            Workbench.Notifications.Error($"{environment.Name} can't run tests: {problem}");
            return;
        }

        var solution = Solutions.Solution!;
        var selection = TestSelection(keys);
        if (selection.Count == 0)
        {
            StatusBar.ShowMessage("There are no tests to run.", TimeSpan.FromSeconds(5));
            return;
        }

        if (Workbench.Settings.GetBoolean(ShowExplorerOnRunSetting, true))
        {
            Layout.ShowToolWindow(TestExplorerViewModel.ToolWindowId, focus: false);
        }

        var channel = Workbench.Output.GetOrCreate("codalus.tests", "Tests");
        channel.Clear();
        var (handle, token) = Tests.BeginExternalRun();
        using var _ = handle;
        try
        {
            await SaveBeforeTestBuildAsync().ConfigureAwait(true);
            var projects = new List<EnvironmentTestProject>();
            foreach (var (project, names) in selection)
            {
                channel.AppendLine($"Building {Path.GetFileNameWithoutExtension(project)}…");
                var built = await Builds.BuildAsync("Build", project, token).ConfigureAwait(true);
                if (!built.Succeeded)
                {
                    Workbench.Notifications.Error($"{Path.GetFileNameWithoutExtension(project)} didn't build; its tests didn't run in {environment.Name}.");
                    return;
                }

                var info = await Solutions.GetRunInfoAsync(project, token).ConfigureAwait(true);
                if (info.TargetPath is not { } targetPath)
                {
                    channel.AppendLine($"{Path.GetFileNameWithoutExtension(project)} has no output assembly; skipped.");
                    continue;
                }

                projects.Add(new EnvironmentTestProject(project, targetPath, names));
            }

            var runner = new EnvironmentTestRunner(environment, SolutionFolder(solution), line => channel.AppendLine(line)) { Containers = ContainerEngine };
            var documents = await Task.Run(() => runner.RunAsync(projects, token), token).ConfigureAwait(true);
            var imported = documents.SelectMany(d => Trx.Import(d, "environment")).ToList();
            var unmatched = Tests.ApplyImported(environment.Name, imported);
            if (imported.Count == 0)
            {
                Workbench.Notifications.Warning($"No results came back from {environment.Name}: see the Tests output.");
            }
            else if (unmatched.Count > 0)
            {
                channel.AppendLine($"{unmatched.Count} results name tests that aren't in the Test Explorer: {string.Join(", ", unmatched.Take(5).Select(u => u.FullyQualifiedName))}");
            }
        }
        catch (OperationCanceledException)
        {
            StatusBar.ShowMessage($"The test run in {environment.Name} was canceled.", TimeSpan.FromSeconds(5));
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.Xml.XmlException or System.ComponentModel.Win32Exception or FormatException)
        {
            channel.AppendLine(ex.ToString());
            Workbench.Notifications.Error($"The tests couldn't run in {environment.Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// The test projects of <paramref name="keys"/> (null: every one), each with the fully qualified names of the tests
    /// to run, or null for all of its tests.
    /// </summary>
    private Dictionary<string, IReadOnlyCollection<string>?> TestSelection(IReadOnlyList<string>? keys) => Tests.Read(roots =>
    {
        var projects = roots.Where(r => r.Uri is { } uri && uri.StartsWith("file:", StringComparison.Ordinal) && Path.GetExtension(new Uri(uri).LocalPath).EndsWith("proj", StringComparison.OrdinalIgnoreCase)).ToList();
        string ProjectOf(TestNode root) => new Uri(root.Uri!).LocalPath;
        var selection = new Dictionary<string, IReadOnlyCollection<string>?>(StringComparer.OrdinalIgnoreCase);
        if (keys is null)
        {
            foreach (var root in projects)
            {
                selection[ProjectOf(root)] = null;
            }

            return selection;
        }

        foreach (var key in keys)
        {
            var node = projects.SelectMany(r => r.SelfAndDescendants()).FirstOrDefault(n => n.Key == key);
            if (node is null)
            {
                continue;
            }

            var root = node;
            while (root.Parent is { } parent)
            {
                root = parent;
            }

            var project = ProjectOf(root);
            if (ReferenceEquals(node, root) || (selection.TryGetValue(project, out var existing) && existing is null))
            {
                selection[project] = null;
                continue;
            }

            var names = (HashSet<string>?)selection.GetValueOrDefault(project) ?? new HashSet<string>(StringComparer.Ordinal);
            foreach (var leaf in node.Leaves())
            {
                if (TestNames.FullyQualifiedName(leaf) is { } name)
                {
                    names.Add(name);
                }
            }

            selection[project] = names;
        }

        return selection;
    });
}
