using System.Text;
using Codalus.Workbench.Projects;

namespace Codalus.Workbench.Tests;

/// <summary>
/// Enlist in Aspire orchestration (ADR 0082): the text edits (line endings and byte order mark kept), finding the
/// solution's AppHost and ServiceDefaults, and creating them with stand-in Aspire templates in a test hive (offline).
/// </summary>
public sealed class AspireEnlistmentTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "codalus-aspire-" + Guid.NewGuid().ToString("N"));

    public AspireEnlistmentTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private string Write(string name, string text, bool bom)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, text, new UTF8Encoding(bom));
        return path;
    }

    [Fact]
    public void Program_cs_gets_the_service_defaults_keeping_crlf_and_the_bom()
    {
        var program = Write("Program.cs", "var builder = WebApplication.CreateBuilder(args);\r\n\r\nbuilder.Services.AddOpenApi();\r\n\r\nvar app = builder.Build();\r\n\r\napp.Run();\r\n", bom: true);
        Assert.Null(AspireEnlistment.AddServiceDefaults(program));
        var bytes = File.ReadAllBytes(program);
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
        Assert.Equal(
            "var builder = WebApplication.CreateBuilder(args);\r\n\r\nbuilder.AddServiceDefaults();\r\n\r\nbuilder.Services.AddOpenApi();\r\n\r\nvar app = builder.Build();\r\n\r\napp.MapDefaultEndpoints();\r\n\r\napp.Run();\r\n",
            Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3));
        Assert.Null(AspireEnlistment.AddServiceDefaults(program)); // once
        Assert.Single(File.ReadAllLines(program), l => l.Contains("AddServiceDefaults", StringComparison.Ordinal));

        // A statement over several lines: after its end. No BOM stays none.
        var multiline = Write("Multi.cs", "var builder = WebApplication.CreateBuilder(\n    args);\nvar app = builder.Build();\n", bom: false);
        Assert.Null(AspireEnlistment.AddServiceDefaults(multiline));
        Assert.Equal("var builder = WebApplication.CreateBuilder(\n    args);\n\nbuilder.AddServiceDefaults();\nvar app = builder.Build();\n\napp.MapDefaultEndpoints();\n", File.ReadAllText(multiline));
        Assert.NotEqual(0xEF, File.ReadAllBytes(multiline)[0]);

        // Without the anchors: the lines to add by hand, the file untouched.
        var other = Write("Other.cs", "Console.WriteLine();\n", bom: false);
        Assert.Contains("builder.AddServiceDefaults();", AspireEnlistment.AddServiceDefaults(other), StringComparison.Ordinal);
        Assert.Equal("Console.WriteLine();\n", File.ReadAllText(other));
    }

    [Fact]
    public void The_apphost_adds_the_project()
    {
        var appHost = Write("AppHost.cs", "var builder = DistributedApplication.CreateBuilder(args);\n\nbuilder.Build().Run();\n", bom: false);
        Assert.Null(AspireEnlistment.AddToAppHost(appHost, Path.Combine(_root, "My.Api", "My.Api.csproj")));
        Assert.Null(AspireEnlistment.AddToAppHost(appHost, Path.Combine(_root, "Web", "Web.csproj")));
        Assert.Null(AspireEnlistment.AddToAppHost(appHost, Path.Combine(_root, "Web", "Web.csproj"))); // once
        Assert.Equal(
            "var builder = DistributedApplication.CreateBuilder(args);\n\nbuilder.AddProject<Projects.My_Api>(\"my-api\");\nbuilder.AddProject<Projects.Web>(\"web\");\nbuilder.Build().Run();\n",
            File.ReadAllText(appHost));
        Assert.Equal("Projects._1App", AspireEnlistment.ProjectsType("1App"));
        Assert.Equal("builder.AddProject<Projects.Web>(\"web\");", AspireEnlistment.AddToAppHost(Write("Empty.cs", "// nothing\n", bom: false), "Web.csproj"));

        Directory.CreateDirectory(Path.Combine(_root, "Host"));
        File.WriteAllText(Path.Combine(_root, "Host", "Program.cs"), "var builder = DistributedApplication.CreateBuilder(args);\nbuilder.Build().Run();\n");
        Assert.Equal(Path.Combine(_root, "Host", "Program.cs"), AspireEnlistment.AppHostFile(Path.Combine(_root, "Host", "Host.csproj")));
    }

    [Fact]
    public void The_solutions_own_apphost_and_service_defaults_are_found()
    {
        var host = Write("Host.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <Sdk Name=\"Aspire.AppHost.Sdk\" Version=\"9.5.0\" />\n</Project>", bom: false);
        var oldHost = Write("OldHost.csproj", "<Project><PropertyGroup><IsAspireHost>true</IsAspireHost></PropertyGroup></Project>", bom: false);
        var defaults = Write("Defaults.csproj", "<Project><PropertyGroup><IsAspireSharedProject>true</IsAspireSharedProject></PropertyGroup></Project>", bom: false);
        var web = Write("Web.csproj", "<Project Sdk=\"Microsoft.NET.Sdk.Web\" />", bom: false);
        Assert.Equal(new AspireProjects(host, defaults), AspireEnlistment.Detect([web, host, defaults, Path.Combine(_root, "missing.csproj")]));
        Assert.Equal(new AspireProjects(oldHost, null), AspireEnlistment.Detect([oldHost, web]));
        Assert.Equal(AspireProjects.None, AspireEnlistment.Detect([web]));
    }

    [Fact]
    public async Task Missing_templates_install_from_nuget_first()
    {
        var cli = new RecordedCli();
        cli.Answers["new list aspire-apphost"] = new CliResult(103, "No templates found matching: 'aspire-apphost'.");
        cli.Fallback = args =>
        {
            var list = args.ToList();
            var output = list[list.IndexOf("--output") + 1];
            var name = list[list.IndexOf("--name") + 1];
            Directory.CreateDirectory(output);
            File.WriteAllText(Path.Combine(output, name + ".csproj"), "<Project />");
            if (args[1] == "aspire-apphost")
            {
                File.WriteAllText(Path.Combine(output, "AppHost.cs"), "var builder = DistributedApplication.CreateBuilder(args);\nbuilder.Build().Run();\n");
            }
            else if (args[1] == "webapi")
            {
                File.WriteAllText(Path.Combine(output, "Program.cs"), "var builder = WebApplication.CreateBuilder(args);\nvar app = builder.Build();\napp.Run();\n");
            }

            return new CliResult(0, "created");
        };

        using var data = new TemplateTestData();
        var catalog = new ProjectTemplateCatalog(RecordedCli.Sdk11(), new FixedCacheLocator(data.Cache11));
        var webapi = (await catalog.LoadAsync(TestContext.Current.CancellationToken)).Single(r => r.Id == "dotnet:Microsoft.Web.WebApi|C#");
        var request = new NewProjectRequest
        {
            Group = webapi,
            Member = webapi.MemberFor("net11.0"),
            ProjectName = "Api",
            Location = _root,
            SolutionMode = SolutionMode.AddToSolution,
            Framework = "net11.0",
            AspireName = "Shop",
            HostOptions = new HostOptionValues { Aspire = true },
        };
        var progress = new List<string>();
        var result = await new NewProjectService(cli, new PathRules(false, true)).CreateAsync(NewProjectPlan.Create(request), new Progress(progress), TestContext.Current.CancellationToken);
        Assert.Equal(["Aspire.ProjectTemplates"], cli.Installed);
        Assert.Contains("Installing the Aspire templates…", progress);
        Assert.Equal(Path.Combine(_root, "Shop.AppHost", "Shop.AppHost.csproj"), result.Aspire!.AppHost);
        Assert.Equal(Path.Combine(_root, "Shop.ServiceDefaults", "Shop.ServiceDefaults.csproj"), result.Aspire.ServiceDefaults);
        Assert.True(result.Aspire.CreatedAppHost && result.Aspire.CreatedServiceDefaults);
        Assert.Null(result.Aspire.ManualSteps);
        Assert.Contains("builder.AddServiceDefaults();", File.ReadAllText(Path.Combine(_root, "Api", "Program.cs")), StringComparison.Ordinal);
        Assert.Contains("builder.AddProject<Projects.Api>(\"api\");", File.ReadAllText(Path.Combine(_root, "Shop.AppHost", "AppHost.cs")), StringComparison.Ordinal);
        Assert.All(cli.Calls.Where(c => c[1].StartsWith("aspire-", StringComparison.Ordinal)), c => Assert.Contains("--no-restore", c));
    }

    private sealed class Progress(List<string> lines) : IProgress<string>
    {
        public void Report(string value) => lines.Add(value);
    }
}

