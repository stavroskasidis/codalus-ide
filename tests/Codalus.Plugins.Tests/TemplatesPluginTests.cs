using System.Text.Json;
using Codalus.Platform;
using Codalus.Plugins.Protocol;
using Codalus.Plugins.Templates;
using Codalus.Rpc;
using Codalus.Workbench;
using Codalus.Workbench.Commands;
using Codalus.Workbench.Plugins;

namespace Codalus.Plugins.Tests;

public class TemplateCatalogTests
{
    private const string Output = """
        These templates matched your input: --language='C#', --type='project'

        Template Name       Short Name                       Language  Tags
        ------------------  -------------------------------  --------  ----------------------
        .NET MAUI App       maui                             C#        MAUI/Android/iOS
        ASP.NET Core Empty  web                              C#,F#     Web/Empty
        Class Library       classlib                         [C#],F#,VB  Common/Library
        Console App         console                          [C#],F#,VB  Common/Console
        ASP.NET Core We...  webapp,razor                     C#        Web/MVC/Razor Pages
        """;

    [Fact]
    public void Parses_columns_from_the_rule_and_lists_known_templates_first()
    {
        var templates = TemplateCatalog.Parse(Output);

        Assert.Equal(["console", "classlib", "web", "webapp", "maui"], templates.Select(t => t.ShortName));
        Assert.Equal("Console App", templates[0].Name);
        Assert.Equal("C#,F#,VB", templates[0].Languages);
        Assert.Equal("ASP.NET Core Web App (Razor Pages)", templates[3].Name); // the truncated name is replaced
        Assert.Equal(".NET MAUI App", templates[4].Name);
        Assert.Equal("ClassLibrary1", templates[1].DefaultName);
    }

    [Fact]
    public void Garbage_parses_to_nothing() => Assert.Empty(TemplateCatalog.Parse("dotnet: command failed"));

    [Fact]
    public void Wpf_and_windows_forms_templates_have_names_and_go_last_off_windows()
    {
        const string desktop = """
            Template Name                    Short Name           Language    Tags
            -------------------------------  -------------------  ----------  ---------------
            Console App                      console              [C#],F#,VB  Common/Console
            Windows Forms App                winforms             [C#],VB     Common/WinForms
            Windows Forms Class Library      winformslib          [C#],VB     Common/WinForms
            Windows Forms Control Library    winformscontrollib   [C#],VB     Common/WinForms
            WPF Application                  wpf                  [C#],VB     Common/WPF
            WPF Class Library                wpflib               [C#],VB     Common/WPF
            WPF User Control Library         wpfusercontrollib    [C#],VB     Common/WPF
            Class Library                    classlib             [C#],F#,VB  Common/Library
            """;
        var templates = TemplateCatalog.Parse(desktop);
        Assert.Equal(["console", "classlib", "wpf", "wpflib", "wpfusercontrollib", "winforms", "winformslib", "winformscontrollib"], templates.Select(t => t.ShortName));
        Assert.Equal("WPF User Control Library", templates.Single(t => t.ShortName == "wpfusercontrollib").Name);
        Assert.Equal("WinFormsApp1", templates.Single(t => t.ShortName == "winforms").DefaultName);
        Assert.Equal("WpfApp1", templates.Single(t => t.ShortName == "wpf").DefaultName);
        Assert.True(templates.Single(t => t.ShortName == "winformslib").IsWindowsDesktop);
        Assert.False(templates[0].IsWindowsDesktop);

        Assert.Equal(templates, TemplateCatalog.ForThisOs(templates, windows: true));
        var elsewhere = TemplateCatalog.ForThisOs([templates[2], templates[0], templates[5], templates[1]], windows: false);
        Assert.Equal(["console", "classlib", "wpf", "winforms"], elsewhere.Select(t => t.ShortName));
    }
}

public class TemplateFrameworksTests
{
    private const string Help = """
        Console App (C#)
        Options:
          -f, --framework <net10.0|net11.0|net6.0|net7.0|net8.0|net9.0>  The target framework for the project.
                                                                         Type: choice
                                                                           net11.0  Target net11.0
                                                                           net10.0  Target net10.0
                                                                         Default: net10.0
          --langVersion <langVersion>                                    Sets the LangVersion property in the created project file
                                                                         Type: text
        """;

    [Fact]
    public void The_default_comes_first_then_the_newest()
    {
        var frameworks = TemplateFrameworks.Parse(Help);
        Assert.Equal(["net10.0", "net11.0", "net9.0", "net8.0", "net7.0", "net6.0"], frameworks.Select(f => f.Framework));
        Assert.Equal([true, false, false, false, false, false], frameworks.Select(f => f.IsDefault));
    }

    [Fact]
    public void A_narrow_console_lists_the_choices_only_under_the_option()
    {
        const string help = """
            Template options:
              -f, --framework <choice>     The target framework for the project.
                                           Type: choice
                                             net11.0  Target net11.0
                                             net10.0  Target net10.0
                                           Default: net11.0
              --langVersion <langVersion>  Sets the LangVersion property in the created
            """;
        Assert.Equal([("net11.0", true), ("net10.0", false)], TemplateFrameworks.Parse(help));
    }

