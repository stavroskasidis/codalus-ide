using System.Text.RegularExpressions;
using Codalus.Workbench.Projects;

namespace Codalus.Workbench.Tests;

/// <summary>A template hive of its own for a test class: the SDK's templates, and nothing the user installed.</summary>
public sealed class TemplateHiveFixture : IDisposable
{
    public TemplateHiveFixture()
    {
        Root = Path.Combine(Path.GetTempPath(), "codalus-new-project-" + Guid.NewGuid().ToString("N"));
        Hive = Path.Combine(Root, "hive");
        Neutral = Path.Combine(Root, "state");
        Directory.CreateDirectory(Hive);
        Directory.CreateDirectory(Neutral);
        Cli = new TemplateService(Hive, Neutral);
        Catalog = new ProjectTemplateCatalog(Cli);
    }

    public string Root { get; }

    public string Hive { get; }

    public string Neutral { get; }

    public TemplateService Cli { get; }

    public ProjectTemplateCatalog Catalog { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>
/// New Project creating projects with the SDK installed here (ADR 0082): in a fresh template hive, through the neutral
/// folder, with <c>--no-restore</c> (nothing reaches nuget.org).
/// </summary>
public sealed partial class NewProjectServiceIntegrationTests(TemplateHiveFixture hive) : IClassFixture<TemplateHiveFixture>, IDisposable
{
    private static readonly PathRules s_rules = new(OperatingSystem.IsWindows(), LongPathsEnabled: true);
    private readonly string _location = Path.Combine(hive.Root, "repos-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_location, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private async Task<ProjectTemplateGroup> RowAsync(string shortName, string language = "C#")
    {
        var rows = await hive.Catalog.LoadAsync(TestContext.Current.CancellationToken);
        return rows.Single(r => r.Kind == ProjectTemplateKind.DotnetNew && r.ShortName == shortName && r.Language == language);
    }

    private NewProjectRequest Request(ProjectTemplateGroup group, string name, string? framework = null, Dictionary<string, string?>? values = null) => new()
    {
        Group = group,
        Member = group.MemberFor(framework ?? group.DefaultFramework()),
        ProjectName = name,
        Location = _location,
        SolutionName = name + "Solution",
        Framework = framework ?? group.DefaultFramework(),
        Values = values ?? [],
    };

    private Task<NewProjectResult> CreateAsync(NewProjectRequest request) =>
        new NewProjectService(hive.Cli, s_rules).CreateAsync(NewProjectPlan.Create(request), null, TestContext.Current.CancellationToken);

    [GeneratedRegex("<TargetFramework>([^<]+)</TargetFramework>")]
    private static partial Regex TargetFramework();

    [Fact]
    public async Task Creates_a_console_app_and_its_solution_in_separate_folders()
    {
        var console = await RowAsync("console");
        var progress = new List<string>();
        var request = Request(console, "App");
        var result = await new NewProjectService(hive.Cli, s_rules).CreateAsync(NewProjectPlan.Create(request), new SyncProgress(progress), TestContext.Current.CancellationToken);

        Assert.Equal(Path.Combine(_location, "AppSolution"), Path.GetDirectoryName(result.SolutionPath));
        Assert.Matches(@"AppSolution\.slnx?$", result.SolutionPath);
        Assert.True(File.Exists(result.SolutionPath));
        Assert.Equal(Path.Combine(_location, "AppSolution", "App", "App.csproj"), result.ProjectPath);
        Assert.Equal(request.Framework, TargetFramework().Match(File.ReadAllText(result.ProjectPath!)).Groups[1].Value);
        Assert.Equal([Path.Combine(_location, "AppSolution", "App", "Program.cs")], result.FilesToOpen);
        Assert.False(File.Exists(Path.Combine(_location, "AppSolution", "App", "obj", "project.assets.json"))); // no restore ran
        Assert.Equal(["Creating AppSolution…", "Creating App…"], progress);
        Assert.Contains("dotnet new sln", result.Log, StringComparison.Ordinal);
        Assert.DoesNotContain("--debug:custom-hive", result.Log, StringComparison.Ordinal); // the CLI adds it, the log shows the request
    }

    [Fact]
    public async Task Visual_basic_and_fsharp_pass_the_language()
    {
        var vb = await CreateAsync(Request(await RowAsync("console", "VB"), "VbApp") with { SameDirectory = true });
        Assert.Equal(Path.Combine(_location, "VbApp", "VbApp.vbproj"), vb.ProjectPath);
        Assert.Equal(Path.Combine(_location, "VbApp"), Path.GetDirectoryName(vb.SolutionPath));
        Assert.Equal([Path.Combine(_location, "VbApp", "Program.vb")], vb.FilesToOpen);

        var fs = await CreateAsync(Request(await RowAsync("console", "F#"), "FsApp") with { SameDirectory = true });
        Assert.Equal(Path.Combine(_location, "FsApp", "FsApp.fsproj"), fs.ProjectPath);
    }

    [Fact]
    public async Task Hidden_ide_parameters_reach_the_template()
    {
        var blazor = await RowAsync("blazor");
        var noSamples = await CreateAsync(Request(blazor, "Site", values: new() { ["IncludeSampleContent"] = "false" }) with { SameDirectory = true });
        var site = Path.GetDirectoryName(noSamples.ProjectPath)!;
        Assert.False(File.Exists(Path.Combine(site, "Components", "Pages", "Counter.razor")));
        Assert.True(File.Exists(Path.Combine(site, "Components", "Pages", "Home.razor")));

        var samples = await CreateAsync(Request(blazor, "Site2") with { SameDirectory = true });
        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(samples.ProjectPath)!, "Components", "Pages", "Counter.razor")));

        // Use controllers is UseMinimalAPIs inverted, a symbol the CLI hides: its CLI name still works.
        var webapi = await RowAsync("webapi");
        var api = await CreateAsync(Request(webapi, "Api", values: new() { ["UseMinimalAPIs"] = "false" }) with { SameDirectory = true });
        Assert.True(Directory.Exists(Path.Combine(Path.GetDirectoryName(api.ProjectPath)!, "Controllers")));

        // A global render mode on WebAssembly: the client project comes along, both found.
        var wasm = await CreateAsync(Request(blazor, "Wasm", values: new() { ["InteractivityPlatform"] = "WebAssembly", ["InteractivityLocation"] = "InteractiveGlobal" }) with { SameDirectory = true });
        Assert.Equal("Wasm.csproj", Path.GetFileName(wasm.ProjectPath));
        Assert.Contains(wasm.ExtraProjects, p => Path.GetFileName(p) == "Wasm.Client.csproj");
    }

