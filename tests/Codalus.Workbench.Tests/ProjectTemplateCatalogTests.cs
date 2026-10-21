using Codalus.Workbench.Projects;

namespace Codalus.Workbench.Tests;

/// <summary>
/// New Project's template catalog (ADR 0082) on the recorded caches and packages of SDK 10.0 and 11.0 (TestData/Templates):
/// groups and members, the IDE host files in both package layouts and any case, parameters, tags, order, the fallback to
/// the list and help, localized strings, packs and the files to open.
/// </summary>
public sealed class ProjectTemplateCatalogTests : IDisposable
{
    private static readonly DateOnly s_today = new(2026, 10, 21);
    private readonly TemplateTestData _data = new();

    public void Dispose() => _data.Dispose();

    private async Task<IReadOnlyList<ProjectTemplateGroup>> LoadAsync(string? cache = null, RecordedCli? cli = null)
    {
        var catalog = new ProjectTemplateCatalog(cli ?? RecordedCli.Sdk11(), new FixedCacheLocator(cache ?? _data.Cache11), () => s_today);
        return await catalog.LoadAsync(TestContext.Current.CancellationToken);
    }

    private static ProjectTemplateGroup Row(IReadOnlyList<ProjectTemplateGroup> rows, string id) => Assert.Single(rows, r => r.Id == id);

    [Fact]
    public async Task One_row_per_group_and_language_filtered_by_the_list_output()
    {
        var rows = await LoadAsync();
        var dotnet = rows.Where(r => r.Kind == ProjectTemplateKind.DotnetNew).Select(r => r.Id).ToList();
        Assert.Equal(
            [
                "dotnet:Microsoft.Common.Console|C#", "dotnet:Microsoft.Common.Console|F#", "dotnet:Microsoft.Common.Console|VB",
                "dotnet:Microsoft.Common.Library|C#", "dotnet:Microsoft.Test.xUnit|C#", "dotnet:Microsoft.Web.WebApi|C#",
                "dotnet:Microsoft.Web.Blazor|C#", "dotnet:Microsoft.Common.WPF|C#",
            ],
            dotnet);

        // The MCP server template is in the cache but not in the list (it isn't usable here); the item template is no project.
        Assert.DoesNotContain(rows, r => r.ShortName is "mcpserver" or "class");
        var console = Row(rows, "dotnet:Microsoft.Common.Console|C#");
        Assert.Equal(("Console App", "console", "C#"), (console.Name, console.ShortName, console.Language));
        Assert.Equal(["Microsoft.Common.Console.CSharp.11.0", "Microsoft.Common.Console.CSharp.10.0"], console.Members.Select(m => m.Identity));
        Assert.Equal("ConsoleApp1", console.MemberFor(null)!.DefaultName);
        Assert.Equal("Company.ConsoleApplication1", console.MemberFor(null)!.SourceName);
        Assert.Equal("--no-restore", console.MemberFor(null)!.SkipRestoreOption);

        // The built-in rows follow: .NET Framework, the packs that aren't installed, Blank Solution last.
        Assert.Equal(["netfx:netfx-console", "netfx:netfx-classlib", "pack:Avalonia.Templates", "pack:CoreWCF.Templates", ProjectTemplateCatalog.BlankSolutionId],
            rows.Where(r => r.Kind != ProjectTemplateKind.DotnetNew).Select(r => r.Id));
    }

