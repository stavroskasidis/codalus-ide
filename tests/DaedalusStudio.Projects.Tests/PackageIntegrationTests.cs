using System.Diagnostics;
using DaedalusStudio.Projects.Protocol;
using DaedalusStudio.Rpc;
using DaedalusStudio.Workbench.Packages;
using DaedalusStudio.Workbench.Projects;

namespace DaedalusStudio.Projects.Tests;

/// <summary>
/// NuGet package management through the real build host (ADR 0012) on a copy of samples/Packages: its local feed,
/// source mapping and CPM folder. Project file edits are compared with what the dotnet CLI writes.
/// </summary>
public sealed class PackageIntegrationTests : IAsyncLifetime
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(120);
    private readonly string _root = Path.Combine(DaedalusStudio.Platform.RealPath.Resolve(Path.GetTempPath()), "daedalus-packages-" + Guid.NewGuid().ToString("N"));
    private SolutionService _service = null!;
    private PackageService _packages = null!;

    private string Fixture => Path.Combine(_root, "Packages");

    private string Slnx => Path.Combine(Fixture, "Packages.slnx");

    private string Plain => Path.Combine(Fixture, "src", "Plain", "Plain.csproj");

    private string Multi => Path.Combine(Fixture, "src", "Multi", "Multi.csproj");

    private string Central => Path.Combine(Fixture, "cpm", "Central", "Central.csproj");

    private string Other => Path.Combine(Fixture, "cpm", "Other", "Other.csproj");

    private string CentralProps => Path.Combine(Fixture, "cpm", "Directory.Packages.props");

    private static CancellationToken Timeout() =>
        CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken, new CancellationTokenSource(s_timeout).Token).Token;

    public ValueTask InitializeAsync()
    {
        Copy(Path.Combine(BuildHostIntegrationTests.RepoRoot, "samples", "Packages"), Fixture);
        File.WriteAllText(Path.Combine(_root, "Directory.Build.props"), "<Project />");
        DaedalusStudio.Tests.FixtureSdk.Pin(_root);
        File.WriteAllText(Path.Combine(_root, "Directory.Packages.props"), "<Project><PropertyGroup><ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally></PropertyGroup></Project>");
        _service = new SolutionService(() => BuildHostIntegrationTests.HostAssemblyPath, new SupervisionPolicy { InitialBackoff = TimeSpan.FromMilliseconds(50) });
        _service.Log += (_, line) => Console.Error.WriteLine("[host] " + line);
        _service.RestoreOutput += (_, line) => Console.Error.WriteLine("[restore] " + line);
        _packages = new PackageService(_service);
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

    /// <summary>Opens the fixture trusted and waits for the restore and the evaluation after it.</summary>
    private async Task OpenRestoredAsync()
    {
        var restored = WaitForRestoreAsync();
        var solution = await _service.OpenAsync(Slnx, trusted: true, restore: true, Timeout());
        await restored;
        await WaitUntilAsync(() => _service.Progress is { Completed: true } && _service.Projects.Count == solution.Projects.Length
                                   && _service.Projects.Values.Where(p => p.Path != Multi).All(p => p.Restored));
    }

    private Task<string> WaitForRestoreAsync()
    {
        var done = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(object? sender, RestoreStateChangedEventArgs e)
        {
            if (e.State != "running")
            {
                _service.RestoreStateChanged -= Handler;
                done.TrySetResult(e.State);
            }
        }

        _service.RestoreStateChanged += Handler;
        return done.Task.WaitAsync(s_timeout, TestContext.Current.CancellationToken);
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

    /// <summary>Runs the dotnet CLI on a fresh copy of the fixture, for golden comparisons.</summary>
    private string CliCopy(params string[][] commands)
    {
        var copy = Path.Combine(_root, "cli-" + Guid.NewGuid().ToString("N")[..8], "Packages");
        Copy(Path.Combine(BuildHostIntegrationTests.RepoRoot, "samples", "Packages"), copy);
        foreach (var command in commands)
        {
            var psi = new ProcessStartInfo("dotnet") { WorkingDirectory = copy, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var arg in command)
            {
                psi.ArgumentList.Add(arg.Replace("{root}", copy, StringComparison.Ordinal));
            }

            psi.Environment["DOTNET_NOLOGO"] = "1";
            psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
            using var process = Process.Start(psi)!;
            var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.True(process.ExitCode == 0, $"dotnet {string.Join(' ', command)} failed: {output}");
        }

        return copy;
    }

    private static string Text(string path) => File.ReadAllText(path).ReplaceLineEndings("\n");

    // ------------------------------------------------------------------ feeds

    [Fact]
    public async Task Search_merges_sources_and_honours_prerelease_and_the_source_filter()
    {
        await OpenRestoredAsync();

        var stable = await _packages.SearchAsync("Daedalus.Sample", prerelease: false, 0, 20, "local", Timeout());
        Assert.Empty(stable.Errors);
        var greeting = Assert.Single(stable.Packages, p => p.Id == "Daedalus.Sample.Greeting");
        Assert.Equal("1.1.0", greeting.Version);
        Assert.Equal("local", greeting.Source);
        Assert.Contains(stable.Packages, p => p.Id == "Daedalus.Sample.Licensed");
        Assert.Contains(stable.Packages, p => p.Id == "Daedalus.Sample.Text");

        var preview = await _packages.SearchAsync("Greeting", prerelease: true, 0, 20, "local", Timeout());
        Assert.Equal("2.0.0-beta.1", Assert.Single(preview.Packages, p => p.Id == "Daedalus.Sample.Greeting").Version);

        // Offline: nuget.org is skipped, the local feed and the global packages folder still answer.
        _packages.Offline = true;
        var offline = await _packages.SearchAsync("Daedalus.Sample.Greeting", prerelease: false, 0, 20, null, Timeout());
        Assert.Empty(offline.Errors);
        Assert.Equal("1.1.0", Assert.Single(offline.Packages, p => p.Id == "Daedalus.Sample.Greeting").Version);
    }

    [Fact]
    public async Task Details_have_versions_readme_licence_and_dependencies()
    {
        await OpenRestoredAsync();

        var details = await _packages.DetailsAsync("Daedalus.Sample.Greeting", prerelease: false, version: null, source: "local", Timeout());
        Assert.Equal("1.1.0", details.Version);
        Assert.Equal(["1.1.0", "1.0.0"], details.Versions.Select(v => v.Version));
        Assert.Contains("loudly", details.Readme, StringComparison.Ordinal);
        Assert.Equal("MIT", details.LicenseExpression);
        Assert.Equal("Daedalus Studio fixtures", details.Authors);
        var group = Assert.Single(details.DependencyGroups);
        Assert.Equal("net8.0", group.TargetFramework);
        Assert.Equal("Daedalus.Sample.Text", Assert.Single(group.Dependencies).Id);

        // An older version, and the README from the global packages folder (restored there by the fixture).
        var older = await _packages.DetailsAsync("Daedalus.Sample.Greeting", prerelease: false, version: "1.0.0", source: null, Timeout());
        Assert.Equal("1.0.0", older.Version);
        Assert.Contains("Greeter.Hello", older.Readme, StringComparison.Ordinal);

        var licensed = await _packages.DetailsAsync("Daedalus.Sample.Licensed", prerelease: false, version: null, source: "local", Timeout());
        Assert.True(licensed.RequireLicenseAcceptance);
        Assert.Equal("Apache-2.0", licensed.LicenseExpression);
        Assert.Null(licensed.Readme);
    }

    [Fact]
    public async Task Deprecation_vulnerabilities_and_readme_come_from_an_http_feed()
    {
        using var server = new MockNuGetServer();
        File.WriteAllText(Path.Combine(Fixture, "nuget.config"), $"""
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <clear />
                <add key="mock" value="{server.IndexUrl}" allowInsecureConnections="true" />
              </packageSources>
            </configuration>
            """);
        await _service.OpenAsync(Slnx, trusted: true, restore: false, Timeout());

        var details = await _packages.DetailsAsync("Old.Package", prerelease: false, version: null, source: null, Timeout());
        Assert.Equal(["1.0.0", "0.9.0"], details.Versions.Select(v => v.Version));
        Assert.True(details.Versions[0].Deprecated);
        Assert.False(details.Versions[1].Deprecated);
        Assert.Equal(["Legacy", "CriticalBugs"], details.Deprecation!.Reasons);
        Assert.Equal("New.Package", details.Deprecation.AlternateId);
        Assert.Equal(2, Assert.Single(details.Vulnerabilities).Severity);
        Assert.Contains("New.Package", details.Readme, StringComparison.Ordinal);

        var status = await _packages.StatusAsync([new PackageStatusQuery("Old.Package", "0.9.0"), new PackageStatusQuery("Missing.Package", "1.0.0")], prerelease: false, Timeout());
        Assert.Equal(("1.0.0", false, -1), (status.Packages[0].Latest, status.Packages[0].Deprecated, status.Packages[0].VulnerabilitySeverity));
        Assert.Null(status.Packages[1].Latest);
    }

    [Fact]
    public async Task An_unreachable_source_is_reported_and_the_others_still_answer()
    {
        File.WriteAllText(Path.Combine(Fixture, "nuget.config"), """
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <clear />
                <add key="local" value="feed" />
                <add key="broken" value="http://127.0.0.1:9/v3/index.json" allowInsecureConnections="true" />
              </packageSources>
            </configuration>
            """);
        await _service.OpenAsync(Slnx, trusted: true, restore: false, Timeout());

        var result = await _packages.SearchAsync("Daedalus.Sample.Text", prerelease: false, 0, 20, null, Timeout(), refresh: true);
        Assert.Equal("Daedalus.Sample.Text", Assert.Single(result.Packages).Id);
        Assert.Equal("broken", Assert.Single(result.Errors).Source);
    }

    // ------------------------------------------------------------------ installed packages

    [Fact]
    public async Task Installed_packages_transitive_packages_and_why()
    {
        await OpenRestoredAsync();
        var installed = await _packages.InstalledAsync(null, Timeout());
        Assert.Equal(4, installed.Count);

        var plain = installed.Single(p => p.Project == Plain);
        Assert.False(plain.CentralPackageManagement);
        Assert.True(plain.Restored);
        var greeting = Assert.Single(plain.Packages);
        Assert.Equal(("Daedalus.Sample.Greeting", "1.0.0", "1.0.0", InstalledPackageKinds.Direct), (greeting.Id, greeting.Requested, greeting.Resolved, greeting.Kind));
        Assert.Equal(Plain, greeting.DefinedIn);
        Assert.Empty(plain.Transitive);

        var central = installed.Single(p => p.Project == Central);
        Assert.True(central.CentralPackageManagement);
        Assert.Equal(CentralProps, central.CentralPackagesFile);
        Assert.Equal(("1.0.0", "1.0.0"), (central.Packages[0].Requested, central.Packages[0].CentralVersion));

        var multi = installed.Single(p => p.Project == Multi);
        Assert.Equal(["net10.0", "net9.0"], multi.TargetFrameworks);

        // 1.1.0 depends on Daedalus.Sample.Text: it shows up as transitive, with the chain that brings it in.
        var restored = WaitForRestoreAsync();
        await _packages.InstallAsync([new PackageEditTarget(Plain)], "Daedalus.Sample.Greeting", "1.1.0", versionOverride: false, Timeout());
        Assert.Equal("succeeded", await restored);
        plain = Assert.Single(await _packages.InstalledAsync(Plain, Timeout()));
        Assert.Equal("1.1.0", plain.Packages[0].Resolved);
        var text = Assert.Single(plain.Transitive);
        Assert.Equal(("Daedalus.Sample.Text", "1.0.0"), (text.Id, text.Resolved));
        var why = Assert.Single(await _packages.WhyAsync(Plain, "Daedalus.Sample.Text", Timeout()));
        Assert.Equal(["Daedalus.Sample.Greeting/1.1.0", "Daedalus.Sample.Text/1.0.0"], why.Chain);
    }

    // ------------------------------------------------------------------ edits (compared with the CLI)

    [Fact]
    public async Task Update_add_and_remove_write_what_the_cli_writes()
    {
        await _service.OpenAsync(Slnx, trusted: true, restore: false, Timeout());
        await WaitUntilAsync(() => _service.Progress is { Completed: true });

        // Update an existing reference; add to a project without package references; remove.
        await _packages.InstallAsync([new PackageEditTarget(Plain)], "Daedalus.Sample.Greeting", "1.1.0", false, Timeout());
        await _packages.InstallAsync([new PackageEditTarget(Plain)], "Daedalus.Sample.Text", "1.0.0", false, Timeout());
        await _packages.InstallAsync([new PackageEditTarget(Multi)], "Daedalus.Sample.Licensed", "1.0.0", false, Timeout());
        var cli = CliCopy(
            ["add", "src/Plain/Plain.csproj", "package", "Daedalus.Sample.Greeting", "--version", "1.1.0", "--no-restore"],
            ["add", "src/Plain/Plain.csproj", "package", "Daedalus.Sample.Text", "--version", "1.0.0", "--no-restore"],
            ["add", "src/Multi/Multi.csproj", "package", "Daedalus.Sample.Licensed", "--version", "1.0.0", "--no-restore"]);
        Assert.Equal(Text(Path.Combine(cli, "src", "Plain", "Plain.csproj")), Text(Plain));
        Assert.Equal(Text(Path.Combine(cli, "src", "Multi", "Multi.csproj")), Text(Multi));

        await _packages.UninstallAsync([Plain], "Daedalus.Sample.Greeting", Timeout());
        await _packages.UninstallAsync([Multi], "Daedalus.Sample.Licensed", Timeout());
        var removed = CliCopy(
            ["add", "src/Plain/Plain.csproj", "package", "Daedalus.Sample.Text", "--version", "1.0.0", "--no-restore"],
            ["remove", "src/Plain/Plain.csproj", "package", "Daedalus.Sample.Greeting"],
            ["add", "src/Multi/Multi.csproj", "package", "Daedalus.Sample.Licensed", "--version", "1.0.0", "--no-restore"],
            ["remove", "src/Multi/Multi.csproj", "package", "Daedalus.Sample.Licensed"]);
        Assert.Equal(Text(Path.Combine(removed, "src", "Plain", "Plain.csproj")), Text(Plain));
        Assert.Equal(Text(Path.Combine(removed, "src", "Multi", "Multi.csproj")), Text(Multi));

        var error = await Assert.ThrowsAsync<JsonRpcRemoteException>(() => _packages.UninstallAsync([Multi], "Daedalus.Sample.Licensed", Timeout()));
        Assert.Contains("does not reference", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Central_package_management_writes_versions_to_directory_packages_props()
    {
        await _service.OpenAsync(Slnx, trusted: true, restore: false, Timeout());
        await WaitUntilAsync(() => _service.Progress is { Completed: true });

        // A new package: a version-less reference in the project, the version in Directory.Packages.props.
        var updated = await _packages.InstallAsync([new PackageEditTarget(Central)], "Daedalus.Sample.Text", "1.0.0", false, Timeout());
        Assert.Equal([Central, CentralProps], updated.Order(StringComparer.Ordinal));
        var cli = CliCopy(["add", "cpm/Central/Central.csproj", "package", "Daedalus.Sample.Text", "--version", "1.0.0", "--no-restore"]);
        Assert.Equal(Text(Path.Combine(cli, "cpm", "Central", "Central.csproj")), Text(Central));
        // The CLI drops the file's final line break; we keep the file as it was.
        Assert.Equal(Text(Path.Combine(cli, "cpm", "Directory.Packages.props")).TrimEnd(), Text(CentralProps).TrimEnd());

        // Updating changes the central version only (both projects follow it).
        updated = await _packages.InstallAsync([new PackageEditTarget(Central)], "Daedalus.Sample.Greeting", "1.1.0", false, Timeout());
        Assert.Equal([CentralProps], updated);
        Assert.Contains("""<PackageVersion Include="Daedalus.Sample.Greeting" Version="1.1.0" />""", Text(CentralProps), StringComparison.Ordinal);

        // An override in one project.
        updated = await _packages.InstallAsync([new PackageEditTarget(Other)], "Daedalus.Sample.Greeting", "1.0.0", versionOverride: true, Timeout());
        Assert.Equal([Other], updated);
        Assert.Contains("""<PackageReference Include="Daedalus.Sample.Greeting" VersionOverride="1.0.0" />""", Text(Other), StringComparison.Ordinal);
        var other = Assert.Single(await _packages.InstalledAsync(Other, Timeout()));
        Assert.Equal(("1.0.0", "1.0.0", "1.1.0"), (other.Packages[0].Requested, other.Packages[0].VersionOverride, other.Packages[0].CentralVersion));

        // Removing leaves the central version, as `dotnet remove package` does.
        await _packages.UninstallAsync([Central], "Daedalus.Sample.Text", Timeout());
        Assert.DoesNotContain("Daedalus.Sample.Text", Text(Central), StringComparison.Ordinal);
        Assert.Contains("Daedalus.Sample.Text", Text(CentralProps), StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ sources and tools

    /// <summary>The user-wide NuGet.Config, which tests must never change.</summary>
    internal static string UserNuGetConfig => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NuGet", "NuGet.Config")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "NuGet", "NuGet.Config");

    internal static string? Snapshot(string path) => File.Exists(path) ? File.ReadAllText(path) : null;

    [Fact]
    public async Task Package_sources_and_mappings_are_read_and_saved()
    {
        var user = Snapshot(UserNuGetConfig);
        await _service.OpenAsync(Slnx, trusted: true, restore: false, Timeout());
        var config = Path.Combine(Fixture, "nuget.config");

        var sources = await _packages.SourcesAsync(Timeout());
        Assert.Equal(["local", "nuget.org"], sources.Sources.Select(s => s.Name));
        Assert.Equal(config, sources.Sources[0].ConfigFile, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(config, sources.ConfigFiles[0], StringComparer.OrdinalIgnoreCase);
        Assert.Equal(["Daedalus.Sample.*"], sources.Mappings.Single(m => m.Source == "local").Patterns);
        Assert.False(string.IsNullOrEmpty(sources.GlobalPackagesFolder));

        // Disable nuget.org, add a folder source, map a pattern to it.
        var extra = Path.Combine(_root, "extra-feed");
        Directory.CreateDirectory(extra);
        await _packages.SetSourcesAsync(
            [sources.Sources[0], sources.Sources[1] with { Enabled = false }, new PackageSourceInfo("extra", extra, true)],
            [.. sources.Mappings, new PackageSourceMapping("extra", ["Extra.*"])],
            Timeout());
        var saved = await _packages.SourcesAsync(Timeout());
        Assert.Equal(["local", "nuget.org", "extra"], saved.Sources.Select(s => s.Name));
        Assert.False(saved.Sources[1].Enabled);
        Assert.Equal(["Extra.*"], saved.Mappings.Single(m => m.Source == "extra").Patterns);
        var xml = File.ReadAllText(config);
        Assert.Contains("extra", xml, StringComparison.Ordinal);
        Assert.Contains("disabledPackageSources", xml, StringComparison.Ordinal);

        var duplicate = await Assert.ThrowsAsync<JsonRpcRemoteException>(() => _packages.SetSourcesAsync(
            [sources.Sources[0], sources.Sources[0]], [], Timeout()));
        Assert.Contains("two package sources", duplicate.Message, StringComparison.Ordinal);

        // Reorder, re-enable, change a mapping, remove a source.
        await _packages.SetSourcesAsync(
            [saved.Sources[2], saved.Sources[0], saved.Sources[1] with { Enabled = true }],
            [new PackageSourceMapping("local", ["Daedalus.*"]), new PackageSourceMapping("extra", ["Extra.*"]), new PackageSourceMapping("nuget.org", ["*"])],
            Timeout());
        saved = await _packages.SourcesAsync(Timeout());
        Assert.Equal(["extra", "local", "nuget.org"], saved.Sources.Select(s => s.Name));
        Assert.All(saved.Sources, s => Assert.True(s.Enabled));
        Assert.Equal(["Daedalus.*"], saved.Mappings.Single(m => m.Source == "local").Patterns);
        await _packages.SetSourcesAsync([saved.Sources[1], saved.Sources[2]], [new PackageSourceMapping("nuget.org", ["*"])], Timeout());
        saved = await _packages.SourcesAsync(Timeout());
        Assert.Equal(["local", "nuget.org"], saved.Sources.Select(s => s.Name));
        Assert.Equal(["nuget.org"], saved.Mappings.Select(m => m.Source));
        Assert.DoesNotContain("extra", File.ReadAllText(config), StringComparison.Ordinal);
        Assert.DoesNotContain("""value="true" """, File.ReadAllText(config), StringComparison.Ordinal);

        Assert.Equal(user, Snapshot(UserNuGetConfig));
    }

    [Fact]
    public async Task A_new_source_goes_to_a_new_solution_config_not_the_user_config()
    {
        var user = Snapshot(UserNuGetConfig);
        File.Delete(Path.Combine(Fixture, "nuget.config"));
        await _service.OpenAsync(Slnx, trusted: true, restore: false, Timeout());
        var sources = await _packages.SourcesAsync(Timeout());
        Assert.DoesNotContain(sources.ConfigFiles, f => f.StartsWith(Fixture, StringComparison.OrdinalIgnoreCase));

        var feed = Path.Combine(_root, "team-feed");
        Directory.CreateDirectory(feed);
        await _packages.SetSourcesAsync([.. sources.Sources, new PackageSourceInfo("team", feed, true)], [], Timeout());
        var created = Path.Combine(Fixture, "nuget.config");
        Assert.True(File.Exists(created));
        Assert.Contains("team-feed", File.ReadAllText(created), StringComparison.Ordinal);
        var saved = await _packages.SourcesAsync(Timeout());
        Assert.Equal(created, saved.Sources.Single(s => s.Name == "team").ConfigFile, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(user, Snapshot(UserNuGetConfig));
    }

    [Fact]
    public async Task Local_tools_come_from_the_manifest()
    {
        Directory.CreateDirectory(Path.Combine(Fixture, ".config"));
        File.WriteAllText(Path.Combine(Fixture, ".config", "dotnet-tools.json"), """
            {
              "version": 1,
              "isRoot": true,
              "tools": {
                "dotnet-example": { "version": "1.2.3", "commands": [ "example" ], "rollForward": false }
              }
            }
            """);
        await _service.OpenAsync(Slnx, trusted: true, restore: false, Timeout());

        var tools = await _packages.ToolsAsync(Timeout());
        Assert.Equal(Path.Combine(Fixture, ".config", "dotnet-tools.json"), tools.Manifest);
        var tool = Assert.Single(tools.Local);
        Assert.Equal(("dotnet-example", "1.2.3", "example"), (tool.Id, tool.Version, tool.Commands.Single()));
    }

    [Fact]
    public async Task Pack_builds_the_project_and_writes_its_package()
    {
        await OpenRestoredAsync();
        var output = new List<string>();
        _service.BuildOutput += (_, line) =>
        {
            lock (output)
            {
                output.Add(line);
            }
        };

        var result = await _service.BuildAsync(BuildTargets.Pack, Plain, Timeout());
        Assert.True(result.Succeeded, string.Join(Environment.NewLine, output));
        var package = Path.Combine(Fixture, "src", "Plain", "bin", "Debug", "Plain.1.0.0.nupkg");
        Assert.True(File.Exists(package), string.Join(Environment.NewLine, output));
        lock (output)
        {
            Assert.Contains(output, l => l.StartsWith("Pack started", StringComparison.Ordinal));
            Assert.Contains(output, l => l.Contains("Successfully created package", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task Plugins_read_packages_and_properties_through_the_proposed_workspace_api()
    {
        await OpenRestoredAsync();
        using var api = new DaedalusStudio.Workbench.Plugins.WorkbenchProposedApi(
            _service,
            new DaedalusStudio.Workbench.Build.BuildService(_service, new DaedalusStudio.Workbench.Output.OutputService()),
            new DaedalusStudio.Workbench.Debugging.DebugService(() => null),
            new DaedalusStudio.Workbench.Languages.LanguageService(new DaedalusStudio.Workbench.Languages.LanguageServiceRegistry(), _service));
        var packages = await api.GetPackagesAsync(Central, Timeout());
        var greeting = Assert.Single(packages.Packages);
        Assert.Equal(("Daedalus.Sample.Greeting", "1.0.0", "1.0.0", false), (greeting.Id, greeting.RequestedVersion, greeting.ResolvedVersion, greeting.IsTransitive));
        Assert.Equal(["net10.0"], greeting.TargetFrameworks);

        var properties = await api.GetPropertiesAsync(Central, ["TargetFramework", "ManagePackageVersionsCentrally", "Nope"], Timeout());
        Assert.Equal("net10.0", properties.Values["TargetFramework"]);
        Assert.Equal("true", properties.Values["ManagePackageVersionsCentrally"]);
        Assert.Equal(string.Empty, properties.Values["Nope"]);
    }

    [Fact]
    public async Task Package_management_needs_a_trusted_solution()
    {
        await _service.OpenAsync(Slnx, trusted: false, restore: false, Timeout());
        var error = await Assert.ThrowsAsync<JsonRpcRemoteException>(() => _packages.SearchAsync("x", false, 0, 10, null, Timeout()));
        Assert.Contains("trusted", error.Message, StringComparison.Ordinal);
    }
}