    [Fact]
    public async Task Xunit_at_each_offered_framework()
    {
        var xunit = await RowAsync("xunit");
        Assert.NotEmpty(xunit.Frameworks);
        foreach (var framework in xunit.Frameworks)
        {
            var name = "Tests" + framework.Moniker.Replace(".", string.Empty, StringComparison.Ordinal);
            var values = new Dictionary<string, string?> { ["XUnitVersion"] = "v3", ["TestRunner"] = "Microsoft.Testing.Platform" }; // remembered from another member
            var result = await CreateAsync(Request(xunit, name, framework.Moniker, values) with { SameDirectory = true });
            Assert.Equal(framework.Moniker, TargetFramework().Match(File.ReadAllText(result.ProjectPath!)).Groups[1].Value);
        }
    }

    [Fact]
    public async Task An_exported_project_template_is_listed()
    {
        var source = Path.Combine(_location, "Exported");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "Exported.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>", TestContext.Current.CancellationToken);
        var exported = await hive.Cli.ExportAsync(
            new TemplateExportRequest("project", Path.Combine(source, "Exported.csproj"), [], "Codalus Exported Test", "codalusexportedtest", "Made by a test", "Codalus", "C#", Path.Combine(hive.Root, "exported-template")),
            TestContext.Current.CancellationToken);
        Assert.True(exported.Succeeded, exported.Output);

