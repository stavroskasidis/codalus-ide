using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Codalus.Shell.ViewModels;
using Codalus.Shell.Views;
using Codalus.Tests;
using Codalus.Workbench.Wcf;

namespace Codalus.Shell.Tests;

/// <summary>
/// M42 in the shell on samples/Wcf: the Service References document's WCF page (the fixture's reference listed, one
/// added from the fixture's WSDL file through the dotnet-svcutil stand-in, then updated), and the WCF Test Client
/// document on the same WSDL with a loopback mock service answering with recorded responses.
/// </summary>
public sealed class WcfShellTests
{
    private static string FakeSvcutil => Path.Combine(ShellFixture.RepoRoot, "tests", "Codalus.Svcutil.Fake", "bin", ShellFixture.Configuration, Codalus.Tests.RepoBuild.TargetFramework, "codalus-fake-svcutil.dll");

    private static string Recorded(string name) => Path.Combine(ShellFixture.RepoRoot, "tests", "Codalus.Workbench.Tests", "TestData", "Wcf", name);

    [AvaloniaFact]
    public async Task Add_service_reference_and_the_test_client()
    {
        Assert.True(File.Exists(FakeSvcutil), "The svcutil stand-in isn't built: " + FakeSvcutil);
        Environment.SetEnvironmentVariable("CODALUS_SVCUTIL", FakeSvcutil); // the build host inherits it
        await using var f = ShellFixture.Create();
        var folder = f.CopySample("Wcf");
        f.Shell.Trust.Trust(Path.GetDirectoryName(folder)!);
        await f.Shell.OpenSolutionAsync(Path.Combine(folder, "Wcf.slnx"));
        await ShellFixture.PumpUntil(() => f.Shell.Solutions.Projects.Count == 2 && f.Shell.Solutions.Progress is { Completed: true }, seconds: 180);
        Assert.NotNull(f.Shell.Workbench.Commands.Find("wcf.addServiceReference"));
        Assert.NotNull(f.Shell.Workbench.Commands.Find("wcf.testClient"));

        // Add Service Reference opens the Service References document on its WCF page: the fixture's reference, svcutil's state.
        var client = Path.Combine(folder, "CalculatorClient", "CalculatorClient.csproj");
        var references = f.Shell.OpenServiceReferences(client, ServiceReferencesViewModel.WcfKind);
        Assert.NotNull(references);
        Assert.True(references.IsWcf);
        await ShellFixture.PumpUntil(() => !references.IsBusy && references.References.Count > 0 && references.WcfStatus is not null, seconds: 120);
        var existing = Assert.Single(references.References);
        Assert.Equal(("WCF", "CalculatorService"), (existing.Kind, existing.Name));
        Assert.True(references.WcfStatus!.ToolAvailable, references.WcfToolMessage);
        Assert.Equal("dotnet-svcutil 8.0.0 (CODALUS_SVCUTIL)", references.WcfToolMessage);
        Assert.Equal("ServiceReference1", references.WcfName);
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<ServiceReferencesView>().Any());

        // A missing address is caught first; then a reference from the fixture's WSDL file.
        await references.RunAsync("add");
        Assert.Contains("metadata address", references.Error, StringComparison.Ordinal);
        references.Source = Path.Combine(folder, "contracts", "CalculatorService.wsdl");
        references.Namespace = "CalculatorClient.Second";
        references.CollectionType = "System.Collections.Generic.List`1";
        references.Sync = true;
        NavigationDepthTests.Screenshot(f, "wcf-service-reference");
        await references.RunAsync("add");
        Assert.True(references.Error is null, references.Error + "\n" + f.Shell.WcfReferences.Channel.GetText());
        Assert.StartsWith("Added ServiceReference1", references.Result, StringComparison.Ordinal);
        var added = Path.Combine(folder, "CalculatorClient", "Connected Services", "ServiceReference1");
        Assert.True(File.Exists(Path.Combine(added, "ConnectedService.json")));
        Assert.Contains("namespace CalculatorClient.Second", File.ReadAllText(Path.Combine(added, "Reference.cs")), StringComparison.Ordinal);
        Assert.Equal(["CalculatorService", "ServiceReference1"], references.References.Select(r => r.Name));
        Assert.Equal("ServiceReference2", references.WcfName);

        // Its options changed: generated again.
        references.Selected = references.References.Single(r => r.Name == "ServiceReference1");
        Assert.True(references.SelectedIsWcf);
        Assert.Equal(("CalculatorClient.Second", "System.Collections.Generic.List`1", true), (references.EditNamespace, references.EditCollectionType, references.EditSync));
        references.EditNamespace = "CalculatorClient.Third";
        await references.RunAsync("save");
        Assert.True(references.Error is null, references.Error);
        Assert.Contains("namespace CalculatorClient.Third", File.ReadAllText(Path.Combine(added, "Reference.cs")), StringComparison.Ordinal);
        Assert.Equal("CalculatorClient.Third", references.Selected?.Wcf?.Namespace);