    [Fact]
    public async Task Frameworks_merge_and_select_the_member()
    {
        var xunit = Row(await LoadAsync(), "dotnet:Microsoft.Test.xUnit|C#");
        Assert.Equal(["net11.0", "net10.0"], xunit.Frameworks.Select(f => f.Moniker));
        Assert.Equal([".NET 11.0 (Preview)", ".NET 10.0 (Long Term Support)"], xunit.Frameworks.Select(f => f.Label));
        Assert.Equal("net10.0", xunit.DefaultFramework()); // the newest that isn't a preview
        Assert.Equal("net11.0", xunit.DefaultFramework(remembered: "net11.0"));
        Assert.Equal("net10.0", xunit.DefaultFramework(remembered: "net6.0")); // not offered: the rule again

        var ten = xunit.MemberFor("net10.0")!;
        Assert.Equal("Microsoft.Test.xUnit.CSharp.10.0", ten.Identity);
        Assert.DoesNotContain(ten.Parameters, p => p.Name is "XUnitVersion" or "TestRunner");

        var eleven = xunit.MemberFor("net11.0")!;
        Assert.Equal("Microsoft.Test.xUnit.CSharp.11.0", eleven.Identity);
        var version = Assert.Single(eleven.Parameters, p => p.Name == "XUnitVersion");
        Assert.Equal(("xUnit.net version", "shared::XUnitVersion", TemplateDataType.Choice), (version.Label, version.PersistenceKey, version.DataType));
        Assert.Equal(["v2", "v3"], version.Choices.Select(c => c.Value));
        var runner = Assert.Single(eleven.Parameters, p => p.Name == "TestRunner");
        Assert.Equal("(XUnitVersion == v3)", runner.EnabledCondition);
        Assert.Equal(xunit.MemberFor("net11.0"), xunit.MemberFor(null)); // no framework: the highest precedence
    }

    [Fact]
    public async Task Reads_host_files_in_both_layouts_and_any_case()
    {
        var rows = await LoadAsync();
        var blazor = Row(rows, "dotnet:Microsoft.Web.Blazor|C#");
        var eleven = blazor.MemberFor("net11.0")!; // its package has the template at the root
        var ten = blazor.MemberFor("net10.0")!; // under content/
        Assert.Equal("Microsoft.Web.Blazor.CSharp.10.0", ten.Identity);
        Assert.Contains(eleven.Parameters, p => p.Name == "InteractivityPlatform");
        Assert.Contains(ten.Parameters, p => p.Name == "InteractivityPlatform");
        Assert.Equal("group:Microsoft.Web.Blazor", eleven.Parameters.Single(p => p.Name == "InteractivityPlatform").PersistenceKey);

        // Console's capitalized PersistenceScope and PersistenceScopeName.
        var console = Row(rows, "dotnet:Microsoft.Common.Console|C#").MemberFor("net11.0")!;
        Assert.Equal(["UseProgramMain", "NativeAot"], console.Parameters.Select(p => p.Name));
        Assert.Equal("shared:Microsoft", console.Parameters[0].PersistenceKey);
        Assert.Null(console.Parameters[1].PersistenceKey);
        Assert.Equal("Do not use _top-level statements", console.Parameters[0].Label);

        // Console 11.0's "isHidden":"true" (a string) hides the symbol on the CLI.
        var cached = TemplateCacheReader.Read(_data.Cache11)!.Single(t => t.Identity == "Microsoft.Common.Console.CSharp.11.0");
        Assert.True(cached.CliSymbols["TargetFrameworkOverride"].IsHidden);
        Assert.Equal("target-framework-override", cached.CliSymbols["targetframeworkoverride"].LongName);

        // A cache with a byte order mark reads the same.
        using var bom = new TemplateTestData(bom: true);
        Assert.Equal(13, TemplateCacheReader.Read(bom.Cache11)!.Count);
        Assert.Equal(3, TemplateCacheReader.Read(bom.Cache10)!.Count);
        Assert.Equal((byte)0xEF, File.ReadAllBytes(bom.Cache11)[0]);
    }

