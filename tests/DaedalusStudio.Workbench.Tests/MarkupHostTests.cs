using System.Reflection;
using DaedalusStudio.LanguageServices.Client;
using DaedalusStudio.LanguageServices.Protocol;
using DaedalusStudio.Rpc;
using DaedalusStudio.Workbench.Languages;
using DaedalusStudio.Workbench.Projects;

namespace DaedalusStudio.Workbench.Tests;

/// <summary>The markup language server (ADR 0016) through the workbench: JSON, MSBuild, XML, YAML and .editorconfig.</summary>
public sealed class MarkupHostTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "daedalus-markup-" + Guid.NewGuid().ToString("N"));
    private readonly LanguageRegistry _languages = new();
    private readonly LanguageServiceRegistry _servers = new();
    private readonly SolutionService _solutions = new(() => null);
    private LanguageService _service = null!;
    private int _version;

    private static string Metadata(string key) =>
        typeof(MarkupHostTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == key).Value!;

    private static string HostPath => Path.Combine(Metadata("DaedalusRepoRoot"), "src", "DaedalusStudio.Markup.Host", "bin", Metadata("DaedalusConfiguration"), "net10.0", "daedalus-markuphost.dll");

    private static CancellationToken Timeout() =>
        CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken, new CancellationTokenSource(TimeSpan.FromSeconds(60)).Token).Token;

    public ValueTask InitializeAsync()
    {
        Assert.True(File.Exists(HostPath), $"Markup host not built: {HostPath}");
        Directory.CreateDirectory(_root);
        foreach (var (id, extensions, names) in new (string, string[], string[])[]
        {
            ("json", [".json"], []), ("jsonc", [".jsonc"], ["launchSettings.json"]), ("xml", [".xml"], []), ("msbuild", [".csproj", ".props"], []),
            ("yaml", [".yml", ".yaml"], []), ("editorconfig", [], [".editorconfig"]),
        })
        {
            _languages.Register(new LanguageDefinition(id, id) { Extensions = extensions, FileNames = names });
        }

        _servers.Register(new LanguageServiceDescriptor("markup", "Markup", [], null, () => ProcessLaunchInfo.FromPath(HostPath))
        {
            Languages = MarkupHostLaunch.Languages,
            InitializationOptions = System.Text.Json.JsonDocument.Parse("""{ "schemaDownload": false }""").RootElement.Clone(),
        });
        _service = new LanguageService(_servers, _solutions, languages: _languages)
        {
            PropertyEvaluator = (project, name, _) => Task.FromResult<(string?, string?)?>(name == "TargetFramework" ? ("net10.0", "set in App.csproj:3") : null),
        };
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _service.DisposeAsync();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private async Task<(string Path, string Uri, LanguageHostClient Client)> OpenAsync(string relative, string text)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, text, TestContext.Current.CancellationToken);
        var language = _service.LanguageIdOf(path)!;
        var version = ++_version;
        Assert.True(await _service.OpenAsync(new LanguageDocument(path, language, () => (text, version))), language);
        return (path, DocumentUris.FromPath(path), (await _service.ClientAsync(language))!);
    }

    private async Task<IReadOnlyList<Diagnostic>> DiagnosticsAsync(string path, Func<IReadOnlyList<Diagnostic>, bool> until, CancellationToken ct)
    {
        while (!until(_service.Diagnostics.For(path)))
        {
            await Task.Delay(20, ct);
        }

        return _service.Diagnostics.For(path);
    }

    private static Position At(string text, string marker)
    {
        var offset = text.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        var line = text[..offset].Count(c => c == '\n');
        return new Position(line, offset - (text.LastIndexOf('\n', Math.Max(0, offset - 1)) + 1));
    }

    [Fact]
    public async Task Json_files_get_syntax_errors_schema_validation_completion_and_hover()
    {
        var ct = Timeout();

        // A syntax error, positioned.
        var (broken, _, _) = await OpenAsync("broken.json", "{\n  \"a\": 1,,\n}\n");
        var syntax = Assert.Single(await DiagnosticsAsync(broken, d => d.Count > 0, ct));
        Assert.Equal("json.syntax", syntax.Code);
        Assert.Equal(1, syntax.Range.Start.Line);

        // global.json: the bundled schema validates, completes and describes.
        var text = "{\n  \"sdk\": {\n    \n    \"rollForward\": \"sideways\"\n  }\n}\n";
        var (global, uri, client) = await OpenAsync("global.json", text);
        var problem = Assert.Single(await DiagnosticsAsync(global, d => d.Count > 0, ct));
        Assert.Equal("json.schema", problem.Code);
        Assert.Contains("latestFeature", problem.Message, StringComparison.Ordinal);

        var keys = await client.CompletionAsync(uri, new Position(2, 4), null, ct);
        Assert.Contains(keys.Items, i => i.Label == "version");
        Assert.Contains(keys.Items, i => i.Label == "allowPrerelease");
        Assert.DoesNotContain(keys.Items, i => i.Label == "rollForward"); // already there
        var values = await client.CompletionAsync(uri, At(text, "\"rollForward\": \""), null, ct);
        Assert.Contains(values.Items, i => i.Label == "latestMinor");

        var hover = await client.HoverAsync(uri, At(text, "\"rollFor"), ct);
        Assert.Contains("roll forward", hover!.Contents.Value, StringComparison.Ordinal);

        // appsettings.Development.json: log levels.
        var settings = "{\n  \"Logging\": { \"LogLevel\": { \"Default\": \"\" } }\n}\n";
        var (_, settingsUri, _) = await OpenAsync("appsettings.Development.json", settings);
        var levels = await client.CompletionAsync(settingsUri, At(settings, "\"Default\": \""), null, ct);
        Assert.Contains(levels.Items, i => i.Label == "Warning");

        // A $schema next to the file.
        await File.WriteAllTextAsync(Path.Combine(_root, "my.schema.json"), """{ "type": "object", "properties": { "port": { "type": "integer", "maximum": 65535 } } }""", ct);
        var (custom, _, _) = await OpenAsync("custom.json", "{ \"$schema\": \"my.schema.json\", \"port\": 70000 }");
        Assert.Contains((await DiagnosticsAsync(custom, d => d.Count > 0, ct)), d => d.Message.Contains("65535", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Msbuild_files_complete_elements_and_values_and_hover_shows_the_evaluated_value()
    {
        var ct = Timeout();
        var text = "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>net10.0</TargetFramework>\n    <OutputType>Ex</OutputType>\n    <\n  </PropertyGroup>\n  <ItemGroup>\n    <PackageReference Include=\"X\" PrivateAssets=\"\" />\n  </ItemGroup>\n</Project>\n";

        // Not yet well-formed (the bare '<'): one XML error. Completion still works.
        var (project, uri, client) = await OpenAsync("App.csproj", text);
        Assert.Contains(await DiagnosticsAsync(project, d => d.Count > 0, ct), d => d.Code == "xml.syntax");

        var properties = await client.CompletionAsync(uri, At(text, "</OutputType>\n    <"), null, ct);
        Assert.Contains(properties.Items, i => i.Label == "Nullable");
        Assert.Contains(properties.Items, i => i.Label == "ImplicitUsings");

        var outputTypes = await client.CompletionAsync(uri, At(text, "<OutputType>"), null, ct);
        Assert.Contains(outputTypes.Items, i => i.Label == "Exe");
        var frameworks = await client.CompletionAsync(uri, At(text, "<TargetFramework>"), null, ct);
        Assert.Contains(frameworks.Items, i => i.Label == "net11.0"); // M30
        var assets = await client.CompletionAsync(uri, At(text, "PrivateAssets=\""), null, ct);
        Assert.Contains(assets.Items, i => i.Label == "all");
        var attributes = await client.CompletionAsync(uri, At(text, "<PackageReference ") with { Character = At(text, "<PackageReference ").Character }, null, ct);
        Assert.Contains(attributes.Items, i => i.Label == "Version");

        var hover = await client.HoverAsync(uri, At(text, "<TargetFram"), ct);
        Assert.Contains("Evaluated: `net10.0`", hover!.Contents.Value, StringComparison.Ordinal);
        Assert.Contains("set in App.csproj:3", hover.Contents.Value, StringComparison.Ordinal);

        // Once well-formed, a value a property doesn't take is a warning.
        var fixedText = text.Replace("    <\n", string.Empty, StringComparison.Ordinal);
        await _service.ChangeAsync(project, ++_version, [new TextDocumentContentChangeEvent(null, fixedText)]);
        var warning = Assert.Single(await DiagnosticsAsync(project, d => d.Count == 1 && d[0].Code == "msbuild.value", ct));
        Assert.Contains("Exe", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Xml_yaml_and_editorconfig_files_are_checked()
    {
        var ct = Timeout();
        var (xml, _, _) = await OpenAsync("data.xml", "<root><a></b></root>");
        Assert.Contains(await DiagnosticsAsync(xml, d => d.Count > 0, ct), d => d.Code == "xml.syntax");

        var (yaml, _, _) = await OpenAsync("ci.yml", "jobs:\n  build:\n    runs-on: [ubuntu\n");
        Assert.Contains(await DiagnosticsAsync(yaml, d => d.Count > 0, ct), d => d.Code == "yaml.syntax");

        var text = "root = true\n\n[*.cs]\nindent_style = tabs\nend_of_line = \n";
        var (config, uri, client) = await OpenAsync(".editorconfig", text);
        var problem = Assert.Single(await DiagnosticsAsync(config, d => d.Count > 0, ct));
        Assert.Equal("editorconfig.value", problem.Code);
        Assert.Equal(3, problem.Range.Start.Line);
        var values = await client.CompletionAsync(uri, At(text, "end_of_line = "), null, ct);
        Assert.Equal(["lf", "crlf", "cr"], values.Items.Select(i => i.Label));
        var keys = await client.CompletionAsync(uri, new Position(5, 0), null, ct);
        Assert.Contains(keys.Items, i => i.Label == "csharp_style_namespace_declarations");
        var hover = await client.HoverAsync(uri, new Position(0, 1), ct);
        Assert.Contains("Stop looking", hover!.Contents.Value, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Compose_files_are_checked_against_the_bundled_compose_schema()
    {
        var ct = Timeout();
        var text = "services:\n  web:\n    build:\n      context: .\n    environment:\n      ASPNETCORE_HTTP_PORTS: 8080\n    ports: 8080\n";
        var (compose, uri, client) = await OpenAsync("compose.yaml", text);
        var problem = Assert.Single(await DiagnosticsAsync(compose, d => d.Count > 0, ct));
        Assert.Equal("yaml.schema", problem.Code);
        Assert.Equal(6, problem.Range.Start.Line);
        var hover = await client.HoverAsync(uri, new Position(0, 2), ct);
        Assert.Contains("The services that will be used", hover!.Contents.Value, StringComparison.Ordinal);

        await _service.ChangeAsync(compose, ++_version, [new TextDocumentContentChangeEvent(null, text.Replace("ports: 8080", "ports:\n      - \"8080:8080\"", StringComparison.Ordinal))]);
        Assert.Empty(await DiagnosticsAsync(compose, d => d.Count == 0, ct));
    }
}