    [Fact]
    public void Net_standard_comes_after_net_and_templates_without_the_option_have_none()
    {
        var frameworks = TemplateFrameworks.Parse("  -f, --framework <net10.0|netstandard2.0|netstandard2.1|net11.0>  The target framework.\n");
        Assert.Equal(["net11.0", "net10.0", "netstandard2.1", "netstandard2.0"], frameworks.Select(f => f.Framework));
        Assert.Empty(TemplateFrameworks.Parse("Solution File (C#)\nOptions:\n  --format <sln|slnx>\n"));
        Assert.Equal(".NET 11.0", TemplateFrameworks.Display("net11.0"));
        Assert.Equal(".NET Standard 2.1", TemplateFrameworks.Display("netstandard2.1"));
        Assert.Equal(".NET Core 3.1", TemplateFrameworks.Display("netcoreapp3.1"));
        Assert.Equal("net48x", TemplateFrameworks.Display("net48x"));
    }
}

/// <summary>End-to-end: the Templates plugin in a real plugin host, answering its prompts from the test.</summary>
public sealed class TemplatesPluginIntegrationTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "codalus-templates-" + Guid.NewGuid().ToString("N"));
    private WorkbenchServices _workbench = null!;
    private PluginManager _manager = null!;

    private static string TemplatesDir => Path.Combine(PluginHostIntegrationTests.RepoRoot, "src", "Plugins", "Codalus.Plugins.Templates", "bin", PluginHostIntegrationTests.Configuration, Codalus.Tests.RepoBuild.TargetFramework);

    public ValueTask InitializeAsync()
    {
        var paths = AppPaths.Resolve(PlatformInfo.Current, _ => null, Path.Combine(_root, "user"));
        paths.EnsureCreated();
        _workbench = new WorkbenchServices(paths, PlatformInfo.Current, watchFiles: false);
        _manager = new PluginManager(_workbench, _ => ProcessLaunchInfo.FromPath(PluginHostIntegrationTests.HostAssembly), "test");
        _manager.AddPlugins(PluginDiscovery.Scan([(TemplatesDir, true)], PlatformInfo.Current));
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _manager.DisposeAsync();
        _workbench.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private sealed class ScriptedPrompts(Queue<string> answers) : IPluginPrompts
    {
        public List<string> Asked { get; } = [];

        public Task<int?> PickAsync(IReadOnlyList<QuickPickItemDto> items, string? placeholder, string source, CancellationToken cancellationToken)
        {
            Asked.Add(placeholder ?? "pick");
            var wanted = answers.Dequeue();
            return Task.FromResult<int?>(items.ToList().FindIndex(i => i.Description == wanted));
        }

        public Task<string?> InputAsync(string title, string? prompt, string? value, string source, CancellationToken cancellationToken)
        {
            Asked.Add(title);
            return Task.FromResult<string?>(answers.Dequeue());
        }
    }

    [Fact]
    public async Task New_project_without_a_solution_creates_and_opens_it()
    {
        Assert.Equal(PluginState.Ready, _manager.Plugins.Single().State);
        var location = Path.Combine(_root, "repos");
        // The framework: one every SDK from 10 on offers, not necessarily the default.
        var prompts = new ScriptedPrompts(new Queue<string>(["console", "C#", location, "1nvalid", "Hello.App", "net10.0"]));
        _manager.Prompts = prompts;
        var opened = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _workbench.Commands.Register(new CommandDescriptor("solution.getInfo", "info", ContributionSource.Shell), (_, _) => Task.FromResult<JsonElement?>(null));
        _workbench.Commands.Register(new CommandDescriptor("workbench.openSolution", "open", ContributionSource.Shell), (args, _) =>
        {
            opened.TrySetResult(args[0].GetString()!);
            return Task.FromResult<JsonElement?>(null);
        });

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        var result = await _workbench.Commands.ExecuteAsync("templates.newProject", timeout.Token);

        var project = await opened.Task.WaitAsync(timeout.Token);
        Assert.Equal(Path.Combine(location, "Hello.App", "Hello.App.csproj"), project);
        Assert.Equal(project, result!.Value.GetString());
        Assert.True(File.Exists(Path.Combine(location, "Hello.App", "Program.cs")));
        Assert.Equal(["Select a project template", "Select the language", "Location", "Project name", "Project name", "Target framework"], prompts.Asked); // the invalid name was asked again
        Assert.Contains("<TargetFramework>net10.0</TargetFramework>", File.ReadAllText(project), StringComparison.Ordinal);
    }

    [Fact]
    public async Task New_project_in_visual_basic_passes_the_language()
    {
        var location = Path.Combine(_root, "vb");
        var prompts = new ScriptedPrompts(new Queue<string>(["console", "VB", location, "Hello.Vb", "net10.0"]));
        _manager.Prompts = prompts;
        var opened = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _workbench.Commands.Register(new CommandDescriptor("solution.getInfo", "info", ContributionSource.Shell), (_, _) => Task.FromResult<JsonElement?>(null));
        _workbench.Commands.Register(new CommandDescriptor("workbench.openSolution", "open", ContributionSource.Shell), (args, _) =>
        {
            opened.TrySetResult(args[0].GetString()!);
            return Task.FromResult<JsonElement?>(null);
        });

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        await _workbench.Commands.ExecuteAsync("templates.newProject", timeout.Token);

        var project = await opened.Task.WaitAsync(timeout.Token);
        Assert.Equal(Path.Combine(location, "Hello.Vb", "Hello.Vb.vbproj"), project);
        Assert.True(File.Exists(Path.Combine(location, "Hello.Vb", "Program.vb")));
    }
}