    [Fact]
    public async Task Visible_parameters_follow_ide_host_json()
    {
        var rows = await LoadAsync();
        var blazor = Row(rows, "dotnet:Microsoft.Web.Blazor|C#").MemberFor("net11.0")!;
        Assert.Equal(["InteractivityPlatform", "InteractivityLocation", "IncludeSampleContent", "UseProgramMain", "LocalhostTld"], blazor.Parameters.Select(p => p.Name));
        Assert.Equal("_Interactive render mode", blazor.Parameters[0].Label);
        Assert.Equal("--interactivity", blazor.Parameters[0].CliOption);
        Assert.Equal("--InteractivityLocation", blazor.Parameters[1].CliOption); // hidden on the CLI, taken by its symbol name
        Assert.Equal("(InteractivityPlatform != \"None\")", blazor.Parameters[1].EnabledCondition);
        var samples = blazor.Parameters[2];
        Assert.Equal(("Include _sample pages", "--empty", true, "true"), (samples.Label, samples.CliOption, samples.InverseCliValue, samples.DisplayDefault));
        Assert.Equal("--use-program-main", blazor.Parameters[3].CliOption);

        // auth, UseLocalDB and NoHttps are the IDE's host options, not rows.
        Assert.Equal(["None", "Individual"], blazor.HostOptions.AuthChoices);
        Assert.Equal(("--auth", "--use-local-db", "--no-https", "NoHttps"), (blazor.HostOptions.AuthOption, blazor.HostOptions.LocalDbOption, blazor.HostOptions.DisableHttpsOption, blazor.HostOptions.DisableHttpsSymbol));
        Assert.True(blazor.HostOptions.Docker);
        Assert.True(blazor.HostOptions.Aspire);
        Assert.True(blazor.ComputedSymbols.ContainsKey("UseWebAssembly"));

        var webapi = Row(rows, "dotnet:Microsoft.Web.WebApi|C#").MemberFor(null)!;
        Assert.Equal(["DisableOpenAPI", "UseProgramMain", "UseMinimalAPIs"], webapi.Parameters.Select(p => p.Name));
        var openApi = webapi.Parameters[0];
        Assert.Equal(("Enable _OpenAPI support", true, "true", "false", "--no-openapi"), (openApi.Label, openApi.Invert, openApi.DisplayDefault, openApi.DefaultValue, openApi.CliOption));
        var controllers = webapi.Parameters[2];
        Assert.Equal(("Use controllers", true, "false", "true", "--use-minimal-apis"), (controllers.Label, controllers.Invert, controllers.DisplayDefault, controllers.DefaultValue, controllers.CliOption));
        Assert.Equal(["None", "SingleOrg", "Windows"], webapi.HostOptions.AuthChoices); // IndividualB2C isn't offered

        // Test projects and libraries aren't enlisted in Aspire; containers only where the template supports them.
        var xunit = Row(rows, "dotnet:Microsoft.Test.xUnit|C#").MemberFor(null)!;
        Assert.False(xunit.HostOptions.Aspire);
        Assert.False(xunit.HostOptions.Docker);
        Assert.True(Row(rows, "dotnet:Microsoft.Common.Console|C#").MemberFor(null)!.HostOptions.IsEmpty);
    }

    [Fact]
    public async Task Templates_without_ide_host_json_show_cli_visible_parameters_minus_plumbing()
    {
        var wpf = Row(await LoadAsync(), "dotnet:Microsoft.Common.WPF|C#").MemberFor(null)!;
        Assert.NotEmpty(wpf.Parameters);
        Assert.DoesNotContain(wpf.Parameters, p => p.Name is "name" or "language" or "type" or "Framework" or "TargetFrameworkOverride" or "langVersion" or "skipRestore");
        Assert.All(wpf.Parameters, p => Assert.Null(p.PersistenceKey));

        // The same rule for a member read without its package's files: console's CLI-visible options.
        var cached = TemplateCacheReader.Read(_data.Cache11)!.Single(t => t.Identity == "Microsoft.Common.Console.CSharp.11.0");
        var bare = ProjectTemplateCatalog.Member(cached, TemplatePackageFiles.Empty, "Microsoft.Common.Console", new HashSet<string>(), s_today);
        Assert.Equal(["UseProgramMain", "NativeAot"], bare.Parameters.Select(p => p.Name));
        Assert.Equal("Enable _native AOT publish", bare.Parameters[1].Label);
        Assert.Null(bare.OpenFileRule);
    }

