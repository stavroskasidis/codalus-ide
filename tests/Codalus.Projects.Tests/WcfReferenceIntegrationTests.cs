using System.Text.Json;
using Codalus.Projects.Protocol;
using Codalus.Workbench.Output;
using Codalus.Workbench.Projects;
using Codalus.Workbench.Wcf;

namespace Codalus.Projects.Tests;

/// <summary>
/// WCF references through the real build host (M42, ADR 0069) on a copy of samples/Wcf, with a stand-in for
/// dotnet-svcutil (tests/Codalus.Svcutil.Fake, through <c>CODALUS_SVCUTIL</c>) that records its arguments and writes
/// recorded output: nothing is downloaded. The fixture's own reference is listed; one is added from the fixture's WSDL
/// file with every option, kept in Visual Studio's layout, updated, refused when it's wrong, and removed.
/// </summary>
public sealed class WcfReferenceIntegrationTests : IAsyncLifetime
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromMinutes(5);

    private readonly string _root = Path.Combine(Codalus.Platform.RealPath.Resolve(Path.GetTempPath()), "codalus-wcfref-" + Guid.NewGuid().ToString("N"));
    private readonly OutputService _output = new();
    private SolutionService _solutions = null!;

    internal static string FakeSvcutil => Path.Combine(BuildHostIntegrationTests.RepoRoot, "tests", "Codalus.Svcutil.Fake", "bin", BuildHostIntegrationTests.Configuration, "net10.0", "codalus-fake-svcutil.dll");

    private string Fixture => Path.Combine(_root, "Wcf");

    private string Client => Path.Combine(Fixture, "CalculatorClient", "CalculatorClient.csproj");

    private static CancellationToken Timeout() =>
        CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken, new CancellationTokenSource(s_timeout).Token).Token;

    public async ValueTask InitializeAsync()
    {
        Assert.True(File.Exists(FakeSvcutil), "The svcutil stand-in isn't built: " + FakeSvcutil);
        Environment.SetEnvironmentVariable("CODALUS_SVCUTIL", FakeSvcutil); // the build host inherits it
        Environment.SetEnvironmentVariable("CODALUS_TRASH_DIR", Path.Combine(_root, ".trash"));
        WebToolsIntegrationTests.Copy(Path.Combine(BuildHostIntegrationTests.RepoRoot, "samples", "Wcf"), Fixture);
        File.WriteAllText(Path.Combine(_root, "Directory.Build.props"), "<Project />");
        Codalus.Tests.FixtureSdk.Pin(_root);
        File.WriteAllText(Path.Combine(_root, "Directory.Packages.props"), "<Project><PropertyGroup><ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally></PropertyGroup></Project>");

        // The client without its packages, so the stand-in adds them as dotnet-svcutil does.
        var text = File.ReadAllText(Client);
        File.WriteAllText(Client, System.Text.RegularExpressions.Regex.Replace(text, @"\s*<ItemGroup>\s*<PackageReference Include=""System\.ServiceModel\.Http""[^<]*/>\s*<PackageReference[^<]*/>\s*</ItemGroup>", string.Empty));
        Assert.DoesNotContain("PackageReference", File.ReadAllText(Client), StringComparison.Ordinal);

        _solutions = new SolutionService(() => BuildHostIntegrationTests.HostAssemblyPath);
        _solutions.Log += (_, line) => Console.Error.WriteLine("[host] " + line);
        var solution = await _solutions.OpenAsync(Path.Combine(Fixture, "Wcf.slnx"), trusted: true, restore: false, Timeout());
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
    public async Task References_are_added_in_visual_studio_layout_updated_and_removed()
    {
        var service = new WcfReferenceService(_solutions, _output);
        var client = Path.GetDirectoryName(Client)!;

        var status = await service.StatusAsync(Client, Timeout());
        Assert.True(status.ToolAvailable, status.Message);
        Assert.Equal(("8.0.0", "CODALUS_SVCUTIL"), (status.ToolVersion, status.ToolSource));

        // The fixture's reference, as Visual Studio keeps it.
        var existing = Assert.Single(await service.ListAsync(Client, Timeout()));
        Assert.Equal(("CalculatorService", "CalculatorClient.Calculator", "System.Array", "System.Collections.Generic.Dictionary`2"), (existing.Name, existing.Namespace, existing.CollectionType, existing.DictionaryCollectionType));
        Assert.Equal(["http://localhost:5280/CalculatorService.svc?wsdl"], existing.Inputs);
        Assert.Equal((false, true, false, "net10.0"), (existing.Sync, existing.ReuseTypes, existing.Internal, existing.TargetFramework));
        Assert.EndsWith(Path.Combine("CalculatorService", "Reference.cs"), existing.Code, StringComparison.Ordinal);

        // A new one from the WSDL file, with every option: the arguments svcutil gets, and Visual Studio's layout after.
        var wsdl = Path.Combine(Fixture, "contracts", "CalculatorService.wsdl");
        var added = await service.AddAsync(
            new WcfReferenceParams(Client, "Calculator2", wsdl, "Client.Calc", "System.Collections.Generic.List`1", "System.Collections.Generic.Dictionary`2", Sync: true, ReuseTypes: false, Internal: true),
            Timeout());
        Assert.Equal(
            [wsdl, "--projectFile", Client, "--outputDir", "Connected Services/Calculator2", "--outputFile", "Reference.cs", "--namespace", "*,Client.Calc",
             "--collectionType", "System.Collections.Generic.List`1", "--collectionType", "System.Collections.Generic.Dictionary`2", "--sync", "--noTypeReuse", "--internal", "--noLogo"],
            Arguments(added.Output));
        var folder = Path.Combine(client, "Connected Services", "Calculator2");
        Assert.Contains(Path.Combine(folder, "Reference.cs"), added.Created);
        Assert.Contains(Path.Combine(folder, "ConnectedService.json"), added.Created);
        Assert.Contains(Client, added.Modified); // the System.ServiceModel packages
        Assert.False(File.Exists(Path.Combine(folder, "dotnet-svcutil.params.json")));
        Assert.Equal(
            $$"""
            {
              "ExtendedData": {
                "inputs": [
                  {{JsonSerializer.Serialize(wsdl)}}
                ],
                "collectionTypes": [
                  "System.Collections.Generic.List`1",
                  "System.Collections.Generic.Dictionary`2"
                ],
                "internalTypeAccess": true,
                "namespaceMappings": [
                  "*, Client.Calc"
                ],
                "outputFile": "Reference.cs",
                "sync": true,
                "targetFramework": "net10.0",
                "typeReuseMode": "None"
              },
              "ProviderId": "Microsoft.VisualStudio.ConnectedService.Wcf",
              "Version": "15.0.40203.910"
            }

            """.ReplaceLineEndings("\n"),
            File.ReadAllText(Path.Combine(folder, "ConnectedService.json")));
        Assert.Contains("namespace Client.Calc", File.ReadAllText(Path.Combine(folder, "Reference.cs")), StringComparison.Ordinal);
        Assert.Equal(("Calculator2", "Client.Calc", true, false, true), (added.Reference.Name, added.Reference.Namespace, added.Reference.Sync, added.Reference.ReuseTypes, added.Reference.Internal));
        Assert.Contains("> dotnet-svcutil (CODALUS_SVCUTIL)", service.Channel.GetText(), StringComparison.Ordinal);
        await WaitUntilAsync(() => _solutions.Projects.TryGetValue(Client, out var project) && project.References.Any(r => r.Name == "System.ServiceModel.Http"));

        // Update: the options written into ConnectedService.json, then svcutil --update on the folder.
        var updated = await service.UpdateAsync(new WcfReferenceParams(Client, "Calculator2", null, "Client.Calculator", "System.Array", null, Sync: false, ReuseTypes: true), Timeout());
        Assert.Equal(["--update", "Connected Services/Calculator2", "--projectFile", Client, "--noLogo"], Arguments(updated.Output));
        var options = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "ConnectedService.json"))).RootElement.GetProperty("ExtendedData");
        Assert.Equal("*, Client.Calculator", options.GetProperty("namespaceMappings")[0].GetString());
        Assert.Equal("All", options.GetProperty("typeReuseMode").GetString());
        Assert.False(options.TryGetProperty("sync", out _));
        Assert.Equal(wsdl, options.GetProperty("inputs")[0].GetString());
        Assert.Contains("namespace Client.Calculator", File.ReadAllText(Path.Combine(folder, "Reference.cs")), StringComparison.Ordinal);
        Assert.Equal(["Calculator2", "CalculatorService"], (await service.ListAsync(Client, Timeout())).Select(r => r.Name));

        // Refused: a name taken or not a folder name, a missing file, and svcutil's own failure in its words.
        var taken = await Assert.ThrowsAnyAsync<Exception>(() => service.AddAsync(new WcfReferenceParams(Client, "Calculator2", wsdl), Timeout()));
        Assert.Contains("already exists", taken.Message, StringComparison.Ordinal);
        var invalid = await Assert.ThrowsAnyAsync<Exception>(() => service.AddAsync(new WcfReferenceParams(Client, "../Out", wsdl), Timeout()));
        Assert.Contains("isn't a valid reference name", invalid.Message, StringComparison.Ordinal);
        var missing = await Assert.ThrowsAnyAsync<Exception>(() => service.AddAsync(new WcfReferenceParams(Client, "Missing", Path.Combine(Fixture, "nothing.wsdl")), Timeout()));
        Assert.Contains("doesn't exist", missing.Message, StringComparison.Ordinal);
        var failed = await Assert.ThrowsAnyAsync<Exception>(() => service.AddAsync(new WcfReferenceParams(Client, "Broken", "http://localhost:1/fail?wsdl"), Timeout()));
        Assert.Contains("Error: Cannot obtain Metadata from http://localhost:1/fail?wsdl", failed.Message, StringComparison.Ordinal);

        // Remove: the folder goes to the trash; the packages stay.
        var left = await service.RemoveAsync(Client, "Calculator2", Timeout());
        Assert.Equal(["CalculatorService"], left.Select(r => r.Name));
        Assert.False(Directory.Exists(folder));
        Assert.Contains("System.ServiceModel.Http", File.ReadAllText(Client), StringComparison.Ordinal);
    }

    /// <summary>The arguments the stand-in printed.</summary>
    private static string[] Arguments(string output)
    {
        var line = output.Split('\n').Single(l => l.StartsWith("Arguments: ", StringComparison.Ordinal));
        return JsonSerializer.Deserialize<string[]>(line["Arguments: ".Length..].Trim())!;
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
}
