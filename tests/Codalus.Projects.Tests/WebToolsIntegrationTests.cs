using Codalus.Projects.Protocol;
using Codalus.Tests;
using Codalus.Workbench.Build;
using Codalus.Workbench.Output;
using Codalus.Workbench.Projects;
using Codalus.Workbench.Web;

namespace Codalus.Projects.Tests;

/// <summary>
/// Scaffolding and service references through the real build host (M15c, ADR 0041) on a copy of samples/Scaffolding:
/// the fixture's local dotnet-aspnet-codegenerator (restored here), the SDK's item templates, and OpenAPI and gRPC
/// references from the fixture's own documents (no network: package versions are pinned to ones the tests restore).
/// </summary>
public sealed class WebToolsIntegrationTests : IAsyncLifetime
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromMinutes(6);

    /// <summary>The gRPC client packages, at versions the other fixtures restore too.</summary>
    internal static readonly ServiceReferencePackage[] GrpcVersions =
    [
        new("Grpc.Net.ClientFactory", "2.71.0"),
        new("Google.Protobuf", "3.30.2"),
        new("Grpc.Tools", "2.72.0"),
    ];

    private static readonly ServiceReferencePackage[] s_openApiVersions =
    [
        new("Microsoft.Extensions.ApiDescription.Client", "10.0.12"),
        new("NSwag.ApiDescription.Client", "14.7.1"),
        new("Newtonsoft.Json", "13.0.4"),
    ];

    private readonly string _root = Path.Combine(Codalus.Platform.RealPath.Resolve(Path.GetTempPath()), "codalus-web-" + Guid.NewGuid().ToString("N"));
    private readonly OutputService _output = new();
    private SolutionService _solutions = null!;

    private string Fixture => Path.Combine(_root, "Scaffolding");

    private string Shop => Path.Combine(Fixture, "Shop", "Shop.csproj");

    private string Client => Path.Combine(Fixture, "Client", "Client.csproj");

    private static CancellationToken Timeout() =>
        CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken, new CancellationTokenSource(s_timeout).Token).Token;

    public async ValueTask InitializeAsync()
    {
        Copy(Path.Combine(BuildHostIntegrationTests.RepoRoot, "samples", "Scaffolding"), Fixture);
        File.WriteAllText(Path.Combine(_root, "Directory.Build.props"), "<Project />");
        Codalus.Tests.FixtureSdk.Pin(_root);
        File.WriteAllText(Path.Combine(_root, "Directory.Packages.props"), "<Project><PropertyGroup><ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally></PropertyGroup></Project>");
        _solutions = new SolutionService(() => BuildHostIntegrationTests.HostAssemblyPath);
        _solutions.Log += (_, line) => Console.Error.WriteLine("[host] " + line);
        var solution = await _solutions.OpenAsync(Path.Combine(Fixture, "Scaffolding.slnx"), trusted: true, restore: false, Timeout());
        await WaitUntilAsync(() => _solutions.Progress is { Completed: true } && _solutions.Projects.Count == solution.Projects.Length);
    }

    public async ValueTask DisposeAsync()
    {
        await _solutions.DisposeAsync();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public async Task Scaffolders_run_the_code_generator_item_templates_and_the_built_in_endpoints()
    {
        var scaffolding = new ScaffoldingService(_solutions, new ProjectManagementService(_solutions), _output);
        var restored = await _solutions.RequestAsync(PackageMethods.ToolCommand, new ToolCommandParams(ToolActions.Restore), PackageJsonContext.Default.ToolCommandParams, PackageJsonContext.Default.ToolCommandResult, Timeout());
        Assert.True(restored.Succeeded, restored.Output);
        await BuildAsync(Shop); // restored and built through the build host, as the IDE does before scaffolding

        var status = await scaffolding.StatusAsync(Shop, Timeout());
        Assert.True(status.ToolAvailable, status.Message);
        Assert.Equal("10.0.2", status.ToolVersion);
        Assert.Equal("local", status.ToolSource);
        Assert.Equal("10.0.2", status.DesignPackageVersion);
        Assert.Equal("10.0", status.DotnetVersion);
        Assert.Contains("Microsoft.EntityFrameworkCore.Sqlite", status.EfProviders);
        Assert.Equal("10.0.12", status.EfToolsVersion);
        Assert.Null(status.Message);

        // An API controller over the existing context: the name defaults to the plural, the provider to the project's.
        var shop = Path.GetDirectoryName(Shop)!;
        var api = await scaffolding.RunAsync(Shop, Scaffolders.Find("api.ef")!, new Dictionary<string, string?> { ["model"] = "Product", ["dbContext"] = "ShopContext" }, Timeout());
        var controller = Path.Combine(shop, "Controllers", "ProductsController.cs");
        Assert.Contains(controller, api.Created);
        var text = File.ReadAllText(controller);
        Assert.Contains("[ApiController]", text, StringComparison.Ordinal);
        Assert.Contains("ShopContext", text, StringComparison.Ordinal);
        Assert.Contains("> dotnet aspnet-codegenerator controller", scaffolding.Channel.GetText(), StringComparison.Ordinal); // the command line and its output

        // Minimal API endpoints: a new file, and Program.cs changed to map them.
        var minimal = await scaffolding.RunAsync(Shop, Scaffolders.Find("minimal.ef")!, new Dictionary<string, string?> { ["model"] = "Product", ["dbContext"] = "ShopContext", ["name"] = "ProductEndpoints" }, Timeout());
        Assert.Contains(Path.Combine(shop, "Endpoints", "ProductEndpoints.cs"), minimal.Created);
        Assert.Contains(Path.Combine(shop, "Program.cs"), minimal.Modified);
        Assert.Contains("MapProductEndpoints", File.ReadAllText(Path.Combine(shop, "Program.cs")), StringComparison.Ordinal);

        // A generator failure says why.
        var failure = await Assert.ThrowsAnyAsync<Exception>(() => scaffolding.RunAsync(Shop, Scaffolders.Find("api.ef")!, new Dictionary<string, string?> { ["model"] = "Missing", ["dbContext"] = "ShopContext", ["name"] = "MissingController" }, Timeout()));
        Assert.Contains("code generator failed", failure.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(shop, "Controllers", "MissingController.cs")));

        // No tool needed: an SDK item template with the folder's namespace, and the IDE's own endpoint group.
        var empty = await scaffolding.RunAsync(Shop, Scaffolders.Find("api.actions")!, new Dictionary<string, string?> { ["name"] = "OrdersController" }, Timeout());
        var orders = Assert.Single(empty.Created);
        Assert.Equal(Path.Combine(shop, "Controllers", "OrdersController.cs"), orders);
        Assert.Contains("namespace Shop.Controllers", File.ReadAllText(orders), StringComparison.Ordinal);
        Assert.Contains("[HttpDelete", File.ReadAllText(orders), StringComparison.Ordinal);
        var group = await scaffolding.RunAsync(Shop, Scaffolders.Find("minimal.empty")!, new Dictionary<string, string?> { ["name"] = "TodoEndpoints" }, Timeout());
        var todo = File.ReadAllText(Assert.Single(group.Created));
        Assert.Contains("namespace Shop.Endpoints;", todo, StringComparison.Ordinal);
        Assert.Contains("MapGroup(\"/api/todos\")", todo, StringComparison.Ordinal);
        Assert.Contains("MapTodoEndpoints", todo, StringComparison.Ordinal);

        // Everything that was generated compiles.
        await BuildAsync(Shop);
    }

    [Fact]
    public async Task Service_references_are_added_listed_changed_refreshed_and_removed()
    {
        var references = new ServiceReferenceService(_solutions, _output);
        var client = Path.GetDirectoryName(Client)!;

        // OpenAPI from a file outside the project: copied into OpenAPIs, the item and three packages added.
        var (file, edit) = await references.AddAsync(
            new ServiceReferenceRequest(Client, ServiceReferenceKinds.OpenApi, Path.Combine(Fixture, "contracts", "weather.openapi.json"), Namespace: "Client.Weather", ClassName: "WeatherClient", Restore: false, Versions: s_openApiVersions),
            Timeout());
        Assert.Equal(Path.Combine(client, "OpenAPIs", "weather.openapi.json"), file);
        Assert.True(File.Exists(file));
        Assert.Contains(Client, edit.ChangedFiles);
        Assert.Equal(["Microsoft.Extensions.ApiDescription.Client 10.0.12", "NSwag.ApiDescription.Client 14.7.1", "Newtonsoft.Json 13.0.4"], edit.AddedPackages);
        var project = File.ReadAllText(Client);
        Assert.Contains("<OpenApiReference Include=\"OpenAPIs\\weather.openapi.json\" CodeGenerator=\"NSwagCSharp\" Namespace=\"Client.Weather\" ClassName=\"WeatherClient\" />", project, StringComparison.Ordinal);
        Assert.Contains("<PackageReference Include=\"NSwag.ApiDescription.Client\" Version=\"14.7.1\">", project, StringComparison.Ordinal);
        Assert.Contains("<PrivateAssets>all</PrivateAssets>", project, StringComparison.Ordinal);

        // gRPC: the client's packages, Grpc.Tools private.
        var (proto, grpc) = await references.AddAsync(
            new ServiceReferenceRequest(Client, ServiceReferenceKinds.Grpc, Path.Combine(Fixture, "contracts", "greet.proto"), GrpcServices: GrpcServiceKinds.Client, Restore: false, Versions: GrpcVersions),
            Timeout());
        Assert.Equal(Path.Combine(client, "Protos", "greet.proto"), proto);
        Assert.Equal(3, grpc.AddedPackages.Length);
        Assert.Contains("<Protobuf Include=\"Protos\\greet.proto\" GrpcServices=\"Client\" />", File.ReadAllText(Client), StringComparison.Ordinal);

        var listed = await references.ListAsync(Client, Timeout());
        Assert.Equal(2, listed.References.Length);
        var openApi = Assert.Single(listed.References, r => r.Kind == ServiceReferenceKinds.OpenApi);
        Assert.Equal((file, "Client.Weather", "WeatherClient", "NSwagCSharp", (string?)null), (openApi.Path, openApi.Namespace, openApi.ClassName, openApi.CodeGenerator, openApi.SourceUrl));
        Assert.Equal(GrpcServiceKinds.Client, Assert.Single(listed.References, r => r.Kind == ServiceReferenceKinds.Grpc).GrpcServices);
        Assert.Equal(6, listed.Packages.Length);

        // Changing the metadata; a file reference has nothing to refresh from.
        await references.UpdateAsync(new ServiceReferenceParams(Client, ServiceReferenceKinds.OpenApi, file, Namespace: "Client.Weather", ClassName: "ForecastClient", Options: "/UseBaseUrl:false"), Timeout());
        openApi = Assert.Single((await references.ListAsync(Client, Timeout())).References, r => r.Kind == ServiceReferenceKinds.OpenApi);
        Assert.Equal(("ForecastClient", "/UseBaseUrl:false"), (openApi.ClassName, openApi.Options));
        await Assert.ThrowsAsync<InvalidOperationException>(() => references.RefreshAsync(Client, openApi, Timeout()));

        // From a URL: downloaded, the URL kept, and downloaded again by Refresh.
        var document = File.ReadAllText(Path.Combine(Fixture, "contracts", "weather.openapi.json"));
        await using var server = new MockHttpServer(request => request.Path == "/swagger/v1/swagger.json" ? MockHttpResponse.Json(document) : MockHttpResponse.NotFound());
        var url = server.BaseUrl + "/swagger/v1/swagger.json";
        var (downloaded, _) = await references.AddAsync(new ServiceReferenceRequest(Client, ServiceReferenceKinds.OpenApi, url, Restore: false, Versions: s_openApiVersions), Timeout());
        Assert.Equal(Path.Combine(client, "OpenAPIs", "swagger.json"), downloaded);
        Assert.Equal(document, File.ReadAllText(downloaded));
        var fromUrl = Assert.Single((await references.ListAsync(Client, Timeout())).References, r => r.SourceUrl is not null);
        Assert.Equal(url, fromUrl.SourceUrl);
        document = document.Replace("\"Weather\"", "\"Weather 2\"", StringComparison.Ordinal);
        await references.RefreshAsync(Client, fromUrl, Timeout());
        Assert.Contains("Weather 2", File.ReadAllText(downloaded), StringComparison.Ordinal);
        await Assert.ThrowsAnyAsync<Exception>(() => references.AddAsync(new ServiceReferenceRequest(Client, ServiceReferenceKinds.OpenApi, server.BaseUrl + "/missing.json", Restore: false, Versions: s_openApiVersions), Timeout()));

        // Removing keeps the document unless asked, and the packages.
        await references.RemoveAsync(Client, fromUrl, deleteFile: false, Timeout());
        await references.RemoveAsync(Client, openApi, deleteFile: false, Timeout());
        listed = await references.ListAsync(Client, Timeout());
        Assert.Equal(ServiceReferenceKinds.Grpc, Assert.Single(listed.References).Kind);
        Assert.True(File.Exists(file));
        Assert.DoesNotContain("OpenApiReference", File.ReadAllText(Client), StringComparison.Ordinal);
        Assert.Contains("NSwag.ApiDescription.Client", File.ReadAllText(Client), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_grpc_reference_generates_its_client_at_build_time()
    {
        var references = new ServiceReferenceService(_solutions, _output);
        await references.AddAsync(
            new ServiceReferenceRequest(Client, ServiceReferenceKinds.Grpc, Path.Combine(Fixture, "contracts", "greet.proto"), GrpcServices: GrpcServiceKinds.Client, Restore: false, Versions: GrpcVersions),
            Timeout());
        var client = Path.GetDirectoryName(Client)!;
        File.WriteAllText(Path.Combine(client, "Program.cs"), "var client = new Contracts.Greet.Greeter.GreeterClient(Grpc.Net.Client.GrpcChannel.ForAddress(\"http://localhost:5000\"));\nConsole.WriteLine(client.GetType().Name);\n");
        await BuildAsync(Client);
        Assert.NotEmpty(Directory.GetFiles(Path.Combine(client, "obj"), "GreetGrpc.cs", SearchOption.AllDirectories));
    }

    /// <summary>
    /// Builds through the build host, which takes turns with its design-time builds: a command-line build next to them
    /// ran Grpc.Tools' protoc twice in one obj folder (CI, Windows).
    /// </summary>
    private async Task BuildAsync(string project)
    {
        var builds = new BuildService(_solutions, _output);
        var result = await builds.BuildAsync(BuildTargets.Build, project, Timeout());
        Assert.True(result.Succeeded, builds.Channel.GetText());
    }
    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + s_timeout;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out.");
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
    }

    private static void Copy(string source, string target)
    {
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, directory);
            if (relative.Split(Path.DirectorySeparatorChar).Any(p => p is "bin" or "obj"))
            {
                continue;
            }

            Directory.CreateDirectory(Path.Combine(target, relative));
        }

        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            if (relative.Split(Path.DirectorySeparatorChar).Any(p => p is "bin" or "obj"))
            {
                continue;
            }

            File.Copy(file, Path.Combine(target, relative), overwrite: true);
        }
    }
}