/// <summary>Aspire enlistment with the SDK installed here and the stand-in Aspire templates installed into a fresh hive.</summary>
public sealed class AspireEnlistmentIntegrationTests(TemplateHiveFixture hive) : IClassFixture<TemplateHiveFixture>
{
    private static string StandIn(string name) => Path.Combine(TemplateTestData.Source, "AspireStandIn", name);

    [Fact]
    public async Task New_and_add_mode_create_or_reuse_the_apphost()
    {
        foreach (var template in new[] { "AppHost", "ServiceDefaults" })
        {
            var (installed, output) = await hive.Cli.InstallAsync(StandIn(template), TestContext.Current.CancellationToken);
            Assert.True(installed, output);
        }

        var rows = await hive.Catalog.LoadAsync(TestContext.Current.CancellationToken);
        var webapi = rows.Single(r => r.ShortName == "webapi" && r.Language == "C#");
        var location = Path.Combine(hive.Root, "aspire-" + Guid.NewGuid().ToString("N")[..8]);
        var service = new NewProjectService(hive.Cli, new PathRules(OperatingSystem.IsWindows(), true));

        // New mode: both projects next to the web project, in the solution's folder.
        var request = new NewProjectRequest
        {
            Group = webapi,
            Member = webapi.MemberFor(webapi.DefaultFramework()),
            ProjectName = "Store.Api",
            Location = location,
            SolutionName = "Store",
            Framework = webapi.DefaultFramework(),
            HostOptions = new HostOptionValues { Aspire = true },
        };
        var created = await service.CreateAsync(NewProjectPlan.Create(request), null, TestContext.Current.CancellationToken);
        var aspire = created.Aspire!;
        Assert.Equal(Path.Combine(location, "Store", "Store.AppHost", "Store.AppHost.csproj"), aspire.AppHost);
        Assert.Equal(Path.Combine(location, "Store", "Store.ServiceDefaults", "Store.ServiceDefaults.csproj"), aspire.ServiceDefaults);
        Assert.Null(aspire.ManualSteps);
        var program = File.ReadAllText(Path.Combine(location, "Store", "Store.Api", "Program.cs"));
        Assert.Contains("builder.AddServiceDefaults();", program, StringComparison.Ordinal);
        Assert.Contains("app.MapDefaultEndpoints();", program, StringComparison.Ordinal);
        Assert.Contains("builder.AddProject<Projects.Store_Api>(\"store-api\");", File.ReadAllText(Path.Combine(location, "Store", "Store.AppHost", "AppHost.cs")), StringComparison.Ordinal);

        // Add mode with the solution's AppHost and ServiceDefaults: reused, nothing new.
        var existing = AspireEnlistment.Detect([created.ProjectPath!, aspire.AppHost, aspire.ServiceDefaults]);
        Assert.Equal(new AspireProjects(aspire.AppHost, aspire.ServiceDefaults), existing);
        var add = request with
        {
            ProjectName = "Store.Admin",
            Location = Path.Combine(location, "Store"),
            SolutionMode = SolutionMode.AddToSolution,
            ExistingAspire = existing,
            AspireName = "Store",
        };
        var added = await service.CreateAsync(NewProjectPlan.Create(add), null, TestContext.Current.CancellationToken);
        Assert.False(added.Aspire!.CreatedAppHost || added.Aspire.CreatedServiceDefaults);
        var appHost = File.ReadAllText(Path.Combine(location, "Store", "Store.AppHost", "AppHost.cs"));
        Assert.Contains("builder.AddProject<Projects.Store_Admin>(\"store-admin\");", appHost, StringComparison.Ordinal);
        Assert.Contains("builder.AddProject<Projects.Store_Api>(\"store-api\");", appHost, StringComparison.Ordinal);
        Assert.Equal(2, Directory.EnumerateDirectories(Path.Combine(location, "Store")).Count(d => Path.GetFileName(d).StartsWith("Store.App", StringComparison.Ordinal) || Path.GetFileName(d).StartsWith("Store.Service", StringComparison.Ordinal)));

        // Place solution and project in the same directory: the AppHost and ServiceDefaults beside the web project's
        // folder, none of their code under it (its default globs would compile AppHost.cs and Extensions.cs into it).
        var same = await service.CreateAsync(NewProjectPlan.Create(request with { ProjectName = "Hello", SameDirectory = true, AspireName = null }), null, TestContext.Current.CancellationToken);
        Assert.Equal(Path.Combine(location, "Hello"), Path.GetDirectoryName(same.SolutionPath)); // .slnx or .sln, as the SDK makes it
        Assert.Equal(Path.Combine(location, "Hello.AppHost", "Hello.AppHost.csproj"), same.Aspire!.AppHost);
        Assert.Equal(Path.Combine(location, "Hello.ServiceDefaults", "Hello.ServiceDefaults.csproj"), same.Aspire.ServiceDefaults);
        Assert.DoesNotContain(Directory.EnumerateDirectories(Path.Combine(location, "Hello")), d => Path.GetFileName(d).StartsWith("Hello.", StringComparison.Ordinal));
        Assert.DoesNotContain(Directory.EnumerateFiles(Path.Combine(location, "Hello"), "*.cs", SearchOption.AllDirectories), f => Path.GetFileName(f) is "AppHost.cs" or "Extensions.cs");
        Assert.Contains("builder.AddProject<Projects.Hello>(\"hello\");", File.ReadAllText(Path.Combine(location, "Hello.AppHost", "AppHost.cs")), StringComparison.Ordinal);
    }
}