    [Fact]
    public async Task Platforms_types_and_windows_only()
    {
        var rows = await LoadAsync();
        var console = Row(rows, "dotnet:Microsoft.Common.Console|C#");
        Assert.Equal(["Windows", "Linux", "macOS"], console.Platforms);
        Assert.Equal(["Console"], console.ProjectTypes);
        Assert.Equal(["C#", "Windows", "Linux", "macOS", "Console"], console.Chips);
        Assert.Equal("template-console", console.IconKind);
        Assert.Equal(["Visual Basic", "Windows", "Linux", "macOS", "Console"], Row(rows, "dotnet:Microsoft.Common.Console|VB").Chips);

        var xunit = Row(rows, "dotnet:Microsoft.Test.xUnit|C#");
        Assert.Contains("Test", xunit.ProjectTypes);
        Assert.Equal(["Windows", "Linux", "macOS"], xunit.Platforms);
        Assert.Equal("template-test", xunit.IconKind);
        Assert.False(xunit.RunsOnlyOnWindows);

        var wpf = Row(rows, "dotnet:Microsoft.Common.WPF|C#");
        Assert.True(wpf.RunsOnlyOnWindows);
        Assert.Equal(["Windows"], wpf.Platforms);
        Assert.Equal(["Desktop"], wpf.ProjectTypes);
        Assert.Equal("template-desktop", wpf.IconKind);
        Assert.Equal("template-library", Row(rows, "dotnet:Microsoft.Common.Library|C#").IconKind);
        Assert.Equal("template-web", Row(rows, "dotnet:Microsoft.Web.Blazor|C#").IconKind);

        // Windows only from an os constraint, though the short name has no known prefix; platform tags can say it too.
        Assert.True(ProjectTemplateCatalog.RunsOnlyOnWindows(["Windows"], [], ["Common"], "mytemplate"));
        Assert.False(ProjectTemplateCatalog.RunsOnlyOnWindows(["Windows", "Linux"], [], ["WPF"], "wpf"));
        Assert.True(ProjectTemplateCatalog.RunsOnlyOnWindows([], [new HostTags("platform", ["Windows"], [])], ["Common"], "mytemplate"));
        Assert.False(ProjectTemplateCatalog.RunsOnlyOnWindows([], [], ["Common", "Console"], "console"));
        Assert.True(ProjectTemplateCatalog.RunsOnlyOnWindows([], [], ["Common", "WinUI"], "winui"));
        Assert.Equal(["Windows", "Android", "iOS", "Mac Catalyst"], TemplateTags.PlatformsOf(["MAUI", "Mobile"], [], runsOnlyOnWindows: false));
        Assert.Equal(["Windows", "Linux", "macOS", "Web"], TemplateTags.PlatformsOf(["Web", "Blazor", "WebAssembly"], [], runsOnlyOnWindows: false));
    }

    [Fact]
    public async Task Known_templates_first_then_ide_order_then_name()
    {
        var rows = await LoadAsync();
        Assert.Equal(["console", "console", "console", "classlib", "xunit", "webapi", "blazor", "wpf"], rows.Where(r => r.Kind == ProjectTemplateKind.DotnetNew).Select(r => r.ShortName));

        // Unknown templates follow the known ones by their IDE host file's order, then by name.
        var known = new ProjectTemplateGroup { Id = "a", Kind = ProjectTemplateKind.DotnetNew, Name = "Zeta", Order = 3 };
        var ordered = new ProjectTemplateGroup { Id = "b", Kind = ProjectTemplateKind.DotnetNew, Name = "Beta", Order = 1000 + 200 };
        var unordered = new ProjectTemplateGroup { Id = "c", Kind = ProjectTemplateKind.DotnetNew, Name = "Alpha", Order = 1000 + 100000 };
        Assert.Equal(["a", "b", "c"], new[] { unordered, ordered, known }.OrderBy(g => g.Order).ThenBy(g => g.Name, StringComparer.Ordinal).Select(g => g.Id));
    }

