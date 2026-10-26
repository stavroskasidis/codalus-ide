using Codalus.Projects.Protocol;
using Codalus.Rpc;
using Codalus.Workbench.Projects;

namespace Codalus.Projects.Tests;

/// <summary>
/// Project properties, references, configurations, dependencies, load/unload, filters and SDKs (ADR 0013) through the
/// real build host, on a copy of samples/ProjectSystem.
/// </summary>
public sealed class ProjectManagementIntegrationTests : IAsyncLifetime
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(120);
    private readonly string _root = Path.Combine(Codalus.Platform.RealPath.Resolve(Path.GetTempPath()), "codalus-projmgmt-" + Guid.NewGuid().ToString("N"));
    private SolutionService _service = null!;
    private ProjectManagementService _management = null!;

    private string Fixture => Path.Combine(_root, "ProjectSystem");

    private string Slnx => Path.Combine(Fixture, "ProjectSystem.slnx");

    private string App => Path.Combine(Fixture, "src", "App", "App.csproj");

    private string Core => Path.Combine(Fixture, "src", "Core", "Core.csproj");

    private string Tests => Path.Combine(Fixture, "tests", "Core.Tests", "Core.Tests.csproj");

    private static CancellationToken Timeout() =>
        CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken, new CancellationTokenSource(s_timeout).Token).Token;

    public ValueTask InitializeAsync()
    {
        Copy(Path.Combine(BuildHostIntegrationTests.RepoRoot, "samples", "ProjectSystem"), Fixture);
        File.WriteAllText(Path.Combine(_root, "Directory.Build.props"), "<Project />");
        Codalus.Tests.FixtureSdk.Pin(_root);
        File.WriteAllText(Path.Combine(_root, "Directory.Packages.props"), "<Project><PropertyGroup><ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally></PropertyGroup></Project>");
        _service = new SolutionService(() => BuildHostIntegrationTests.HostAssemblyPath, new SupervisionPolicy { InitialBackoff = TimeSpan.FromMilliseconds(50) });
        _service.Log += (_, line) => Console.Error.WriteLine("[host] " + line);
        _management = new ProjectManagementService(_service);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _service.DisposeAsync();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
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

    private async Task OpenAsync(string? path = null)
    {
        var solution = await _service.OpenAsync(path ?? Slnx, trusted: true, restore: false, Timeout());
        await WaitUntilAsync(() => _service.Progress is { Completed: true } && _service.Projects.Count == solution.Projects.Length);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + s_timeout;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out.");
            await Task.Delay(25, TestContext.Current.CancellationToken);
        }
    }

    private static string Text(string path) => File.ReadAllText(path).ReplaceLineEndings("\n");

    // ------------------------------------------------------------------ properties

    [Fact]
    public async Task Properties_report_their_values_and_where_they_come_from()
    {
        await OpenAsync();
        var result = await _management.GetPropertiesAsync(App, ["OutputType", "Nullable", "TargetFramework", "Configuration", "AssemblyName", "Optimize", "NoSuchProperty"], null, Timeout());
        Assert.Equal("Debug", result.Configuration);
        Assert.Equal(["Debug", "Release"], result.Configurations);
        var p = result.Properties.ToDictionary(v => v.Name);

        Assert.Equal(("Exe", PropertyOrigins.Project, App, 3), (p["OutputType"].Value, p["OutputType"].Origin, p["OutputType"].DefinedIn, p["OutputType"].Line));
        Assert.Equal("Exe", p["OutputType"].ProjectValue);
        Assert.Equal(("enable", PropertyOrigins.Import), (p["Nullable"].Value, p["Nullable"].Origin));
        Assert.Equal(Path.Combine(Fixture, "Directory.Build.props"), p["Nullable"].DefinedIn);
        Assert.Null(p["Nullable"].ProjectValue);
        Assert.Equal(("Debug", PropertyOrigins.Global), (p["Configuration"].Value, p["Configuration"].Origin));
        Assert.Equal(("App", PropertyOrigins.Sdk), (p["AssemblyName"].Value, p["AssemblyName"].Origin));
        Assert.Equal(("", PropertyOrigins.Unset), (p["NoSuchProperty"].Value, p["NoSuchProperty"].Origin));

        // Another configuration: evaluated with it.
        var release = await _management.GetPropertiesAsync(App, ["Optimize", "Configuration"], "Release", Timeout());
        Assert.Equal("true", release.Properties.Single(v => v.Name == "Optimize").Value);
        Assert.Equal("Release", release.Configuration);
    }

    [Fact]
    public async Task Setting_properties_writes_the_minimal_change()
    {
        await OpenAsync();

        // Update in place; add to the first unconditioned group; override an imported value.
        Assert.True(await _management.SetPropertyAsync(App, "OutputType", "WinExe", null, Timeout()));
        Assert.True(await _management.SetPropertyAsync(App, "RootNamespace", "Acme.App", null, Timeout()));
        Assert.True(await _management.SetPropertyAsync(App, "Nullable", "disable", null, Timeout()));
        Assert.False(await _management.SetPropertyAsync(App, "RootNamespace", "Acme.App", null, Timeout()));

        // One configuration: a conditioned group, created after the last property group.
        Assert.True(await _management.SetPropertyAsync(App, "Optimize", "false", "Release", Timeout()));
        Assert.True(await _management.SetPropertyAsync(App, "DefineConstants", "$(DefineConstants);SHIPPING", "Release", Timeout()));
        Assert.Equal("""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>WinExe</OutputType>
                <TargetFramework>net10.0</TargetFramework>
                <RootNamespace>Acme.App</RootNamespace>
                <Nullable>disable</Nullable>
              </PropertyGroup>
              <PropertyGroup Condition="'$(Configuration)' == 'Release'">
                <Optimize>false</Optimize>
                <DefineConstants>$(DefineConstants);SHIPPING</DefineConstants>
              </PropertyGroup>
              <ItemGroup>
            """.ReplaceLineEndings("\n"), Text(App)[..Text(App).IndexOf("  <ItemGroup>", StringComparison.Ordinal)] + "  <ItemGroup>");

        var release = await _management.GetPropertiesAsync(App, ["Optimize", "DefineConstants", "Nullable"], "Release", Timeout());
        var optimize = release.Properties.Single(v => v.Name == "Optimize");
        Assert.Equal(("false", "false", PropertyOrigins.Project), (optimize.Value, optimize.ProjectValue, optimize.Origin));
        Assert.Equal("'$(Configuration)' == 'Release'", optimize.Condition);
        Assert.Contains("SHIPPING", release.Properties.Single(v => v.Name == "DefineConstants").Value, StringComparison.Ordinal);
        Assert.Null(release.Properties.Single(v => v.Name == "Nullable").ProjectValue); // set for all configurations, not in Release's scope
        var debug = await _management.GetPropertiesAsync(App, ["Optimize"], "Debug", Timeout());
        Assert.Equal("false", debug.Properties.Single().Value);
        Assert.Null(debug.Properties.Single().ProjectValue);

        // Resetting removes the project's value (and an emptied group): the imported value is back.
        Assert.True(await _management.SetPropertyAsync(App, "Nullable", null, null, Timeout()));
        Assert.True(await _management.SetPropertyAsync(App, "Optimize", null, "Release", Timeout()));
        Assert.True(await _management.SetPropertyAsync(App, "DefineConstants", null, "Release", Timeout()));
        Assert.DoesNotContain("Condition=\"'$(Configuration)' == 'Release'\"", File.ReadAllText(App), StringComparison.Ordinal);
        var nullable = (await _management.GetPropertiesAsync(App, ["Nullable"], null, Timeout())).Properties.Single();
        Assert.Equal(("enable", PropertyOrigins.Import), (nullable.Value, nullable.Origin));

        // The classic IDE's 'Configuration|Platform' groups count as that configuration's scope.
        File.WriteAllText(Core, Text(Core).Replace("</Project>", "  <PropertyGroup Condition=\" '$(Configuration)|$(Platform)' == 'Release|AnyCPU' \">\n    <DebugType>none</DebugType>\n  </PropertyGroup>\n</Project>", StringComparison.Ordinal));
        Assert.True(await _management.SetPropertyAsync(Core, "DebugType", "portable", "Release", Timeout()));
        Assert.Contains("<DebugType>portable</DebugType>", File.ReadAllText(Core), StringComparison.Ordinal);
        Assert.Single(File.ReadAllLines(Core), l => l.Contains("<PropertyGroup Condition", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ references

    [Fact]
    public async Task Assembly_analyzer_and_project_references_are_added_and_removed()
    {
        await OpenAsync();
        var lib = Path.Combine(Fixture, "lib");
        Directory.CreateDirectory(lib);
        var assembly = Path.Combine(lib, "Vendor.Widgets.dll");
        var analyzer = Path.Combine(lib, "Acme.Analyzers.dll");
        File.WriteAllBytes(assembly, [0]);
        File.WriteAllBytes(analyzer, [0]);

        await _management.AddReferenceAsync(Core, ReferenceEditKinds.Assembly, assembly, Timeout());
        await _management.AddReferenceAsync(Core, ReferenceEditKinds.Analyzer, analyzer, Timeout());
        await _management.AddReferenceAsync(Tests, ReferenceEditKinds.Project, App, Timeout());
        await _management.AddReferenceAsync(Core, ReferenceEditKinds.Assembly, assembly, Timeout()); // already there: no change
        var core = Text(Core);
        Assert.Contains("<Reference Include=\"Vendor.Widgets\">\n      <HintPath>..\\..\\lib\\Vendor.Widgets.dll</HintPath>\n    </Reference>".Replace('\\', Path.DirectorySeparatorChar == '\\' ? '\\' : '/'), core, StringComparison.Ordinal);
        Assert.Contains("<Analyzer Include=", core, StringComparison.Ordinal);
        Assert.Single(core.Split('\n'), l => l.Contains("Vendor.Widgets\"", StringComparison.Ordinal));
        await WaitUntilAsync(() => _service.Projects[Core].References.Any(r => r is { Kind: ReferenceKinds.Assembly, Name: "Vendor.Widgets" }));
        Assert.Contains("App.csproj", File.ReadAllText(Tests), StringComparison.Ordinal);

        await _management.RemoveReferenceAsync(Core, ReferenceEditKinds.Assembly, assembly, Timeout());
        await _management.RemoveReferenceAsync(Core, ReferenceEditKinds.Analyzer, analyzer, Timeout());
        await _management.RemoveReferenceAsync(Tests, ReferenceEditKinds.Project, App, Timeout());
        Assert.Equal(Text(Path.Combine(BuildHostIntegrationTests.RepoRoot, "samples", "ProjectSystem", "src", "Core", "Core.csproj")), Text(Core));
        await Assert.ThrowsAsync<JsonRpcRemoteException>(() => _management.RemoveReferenceAsync(Core, ReferenceEditKinds.Analyzer, analyzer, Timeout()));
    }

    // ------------------------------------------------------------------ configurations and dependencies

    [Fact]
    public async Task The_configuration_manager_reads_and_edits_project_mappings()
    {
        await OpenAsync();
        var configurations = await _management.GetConfigurationsAsync(Timeout());
        Assert.Equal(["Debug", "Release"], configurations.BuildTypes);
        Assert.Equal(["Any CPU", "x64", "x86"], configurations.Platforms);
        var app = configurations.Projects.Single(p => p.Project == App);
        Assert.Equal(6, app.Entries.Length);
        var releaseX64 = app.Entries.Single(e => e is { SolutionConfiguration: "Release", SolutionPlatform: "x64" });
        Assert.True(releaseX64.Build);
        Assert.Equal("Release", releaseX64.Configuration);

        // Don't build App in Release|x64, and map it to Debug in Release|x86.
        await _management.SetProjectConfigurationAsync(new SetProjectConfigurationParams(App, "Release", "x64", null, null, Build: false), Timeout());
        await _management.SetProjectConfigurationAsync(new SetProjectConfigurationParams(App, "Release", "x86", "Debug", null, null), Timeout());
        app = (await _management.GetConfigurationsAsync(Timeout())).Projects.Single(p => p.Project == App);
        Assert.False(app.Entries.Single(e => e is { SolutionConfiguration: "Release", SolutionPlatform: "x64" }).Build);
        Assert.Equal("Debug", app.Entries.Single(e => e is { SolutionConfiguration: "Release", SolutionPlatform: "x86" }).Configuration);
        Assert.True(app.Entries.Single(e => e is { SolutionConfiguration: "Debug", SolutionPlatform: "x64" }).Build);

        // A new build type copied from Release keeps Release's project mappings.
        await _management.AddConfigurationAsync(ConfigurationDimensions.BuildType, "Staging", "Release", Timeout());
        configurations = await _management.GetConfigurationsAsync(Timeout());
        Assert.Contains("Staging", configurations.BuildTypes);
        app = configurations.Projects.Single(p => p.Project == App);
        Assert.Equal("Release", app.Entries.Single(e => e is { SolutionConfiguration: "Staging", SolutionPlatform: "Any CPU" }).Configuration);
        Assert.False(app.Entries.Single(e => e is { SolutionConfiguration: "Staging", SolutionPlatform: "x64" }).Build);
        await WaitUntilAsync(() => _service.Solution!.Configurations.Any(c => c.Configuration == "Staging"));

        await _management.RemoveConfigurationAsync(ConfigurationDimensions.BuildType, "Staging", Timeout());
        Assert.DoesNotContain("Staging", (await _management.GetConfigurationsAsync(Timeout())).BuildTypes);
        var duplicate = await Assert.ThrowsAsync<JsonRpcRemoteException>(() => _management.AddConfigurationAsync(ConfigurationDimensions.Platform, "x64", null, Timeout()));
        Assert.Contains("already", duplicate.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Build_dependencies_are_saved_and_order_the_build()
    {
        await OpenAsync();
        await _management.SetDependenciesAsync(Core, [Tests], Timeout());
        var dependencies = await _management.GetDependenciesAsync(Timeout());
        Assert.Equal([Tests], dependencies.Single(d => d.Project == Core).DependsOn);
        Assert.Contains("BuildDependency", File.ReadAllText(Slnx), StringComparison.Ordinal);

        var order = ProjectManagementService.BuildOrder([App, Core, Tests], _service.Projects, dependencies);
        Assert.True(order.ToList().IndexOf(Core) < order.ToList().IndexOf(App)); // App references Core
        Assert.True(order.ToList().IndexOf(Tests) < order.ToList().IndexOf(Core)); // Core depends on Tests (declared)

        await _management.SetDependenciesAsync(Core, [], Timeout());
        Assert.Empty((await _management.GetDependenciesAsync(Timeout())).Single(d => d.Project == Core).DependsOn);
        await Assert.ThrowsAsync<JsonRpcRemoteException>(() => _management.SetDependenciesAsync(Core, [Core], Timeout()));
    }

    // ------------------------------------------------------------------ load, unload, filters

    [Fact]
    public async Task Unloaded_projects_are_remembered_skipped_by_builds_and_reloaded()
    {
        await OpenAsync();
        await _management.UnloadAsync(Tests, Timeout());
        await WaitUntilAsync(() => _service.Projects[Tests].State == ProjectStates.Unloaded);
        Assert.Contains("Core.Tests.csproj", File.ReadAllText(UnloadedStateFile), StringComparison.Ordinal);
        Assert.Equal(Path.Combine(Fixture, ".codalus", "state", "ProjectSystem.slnx", "solution.json"), UnloadedStateFile); // per user, next to the solution (ADR 0070)
        Assert.Contains("*", File.ReadAllLines(Path.Combine(Fixture, ".codalus", "state", ".gitignore")));

        // Reopened: still unloaded.
        await OpenAsync();
        Assert.Equal(ProjectStates.Unloaded, _service.Projects[Tests].State);
        Assert.Equal(ProjectStates.Loaded, _service.Projects[App].State);

        var output = new System.Collections.Concurrent.ConcurrentQueue<string>();
        _service.BuildOutput += (_, line) => output.Enqueue(line);
        _service.BuildDiagnostic += (_, d) => output.Enqueue($"{d.Severity} {d.Code}: {d.Message} ({d.File})");
        var build = await _service.BuildAsync(BuildTargets.Build, null, Timeout());
        Assert.True(build.Succeeded, "The build failed:\n" + string.Join("\n", output.TakeLast(60)));
        Assert.True(Directory.Exists(Path.Combine(Fixture, "src", "App", "bin")));
        // Not built: no assembly. (bin/ itself may exist: a design-time build from before the unload creates the output
        // folder in PrepareForBuild, which a loaded CI runner reaches before the unload.)
        var testsBin = Path.Combine(Fixture, "tests", "Core.Tests", "bin");
        Assert.False(Directory.Exists(testsBin) && Directory.EnumerateFiles(testsBin, "Core.Tests.dll", SearchOption.AllDirectories).Any());

        await _management.ReloadAsync(Tests, Timeout());
        await WaitUntilAsync(() => _service.Projects[Tests].State == ProjectStates.Loaded);
        Assert.DoesNotContain("Core.Tests.csproj", File.ReadAllText(UnloadedStateFile), StringComparison.Ordinal);
    }

    private string UnloadedStateFile => Path.Combine(SolutionStateLayout.Directory(Slnx, isFolder: false), SolutionStateLayout.UnloadedProjectsFile);

    [Fact]
    public async Task Unloaded_projects_move_from_the_old_codalus_solution_json_into_the_state_folder()
    {
        var legacy = Path.Combine(Fixture, ".codalus", "solution.json");
        Directory.CreateDirectory(Path.GetDirectoryName(legacy)!);
        await File.WriteAllTextAsync(legacy, """{ "unloadedProjects": ["tests/Core.Tests/Core.Tests.csproj"] }""", TestContext.Current.CancellationToken);

        await OpenAsync();
        Assert.Equal(ProjectStates.Unloaded, _service.Projects[Tests].State);
        Assert.Equal(ProjectStates.Loaded, _service.Projects[App].State);
        Assert.False(File.Exists(legacy));
        Assert.Contains("Core.Tests.csproj", File.ReadAllText(UnloadedStateFile), StringComparison.Ordinal);

        // Another solution in the same folder keeps its own unloaded projects.
        var other = Path.Combine(Fixture, "Other.slnx");
        File.Copy(Slnx, other);
        await OpenAsync(other);
        Assert.Equal(ProjectStates.Loaded, _service.Projects[Tests].State);
        await _management.UnloadAsync(App, Timeout());
        await WaitUntilAsync(() => _service.Projects[App].State == ProjectStates.Unloaded);
        Assert.True(File.Exists(Path.Combine(Fixture, ".codalus", "state", "Other.slnx", "solution.json")));
        await OpenAsync();
        Assert.Equal(ProjectStates.Unloaded, _service.Projects[Tests].State);
        Assert.Equal(ProjectStates.Loaded, _service.Projects[App].State);
    }

    [Fact]
    public async Task A_solution_filter_opens_its_solution_with_only_its_projects()
    {
        await OpenAsync();
        var filter = Path.Combine(Fixture, "AppOnly.slnf");
        await _management.SaveFilterAsync(filter, [App, Core], Timeout());
        Assert.Contains("ProjectSystem.slnx", File.ReadAllText(filter), StringComparison.Ordinal);

        var solution = await _service.OpenAsync(filter, trusted: true, restore: false, Timeout());
        Assert.Equal(Slnx, solution.Path);
        Assert.Equal(filter, solution.FilterPath);
        await WaitUntilAsync(() => _service.Progress is { Completed: true } && _service.Projects.Count == 3);
        Assert.Equal(ProjectStates.Unloaded, _service.Projects[Tests].State);
        Assert.Equal(ProjectStates.Loaded, _service.Projects[Core].State);
        Assert.False(File.Exists(UnloadedStateFile)); // a filter's state is the filter itself
    }

    // ------------------------------------------------------------------ SDKs

    [Fact]
    public async Task Sdk_information_and_global_json()
    {
        await OpenAsync();
        var info = await _management.GetSdkInfoAsync(Timeout());
        Assert.Null(info.Error);
        Assert.NotEmpty(info.Sdks);
        Assert.Contains(info.Sdks, s => s.Version == info.ResolvedVersion);
        Assert.NotNull(info.ResolvedSdk);

        var version = info.Sdks[^1].Version;
        await _management.SetGlobalJsonAsync(version, "latestFeature", Timeout());
        var globalJson = Path.Combine(Fixture, "global.json");
        if (info.GlobalJson is null)
        {
            Assert.True(File.Exists(globalJson));
        }

        info = await _management.GetSdkInfoAsync(Timeout());
        Assert.Equal((version, "latestFeature"), (info.RequestedVersion, info.RollForward));
        await _management.SetGlobalJsonAsync(null, null, Timeout());
        info = await _management.GetSdkInfoAsync(Timeout());
        Assert.Null(info.RequestedVersion);
    }

    [Fact]
    public void The_build_order_survives_dependency_cycles()
    {
        // Parsers are internal to the host; exercised through the protocol above. Here: the build order of a cycle.
        var a = Path.Combine(_root, "a.csproj");
        var b = Path.Combine(_root, "b.csproj");
        var order = ProjectManagementService.BuildOrder([a, b], new Dictionary<string, ProjectInfo>(), [new ProjectDependencies(a, [b]), new ProjectDependencies(b, [a])]);
        Assert.Equal(2, order.Count);
    }
}
