using System.Diagnostics;
using System.Reflection;
using Codalus.Projects.Protocol;
using Codalus.Rpc;
using Codalus.Workbench.Projects;

namespace Codalus.Projects.Tests;

/// <summary>End-to-end: the real build host (MSBuild from the installed SDK) on a copy of samples/ProjectSystem.</summary>
public sealed class BuildHostIntegrationTests : IAsyncLifetime
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(90);
    private readonly string _root = Path.Combine(Codalus.Platform.RealPath.Resolve(Path.GetTempPath()), "codalus-projects-" + Guid.NewGuid().ToString("N"));
    private SolutionService _service = null!;

    internal static string RepoRoot => Metadata("CodalusRepoRoot");

    internal static string Configuration => Metadata("CodalusConfiguration");

    /// <summary>The build host the build tests use: <c>CODALUS_BUILDHOST</c> (e.g. a published one), else the built one.</summary>
    internal static string HostAssemblyPath => Environment.GetEnvironmentVariable("CODALUS_BUILDHOST") is { Length: > 0 } host ? host : HostAssembly;

    private static string HostAssembly => Path.Combine(RepoRoot, "src", "Codalus.Projects.Host", "bin", Metadata("CodalusConfiguration"), Codalus.Tests.RepoBuild.TargetFramework, "codalus-buildhost.dll");

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
        Codalus.Tests.FixtureSdk.Pin(_root);
        File.WriteAllText(Path.Combine(_root, "Directory.Packages.props"), "<Project><PropertyGroup><ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally></PropertyGroup></Project>");
        Environment.SetEnvironmentVariable("CODALUS_TRASH_DIR", Path.Combine(_root, ".trash"));
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
        var started = System.Diagnostics.Stopwatch.StartNew(); // monotonic: immune to the machine sleeping
        while (!condition())
        {
            Assert.True(started.Elapsed < (timeout ?? s_timeout), "Timed out.");
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

        await File.WriteAllTextAsync(Path.Combine(appDir, "Added.cs"), "// added outside Codalus\n", TestContext.Current.CancellationToken);
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

    [Fact]
    public async Task A_project_that_cant_restore_leaves_the_others_restored()
    {
        // As a MAUI app without its workload does: an error while the restore reads it, which fails the whole
        // solution's restore before any assets file is written.
        var bad = Path.Combine(Fixture, "src", "Bad", "Bad.csproj");
        Directory.CreateDirectory(Path.GetDirectoryName(bad)!);
        File.WriteAllText(bad, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <Target Name="NeedsAWorkload" BeforeTargets="_GenerateRestoreProjectSpec"><Error Text="This project needs a workload." /></Target>
            </Project>
            """);
        var broken = Path.Combine(Fixture, "Broken.slnx");
        File.WriteAllText(broken, """
            <Solution>
              <Project Path="src/App/App.csproj" />
              <Project Path="src/Bad/Bad.csproj" />
            </Solution>
            """);
        var states = new List<string>();
        _service.RestoreStateChanged += (_, e) =>
        {
            lock (states)
            {
                states.Add(e.Message is null ? e.State : $"{e.State}: {e.Message}");
            }
        };

        await _service.OpenAsync(broken, trusted: true, restore: true, Timeout());

        await WaitUntilAsync(() => { lock (states) { return states.Any(s => s.StartsWith("failed", StringComparison.Ordinal)); } }, TimeSpan.FromMinutes(4));
        Assert.Contains(states, s => s.Contains("1 project(s) restored on their own", StringComparison.Ordinal));
        await WaitForProjectAsync(AppProject, p => p.Restored && p.References.Any(r => r is { Name: "Microsoft.Build.Locator", Resolved: true }));
        Assert.False(Project(bad).Restored);
    }

    [Fact]
    public async Task An_unreachable_package_source_fails_the_restore_once_and_says_so()
    {
        // As a company feed does off its VPN: each project restored on its own waited for the feed in turn, minutes
        // each, and every one it restored was evaluated and built again (the C# analysis restarting each time).
        var folder = Path.Combine(_root, "Offline");
        foreach (var name in new[] { "One", "Two" })
        {
            Directory.CreateDirectory(Path.Combine(folder, name));
            File.WriteAllText(Path.Combine(folder, name, name + ".csproj"), """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                  <ItemGroup><PackageReference Include="Codalus.Tests.OnlyOnTheCompanyFeed" Version="1.0.0" /></ItemGroup>
                </Project>
                """);
        }

        // Nothing listens on port 9 (discard): the connection is refused at once.
        File.WriteAllText(Path.Combine(folder, "NuGet.Config"), """
            <configuration>
              <packageSources><clear /><add key="company" value="http://127.0.0.1:9/v3/index.json" allowInsecureConnections="true" /></packageSources>
            </configuration>
            """);
        var slnx = Path.Combine(folder, "Offline.slnx");
        File.WriteAllText(slnx, """<Solution><Project Path="One/One.csproj" /><Project Path="Two/Two.csproj" /></Solution>""");
        var states = new List<string>();
        _service.RestoreStateChanged += (_, e) =>
        {
            lock (states)
            {
                states.Add(e.Message is null ? e.State : $"{e.State}: {e.Message}");
            }
        };
        var runs = 0;
        _service.RestoreOutput += (_, line) =>
        {
            if (line.Contains("Determining projects to restore", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref runs);
            }
        };

        await _service.OpenAsync(slnx, trusted: true, restore: true, Timeout());

        await WaitUntilAsync(() => { lock (states) { return states.Any(s => s.StartsWith("failed", StringComparison.Ordinal)); } }, TimeSpan.FromMinutes(4));
        Assert.Contains(states, s => s.Contains("a package source couldn't be reached (NU1301)", StringComparison.Ordinal));
        Assert.Equal(1, Volatile.Read(ref runs)); // the solution's restore only, no project on its own
    }

    // ------------------------------------------------------------------ design-time builds

    [Fact]
    public async Task Design_time_builds_produce_compiler_inputs_for_the_active_framework()
    {
        await _service.OpenAsync(Slnx, trusted: true, restore: true, Timeout());
        var testsProject = Path.Combine(Fixture, "tests", "Core.Tests", "Core.Tests.csproj");
        bool Ok(string path) => _service.CompilerInputs.TryGetValue(path, out var i) && i.State == CompilerInputStates.Ok;
        await WaitUntilAsync(() => Ok(AppProject) && Ok(CoreProject) && Ok(testsProject), TimeSpan.FromMinutes(4));

        var core = _service.CompilerInputs[CoreProject];
        Assert.Equal("C#", core.Language);
        Assert.Equal("net10.0", core.TargetFramework); // the first of a multi-targeted project
        Assert.Contains(core.Arguments, a => a.StartsWith("/define:", StringComparison.Ordinal) && a.Contains("NET10_0", StringComparison.Ordinal));
        Assert.EndsWith("Core.dll", core.OutputPath, StringComparison.Ordinal);

        // App references Core's output (or its reference assembly), which the language host maps back to the project.
        var app = _service.CompilerInputs[AppProject];
        Assert.Contains(CoreProject, app.ProjectReferences);
        Assert.Contains(app.Arguments, a => a.StartsWith("/reference:", StringComparison.Ordinal)
                                            && (a.EndsWith(core.OutputPath!, StringComparison.OrdinalIgnoreCase) || (core.OutputRefPath is { } r && a.EndsWith(r, StringComparison.OrdinalIgnoreCase))));
        Assert.Contains("Program.cs", app.Arguments); // sources are relative to the project directory, as csc gets them
        Assert.Contains(app.Arguments, a => a.StartsWith("/analyzer:", StringComparison.Ordinal));

        await _service.SetActiveFrameworkAsync(CoreProject, "netstandard2.0", Timeout());
        await WaitUntilAsync(() => _service.CompilerInputs[CoreProject].TargetFramework == "netstandard2.0");
        Assert.Contains(_service.CompilerInputs[CoreProject].Arguments, a => a.StartsWith("/define:", StringComparison.Ordinal) && a.Contains("NETSTANDARD2_0", StringComparison.Ordinal));
        Assert.Equal("netstandard2.0", _service.ActiveFramework(CoreProject));
    }

    [Fact]
    public async Task Design_time_builds_come_from_the_cache_until_their_inputs_change()
    {
        var log = new List<string>();
        var restoreStates = new List<string>();
        const string completedMarker = "<design-time builds completed>";
        _service.Log += (_, line) => { lock (log) { log.Add(line); } };

        // The host sends a batch's completed progress before its log lines (in that order, which the connection keeps):
        // the marker tells the lines of the last batch from an earlier one's.
        _service.DesignTimeProgressChanged += (_, p) =>
        {
            if (p.Completed)
            {
                lock (log)
                {
                    log.Add(completedMarker);
                }
            }
        };
        _service.RestoreStateChanged += (_, e) => { lock (restoreStates) { restoreStates.Add(e.State); } };
        var testsProject = Path.Combine(Fixture, "tests", "Core.Tests", "Core.Tests.csproj");
        bool Ok(string path) => _service.CompilerInputs.TryGetValue(path, out var i) && i.State == CompilerInputStates.Ok;

        // Each open is a new session, as after a restart: only the cache in obj/ carries over.
        async Task<string> OpenAsync(bool restores = false)
        {
            lock (log)
            {
                log.Clear();
            }

            lock (restoreStates)
            {
                restoreStates.Clear();
            }

            await _service.OpenAsync(Slnx, trusted: true, restore: true, Timeout());
            await WaitUntilAsync(() => Ok(AppProject) && Ok(CoreProject) && Ok(testsProject) && _service.DesignTimeProgress is { Completed: true }
                                       && _service.RestoreState is "succeeded", TimeSpan.FromMinutes(4));
            if (restores)
            {
                // Built once before the restore, and again once it wrote the assets files.
                await WaitUntilAsync(() => { lock (log) { return log.Count(l => l.Contains("Design-time build of", StringComparison.Ordinal)) >= 2; } }, TimeSpan.FromMinutes(4));

                // Then quiet: the file watcher's evaluations after the restore are done too.
                var lines = -1;
                await WaitUntilAsync(() =>
                {
                    Thread.Sleep(3000);
                    lock (log)
                    {
                        // An evaluation in progress (whose design-time builds come next) shows in Progress.
                        var settled = log.Count == lines && _service.Progress is { Completed: true } && _service.DesignTimeProgress is { Completed: true };
                        lines = log.Count;
                        return settled;
                    }
                }, TimeSpan.FromMinutes(2));
            }

            // The last batch's lines: "Design-time build of n project(s)", then the cache's hits and misses (every
            // project built or taken from the cache is one or the other; only an empty batch has none). Waiting for the
            // completed progress alone returned before they arrived (the Linux runner: the warm open had no such line).
            await WaitUntilAsync(() =>
            {
                lock (log)
                {
                    var last = log.LastIndexOf(completedMarker);
                    var batch = last < 0 ? -1 : log.FindIndex(last, l => l.Contains("Design-time build of", StringComparison.Ordinal));
                    return batch >= 0
                           && ((log[batch].Contains("Design-time build of 0 project(s)", StringComparison.Ordinal) && log[batch].EndsWith(" ms.", StringComparison.Ordinal))
                               || log.FindIndex(batch, l => l.Contains("Design-time build cache:", StringComparison.Ordinal)) >= 0);
                }
            }, TimeSpan.FromMinutes(1));

            lock (log)
            {
                return string.Join(Environment.NewLine, log.Where(l => l.Contains("Design-time build of", StringComparison.Ordinal)
                                                                       || l.Contains("Evaluated", StringComparison.Ordinal) || l.Contains("Packages of", StringComparison.Ordinal) || l.Contains("build cache:", StringComparison.Ordinal)));
            }
        }

        var cold = await OpenAsync(restores: true);
        // The first design-time builds find no cache. (After the restore, projects whose assets it didn't change come
        // from the cache this session just wrote: on the macOS runner that was 2 of 3.)
        var firstCache = cold.Split('\n').FirstOrDefault(l => l.Contains("build cache:", StringComparison.Ordinal)) ?? string.Empty;
        Assert.True(firstCache.Contains(" 0 hit(s)", StringComparison.Ordinal), $"Cold:{Environment.NewLine}{cold}");
        var coldApp = _service.CompilerInputs[AppProject];

        // Unchanged: nothing builds, the inputs are the same, and the packages restored a moment ago aren't restored again.
        var warm = await OpenAsync();
        Assert.True(warm.Contains("Design-time build of 0 project(s)", StringComparison.Ordinal), $"Cold:{Environment.NewLine}{cold}{Environment.NewLine}Warm:{Environment.NewLine}{warm}");
        Assert.Contains("3 unchanged project(s) from the cache", warm, StringComparison.Ordinal);
        Assert.Contains("Design-time build cache: 3 hit(s), 0 miss(es).", warm, StringComparison.Ordinal);
        Assert.Equal(3, _service.DesignTimeProgress?.CacheHits); // M34c: what the bench event designtime_done reports
        Assert.Empty(_service.DesignTimeProgress?.CacheMisses ?? ["none sent"]);
        Assert.Equal(coldApp.Arguments, _service.CompilerInputs[AppProject].Arguments);
        lock (restoreStates)
        {
            Assert.Equal(["succeeded"], restoreStates);
        }

        // Packages the design-time build can't read, though nothing is newer than their restore (0.9: projects of another
        // repository whose global.json pins another SDK, restored by it): the host restores once, and they build again.
        var assets = Path.Combine(Fixture, "src", "App", "obj", "project.assets.json");
        var written = File.GetLastWriteTimeUtc(assets);
        var text = File.ReadAllText(assets);
        Assert.Contains("\"net10.0\": {", text, StringComparison.Ordinal);
        File.WriteAllText(assets, System.Text.RegularExpressions.Regex.Replace(text, @"(""targets"": \{\s*"")net10\.0""", "${1}net8.0\""));
        Assert.NotEqual(text, File.ReadAllText(assets));
        File.SetLastWriteTimeUtc(assets, written);

        // ResolvePackageAssets reads the file again (another SDK finds its cache of the file stale, its settings differ).
        foreach (var cache in Directory.EnumerateFiles(Path.Combine(Fixture, "src", "App", "obj"), "*.assets.cache", SearchOption.AllDirectories))
        {
            File.Delete(cache);
        }
        lock (log)
        {
            log.Clear();
        }

        await _service.OpenAsync(Slnx, trusted: true, restore: true, Timeout());
        await WaitUntilAsync(() => { lock (log) { return log.Any(l => l.Contains("couldn't read their packages (App.csproj): restoring.", StringComparison.Ordinal)); } }, TimeSpan.FromMinutes(2));
        // (NuGet reports this test's broken file, which it rewrites anyway; another SDK's file it reads.)
        await WaitUntilAsync(() => _service.RestoreState is "succeeded" or "failed" && Ok(AppProject) && _service.DesignTimeProgress is { Completed: true }, TimeSpan.FromMinutes(4));
        Assert.Contains("\"net10.0\": {", File.ReadAllText(assets), StringComparison.Ordinal);
        Assert.Contains("Packages of 3 project(s) are up to date", await OpenAsync(), StringComparison.Ordinal);

        // The watcher evaluates Core alone (a file added to its folder, as NuGet's outputs after a restore): the
        // projects that reference it build again with it, since their keys hold Core's fingerprint, and the next open
        // finds all three in the cache.
        lock (log)
        {
            log.Clear();
        }

        File.WriteAllText(Path.Combine(Fixture, "src", "Core", "Notes.txt"), "Not compiled.");
        await WaitUntilAsync(() =>
        {
            lock (log)
            {
                return log.Any(l => l.Contains("Core.csproj: inputs changed", StringComparison.Ordinal)) && _service.DesignTimeProgress is { Completed: true };
            }
        }, TimeSpan.FromMinutes(2));
        var watched = await OpenAsync();
        Assert.True(watched.Contains("Design-time build cache: 3 hit(s), 0 miss(es).", StringComparison.Ordinal), $"Watched:{Environment.NewLine}{watched}");

        // A new source file in Core (a glob, no project file change): Core builds again, and so do the projects that
        // reference it; asking for design-time builds bypasses the cache.
        File.WriteAllText(Path.Combine(Fixture, "src", "Core", "Added.cs"), "namespace Core; public static class Added { }");
        var changed = await OpenAsync();
        Assert.DoesNotContain("Design-time build of 0 project(s)", changed, StringComparison.Ordinal);
        Assert.Contains(_service.CompilerInputs[CoreProject].Arguments, a => a.EndsWith("Added.cs", StringComparison.Ordinal));
        Assert.Contains("Design-time build of 3 project(s)", changed, StringComparison.Ordinal);
        Assert.Contains(_service.DesignTimeProgress?.CacheMisses ?? [], m => m.StartsWith("Core.csproj: ", StringComparison.Ordinal));

        lock (log)
        {
            log.Clear();
        }

        await _service.DesignTimeBuildAsync(CoreProject, Timeout());
        await WaitUntilAsync(() => { lock (log) { return log.Any(l => l.Contains("Design-time build of 1 project(s)", StringComparison.Ordinal)); } });

        // A corrupt cache is a miss, never an error.
        File.WriteAllText(Path.Combine(Fixture, "src", "App", "obj", "codalus.designtime.json"), "{ not json");
        var corrupt = await OpenAsync();
        Assert.Contains("Design-time build of 1 project(s)", corrupt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Untrusted_solutions_get_no_design_time_builds()
    {
        await _service.OpenAsync(Slnx, trusted: false, restore: false, Timeout());
        await Task.Delay(500, TestContext.Current.CancellationToken);
        Assert.Empty(_service.CompilerInputs);
        await Assert.ThrowsAsync<JsonRpcRemoteException>(() => _service.DesignTimeBuildAsync(null, Timeout()));
    }
}