    [Fact]
    public async Task Unknown_or_missing_cache_falls_back_to_list_columns_and_help()
    {
        var cli = RecordedCli.Sdk11();
        var catalog = new ProjectTemplateCatalog(cli, new FixedCacheLocator(null), () => s_today);
        var rows = await catalog.LoadAsync(TestContext.Current.CancellationToken);
        Assert.Equal("list", catalog.Source);
        var dotnet = rows.Where(r => r.Kind == ProjectTemplateKind.DotnetNew).ToList();
        Assert.Equal(["console", "console", "console", "classlib", "classlib", "classlib", "xunit", "xunit", "xunit", "webapi", "webapi", "blazor", "wpf", "wpf"], dotnet.Select(r => r.ShortName));
        var blazor = Assert.Single(dotnet, r => r.ShortName == "blazor");
        Assert.Equal(("Blazor Web App", "dotnet:blazor|C#"), (blazor.Name, blazor.Id));
        Assert.Equal(["Web", "Blazor", "WebAssembly"], blazor.Classifications);
        Assert.True(Assert.Single(blazor.Members).NeedsHelp);
        Assert.Null(blazor.Members[0].OpenFileRule);

        // Choosing it reads its help: frameworks, options, and the host options taken out of them.
        var member = await catalog.CompleteAsync(blazor, blazor.Members[0], TestContext.Current.CancellationToken);
        Assert.False(member.NeedsHelp);
        Assert.Equal(["net11.0", "net10.0"], member.Frameworks.Select(f => f.Moniker));
        Assert.Equal("net11.0", member.DefaultFramework);
        Assert.Equal(["--interactivity", "--empty", "--all-interactive", "--use-program-main", "--localhost-tld"], member.Parameters.Select(p => p.CliOption));
        Assert.Equal(["None", "Individual"], member.HostOptions.AuthChoices);
        Assert.Equal(("--no-https", "--use-local-db", "--no-restore"), (member.HostOptions.DisableHttpsOption, member.HostOptions.LocalDbOption, member.SkipRestoreOption));
        Assert.Same(member, await catalog.CompleteAsync(blazor, blazor.Members[0], TestContext.Current.CancellationToken)); // once per row
        Assert.Single(cli.Calls, c => c.Contains("--help"));

        // A cache of another format reads as none.
        var other = Path.Combine(_data.Root, "other.json");
        await File.WriteAllTextAsync(other, "{ \"Version\": \"2.0.0.0\", \"TemplateInfo\": [] }", TestContext.Current.CancellationToken);
        Assert.Null(TemplateCacheReader.Read(other));
        var withOther = new ProjectTemplateCatalog(RecordedCli.Sdk11(), new FixedCacheLocator(other), () => s_today);
        await withOther.LoadAsync(TestContext.Current.CancellationToken);
        Assert.Equal("list", withOther.Source);
    }

    [Fact]
    public void Localized_strings_from_the_templates_localize_file()
    {
        var cached = TemplateCacheReader.Read(_data.Cache11)!;
        var available = new HashSet<(string, string)> { ("console", "C#") };
        var french = ProjectTemplateCatalog.FromCache(cached, available, "fr", s_today);
        var console = Assert.Single(french);
        Assert.Equal("Application console", console.Name);
        Assert.StartsWith("Projet de création", console.Description, StringComparison.Ordinal);
        Assert.Equal("N’utilisez pas _d’instructions de niveau global", console.MemberFor("net11.0")!.Parameters[0].Label);

        // The 10.0 member has no French strings in the recorded package: the cache's English.
        Assert.Equal("Do not use _top-level statements", console.MemberFor("net10.0")!.Parameters[0].Label);
        Assert.Equal("Console App", ProjectTemplateCatalog.FromCache(cached, available, "pt-BR", s_today)[0].Name);
    }

