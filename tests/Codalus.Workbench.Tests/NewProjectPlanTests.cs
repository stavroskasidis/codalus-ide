using Codalus.Workbench.Projects;

namespace Codalus.Workbench.Tests;

/// <summary>New Project's folders, names, checks and <c>dotnet new</c> arguments (ADR 0082), with every OS's rules on each.</summary>
public sealed class NewProjectPlanTests : IDisposable
{
    private static readonly PathRules s_linux = new(IsWindows: false, LongPathsEnabled: true);
    private static readonly PathRules s_windows = new(IsWindows: true, LongPathsEnabled: false);
    private readonly TemplateTestData _data = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "codalus-plan-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        _data.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private async Task<ProjectTemplateGroup> RowAsync(string id)
    {
        var catalog = new ProjectTemplateCatalog(RecordedCli.Sdk11(), new FixedCacheLocator(_data.Cache11), () => new DateOnly(2026, 10, 21));
        return (await catalog.LoadAsync(TestContext.Current.CancellationToken)).Single(r => r.Id == id);
    }

    private static NewProjectRequest Request(ProjectTemplateGroup group, string? framework = null, IReadOnlyDictionary<string, string?>? values = null, HostOptionValues? host = null) => new()
    {
        Group = group,
        Member = group.MemberFor(framework),
        ProjectName = "Hello.App",
        Location = Path.Combine(Path.GetTempPath(), "repos"),
        SolutionName = "Hello",
        Framework = framework,
        Values = values ?? new Dictionary<string, string?>(),
        HostOptions = host ?? new HostOptionValues(),
    };