        var rows = await hive.Catalog.LoadAsync(TestContext.Current.CancellationToken); // reloaded: the CLI changed
        var row = Assert.Single(rows, r => r.ShortName == "codalusexportedtest");
        Assert.Equal(("Codalus Exported Test", "Made by a test"), (row.Name, row.Description));
        Assert.Empty(row.MemberFor(null)!.Parameters); // no IDE host file and nothing but plumbing
        var result = await CreateAsync(Request(row, "FromExport") with { SameDirectory = true });
        Assert.Equal("FromExport.csproj", Path.GetFileName(result.ProjectPath));
    }

    [Fact]
    public async Task Dotnet_runs_in_the_neutral_folder()
    {
        Directory.CreateDirectory(_location);
        await File.WriteAllTextAsync(Path.Combine(_location, "global.json"), """{ "sdk": { "version": "4.0.100" } }""", TestContext.Current.CancellationToken);
        var result = await CreateAsync(Request(await RowAsync("classlib"), "Lib") with { SameDirectory = true });
        Assert.True(File.Exists(result.ProjectPath));
    }

    [Fact]
    public async Task A_net_framework_template_and_a_blank_solution()
    {
        var rows = await hive.Catalog.LoadAsync(TestContext.Current.CancellationToken);
        var netfx = rows.Single(r => r.Id == "netfx:" + NetFrameworkTemplates.ConsoleShortName);
        var result = await CreateAsync(Request(netfx, "Legacy", "net472") with { SameDirectory = true });
        Assert.Equal(Path.Combine(_location, "Legacy", "Legacy.csproj"), result.ProjectPath);
        Assert.Contains("<TargetFramework>net472</TargetFramework>", File.ReadAllText(result.ProjectPath!), StringComparison.Ordinal);
        Assert.Equal([Path.Combine(_location, "Legacy", "Program.cs")], result.FilesToOpen);
        Assert.True(File.Exists(result.SolutionPath));

        var blank = rows.Single(r => r.Kind == ProjectTemplateKind.BlankSolution);
        var solution = await CreateAsync(new NewProjectRequest { Group = blank, Location = _location, SolutionName = "Empty" });
        Assert.Null(solution.ProjectPath);
        Assert.Equal(Path.Combine(_location, "Empty"), Path.GetDirectoryName(solution.SolutionPath));
    }

    [Fact]
    public async Task A_failure_says_why_and_removes_what_it_created()
    {
        var console = await RowAsync("console");
        var cli = new RecordedCli
        {
            Fallback = args =>
            {
                if (args[1] != "sln")
                {
                    return new CliResult(106, "Error: something about the template went wrong.\n\nFor details on the exit code, refer to https://aka.ms/templating-exit-codes#106\n");
                }

                var folder = args[args.ToList().IndexOf("--output") + 1];
                Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder, args[3] + ".slnx"), "<Solution />");
                return new CliResult(0, "The template \"Solution File\" was created successfully.");
            },
        };
        var plan = NewProjectPlan.Create(Request(console, "Broken"));
        Directory.CreateDirectory(_location);
        var error = await Assert.ThrowsAsync<NewProjectException>(() => new NewProjectService(cli, s_rules).CreateAsync(plan, null, TestContext.Current.CancellationToken));
        Assert.Equal("Error: something about the template went wrong.", error.Message);
        Assert.Contains("dotnet new console", error.Log, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(_location, "BrokenSolution")));
        Assert.True(Directory.Exists(_location)); // it existed before

        // Cancelling stops it the same way.
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new NewProjectService(hive.Cli, s_rules).CreateAsync(plan, null, cancelled.Token));
    }

    private sealed class SyncProgress(List<string> lines) : IProgress<string>
    {
        public void Report(string value) => lines.Add(value);
    }
}