    [Fact]
    public void Template_packs_not_installed_are_offered()
    {
        Assert.Equal(["Avalonia.Templates", "CoreWCF.Templates"], TemplatePacks.Missing(["console", "classlib"]).Select(p => p.Package));
        Assert.Equal(["Avalonia.Templates"], TemplatePacks.Missing(["console", "corewcf"]).Select(p => p.Package));
        var packs = ProjectTemplateCatalog.BuiltInRows(["avalonia.app", "console"], s_today).Where(r => r.Kind == ProjectTemplateKind.Pack).ToList();
        var coreWcf = Assert.Single(packs);
        Assert.Equal(("CoreWCF templates", "CoreWCF.Templates", "template-install"), (coreWcf.Name, coreWcf.Package, coreWcf.IconKind));
        Assert.Equal(["Not installed"], coreWcf.Chips);
        Assert.Equal("CoreWCFService1", ProjectTemplateCatalog.DefaultNameFor("corewcf", "CoreWCF Service"));
    }

    [Fact]
    public void Wpf_and_windows_forms_have_readable_names()
    {
        const string desktop = """
            Template Name                    Short Name           Language    Type     Author     Tags
            -------------------------------  -------------------  ----------  -------  ---------  ---------------
            Console App                      console              [C#],F#,VB  project  Microsoft  Common/Console
            Windows Forms App                winforms             [C#],VB     project  Microsoft  Common/WinForms
            Windows Forms Class Library      winformslib          [C#],VB     project  Microsoft  Common/WinForms
            WPF User Control Library         wpfusercontrollib    [C#],VB     project  Microsoft  Common/WPF
            WPF Applica...                   wpf                  [C#],VB     project  Microsoft  Common/WPF
            """;
        var rows = ProjectTemplateCatalog.FromList(desktop);
        Assert.Equal("WPF Application", rows.First(r => r.ShortName == "wpf").Name); // the truncated name is replaced
        Assert.Equal("WPF User Control Library", rows.First(r => r.ShortName == "wpfusercontrollib").Name);
        Assert.Equal("WinFormsApp1", rows.First(r => r.ShortName == "winforms").Members[0].DefaultName);
        Assert.Equal("WpfApp1", rows.First(r => r.ShortName == "wpf").Members[0].DefaultName);
        Assert.True(rows.First(r => r.ShortName == "winformslib").RunsOnlyOnWindows);
        Assert.False(rows.First(r => r.ShortName == "console").RunsOnlyOnWindows);
        Assert.Equal(["C#", "VB"], rows.Where(r => r.ShortName == "winforms").Select(r => r.Language));

        var netfx = ProjectTemplateCatalog.BuiltInRows([], s_today).Where(r => r.Kind == ProjectTemplateKind.NetFramework).ToList();
        Assert.Equal([".NET Framework Console App", ".NET Framework Class Library"], netfx.Select(r => r.Name));
        Assert.All(netfx, r => Assert.True(r.RunsOnlyOnWindows));
        Assert.Equal([".NET Framework 4.8.1", ".NET Framework 4.8", ".NET Framework 4.7.2", ".NET Framework 4.6.2"], netfx[0].Frameworks.Select(f => f.Label));
        Assert.Equal("net48", netfx[0].DefaultFramework());
    }

