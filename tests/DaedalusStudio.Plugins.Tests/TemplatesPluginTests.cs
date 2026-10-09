using System.Text.Json;
using DaedalusStudio.Platform;
using DaedalusStudio.Plugins.Protocol;
using DaedalusStudio.Plugins.Templates;
using DaedalusStudio.Rpc;
using DaedalusStudio.Workbench;
using DaedalusStudio.Workbench.Commands;
using DaedalusStudio.Workbench.Plugins;

namespace DaedalusStudio.Plugins.Tests;

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
}

/// <summary>End-to-end: the Templates plugin in a real plugin host, answering its prompts from the test.</summary>
public sealed class TemplatesPluginIntegrationTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "daedalus-templates-" + Guid.NewGuid().ToString("N"));
    private WorkbenchServices _workbench = null!;
    private PluginManager _manager = null!;

    private static string TemplatesDir => Path.Combine(PluginHostIntegrationTests.RepoRoot, "src", "Plugins", "DaedalusStudio.Plugins.Templates", "bin", PluginHostIntegrationTests.Configuration, "net10.0");

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
        var prompts = new ScriptedPrompts(new Queue<string>(["console", location, "1nvalid", "Hello.App"]));
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
        Assert.Equal(["Select a project template", "Location", "Project name", "Project name"], prompts.Asked); // the invalid name was asked again
    }
}