    [Fact]
    public async Task Folders_for_each_way_of_creating()
    {
        var console = await RowAsync("dotnet:Microsoft.Common.Console|C#");
        var repos = Path.Combine(Path.GetTempPath(), "repos");
        var separate = NewProjectPlan.Create(Request(console));
        Assert.Equal((Path.Combine(repos, "Hello"), "Hello", Path.Combine(repos, "Hello", "Hello.App")), (separate.SolutionDirectory, separate.SolutionName, separate.ProjectDirectory));
        Assert.Equal(Path.Combine(repos, "Hello", "Hello.App") + Path.DirectorySeparatorChar, separate.CreatedIn);
        Assert.True(separate.CreatesSolution);

        var same = NewProjectPlan.Create(Request(console) with { SameDirectory = true });
        Assert.Equal((Path.Combine(repos, "Hello.App"), "Hello.App", Path.Combine(repos, "Hello.App")), (same.SolutionDirectory, same.SolutionName, same.ProjectDirectory));

        var add = NewProjectPlan.Create(Request(console) with { SolutionMode = SolutionMode.AddToSolution });
        Assert.Equal(((string?)null, Path.Combine(repos, "Hello.App")), (add.SolutionDirectory, add.ProjectDirectory));
        Assert.False(add.CreatesSolution);
        var folder = NewProjectPlan.Create(Request(console) with { SolutionMode = SolutionMode.AddToFolder, Location = repos + Path.DirectorySeparatorChar });
        Assert.Equal(Path.Combine(repos, "Hello.App"), folder.ProjectDirectory);

        var blank = ProjectTemplateCatalog.BuiltInRows([], new DateOnly(2026, 10, 21)).Single(r => r.Kind == ProjectTemplateKind.BlankSolution);
        var solution = NewProjectPlan.Create(new NewProjectRequest { Group = blank, Location = repos, SolutionName = "Empty" });
        Assert.Equal((Path.Combine(repos, "Empty"), (string?)null), (solution.SolutionDirectory, solution.ProjectDirectory));
        Assert.Equal(Path.Combine(repos, "Empty") + Path.DirectorySeparatorChar, solution.CreatedIn);
        Assert.True(solution.IsBlankSolution);

        // The location is typed: ~ expands, relative text isn't a location.
        var home = NewProjectPlan.Create(Request(console) with { Location = "~" });
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), home.Location);
        var relative = NewProjectPlan.Create(Request(console) with { Location = "some/folder" });
        Assert.Null(relative.Location);
        Assert.Equal("\"some/folder\" isn't a folder path.", relative.ValidateLocation());
        Assert.Equal("Enter a location.", NewProjectPlan.Create(Request(console) with { Location = "  " }).ValidateLocation());
    }

    [Theory]
    [InlineData("Hello.App", null)]
    [InlineData("my-app_2", null)]
    [InlineData("", "Enter a name.")]
    [InlineData("a/b", "A name can't contain /.")]
    [InlineData("a:b?c", "A name can't contain : ?.")]
    [InlineData("50%", "A name can't contain %.")]
    [InlineData(".hidden", "A name can't start or end with a dot or a space.")]
    [InlineData("App.", "A name can't start or end with a dot or a space.")]
    [InlineData("CON", "\"CON\" is a device name Windows reserves. Choose another name.")]
    [InlineData("com1", "\"com1\" is a device name Windows reserves. Choose another name.")]
    [InlineData("LPT9.txt", "\"LPT9\" is a device name Windows reserves. Choose another name.")]
    [InlineData("NUL", "\"NUL\" is a device name Windows reserves. Choose another name.")]
    [InlineData("Console", null)]
    public void Name_rules(string name, string? error) => Assert.Equal(error, NewProjectPlan.ValidateName(name));

    [Fact]
    public void Names_longer_than_128_characters_are_refused()
    {
        Assert.Null(NewProjectPlan.ValidateName(new string('a', 128)));
        Assert.Equal("A name can be at most 128 characters long.", NewProjectPlan.ValidateName(new string('a', 129)));
    }

    [Fact]
    public async Task Existing_folders_and_windows_path_length()
    {
        var blazor = await RowAsync("dotnet:Microsoft.Web.Blazor|C#");
        var request = Request(blazor, "net11.0") with { Location = _root, ProjectName = "App", SolutionName = "Sol" };
        Assert.Null(NewProjectPlan.Create(request).ValidateTarget(s_linux));

        // The project's folder existing and not empty is an error; empty, it's fine; the solution's folder may hold other
        // things, not a solution of the same name.
        Directory.CreateDirectory(Path.Combine(_root, "Sol", "App"));
        File.WriteAllText(Path.Combine(_root, "Sol", "keep.txt"), "x");
        Assert.Null(NewProjectPlan.Create(request).ValidateTarget(s_linux));
        File.WriteAllText(Path.Combine(_root, "Sol", "App", "keep.txt"), "x");
        Assert.Equal($"The folder \"{Path.Combine(_root, "Sol", "App")}\" already exists. Choose another name or location.", NewProjectPlan.Create(request).ValidateTarget(s_linux));
        File.Delete(Path.Combine(_root, "Sol", "App", "keep.txt"));
        File.WriteAllText(Path.Combine(_root, "Sol", "Sol.slnx"), "<Solution />");
        Assert.Equal($"The solution \"{Path.Combine(_root, "Sol", "Sol.slnx")}\" already exists. Choose another name or location.", NewProjectPlan.Create(request).ValidateTarget(s_linux));
        Assert.Null(NewProjectPlan.Create(request with { SolutionMode = SolutionMode.AddToSolution }).ValidateTarget(s_linux)); // no new solution

        // Windows without long paths: the project folder plus the template's deepest file stays under 260 characters.
        var deep = request with { Location = Path.Combine(_root, new string('d', 120)), SolutionName = new string('s', 40) };
        var plan = NewProjectPlan.Create(deep);
        var length = plan.LongestPath(s_windows)!.Value;
        Assert.Equal(plan.ProjectDirectory!.Length + 1 + "App/Components/Account/IdentityComponentsEndpointRouteBuilderExtensions.cs".Length, length);
        Assert.True(length >= PathRules.MaxPath);
        Assert.Equal($"The path of a file in this project would be {length} characters long, more than Windows allows (260). Choose a shorter location or name, or turn on long paths in Windows.", plan.ValidateTarget(s_windows));
        Assert.Null(plan.ValidateTarget(s_windows with { LongPathsEnabled = true }));
        Assert.Null(plan.ValidateTarget(s_linux));
        Assert.Null(plan.LongestPath(s_linux));
    }

    [Fact]
    public async Task Aspire_projects_go_beside_the_project_with_the_same_directory()
    {
        var webapi = await RowAsync("dotnet:Microsoft.Web.WebApi|C#");
        var request = Request(webapi, "net11.0") with { Location = _root, ProjectName = "Api", SolutionName = "Shop", HostOptions = new HostOptionValues { Aspire = true } };

        // Separate folders: in the solution's folder, next to the project's.
        var separate = NewProjectPlan.Create(request);
        Assert.True(separate.EnlistsInAspire);
        Assert.Equal(Path.Combine(_root, "Shop"), separate.AspireDirectory);
        Assert.Equal([Path.Combine(_root, "Shop", "Shop.ServiceDefaults"), Path.Combine(_root, "Shop", "Shop.AppHost")], separate.AspireProjectDirectories());

        // The same directory: the solution's folder is the project's, so they go beside it, not inside the web project
        // (whose default globs would compile AppHost.cs and Extensions.cs into it).
        var same = NewProjectPlan.Create(request with { SameDirectory = true });
        Assert.Equal(Path.Combine(_root, "Api"), same.ProjectDirectory);
        Assert.Equal(_root, same.AspireDirectory);
        Assert.Equal([Path.Combine(_root, "Api.ServiceDefaults"), Path.Combine(_root, "Api.AppHost")], same.AspireProjectDirectories());

        // Adding: in the location; the solution's own AppHost is reused, so only the ServiceDefaults is new.
        var add = NewProjectPlan.Create(request with { SolutionMode = SolutionMode.AddToSolution, AspireName = "Shop", ExistingAspire = new AspireProjects(Path.Combine(_root, "Shop.AppHost", "Shop.AppHost.csproj"), null) });
        Assert.Equal([Path.Combine(_root, "Shop.ServiceDefaults")], add.AspireProjectDirectories());

        // An Aspire project's folder that exists and isn't empty stops Create, like the project's own.
        Directory.CreateDirectory(Path.Combine(_root, "Api.AppHost"));
        Assert.Null(same.ValidateTarget(s_linux));
        File.WriteAllText(Path.Combine(_root, "Api.AppHost", "AppHost.cs"), "// an earlier one");
        Assert.Equal($"The folder \"{Path.Combine(_root, "Api.AppHost")}\" already exists. Choose another name or location.", same.ValidateTarget(s_linux));
        Assert.Null(NewProjectPlan.Create(request with { SameDirectory = true, HostOptions = new HostOptionValues() }).ValidateTarget(s_linux)); // without Aspire
        Assert.Empty(NewProjectPlan.Create(request with { HostOptions = new HostOptionValues() }).AspireProjectDirectories());
    }

    [Fact]
    public async Task A_folder_that_cant_be_read_is_an_error_not_an_exception()
    {
        var console = await RowAsync("dotnet:Microsoft.Common.Console|C#");
        var project = Directory.CreateDirectory(Path.Combine(_root, "Sol", "App")).FullName;

        // Root reads any folder, and Windows has no Unix modes: there, nothing here can be denied.
        if (OperatingSystem.IsWindows() || Environment.UserName == "root")
        {
            Assert.Skip("Needs a non-root user on Linux or macOS to deny a folder.");
        }
        else
        {
            File.SetUnixFileMode(project, UnixFileMode.None);
            try
            {
                var error = NewProjectPlan.Create(Request(console) with { Location = _root, ProjectName = "App", SolutionName = "Sol" }).ValidateTarget(s_linux);
                Assert.NotNull(error);
                Assert.StartsWith("Couldn't check the location: ", error, StringComparison.Ordinal);
            }
            finally
            {
                File.SetUnixFileMode(project, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }
    }

    [Fact]
    public void Global_json_warnings()
    {
        var location = Path.Combine(_root, "pinned", "deeper");
        Directory.CreateDirectory(location);
        Assert.Null(NewProjectPlan.GlobalJsonWarning(location, "net11.0")); // none found up to the root (unless the machine's own)

        File.WriteAllText(Path.Combine(_root, "pinned", "global.json"), """
            {
              // the repository's SDK
              "sdk": { "version": "10.0.100", "rollForward": "latestFeature", },
            }
            """);
        Assert.Equal("This location's global.json selects .NET SDK 10.0.100, which can't build .NET 11.0 projects.", NewProjectPlan.GlobalJsonWarning(location, "net11.0"));
        Assert.Null(NewProjectPlan.GlobalJsonWarning(location, "net10.0"));
        Assert.Null(NewProjectPlan.GlobalJsonWarning(location, "net48"));
        Assert.Null(NewProjectPlan.GlobalJsonWarning(Path.Combine(location, "not-yet"), "net10.0")); // a folder to create reads from its parents

        File.WriteAllText(Path.Combine(_root, "pinned", "global.json"), """{ "sdk": { "version": "10.0.100", "rollForward": "latestMajor" } }""");
        Assert.Null(NewProjectPlan.GlobalJsonWarning(location, "net11.0"));
        File.WriteAllText(Path.Combine(_root, "pinned", "global.json"), "not json");
        Assert.Null(NewProjectPlan.GlobalJsonWarning(location, "net11.0"));
    }

    [Fact]
    public async Task Arguments_carry_only_changed_values_of_the_chosen_member()
    {
        var console = await RowAsync("dotnet:Microsoft.Common.Console|C#");
        var plan = NewProjectPlan.Create(Request(console, "net10.0", new Dictionary<string, string?> { ["UseProgramMain"] = "true", ["NativeAot"] = "false" }));
        Assert.Equal(["console", "--name", "Hello.App", "--output", plan.ProjectDirectory!, "--language", "C#", "--framework", "net10.0", "--use-program-main", "true", "--no-restore"], plan.Arguments(s_linux));
        Assert.DoesNotContain("--debug:custom-hive", plan.Arguments(s_linux)); // the CLI adds the hive

        // Visual Basic passes its language.
        var vb = await RowAsync("dotnet:Microsoft.Common.Console|VB");
        Assert.Contains("VB", NewProjectPlan.Create(Request(vb)).Arguments(s_linux));

        // Inverted check boxes are already turned back: Enable OpenAPI unchecked is DisableOpenAPI=true.
        var webapi = await RowAsync("dotnet:Microsoft.Web.WebApi|C#");
        var api = NewProjectPlan.Create(Request(webapi, "net11.0", new Dictionary<string, string?> { ["DisableOpenAPI"] = "true", ["UseMinimalAPIs"] = "false" }));
        var args = api.Arguments(s_linux);
        Assert.Equal(["--no-openapi", "true", "--use-minimal-apis", "false"], args.SkipWhile(a => a != "--no-openapi").Take(4));
    }

    [Fact]
    public async Task Xunit_at_net10_never_gets_the_options_only_its_11_member_has()
    {
        var xunit = await RowAsync("dotnet:Microsoft.Test.xUnit|C#");
        var remembered = new Dictionary<string, string?> { ["XUnitVersion"] = "v3", ["TestRunner"] = "VSTest" };
        var ten = NewProjectPlan.Create(Request(xunit, "net10.0", remembered)).Arguments(s_linux);
        Assert.DoesNotContain("--xunit-version", ten);
        Assert.DoesNotContain("--test-runner", ten);
        Assert.DoesNotContain("--XUnitVersion", ten);

        var eleven = NewProjectPlan.Create(Request(xunit, "net11.0", remembered)).Arguments(s_linux);
        var member = xunit.MemberFor("net11.0")!;
        Assert.Contains(member.Parameters.Single(p => p.Name == "XUnitVersion").CliOption, eleven);
        Assert.Contains(member.Parameters.Single(p => p.Name == "TestRunner").CliOption, eleven);

        // A disabled option is left out: the test runner needs xUnit v3.
        var v2 = NewProjectPlan.Create(Request(xunit, "net11.0", new Dictionary<string, string?> { ["XUnitVersion"] = "v2", ["TestRunner"] = "VSTest" })).Arguments(s_linux);
        Assert.DoesNotContain(member.Parameters.Single(p => p.Name == "TestRunner").CliOption, v2);
    }

    [Fact]
    public async Task Blazor_options_reach_the_cli_by_their_cli_names()
    {
        var blazor = await RowAsync("dotnet:Microsoft.Web.Blazor|C#");
        var values = new Dictionary<string, string?>
        {
            ["InteractivityPlatform"] = "None",
            ["InteractivityLocation"] = "InteractiveGlobal", // disabled while the mode is None
            ["IncludeSampleContent"] = "false",
        };
        var args = NewProjectPlan.Create(Request(blazor, "net11.0", values)).Arguments(s_linux);
        Assert.Equal(["--interactivity", "None", "--empty", "true", "--no-restore"], args.SkipWhile(a => a != "--interactivity"));

        values["InteractivityPlatform"] = "Auto";
        values["IncludeSampleContent"] = "true";
        args = NewProjectPlan.Create(Request(blazor, "net11.0", values)).Arguments(s_linux);
        Assert.Equal(["--interactivity", "Auto", "--InteractivityLocation", "InteractiveGlobal", "--no-restore"], args.SkipWhile(a => a != "--interactivity"));
    }

    [Fact]
    public async Task Authentication_follows_the_classic_ide()
    {
        var blazor = await RowAsync("dotnet:Microsoft.Web.Blazor|C#");

        // Individual Accounts needs HTTPS: --no-https never goes with it; LocalDB only on Windows.
        var individual = NewProjectPlan.Create(Request(blazor, "net11.0", host: new HostOptionValues { Auth = "Individual", Https = false }));
        Assert.Equal(["--auth", "Individual", "--no-restore"], individual.Arguments(s_linux).SkipWhile(a => a != "--auth"));
        Assert.Equal(["--auth", "Individual", "--use-local-db", "true", "--no-restore"], individual.Arguments(s_windows).SkipWhile(a => a != "--auth"));
        Assert.True(TemplateHostOptions.ForcesHttps("Individual"));
        Assert.True(TemplateHostOptions.ForcesHttps("SingleOrg"));
        Assert.False(TemplateHostOptions.ForcesHttps("Windows"));

        var none = NewProjectPlan.Create(Request(blazor, "net11.0", host: new HostOptionValues { Auth = "None", Https = false }));
        Assert.Equal(["--no-https", "true", "--no-restore"], none.Arguments(s_linux).SkipWhile(a => a != "--no-https"));
        Assert.DoesNotContain("--auth", none.Arguments(s_linux));

        var webapi = await RowAsync("dotnet:Microsoft.Web.WebApi|C#");
        var singleOrg = NewProjectPlan.Create(Request(webapi, "net11.0", host: new HostOptionValues { Auth = "SingleOrg" }));
        Assert.Equal(["--auth", "SingleOrg"], singleOrg.Arguments(s_linux).SkipWhile(a => a != "--auth").Take(2));
        Assert.DoesNotContain("--use-local-db", singleOrg.Arguments(s_windows));

        // A choice the template doesn't have (IndividualB2C isn't offered) isn't passed.
        var b2c = NewProjectPlan.Create(Request(webapi, "net11.0", host: new HostOptionValues { Auth = "IndividualB2C" }));
        Assert.DoesNotContain("--auth", b2c.Arguments(s_linux));
        Assert.Equal(["None", "Individual Accounts", "Microsoft identity platform", "Windows"], TemplateHostOptions.AuthOrder.Select(TemplateHostOptions.AuthLabel));
        Assert.NotNull(TemplateHostOptions.AuthNote("SingleOrg", windows: true));
        Assert.NotNull(TemplateHostOptions.AuthNote("Windows", windows: false));
        Assert.Null(TemplateHostOptions.AuthNote("Windows", windows: true));
    }
}