    [Fact]
    public async Task Open_file_rule_from_template_json()
    {
        var rows = await LoadAsync();
        var values = new Dictionary<string, string?>();
        foreach (var (id, file) in new[] { ("dotnet:Microsoft.Common.Console|C#", "Program.cs"), ("dotnet:Microsoft.Common.Console|VB", "Program.vb"), ("dotnet:Microsoft.Common.Console|F#", "Program.fs") })
        {
            var member = Row(rows, id).MemberFor(null)!;
            var project = Path.Combine(_data.Root, "Hello" + Path.GetExtension(file).TrimStart('.'));
            Directory.CreateDirectory(project);
            await File.WriteAllTextAsync(Path.Combine(project, file), string.Empty, TestContext.Current.CancellationToken);
            Assert.Equal([Path.Combine(project, file)], member.OpenFileRule!.Resolve(values, project, "Hello"));
            File.Delete(Path.Combine(project, file));
            Assert.Empty(member.OpenFileRule.Resolve(values, project, "Hello")); // only files that exist
        }

        // xUnit opens UnitTest1.cs; Blazor and the Web API open nothing.
        var xunit = Row(rows, "dotnet:Microsoft.Test.xUnit|C#").MemberFor("net11.0")!;
        var tests = Path.Combine(_data.Root, "Tests");
        Directory.CreateDirectory(tests);
        await File.WriteAllTextAsync(Path.Combine(tests, "UnitTest1.cs"), string.Empty, TestContext.Current.CancellationToken);
        Assert.Equal([Path.Combine(tests, "UnitTest1.cs")], xunit.OpenFileRule!.Resolve(values, tests, "Tests"));
        Assert.Null(Row(rows, "dotnet:Microsoft.Web.Blazor|C#").MemberFor(null)!.OpenFileRule);
        Assert.Null(Row(rows, "dotnet:Microsoft.Web.WebApi|C#").MemberFor(null)!.OpenFileRule);

        // The template's longest file (Blazor's account pages), with its source name, for the path-length check.
        Assert.Equal("BlazorWebCSharp.1/Components/Account/IdentityComponentsEndpointRouteBuilderExtensions.cs", Row(rows, "dotnet:Microsoft.Web.Blazor|C#").MemberFor("net11.0")!.LongestContentPath);
    }

    [Fact]
    public async Task The_cli_changing_reloads_the_catalog()
    {
        var cli = RecordedCli.Sdk11();
        var catalog = new ProjectTemplateCatalog(cli, new FixedCacheLocator(_data.Cache11), () => s_today);
        var first = catalog.LoadAsync(TestContext.Current.CancellationToken);
        Assert.Same(first, catalog.LoadAsync(TestContext.Current.CancellationToken));
        await first;
        var changed = 0;
        catalog.Changed += (_, _) => changed++;
        await cli.InstallAsync("Avalonia.Templates", TestContext.Current.CancellationToken);
        Assert.Equal(1, changed);
        Assert.NotSame(first, catalog.LoadAsync(TestContext.Current.CancellationToken));
    }
}

/// <summary>The catalog on the SDKs installed here (a fresh hive, so the user's own template settings are never touched).</summary>
public sealed class ProjectTemplateCatalogSdkTests : IDisposable
{
    private readonly string _hive = Path.Combine(Path.GetTempPath(), "codalus-hive-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_hive, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public async Task Reads_the_installed_sdks_cache()
    {
        Directory.CreateDirectory(_hive);
        var cli = new TemplateService(hive: _hive, workingDirectory: _hive);
        var catalog = new ProjectTemplateCatalog(cli);
        var rows = await catalog.LoadAsync(TestContext.Current.CancellationToken);
        Assert.True(catalog.Source == "cache", $"Read from {catalog.Source}: {catalog.LoadError}");
        Assert.StartsWith(_hive, catalog.CacheFile, StringComparison.Ordinal);
        Assert.Equal("templatecache.json", Path.GetFileName(catalog.CacheFile));
        foreach (var shortName in new[] { "console", "classlib", "xunit" })
        {
            var row = Assert.Single(rows, r => r.ShortName == shortName && r.Language == "C#");
            Assert.NotEmpty(row.Frameworks);
            Assert.All(row.Frameworks.Where(f => !f.Moniker.StartsWith("netstandard", StringComparison.Ordinal)), f => Assert.True(f.Label.EndsWith("Support)", StringComparison.Ordinal) || f.Label.EndsWith("(Preview)", StringComparison.Ordinal), f.Label));
            Assert.NotNull(row.DefaultFramework());
        }

        Assert.NotNull(Assert.Single(rows, r => r.ShortName == "console" && r.Language == "C#").MemberFor(null)!.OpenFileRule);
    }
}
