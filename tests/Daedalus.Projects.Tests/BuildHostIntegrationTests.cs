using System.Diagnostics;
using System.Reflection;
using Daedalus.Projects.Protocol;
using Daedalus.Rpc;
using Daedalus.Workbench.Projects;

namespace Daedalus.Projects.Tests;

/// <summary>End-to-end: the real build host (MSBuild from the installed SDK) on a copy of samples/ProjectSystem.</summary>
public sealed class BuildHostIntegrationTests : IAsyncLifetime
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(90);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "daedalus-projects-" + Guid.NewGuid().ToString("N"));
    private SolutionService _service = null!;

    internal static string RepoRoot => Metadata("DaedalusRepoRoot");

    private static string HostAssembly => Path.Combine(RepoRoot, "src", "Daedalus.Projects.Host", "bin", Metadata("DaedalusConfiguration"), "net10.0", "daedalus-buildhost.dll");

    private static string Metadata(string key) =>
        typeof(BuildHostIntegrationTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == key).Value!;

    private string Fixture => Path.Combine(_root, "ProjectSystem");

    private string Slnx => Path.Combine(Fixture, "ProjectSystem.slnx");

    private string AppProject => Path.Combine(Fixture, "src", "App", "App.csproj");

    private string CoreProject => Path.Combine(Fixture, "src", "Core", "Core.csproj");

    private static CancellationToken Timeout() =>
        CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken, new CancellationTokenSource(s_timeout).Token).Token;

    public ValueTask InitializeAsync()
    {
        Assert.True(File.Exists(HostAssembly), $"Build host not built: {HostAssembly}");
        Copy(Path.Combine(RepoRoot, "samples", "ProjectSystem"), Fixture);

        // The fixture inherits nothing from the repository, as in samples/.
        File.WriteAllText(Path.Combine(_root, "Directory.Build.props"), "<Project />");
        File.WriteAllText(Path.Combine(_root, "Directory.Packages.props"), "<Project><PropertyGroup><ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally></PropertyGroup></Project>");
        Environment.SetEnvironmentVariable("DAEDALUS_TRASH_DIR", Path.Combine(_root, ".trash"));
        _service = new SolutionService(() => HostAssembly, new SupervisionPolicy { InitialBackoff = TimeSpan.FromMilliseconds(50) });
        _service.Log += (_, line) => Console.Error.WriteLine("[host] " + line);
        _service.RestoreOutput += (_, line) => Console.Error.WriteLine("[restore] " + line);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _service.DisposeAsync();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
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

    private async Task<SolutionInfo> OpenEvaluatedAsync(string? path = null)
    {
        var solution = await _service.OpenAsync(path ?? Slnx, trusted: true, restore: false, Timeout());
        await WaitUntilAsync(() => _service.Progress is { Completed: true } && _service.Projects.Count == solution.Projects.Length);
        return solution;
    }

    private ProjectInfo Project(string path) => _service.Projects[path];

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? s_timeout);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out.");
            await Task.Delay(25, TestContext.Current.CancellationToken);
        }
    }

    private Task WaitForProjectAsync(string path, Func<ProjectInfo, bool> condition) =>
        WaitUntilAsync(() => _service.Projects.TryGetValue(path, out var p) && condition(p));

    // ------------------------------------------------------------------ opening

    [Theory]
    [InlineData("ProjectSystem.slnx")]
    [InlineData("ProjectSystem.sln")]
    public async Task Untrusted_solutions_show_their_structure_without_evaluation(string file)
    {
        var solution = await _service.OpenAsync(Path.Combine(Fixture, file), trusted: false, restore: false, Timeout());

        Assert.False(solution.Trusted);
        Assert.Equal(["App", "Core", "Core.Tests"], solution.Projects.Select(p => p.Name).Order());
        Assert.Equal("/src/libs/", solution.Projects.Single(p => p.Name == "Core").Parent);
        Assert.Equal("/src/", solution.Folders.Single(f => f.Name == "libs").Parent);
        Assert.Equal(["Directory.Build.props", "README.md"], solution.Folders.Single(f => f.Name == "Solution Items").Files.Select(Path.GetFileName));
        Assert.Contains(solution.Configurations, c => c.Configuration == "Release" && c.Platform == "x64");
        Assert.Equal(new SolutionConfigurationInfo("Debug", "Any CPU"), solution.Active);

        await Task.Delay(500, TestContext.Current.CancellationToken);
        Assert.Empty(_service.Projects);
        var app = SolutionTree.Build(solution, _service.Projects).Children.Single(n => n.Name == "src").Children.Single(n => n.Name == "App");
        Assert.Equal("(not loaded: untrusted folder)", app.Detail);
    }

    [Fact]
    public async Task Trusted_solutions_evaluate_items_references_and_target_frameworks()
    {
        await OpenEvaluatedAsync();
        var app = Project(AppProject);

        Assert.Equal(ProjectStates.Loaded, app.State);
        Assert.Equal(["net10.0"], app.TargetFrameworks);
        Assert.Equal("Exe", app.OutputType);
        string Relative(ProjectItemInfo item) => Path.GetRelativePath(Path.GetDirectoryName(AppProject)!, item.Path).Replace('\\', '/');
        var items = app.Items.Select(Relative).ToHashSet();
        Assert.Contains("Program.cs", items);
        Assert.Contains("Forms/MainForm.Designer.cs", items);
        Assert.Contains("Resources/Strings.resx", items);
        Assert.Contains("appsettings.Development.json", items);
        Assert.DoesNotContain("Excluded/Ignored.cs", items);
        Assert.DoesNotContain("ReleaseOnly.cs", items); // removed unless Release
        Assert.Equal("Shared\\Shared.cs", app.Items.Single(i => i.Path.EndsWith("Shared.cs", StringComparison.Ordinal)).Link);
        Assert.Contains(app.Items, i => i.Kind == "Folder" && Relative(i) == "Empty");

        Assert.Contains(app.References, r => r is { Kind: ReferenceKinds.Project, Name: "Core" });
        Assert.Contains(app.References, r => r is { Kind: ReferenceKinds.Package, Name: "Microsoft.Build.Locator", Version: "1.11.2" });
        Assert.Contains(app.References, r => r is { Kind: ReferenceKinds.Framework, Name: "Microsoft.NETCore.App" });

        var core = Project(CoreProject);
        Assert.Equal(["net10.0", "netstandard2.0"], core.TargetFrameworks);
    }

    [Fact]
    public async Task The_tree_has_solution_folders_nesting_links_and_dependencies()
    {
        var solution = await OpenEvaluatedAsync();
        var tree = SolutionTree.Build(solution, _service.Projects);

        ExplorerNode Child(ExplorerNode node, string name) => Assert.Single(node.Children, c => c.Name == name);
        var src = Child(tree, "src");
        var app = Child(src, "App");
        Assert.Equal(ExplorerNodeKind.Project, Child(Child(src, "libs"), "Core").Kind);
        Assert.Equal("Dependencies", app.Children[0].Name);
        Assert.Equal(["Frameworks", "Packages", "Projects"], app.Children[0].Children.Select(g => g.Name));
        Assert.Equal("MainForm.Designer.cs", Child(Child(Child(app, "Forms"), "MainForm.cs"), "MainForm.Designer.cs").Name);
        Assert.Equal("appsettings.Development.json", Child(Child(app, "appsettings.json"), "appsettings.Development.json").Name);
        Assert.True(Child(Child(app, "Shared"), "Shared.cs").IsLinked);
        Assert.Empty(Child(app, "Empty").Children);

        var core = Child(Child(src, "libs"), "Core");
        Assert.Equal(["net10.0", "netstandard2.0"], core.Children[0].Children.Select(c => c.Name));
    }

    [Fact]
    public async Task Switching_the_configuration_re_evaluates_conditional_items()
    {
        var solution = await OpenEvaluatedAsync();
        await _service.SetConfigurationAsync(solution.Configurations.First(c => c.Configuration == "Release" && c.Platform == "Any CPU"), Timeout());

        await WaitForProjectAsync(AppProject, p => p.Items.Any(i => i.Path.EndsWith("ReleaseOnly.cs", StringComparison.Ordinal)));
        Assert.Equal("Release", _service.Solution!.Active!.Configuration);
    }

    [Fact]
    public async Task Trusting_later_starts_evaluation()
    {
        await _service.OpenAsync(Slnx, trusted: false, restore: false, Timeout());
        await _service.TrustAsync(restore: false, Timeout());

        await WaitUntilAsync(() => _service.Projects.Count == 3);
        Assert.True(_service.Solution!.Trusted);
    }

    [Fact]
    public async Task A_project_file_can_be_opened_on_its_own()
    {
        var solution = await OpenEvaluatedAsync(CoreProject);

        Assert.Equal("project", solution.Format);
        Assert.Equal(["net10.0", "netstandard2.0"], Project(CoreProject).TargetFrameworks);
    }

    // ------------------------------------------------------------------ file operations

    [Fact]
    public async Task Renaming_a_file_updates_explicit_items_and_the_tree()
    {
        await OpenEvaluatedAsync();
        var appDir = Path.GetDirectoryName(AppProject)!;

        var result = await _service.RenameAsync(Path.Combine(appDir, "appsettings.json"), Path.Combine(appDir, "settings.json"), Timeout());

        Assert.Equal([AppProject], result.UpdatedFiles);
        var xml = await File.ReadAllTextAsync(AppProject, TestContext.Current.CancellationToken);
        Assert.Contains("<None Update=\"settings.json\" CopyToOutputDirectory=\"PreserveNewest\" />", xml, StringComparison.Ordinal);
        Assert.Contains("    <!-- A file outside the project folder, shown under Shared\\. -->", xml, StringComparison.Ordinal); // formatting kept
        await WaitForProjectAsync(AppProject, p => p.Items.Any(i => i.Path.EndsWith("settings.json", StringComparison.Ordinal) && !i.Path.EndsWith("appsettings.json", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Renaming_a_project_folder_updates_the_solution_and_project_references()
    {
        await OpenEvaluatedAsync();
        var newDir = Path.Combine(Fixture, "src", "Lib");

        var result = await _service.RenameAsync(Path.GetDirectoryName(CoreProject)!, newDir, Timeout());

        Assert.Contains(Slnx, result.UpdatedFiles);
        Assert.Contains(AppProject, result.UpdatedFiles);
        Assert.Contains("src/Lib/Core.csproj", await File.ReadAllTextAsync(Slnx, TestContext.Current.CancellationToken), StringComparison.Ordinal);
        Assert.Contains("..\\Lib\\Core.csproj", await File.ReadAllTextAsync(AppProject, TestContext.Current.CancellationToken), StringComparison.Ordinal);
        Assert.Contains("..\\..\\src\\Lib\\Core.csproj", await File.ReadAllTextAsync(Path.Combine(Fixture, "tests", "Core.Tests", "Core.Tests.csproj"), TestContext.Current.CancellationToken), StringComparison.Ordinal);
        var moved = Path.Combine(newDir, "Core.csproj");
        await WaitForProjectAsync(moved, p => p.State == ProjectStates.Loaded);
        await WaitForProjectAsync(AppProject, p => p.References.Any(r => r.Kind == ReferenceKinds.Project && r.Path == moved && r.Resolved));
    }

    [Fact]
    public async Task Creating_moving_and_deleting_files()
    {
        await OpenEvaluatedAsync();
        var appDir = Path.GetDirectoryName(AppProject)!;

        await _service.CreateFolderAsync(Path.Combine(appDir, "Models"), Timeout());
        await _service.CreateFileAsync(Path.Combine(appDir, "Models", "Order.cs"), "namespace App.Models;\n", Timeout());
        await WaitForProjectAsync(AppProject, p => p.Items.Any(i => i.Path.EndsWith("Order.cs", StringComparison.Ordinal)));

        await _service.MoveAsync([Path.Combine(appDir, "Models", "Order.cs")], Path.GetDirectoryName(CoreProject)!, copy: false, Timeout());
        await WaitForProjectAsync(CoreProject, p => p.Items.Any(i => i.Path.EndsWith("Order.cs", StringComparison.Ordinal)));
        await WaitForProjectAsync(AppProject, p => !p.Items.Any(i => i.Path.EndsWith("Order.cs", StringComparison.Ordinal)));

        var deleted = await _service.DeleteAsync([Path.Combine(appDir, "appsettings.json")], Timeout());
        Assert.Equal([AppProject], deleted.UpdatedFiles); // the explicit <None Update> went away with the file
        Assert.DoesNotContain("appsettings.json\"", await File.ReadAllTextAsync(AppProject, TestContext.Current.CancellationToken), StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(_root, ".trash", "appsettings.json")));

        await Assert.ThrowsAsync<JsonRpcRemoteException>(() => _service.CreateFileAsync(Path.Combine(appDir, "Program.cs"), null, Timeout()));
    }

    [Fact]
    public async Task External_changes_are_picked_up()
    {
        await OpenEvaluatedAsync();
        var appDir = Path.GetDirectoryName(AppProject)!;

        await File.WriteAllTextAsync(Path.Combine(appDir, "Added.cs"), "// added outside Daedalus\n", TestContext.Current.CancellationToken);
        await WaitForProjectAsync(AppProject, p => p.Items.Any(i => i.Path.EndsWith("Added.cs", StringComparison.Ordinal)));

        // An imported file: every project that imports it re-evaluates.
        await File.WriteAllTextAsync(Path.Combine(Fixture, "Directory.Build.props"),
            "<Project><ItemGroup><None Include=\"$(MSBuildThisFileDirectory)README.md\" Link=\"README.md\" /></ItemGroup></Project>", TestContext.Current.CancellationToken);
        await WaitForProjectAsync(Path.Combine(Fixture, "tests", "Core.Tests", "Core.Tests.csproj"), p => p.Items.Any(i => i.Link == "README.md"));
    }

    // ------------------------------------------------------------------ solution edits

    [Fact]
    public async Task Solution_folders_and_project_references_can_be_edited()
    {
        await OpenEvaluatedAsync();
        var testsProject = Path.Combine(Fixture, "tests", "Core.Tests", "Core.Tests.csproj");

        await _service.AddSolutionFolderAsync("integration", "/tests/", Timeout());
        await _service.MoveInSolutionAsync(testsProject, "/tests/integration/", Timeout());
        await _service.RenameSolutionFolderAsync("/tests/integration/", "it", Timeout());
        await WaitUntilAsync(() => _service.Solution!.Projects.Single(p => p.Name == "Core.Tests").Parent == "/tests/it/");
        Assert.Contains("<Folder Name=\"/tests/it/\">", await File.ReadAllTextAsync(Slnx, TestContext.Current.CancellationToken), StringComparison.Ordinal);

        await _service.AddProjectReferenceAsync(testsProject, AppProject, Timeout());
        await WaitForProjectAsync(testsProject, p => p.References.Any(r => r is { Kind: ReferenceKinds.Project, Name: "App" }));
        await _service.RemoveProjectReferenceAsync(testsProject, AppProject, Timeout());
        await WaitForProjectAsync(testsProject, p => !p.References.Any(r => r is { Kind: ReferenceKinds.Project, Name: "App" }));

        await _service.RemoveProjectAsync(testsProject, Timeout());
        await WaitUntilAsync(() => _service.Solution!.Projects.Length == 2);
        Assert.DoesNotContain("Core.Tests", await File.ReadAllTextAsync(Slnx, TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Editing_the_classic_sln_format_keeps_it_classic()
    {
        var sln = Path.Combine(Fixture, "ProjectSystem.sln");
        await OpenEvaluatedAsync(sln);
        await _service.AddSolutionFolderAsync("docs", null, Timeout());

        var text = await File.ReadAllTextAsync(sln, TestContext.Current.CancellationToken);
        Assert.Contains("Microsoft Visual Studio Solution File, Format Version 12.00", text, StringComparison.Ordinal);
        Assert.Contains("\"docs\", \"docs\"", text, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ supervision and restore

    [Fact]
    public async Task A_crashed_build_host_restarts_and_reopens_the_solution()
    {
        await OpenEvaluatedAsync();
        var restarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _service.HostRestarted += (_, _) => restarted.TrySetResult();

        using (var process = Process.GetProcessById(_service.HostProcessId!.Value))
        {
            process.Kill();
        }

        await restarted.Task.WaitAsync(Timeout());
        await WaitUntilAsync(() => _service.Projects.Count == 3 && _service.Progress is { Completed: true });
        Assert.True(_service.Solution!.Trusted);
    }

    [Fact]
    public async Task Restore_resolves_package_versions()
    {
        var states = new List<string>();
        _service.RestoreStateChanged += (_, e) =>
        {
            lock (states)
            {
                states.Add(e.Message is null ? e.State : $"{e.State}: {e.Message}");
            }
        };

        await _service.OpenAsync(Slnx, trusted: true, restore: true, Timeout());

        await WaitUntilAsync(() => { lock (states) { return states.Any(s => s.StartsWith("succeeded", StringComparison.Ordinal) || s.StartsWith("failed", StringComparison.Ordinal)); } }, TimeSpan.FromMinutes(4));
        Assert.Contains("succeeded", states);
        await WaitForProjectAsync(AppProject, p => p.Restored && p.References.Any(r => r is { Name: "Microsoft.Build.Locator", Resolved: true }));
    }
}
