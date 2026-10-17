using Codalus.Projects.Host;
using Codalus.Projects.Protocol;
using Codalus.Workbench.Build;
using Codalus.Workbench.Output;
using Codalus.Workbench.Projects;

namespace Codalus.Projects.Tests;

/// <summary>
/// .NET Framework projects (M43a, ADR 0075) in the real build host on a copy of samples/NetFramework, on every OS:
/// SDK-style <c>net48</c> projects restore the reference assemblies package and build; the old-style project gets its
/// <c>packages.config</c> package (from the fixture's local feed) and the reference assemblies from Codalus's own
/// restore, loads with C# 7.3 and builds with the SDK's MSBuild; the project that imports Visual Studio's web targets
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
        _solutions = new SolutionService(() => BuildHostIntegrationTests.HostAssemblyPath);
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
        Directory.CreateDirectory(Path.GetDirectoryName(web)!);
        File.WriteAllText(web, "<Project />");
        Assert.Null(NetFramework.VisualStudioTargetsReason("WebApp", [(web, null)])); // there (the user's Build Tools): it builds
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
                  && inputs.Arguments.Any(a => a.StartsWith("/reference:" + gpf, StringComparison.Ordinal) && a.EndsWith("System.Core.dll", StringComparison.OrdinalIgnoreCase)),
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
                  && app.Arguments.Any(a => a.Contains("Codalus.Sample.Legacy.dll", StringComparison.OrdinalIgnoreCase))
                  && _solutions.CompilerInputs.TryGetValue(console, out var sdk) && sdk.Arguments.Any(a => a.Contains("microsoft.netframework.referenceassemblies.net48", StringComparison.OrdinalIgnoreCase)),
            () => $"restore: {string.Join(", ", restore)}; {DesignTimeState()}");

        // The SDK-style app: net48, C# 7.3, the reference assemblies package as its framework.
        var sdkInfo = _solutions.Projects[console];
        Assert.False(sdkInfo.IsNonSdkStyle);
        Assert.Equal(["net48"], sdkInfo.TargetFrameworks);
        Assert.Equal("Exe", sdkInfo.OutputType);
        Assert.Contains(sdkInfo.References, r => r.Kind == ReferenceKinds.Framework && r.Name == "Microsoft.NETFramework.ReferenceAssemblies");
        var sdkArguments = _solutions.CompilerInputs[console].Arguments;
        Assert.Contains("/langversion:7.3", sdkArguments);
        Assert.Contains(sdkArguments, a => a.Contains("microsoft.netframework.referenceassemblies.net48", StringComparison.OrdinalIgnoreCase) && a.EndsWith("mscorlib.dll", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(["net48", "net472"], _solutions.Projects[Project("Library")].TargetFrameworks);
        Assert.True(_solutions.Projects[Project("ConsoleApp.Tests")].IsTestProject);
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
        Assert.Equal("Microsoft.NETFramework.ReferenceAssemblies.net472", framework.Name);
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
        Assert.Contains(inputs.Arguments, a => a.Contains("microsoft.netframework.referenceassemblies.net472", StringComparison.OrdinalIgnoreCase) && a.EndsWith("System.Configuration.dll", StringComparison.OrdinalIgnoreCase));
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
        Assert.Equal(CompilerInputStates.Ok, _solutions.CompilerInputs[Project("LegacyWebApp")].State);
        Assert.Contains(_solutions.CompilerInputs[Project("LegacyWebApp")].Arguments, a => a.EndsWith("System.Web.dll", StringComparison.OrdinalIgnoreCase));

        // The solution builds without it, saying so.
        var result = await _builds.BuildAsync(BuildTargets.Build, null, Timeout());
        Assert.True(result.Succeeded, _builds.Channel.GetText());
        Assert.Contains("LegacyWebApp is left out of the build", _builds.Channel.GetText(), StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(Fixture, "LegacyApp", "bin", "Debug", "LegacyApp.exe")));
        Assert.True(File.Exists(Path.Combine(Fixture, "LegacyApp", "bin", "Debug", "Codalus.Sample.Legacy.dll")));
        Assert.True(File.Exists(Path.Combine(Fixture, "LegacyApp", "bin", "Debug", "LegacyApp.exe.config")));
        Assert.True(File.Exists(Path.Combine(Fixture, "ConsoleApp", "bin", "Debug", "net48", "ConsoleApp.exe")));
        Assert.True(File.Exists(Path.Combine(Fixture, "ConsoleApp", "bin", "Debug", "net48", "ConsoleApp.exe.config")));
        Assert.True(File.Exists(Path.Combine(Fixture, "WinFormsApp", "bin", "Debug", "net48", "WinFormsApp.exe")));
        Assert.True(File.Exists(Path.Combine(Fixture, "ConsoleApp.Tests", "bin", "Debug", "net48", "ConsoleApp.Tests.dll")));
        Assert.False(File.Exists(Path.Combine(Fixture, "LegacyWebApp", "bin", "LegacyWebApp.dll")));

        // On its own it doesn't build, and says why.
        result = await _builds.BuildAsync(BuildTargets.Build, Project("LegacyWebApp"), Timeout());
        Assert.False(result.Succeeded);
        Assert.Contains("imports Visual Studio's own MSBuild targets", _builds.Channel.GetText(), StringComparison.Ordinal);

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
