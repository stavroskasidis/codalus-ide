using Codalus.Projects.Host;
using Codalus.Projects.Protocol;
using Codalus.Workbench.Build;
using Codalus.Workbench.Interactive;
using Codalus.Workbench.Output;
using Codalus.Workbench.Projects;

namespace Codalus.Projects.Tests;

/// <summary>
/// .NET Framework projects (M43a, ADR 0075) in the real build host on a copy of samples/NetFramework, on every OS:
/// SDK-style <c>net48</c> projects restore the reference assemblies package and build; the old-style project gets its
/// <c>packages.config</c> package (from the fixture's local feed) and the reference assemblies from Codalus's own
/// restore (both use the Developer Pack's instead where it's installed, as on CI's Windows runners), loads with C# 7.3 and builds with the SDK's MSBuild; the project that imports Visual Studio's web targets
/// loads, says why it can't build and is left out of the solution's build; the converter's output builds. The reference
/// assemblies and xUnit come from nuget.org, as the other restore tests' targeting packs do.
/// </summary>
public sealed class NetFrameworkTests : IAsyncLifetime
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromMinutes(6);
    private readonly string _root = Path.Combine(Codalus.Platform.RealPath.Resolve(Path.GetTempPath()), "codalus-netfx-" + Guid.NewGuid().ToString("N"));
    private readonly OutputService _output = new();
    private SolutionService _solutions = null!;
    private BuildService _builds = null!;

    private string Fixture => Path.Combine(_root, "NetFramework");

    private string Project(string name) => Path.Combine(Fixture, name, name + ".csproj");

    private static CancellationToken Timeout() =>
        CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken, new CancellationTokenSource(s_timeout).Token).Token;

    public ValueTask InitializeAsync()
    {
        Copy(Path.Combine(BuildHostIntegrationTests.RepoRoot, "samples", "NetFramework"), Fixture);
        File.WriteAllText(Path.Combine(_root, "Directory.Build.props"), "<Project />");
        Codalus.Tests.FixtureSdk.Pin(_root);
        File.WriteAllText(Path.Combine(_root, "Directory.Packages.props"), "<Project><PropertyGroup><ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally></PropertyGroup></Project>");
        // The fixture's batches of design-time builds always use worker nodes, as on a runner with more processors: the
        // project loaded without its missing imports (LegacyWebApp) failed on one (PR #27's Linux CI).
        _solutions = new SolutionService(() => BuildHostIntegrationTests.HostAssemblyPath, hostEnvironment: async (folder, cancellationToken) =>
            new Dictionary<string, string>(await BuildHostRuntime.EnvironmentAsync(folder, cancellationToken) ?? new Dictionary<string, string>())
            {
                ["CODALUS_DESIGNTIME_NODES"] = "4",
            });
        _solutions.Log += (_, line) => Console.Error.WriteLine("[host] " + line);
        _solutions.RestoreOutput += (_, line) => Console.Error.WriteLine("[restore] " + line);
        _builds = new BuildService(_solutions, _output);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _solutions.DisposeAsync();
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

        foreach (var directory in Directory.EnumerateDirectories(source).Where(d => Path.GetFileName(d) is not ("bin" or "obj" or "packages")))
        {
            Copy(directory, Path.Combine(target, Path.GetFileName(directory)));
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, Func<string> message)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(started.Elapsed < s_timeout, "Timed out. " + message());
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }
    }

    /// <summary>
    /// The Developer Pack's reference assemblies of <paramref name="moniker"/> where they're installed (a Windows machine
    /// with Visual Studio or the targeting pack, as CI's runners), or null. The SDK and Codalus's targets then use them
    /// instead of the reference assemblies package.
    /// </summary>
    private static string? InstalledReferenceAssemblies(string moniker)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Reference Assemblies", "Microsoft", "Framework", ".NETFramework", NetFramework.VersionFolder(moniker));
        return File.Exists(Path.Combine(folder, "mscorlib.dll")) ? folder : null;
    }

    /// <summary>What the compiler's references to <paramref name="moniker"/>'s reference assemblies contain: the Developer Pack's folder, or the package's.</summary>
    private static string ReferenceAssembliesOf(string moniker) => InstalledReferenceAssemblies(moniker) ?? NetFramework.ReferenceAssembliesPackageOf(moniker);

    /// <summary>
    /// Whether the compiler's <c>/reference:</c> arguments have <paramref name="file"/> from a folder containing
    /// <paramref name="folder"/>. The arguments keep MSBuild's quotes (a path with spaces, as under Program Files (x86), is
    /// <c>/reference:"C:\...\mscorlib.dll"</c>), which the paths here are read without.
    /// </summary>
    private static bool HasReference(IReadOnlyList<string> arguments, string file, string? folder = null) =>
        InteractiveProjects.CompileReferences(arguments).Any(r =>
            Path.GetFileName(r).Equals(file, StringComparison.OrdinalIgnoreCase)
            && (folder is null || r.Contains(folder, StringComparison.OrdinalIgnoreCase)));

    /// <summary>Asserts <see cref="HasReference"/>, printing the <c>/reference:</c> arguments when it fails.</summary>
    private static void AssertReference(IReadOnlyList<string> arguments, string file, string? folder = null) =>
        Assert.True(
            HasReference(arguments, file, folder),
            $"No {file}{(folder is null ? "" : $" from {folder}")} among the /reference: arguments:\n{string.Join("\n", arguments.Where(a => a.StartsWith("/reference:", StringComparison.OrdinalIgnoreCase) || a.StartsWith("/r:", StringComparison.OrdinalIgnoreCase)))}");

    private string DesignTimeState() =>
        $"design-time builds: {string.Join("; ", _solutions.CompilerInputs.Select(i => $"{Path.GetFileName(i.Key)} {i.Value.State} {i.Value.TargetFramework} {i.Value.Error}"))}";

    // ------------------------------------------------------------------ units

    [Theory]
    [InlineData("v4.8", "net48")]
    [InlineData("v4.7.2", "net472")]
    [InlineData("v4.8.1", "net481")]
    [InlineData("v4.0", "net40")]
    [InlineData("v4.0.3", "net403")]
    [InlineData("v3.5", "net35")]
    [InlineData("v2.0", "net20")]
    [InlineData("4.6.1", "net461")]
    [InlineData("v5.0", null)]
    [InlineData("", null)]
    [InlineData("vNext", null)]
    public void Framework_versions_have_nugets_short_names(string version, string? moniker)
    {
        Assert.Equal(moniker, NetFramework.ShortName(version));
        if (moniker is not null)
        {
            Assert.True(NetFramework.IsMoniker(moniker));
            Assert.Equal(version.StartsWith('v') ? version : "v" + version, NetFramework.VersionFolder(moniker));
        }
    }

    [Theory]
    [InlineData("net48", true)]
    [InlineData("net472", true)]
    [InlineData("net20", true)]
    [InlineData("net8.0", false)]
    [InlineData("net11.0-windows", false)]
    [InlineData("netstandard2.0", false)]
    [InlineData("netcoreapp3.1", false)]
    public void Net_framework_monikers_are_told_apart(string moniker, bool expected) => Assert.Equal(expected, NetFramework.IsMoniker(moniker));

    [Fact]
    public void Packages_config_is_read()
    {
        var packages = NetFramework.ParsePackagesConfig("""
            <?xml version="1.0" encoding="utf-8"?>
            <packages>
              <package id="Newtonsoft.Json" version="13.0.3" targetFramework="net472" />
              <package id="StyleCop.Analyzers" version="1.1.118" targetFramework="net472" developmentDependency="true" />
              <package id="" version="1.0.0" />
              <package id="NoVersion" />
            </packages>
            """);
        Assert.Equal(2, packages.Count);
        Assert.Equal(new NetFramework.PackagesConfigEntry("Newtonsoft.Json", "13.0.3", "net472", false), packages[0]);
        Assert.True(packages[1].DevelopmentDependency);
        Assert.Empty(NetFramework.ParsePackagesConfig("<packages"));
        Assert.Equal(Path.Combine(_root, "packages"), NetFramework.RepositoryPath(null, _root));
        Assert.Equal(Path.Combine(_root, "lib", "pkgs"), NetFramework.RepositoryPath(Path.Combine("lib", "pkgs"), _root));
        Assert.Equal(Path.Combine(_root, "packages", "Newtonsoft.Json.13.0.3"), NetFramework.PackageFolder(Path.Combine(_root, "packages"), packages[0]));
    }

    [Fact]
    public void Visual_studios_own_targets_are_named_as_the_reason()
    {
        var web = Path.Combine(_root, "sdk", "Microsoft", "VisualStudio", "v17.0", "WebApplications", "Microsoft.WebApplication.targets");
        var reason = NetFramework.VisualStudioTargetsReason("WebApp", [(Path.Combine(_root, "sdk", "Microsoft.CSharp.targets"), null), (web, "'$(VSToolsPath)' != ''")]);
        Assert.NotNull(reason);
        Assert.Contains("WebApp imports Visual Studio's own MSBuild targets (WebApplications/Microsoft.WebApplication.targets)", reason, StringComparison.Ordinal);
        Assert.Null(NetFramework.VisualStudioTargetsReason("WebApp", [(web, "false")])); // never imported
        Assert.Null(NetFramework.VisualStudioTargetsReason("Tests", [(web, $"Exists('{web}')")])); // imported only where it is (M43b)
        Directory.CreateDirectory(Path.GetDirectoryName(web)!);
        File.WriteAllText(web, "<Project />");
        Assert.Null(NetFramework.VisualStudioTargetsReason("WebApp", [(web, null)])); // there (the user's Build Tools): it builds
    }

    [Theory]
    [InlineData("{3AC096D0-A1C2-E12C-1390-A8335801FDAB};{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}", true)]
    [InlineData("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}; {3ac096d0-a1c2-e12c-1390-a8335801fdab}", true)]
    [InlineData("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Old_style_test_projects_are_told_by_their_project_type(string? guids, bool expected) =>
        Assert.Equal(expected, NetFramework.IsTestProjectType(guids));

    [Fact]
    public void Old_style_projects_build_portable_pdbs_unless_they_keep_their_debug_type()
    {
        // M43b: the desktop-CLR debugger reads portable PDBs; Visual Studio's old projects ask for Windows ones.
        Assert.Contains("<DebugType>portable</DebugType>", NetFramework.ReferenceAssembliesTargetsText, StringComparison.Ordinal);
        Assert.Contains("'$(DebugType)' == 'full' or '$(DebugType)' == 'pdbonly'", NetFramework.ReferenceAssembliesTargetsText, StringComparison.Ordinal);
        Assert.Contains("'$(CodalusKeepDebugType)' != 'true'", NetFramework.ReferenceAssembliesTargetsText, StringComparison.Ordinal);
        Assert.Contains("'$(UsingMicrosoftNETSdk)' != 'true'", NetFramework.ReferenceAssembliesTargetsText, StringComparison.Ordinal);
    }

    [Fact]
    public void The_reference_assemblies_targets_are_imported_unless_the_user_imports_their_own()
    {
        var environment = new Dictionary<string, string?>();
        var path = NetFramework.UseReferenceAssemblies(Path.Combine(_root, "targets"), n => environment.GetValueOrDefault(n), (n, v) => environment[n] = v);
        Assert.NotNull(path);
        Assert.Equal(path, environment[NetFramework.CustomBeforeMicrosoftCommonTargets]);
        Assert.Equal(NetFramework.ReferenceAssembliesTargetsText, File.ReadAllText(path));
        Assert.Contains("<TargetFrameworkRootPath>", NetFramework.ReferenceAssembliesTargetsText, StringComparison.Ordinal);

        environment[NetFramework.CustomBeforeMicrosoftCommonTargets] = "/mine.targets";
        Assert.Null(NetFramework.UseReferenceAssemblies(Path.Combine(_root, "targets"), n => environment.GetValueOrDefault(n), (n, v) => environment[n] = v));
        Assert.Equal("/mine.targets", environment[NetFramework.CustomBeforeMicrosoftCommonTargets]);
    }

    /// <summary>
    /// .NET Framework 3.5 (best effort) in an old-style library opened on its own, with a global packages folder of its
    /// own: Codalus's restore installs the reference assemblies package (6.8 MB, from nuget.org) there, the project then
    /// loads with them and C# 7.3, and builds.
    /// </summary>
    [Fact]
    public async Task An_old_style_net35_library_gets_its_reference_assemblies_from_codalus_restore()
    {
        var folder = Path.Combine(_root, "Net35");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "nuget.config"), """
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <config>
                <add key="globalPackagesFolder" value="gpf" />
              </config>
              <packageSources>
                <clear />
                <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
              </packageSources>
            </configuration>
            """);
        File.WriteAllText(Path.Combine(folder, "Net35Lib.csproj"), """
            <?xml version="1.0" encoding="utf-8"?>
            <Project ToolsVersion="4.0" DefaultTargets="Build" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
              <PropertyGroup>
                <Configuration Condition=" '$(Configuration)' == '' ">Debug</Configuration>
                <Platform Condition=" '$(Platform)' == '' ">AnyCPU</Platform>
                <OutputType>Library</OutputType>
                <RootNamespace>Net35Lib</RootNamespace>
                <AssemblyName>Net35Lib</AssemblyName>
                <TargetFrameworkVersion>v3.5</TargetFrameworkVersion>
                <OutputPath>bin\$(Configuration)\</OutputPath>
              </PropertyGroup>
              <ItemGroup>
                <Reference Include="System" />
                <Reference Include="System.Core" />
              </ItemGroup>
              <ItemGroup>
                <Compile Include="Numbers.cs" />
              </ItemGroup>
              <Import Project="$(MSBuildToolsPath)\Microsoft.CSharp.targets" />
            </Project>
            """);
        File.WriteAllText(Path.Combine(folder, "Numbers.cs"), "using System.Linq;\n\nnamespace Net35Lib\n{\n    public static class Numbers\n    {\n        public static int Sum(int[] values) { return values.Sum(); }\n    }\n}\n");
        var project = Path.Combine(folder, "Net35Lib.csproj");
        var gpf = Path.Combine(folder, "gpf");

        await _solutions.OpenAsync(project, trusted: true, restore: true, Timeout());
        await WaitUntilAsync(
            () => _solutions.CompilerInputs.TryGetValue(project, out var inputs) && inputs.State == CompilerInputStates.Ok
                  && HasReference(inputs.Arguments, "System.Core.dll", gpf),
            DesignTimeState);

        Assert.True(File.Exists(Path.Combine(gpf, "microsoft.netframework.referenceassemblies.net35", "1.0.3", "build", ".NETFramework", "v3.5", "mscorlib.dll")));
        var info = _solutions.Projects[project];
        Assert.Equal(["net35"], info.TargetFrameworks);
        Assert.Contains(info.References, r => r.Kind == ReferenceKinds.Framework && r.Name == "Microsoft.NETFramework.ReferenceAssemblies.net35" && r.Resolved);
        Assert.Contains("/langversion:7.3", _solutions.CompilerInputs[project].Arguments);

        var result = await _builds.BuildAsync(BuildTargets.Build, project, Timeout());
        Assert.True(result.Succeeded, _builds.Channel.GetText());
        Assert.True(File.Exists(Path.Combine(folder, "bin", "Debug", "Net35Lib.dll")));
    }

    /// <summary>
    /// Convert to SDK-style's output (the golden file of the workbench's tests) builds: the old-style app converted,
    /// written as the IDE writes it, loads SDK-style with its package as a PackageReference from the local feed, keeps
    /// its stray file out (it would fail the build), and builds with the SDK's AssemblyInfo attributes.
    /// </summary>
    [Fact]
    public async Task The_converted_old_style_project_builds()
    {
        var legacy = Project("LegacyApp");
        var conversion = SdkStyleConverter.Convert(legacy, Path.Combine(Fixture, "packages"));
        File.WriteAllText(legacy, conversion.ProjectText);
        foreach (var file in conversion.ChangedFiles)
        {
            File.WriteAllText(file.Path, file.Text);
        }

        foreach (var file in conversion.DeletedFiles)
        {
            File.Delete(file);
        }

        await _solutions.OpenAsync(Path.Combine(Fixture, "NetFramework.slnx"), trusted: true, restore: true, Timeout());
        await WaitUntilAsync(
            () => _solutions.CompilerInputs.TryGetValue(legacy, out var inputs) && inputs.State == CompilerInputStates.Ok
                  && inputs.Arguments.Any(a => a.Contains("codalus.sample.legacy", StringComparison.OrdinalIgnoreCase)),
            DesignTimeState);

        var info = _solutions.Projects[legacy];
        Assert.False(info.IsNonSdkStyle);
        Assert.Equal(["net472"], info.TargetFrameworks);
        Assert.Contains(info.References, r => r.Kind == ReferenceKinds.Package && r.Name == "Codalus.Sample.Legacy" && r.Version == "1.0.0" && r.Resolved);
        Assert.DoesNotContain(info.Items, i => i.Path.EndsWith("NotInProject.cs", StringComparison.Ordinal));
        Assert.Contains("/langversion:7.3", _solutions.CompilerInputs[legacy].Arguments);

        var result = await _builds.BuildAsync(BuildTargets.Build, legacy, Timeout());
        Assert.True(result.Succeeded, _builds.Channel.GetText());
        var output = Path.Combine(Fixture, "LegacyApp", "bin", "Debug", "net472");
        Assert.True(File.Exists(Path.Combine(output, "LegacyApp.exe")));
        Assert.True(File.Exists(Path.Combine(output, "Codalus.Sample.Legacy.dll")));
        Assert.True(File.Exists(Path.Combine(output, "LegacyApp.exe.config")));
        var generated = Directory.EnumerateFiles(Path.Combine(Fixture, "LegacyApp", "obj"), "LegacyApp.AssemblyInfo.cs", SearchOption.AllDirectories).Single();
        var attributes = await File.ReadAllTextAsync(generated, TestContext.Current.CancellationToken);
        Assert.Contains("AssemblyCompanyAttribute(\"Codalus IDE fixtures\")", attributes, StringComparison.Ordinal);
        Assert.Contains("AssemblyVersionAttribute(\"1.2.0.0\")", attributes, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ the fixture

    /// <summary>
    /// The whole fixture: restore (dotnet restore, then the old-style project's packages.config package and reference
    /// assemblies), loading (frameworks, Dependencies, C# 7.3, the reference assemblies on the command line), the web
    /// project's reason, the solution build without it, its own build refused, and the run information.
    /// </summary>
    [Fact]
    public async Task Net_framework_projects_restore_load_and_build_on_every_os()
    {
        var restore = new List<string>();
        _solutions.RestoreStateChanged += (_, e) =>
        {
            lock (restore)
            {
                restore.Add(e.Message is null ? e.State : $"{e.State}: {e.Message}");
            }
        };

        var solution = await _solutions.OpenAsync(Path.Combine(Fixture, "NetFramework.slnx"), trusted: true, restore: true, Timeout());
        var legacy = Project("LegacyApp");
        var console = Project("ConsoleApp");
        await WaitUntilAsync(
            () => _solutions.CompilerInputs.Count(i => i.Value.State == CompilerInputStates.Ok) >= solution.Projects.Length - 1
                  && _solutions.CompilerInputs.TryGetValue(legacy, out var app) && app.State == CompilerInputStates.Ok
                  && HasReference(app.Arguments, "Codalus.Sample.Legacy.dll")
                  && _solutions.CompilerInputs.TryGetValue(console, out var sdk) && sdk.Arguments.Any(a => a.Contains(ReferenceAssembliesOf("net48"), StringComparison.OrdinalIgnoreCase))
                  && _solutions.Projects.TryGetValue(Project("LegacyTests"), out var tests) && tests.References.Count(r => r.Kind == ReferenceKinds.Package && r.Resolved) == 2,
            () => $"restore: {string.Join(", ", restore)}; {DesignTimeState()}");

        // The SDK-style app: net48, C# 7.3, the reference assemblies package as its framework (the Developer Pack where it's installed).
        var sdkInfo = _solutions.Projects[console];
        Assert.False(sdkInfo.IsNonSdkStyle);
        Assert.Equal(["net48"], sdkInfo.TargetFrameworks);
        Assert.Equal("Exe", sdkInfo.OutputType);
        Assert.Contains(sdkInfo.References, r => r.Kind == ReferenceKinds.Framework && r.Name == (InstalledReferenceAssemblies("net48") is null ? "Microsoft.NETFramework.ReferenceAssemblies" : ".NETFramework,Version=v4.8"));
        var sdkArguments = _solutions.CompilerInputs[console].Arguments;
        Assert.Contains("/langversion:7.3", sdkArguments);
        AssertReference(sdkArguments, "mscorlib.dll", ReferenceAssembliesOf("net48"));
        Assert.Equal(["net48", "net472"], _solutions.Projects[Project("Library")].TargetFrameworks);
        Assert.True(_solutions.Projects[Project("ConsoleApp.Tests")].IsTestProject);
        Assert.Equal(TestPlatforms.VSTest, _solutions.Projects[Project("NUnitTests")].TestPlatform);

        // The old-style MSTest project (M43b): a test project by its ProjectTypeGuids, its packages from packages.config.
        var legacyTests = _solutions.Projects[Project("LegacyTests")];
        Assert.True(legacyTests.IsNonSdkStyle);
        Assert.True(legacyTests.IsTestProject);
        Assert.Equal(TestPlatforms.VSTest, legacyTests.TestPlatform);
        Assert.Null(legacyTests.CannotBuildReason);
        Assert.Contains(legacyTests.References, r => r is { Kind: ReferenceKinds.Package, Name: "MSTest.TestAdapter", Resolved: true });
        Assert.True(_solutions.Projects[Project("WinFormsApp")].UsesWindowsForms);

        // The old-style app: net472, its items, its Dependencies (framework, packages.config, references, the project).
        var info = _solutions.Projects[legacy];
        Assert.Equal(ProjectStates.Loaded, info.State);
        Assert.True(info.IsNonSdkStyle);
        Assert.Null(info.CannotBuildReason);
        Assert.Equal(["net472"], info.TargetFrameworks);
        Assert.Equal("Exe", info.OutputType);
        Assert.Contains(info.Items, i => i.Kind == "Compile" && i.Path.EndsWith(Path.Combine("Properties", "AssemblyInfo.cs"), StringComparison.Ordinal));
        Assert.DoesNotContain(info.Items, i => i.Path.EndsWith("NotInProject.cs", StringComparison.Ordinal));
        Assert.Contains(info.Items, i => i.Kind == "None" && i.Path.EndsWith("packages.config", StringComparison.Ordinal));
        var framework = Assert.Single(info.References, r => r.Kind == ReferenceKinds.Framework);
        Assert.Equal(InstalledReferenceAssemblies("net472") is null ? "Microsoft.NETFramework.ReferenceAssemblies.net472" : ".NETFramework,Version=v4.7.2", framework.Name);
        Assert.True(framework.Resolved);
        var package = Assert.Single(info.References, r => r.Kind == ReferenceKinds.Package);
        Assert.Equal(("Codalus.Sample.Legacy", "1.0.0", true), (package.Name, package.Version, package.Resolved));
        Assert.Equal(Path.Combine(Fixture, "packages", "Codalus.Sample.Legacy.1.0.0"), package.Path);
        var hinted = Assert.Single(info.References, r => r.Kind == ReferenceKinds.Assembly && r.Name == "Codalus.Sample.Legacy");
        Assert.True(hinted.Resolved);
        Assert.Contains(info.References, r => r.Kind == ReferenceKinds.Assembly && r.Name == "System.Configuration");
        Assert.Contains(info.References, r => r.Kind == ReferenceKinds.Project && r.Path == Project("Library"));
        Assert.DoesNotContain(info.References, r => r.Name == "mscorlib"); // the reference the targets add is implicit
        Assert.True(info.Restored);

        // Its design-time build: C# 7.3, the reference assemblies, the package's assembly, the library's net472 build.
        var inputs = _solutions.CompilerInputs[legacy];
        Assert.Equal("net472", inputs.TargetFramework);
        Assert.Contains("/langversion:7.3", inputs.Arguments);
        AssertReference(inputs.Arguments, "System.Configuration.dll", ReferenceAssembliesOf("net472"));
        Assert.Contains(inputs.Arguments, a => a.EndsWith("Greeting.cs", StringComparison.Ordinal));
        Assert.Contains(Project("Library"), inputs.ProjectReferences);

        // The project that needs Visual Studio's targets: loaded, with the reason, and still IntelliSense.
        var web = _solutions.Projects[Project("LegacyWebApp")];
        Assert.Equal(ProjectStates.Loaded, web.State);
        Assert.True(web.IsNonSdkStyle);
        Assert.Equal(["net48"], web.TargetFrameworks);
        Assert.Contains("WebApplications/Microsoft.WebApplication.targets", web.CannotBuildReason, StringComparison.Ordinal);
        Assert.Contains("Visual Studio's is proprietary", web.CannotBuildReason, StringComparison.Ordinal);
        await WaitUntilAsync(() => _solutions.CompilerInputs.ContainsKey(Project("LegacyWebApp")), DesignTimeState);
        Assert.True(_solutions.CompilerInputs[Project("LegacyWebApp")].State == CompilerInputStates.Ok, DesignTimeState());
        AssertReference(_solutions.CompilerInputs[Project("LegacyWebApp")].Arguments, "System.Web.dll");

        // The solution builds without it, saying so.
        var result = await _builds.BuildAsync(BuildTargets.Build, null, Timeout());
        Assert.True(result.Succeeded, _builds.Channel.GetText());
        // Output notifications may be handled just after the result arrives (the result doesn't queue behind them).
        await WaitUntilAsync(() => _builds.Channel.GetText().Contains("LegacyWebApp is left out of the build", StringComparison.Ordinal), _builds.Channel.GetText);
        Assert.Contains(_builds.Diagnostics, d => d.Severity == BuildSeverities.Warning && string.Equals(d.File, Project("LegacyWebApp"), StringComparison.OrdinalIgnoreCase) && d.Message.StartsWith("LegacyWebApp is left out of the build", StringComparison.Ordinal));
        Assert.True(File.Exists(Path.Combine(Fixture, "LegacyApp", "bin", "Debug", "LegacyApp.exe")));
        Assert.True(File.Exists(Path.Combine(Fixture, "LegacyApp", "bin", "Debug", "Codalus.Sample.Legacy.dll")));
        Assert.True(File.Exists(Path.Combine(Fixture, "LegacyApp", "bin", "Debug", "LegacyApp.exe.config")));
        Assert.True(File.Exists(Path.Combine(Fixture, "ConsoleApp", "bin", "Debug", "net48", "ConsoleApp.exe")));
        Assert.True(File.Exists(Path.Combine(Fixture, "ConsoleApp", "bin", "Debug", "net48", "ConsoleApp.exe.config")));
        Assert.True(File.Exists(Path.Combine(Fixture, "WinFormsApp", "bin", "Debug", "net48", "WinFormsApp.exe")));
        Assert.True(File.Exists(Path.Combine(Fixture, "ConsoleApp.Tests", "bin", "Debug", "net48", "ConsoleApp.Tests.dll")));
        Assert.True(File.Exists(Path.Combine(Fixture, "NUnitTests", "bin", "Debug", "net48", "NUnitTests.dll")));
        Assert.True(File.Exists(Path.Combine(Fixture, "LegacyTests", "bin", "Debug", "LegacyTests.dll")));
        Assert.True(File.Exists(Path.Combine(Fixture, "LegacyTests", "bin", "Debug", "Microsoft.VisualStudio.TestPlatform.MSTest.TestAdapter.dll")));

        // Old-style projects' Windows PDBs (DebugType full) are built portable, which the debugger reads (M43b).
        Assert.Equal("BSJB"u8.ToArray(), File.ReadAllBytes(Path.Combine(Fixture, "LegacyApp", "bin", "Debug", "LegacyApp.pdb")).Take(4).ToArray());
        Assert.False(File.Exists(Path.Combine(Fixture, "LegacyWebApp", "bin", "LegacyWebApp.dll")));

        // On its own it doesn't build, and says why.
        result = await _builds.BuildAsync(BuildTargets.Build, Project("LegacyWebApp"), Timeout());
        Assert.False(result.Succeeded);
        await WaitUntilAsync(() => _builds.Channel.GetText().Contains("imports Visual Studio's own MSBuild targets", StringComparison.Ordinal), _builds.Channel.GetText);
        var blocked = Assert.Single(_builds.Diagnostics); // a failed build always says why in the Error List
        Assert.Equal(BuildSeverities.Error, blocked.Severity);
        Assert.Equal(Project("LegacyWebApp"), blocked.File, ignoreCase: true);
        Assert.Contains("imports Visual Studio's own MSBuild targets", blocked.Message, StringComparison.Ordinal);

        // How they start: the .exe itself (the desktop CLR runs it, not dotnet).
        var run = await _solutions.GetRunInfoAsync(legacy, Timeout());
        Assert.Equal("net472", run.TargetFramework);
        Assert.Equal(Path.Combine(Fixture, "LegacyApp", "bin", "Debug", "LegacyApp.exe"), run.TargetPath);
        var sdkRun = await _solutions.GetRunInfoAsync(console, Timeout());
        Assert.Equal("net48", sdkRun.TargetFramework);
        Assert.EndsWith("ConsoleApp.exe", sdkRun.TargetPath, StringComparison.Ordinal);
        if (OperatingSystem.IsWindows())
        {
            Assert.EndsWith("ConsoleApp.exe", sdkRun.RunCommand, StringComparison.Ordinal);
        }
    }
}