        // The test client on the WSDL, the service a loopback mock with recorded responses.
        MockHttpServer? server = null;
        server = new MockHttpServer(request =>
        {
            if (request.Method == "GET")
            {
                return MockHttpResponse.Text(File.ReadAllText(Path.Combine(folder, "contracts", "CalculatorService.wsdl")).Replace("http://localhost:5280/", server!.BaseUrl + "/", StringComparison.Ordinal), contentType: "text/xml; charset=utf-8");
            }

            var name = request.Body.Contains("<Divide", StringComparison.Ordinal) ? "response-divide11.xml" : request.Body.Contains("<Greet", StringComparison.Ordinal) ? "response-greet11.xml" : "response-add11.xml";
            return MockHttpResponse.Text(File.ReadAllText(Recorded(name)), name.Contains("divide", StringComparison.Ordinal) ? 500 : 200, "text/xml; charset=utf-8");
        });
        await using (server)
        {
            var testClient = f.Shell.OpenWcfTestClient(server.BaseUrl + "/CalculatorService.svc");
            await ShellFixture.PumpUntil(() => !testClient.IsBusy && testClient.Endpoints.Count == 2, seconds: 60);
            Assert.True(testClient.Error is null, testClient.Error);
            Assert.Equal(["Add", "Divide", "Greet", "FindPeople"], testClient.Operations.Select(o => o.Name));
            await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<WcfTestClientView>().Any());

            // The form of Greet: a data contract with an enum and a list; the XML follows the form.
            testClient.Select("Greet");
            // M34h: a list (ArrayOfstring's repeated string) is one row, Tags string[], with its items under it as [0], [1]…
            Assert.Equal(["person", "Name", "Age", "Mood", "Tags"], testClient.Rows.Select(r => r.Name));
            testClient.Row("person.Name")!.Value = "Ada";
            testClient.Row("person.Age")!.Value = "36";
            var mood = testClient.Row("person.Mood")!;
            Assert.True(mood.IsChoice);
            Assert.Equal(["Calm", "Happy", "Grumpy"], mood.Choices);
            mood.Value = "Happy";
            var strings = testClient.Row("person.Tags.string")!;
            Assert.True(strings.CanAdd);
            Assert.Same(testClient.Row("person.Tags"), strings);
            strings.AddCommand.Execute(null);
            Assert.Equal(6, testClient.Rows.Count);
            Assert.Equal("[0]", testClient.Rows[5].Label);
            testClient.Rows[5].Value = "math";
            Assert.True(testClient.Rows[5].CanRemove);
            testClient.RequestTab = 1;
            Assert.Contains("<q1:string>math</q1:string>", testClient.RequestXml, StringComparison.Ordinal);
            testClient.RequestTab = 0;

            await testClient.SendAsync();
            Assert.True(testClient.Error is null, testClient.Error);
            Assert.Equal("GreetResult = Hello Ada, 36 and happy (math)!", Assert.Single(Assert.Single(testClient.ResponseTree).Children).Label);
            Assert.Contains("<GreetResponse", testClient.ResponseText, StringComparison.Ordinal);
            Assert.Contains("<Greet xmlns=\"http://samples.codalus.dev/calculator\"", server.Requests.Last().Body, StringComparison.Ordinal);
            NavigationDepthTests.Screenshot(f, "wcf-test-client");

            // A value that doesn't fit its type is caught before sending; a fault shows its reason.
            testClient.Select("Divide");
            testClient.Row("a")!.Value = "one";
            await testClient.SendAsync();
            Assert.Equal("Divide.a: 'one' isn't a valid double.", testClient.Error);
            testClient.Row("a")!.Value = "1";
            testClient.Row("b")!.Value = "0";
            await testClient.SendAsync();
            Assert.True(testClient.IsFault);
            Assert.StartsWith("Fault s:Client: Division by zero", testClient.ResponseSummary, StringComparison.Ordinal);

            // Edited XML is sent as it is.
            testClient.Select("Add");
            testClient.RequestTab = 1;
            testClient.RequestXml = testClient.RequestXml.Replace("<Add xmlns", "<Add data-edited=\"1\" xmlns", StringComparison.Ordinal);
            await testClient.SendAsync();
            Assert.Contains("data-edited=\"1\"", server.Requests.Last().Body, StringComparison.Ordinal);
        }
    }
}
