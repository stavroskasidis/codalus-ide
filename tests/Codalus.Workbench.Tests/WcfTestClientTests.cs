using System.Xml.Linq;
using Codalus.Tests;
using Codalus.Workbench.Wcf;

namespace Codalus.Workbench.Tests;

/// <summary>
/// The WCF test client's engine (M42, ADR 0069) against metadata and responses recorded from samples/Wcf's CoreWCF
/// service (TestData/Wcf): WSDL with its imports, request forms from the schemas, envelopes for SOAP 1.1 and SOAP 1.2
/// with WS-Addressing, responses and faults, the HTTP requests, and finding a running service's metadata.
/// </summary>
public sealed class WcfTestClientTests
{
    private const string Recorded = "http://localhost:5280/CalculatorService.svc";
    private const string Contract = "http://samples.codalus.dev/calculator";

    private static string TestData(string name) => Path.Combine(ContainerToolsTests.RepoRoot, "tests", "Codalus.Workbench.Tests", "TestData", "Wcf", name);

    /// <summary>The recorded documents by their address: ?wsdl, ?wsdl=wsdl0, ?xsd=xsd0…</summary>
    private static Task<string> FetchRecorded(Uri uri, CancellationToken cancellationToken)
    {
        Assert.Equal(Recorded, uri.GetLeftPart(UriPartial.Path));
        var name = uri.Query.TrimStart('?') switch
        {
            "wsdl" => "wsdl.xml",
            var query when query.StartsWith("wsdl=", StringComparison.Ordinal) => query[5..] + ".xml",
            var query when query.StartsWith("xsd=", StringComparison.Ordinal) => query[4..] + ".xml",
            var query => throw new InvalidOperationException("Not recorded: " + query),
        };
        return File.ReadAllTextAsync(TestData(name), cancellationToken);
    }

    private static Task<WsdlDocument> LoadRecordedAsync() => WsdlReader.LoadAsync(Recorded, FetchRecorded, TestContext.Current.CancellationToken);

    [Fact]
    public async Task Metadata_is_read_across_wsdl_and_schema_imports()
    {
        Assert.Equal(Recorded + "?wsdl", WsdlReader.MetadataUri(Recorded).ToString());
        Assert.Equal(Recorded + "?singleWsdl", WsdlReader.MetadataUri(Recorded + "?singleWsdl").ToString());

        var document = await LoadRecordedAsync();
        Assert.Equal(5, document.DocumentCount); // ?wsdl, ?wsdl=wsdl0 and three schemas
        Assert.Empty(document.Warnings);
        Assert.Equal(2, document.Endpoints.Count);

        var basic = document.Endpoints[0];
        Assert.Equal(("CalculatorService", "BasicHttpBinding_ICalculatorService", Recorded, SoapVersion.Soap11, false, "ICalculatorService"), (basic.Service, basic.Name, basic.Address, basic.Soap, basic.Addressing, basic.Contract));
        Assert.Null(basic.Problem);
        Assert.Equal(["Add", "Divide", "Greet", "FindPeople"], basic.Operations.Select(o => o.Name));
        var divide = basic.Operations[1];
        Assert.Equal(Contract + "/ICalculatorService/Divide", divide.Action);
        Assert.Equal(Contract + "/ICalculatorService/DivideResponse", divide.ReplyAction);
        Assert.Equal(["CalculationFaultFault"], divide.Faults);
        Assert.Equal(XName.Get("Divide", Contract), Assert.Single(divide.Input.Parts).Element);
        Assert.False(divide.Rpc);

        var ws = document.Endpoints[1];
        Assert.Equal((SoapVersion.Soap12, true, "http://localhost:5280/CalculatorServiceWs.svc"), (ws.Soap, ws.Addressing, ws.Address));
        Assert.Equal("SOAP 1.2, WS-Addressing", ws.BindingLabel);

        // The schemas: the data contract, its enum, and WCF's arrays.
        Assert.True(document.Schemas.ComplexTypes.ContainsKey(XName.Get("Person", Contract)));
        Assert.Equal(["Calm", "Happy", "Grumpy"], document.Schemas.SimpleTypes[XName.Get("Mood", Contract)].Enumeration);
        Assert.True(document.Schemas.ComplexTypes.ContainsKey(XName.Get("ArrayOfstring", "http://schemas.microsoft.com/2003/10/Serialization/Arrays")));
    }

    [Fact]
    public async Task A_single_wsdl_file_reads_the_same()
    {
        var file = Path.Combine(ContainerToolsTests.RepoRoot, "samples", "Wcf", "contracts", "CalculatorService.wsdl");
        using var client = new WcfTestClient(new HttpClient());
        var document = await client.LoadAsync(file, TestContext.Current.CancellationToken);
        Assert.Equal(1, document.DocumentCount);
        Assert.Equal(["BasicHttpBinding_ICalculatorService", "WSHttpBinding_ICalculatorService"], document.Endpoints.Select(e => e.Name));
        Assert.Equal(4, document.Endpoints[1].Operations.Count);

        // Not a WSDL document: says so.
        var notWsdl = Path.Combine(Path.GetTempPath(), "codalus-not-wsdl-" + Guid.NewGuid().ToString("N") + ".xml");
        await File.WriteAllTextAsync(notWsdl, "<html><body>Service</body></html>", TestContext.Current.CancellationToken);
        try
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.LoadAsync(notWsdl, TestContext.Current.CancellationToken));
            Assert.Contains("isn't a WSDL document", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(notWsdl);
        }
    }

    [Fact]
    public async Task Request_forms_follow_the_schema_and_build_envelopes()
    {
        var document = await LoadRecordedAsync();
        var basic = document.Endpoints[0];

        // Add: two optional doubles under the wrapper element.
        var add = new SoapRequest(document, basic, basic.Operations[0]);
        Assert.Equal([("a", SoapFieldKind.Simple, "double"), ("b", SoapFieldKind.Simple, "double")], add.Parameters.Select(p => (p.Name.LocalName, p.Kind, p.TypeLabel)));
        add.Parameters[0].Value = "2";
        add.Parameters[1].Value = "3.5";
        Assert.Empty(add.Validate());
        Assert.Equal(
            """
            <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/">
              <s:Body>
                <Add xmlns="http://samples.codalus.dev/calculator">
                  <a>2</a>
                  <b>3.5</b>
                </Add>
              </s:Body>
            </s:Envelope>
            """.ReplaceLineEndings("\n"),
            add.BuildEnvelope());

        // Greet: a data contract with an enum and a list of strings.
        var greet = new SoapRequest(document, basic, basic.Operations[2]);
        var person = Assert.Single(greet.Parameters);
        Assert.Equal((SoapFieldKind.Complex, "Person", true, true), (person.Kind, person.TypeLabel, person.Optional, person.Nillable));
        Assert.Equal(["Name", "Age", "Mood", "Tags"], person.Children.Select(c => c.Name.LocalName));
        var mood = person.Children[2];
        Assert.Equal(SoapFieldKind.Choice, mood.Kind);
        Assert.Equal(["Calm", "Happy", "Grumpy"], mood.Choices);
        var tags = person.Children[3];
        var strings = Assert.Single(tags.Children);
        Assert.Equal((SoapFieldKind.Repeated, "string[]", 0), (strings.Kind, strings.TypeLabel, strings.Children.Count));
        person.Children[0].Value = "Ada";
        person.Children[1].Value = "thirty";
        mood.Value = "Happy";
        strings.NewItem().Value = "math";
        Assert.Equal(["person.Age: 'thirty' isn't a valid int."], greet.Validate().Select(p => p.Replace("Greet.", string.Empty, StringComparison.Ordinal)));
        person.Children[1].Value = "36";
        Assert.Empty(greet.Validate());
        Assert.Equal(
            """
            <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/">
              <s:Body>
                <Greet xmlns="http://samples.codalus.dev/calculator" xmlns:q1="http://schemas.microsoft.com/2003/10/Serialization/Arrays">
                  <person>
                    <Name>Ada</Name>
                    <Age>36</Age>
                    <Mood>Happy</Mood>
                    <Tags>
                      <q1:string>math</q1:string>
                    </Tags>
                  </person>
                </Greet>
              </s:Body>
            </s:Envelope>
            """.ReplaceLineEndings("\n"),
            greet.BuildEnvelope());

        // An empty optional field is left out; nil is sent as xsi:nil.
        person.Children[0].Value = string.Empty;
        tags.IsNil = true;
        var envelope = XDocument.Parse(greet.BuildEnvelope());
        Assert.Null(envelope.Descendants(XName.Get("Name", Contract)).FirstOrDefault());
        Assert.Equal("true", envelope.Descendants(XName.Get("Tags", Contract)).Single().Attribute(XName.Get("nil", SoapRequest.XsiNamespace))?.Value);

        // SOAP 1.2 on the WSHttpBinding endpoint: WS-Addressing headers.
        var ws = document.Endpoints[1];
        var wsAdd = new SoapRequest(document, ws, ws.Operations[0]);
        wsAdd.Parameters[0].Value = "1";
        Assert.Equal(
            """
            <s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope" xmlns:a="http://www.w3.org/2005/08/addressing">
              <s:Header>
                <a:Action s:mustUnderstand="1">http://samples.codalus.dev/calculator/ICalculatorService/Add</a:Action>
                <a:MessageID>urn:uuid:00000000-0000-0000-0000-000000000001</a:MessageID>
                <a:ReplyTo>
                  <a:Address>http://www.w3.org/2005/08/addressing/anonymous</a:Address>
                </a:ReplyTo>
                <a:To s:mustUnderstand="1">http://localhost:5280/CalculatorServiceWs.svc</a:To>
              </s:Header>
              <s:Body>
                <Add xmlns="http://samples.codalus.dev/calculator">
                  <a>1</a>
                </Add>
              </s:Body>
            </s:Envelope>
            """.ReplaceLineEndings("\n"),
            wsAdd.BuildEnvelope("urn:uuid:00000000-0000-0000-0000-000000000001"));

        // FindPeople's response type is an array of data contracts; its request is two simple values.
        var find = new SoapRequest(document, basic, basic.Operations[3]);
        Assert.Equal(["prefix", "max"], find.Parameters.Select(p => p.Name.LocalName));
    }

    [Fact]
    public void Values_are_checked_against_their_built_in_types()
    {
        Assert.Null(SoapRequest.ValueProblem("int", "-12"));
        Assert.NotNull(SoapRequest.ValueProblem("int", "1.5"));
        Assert.Null(SoapRequest.ValueProblem("double", "1.5e3"));
        Assert.Null(SoapRequest.ValueProblem("boolean", "true"));
        Assert.NotNull(SoapRequest.ValueProblem("boolean", "yes"));
        Assert.Null(SoapRequest.ValueProblem("dateTime", "2026-10-16T12:00:00Z"));
        Assert.Null(SoapRequest.ValueProblem("duration", "PT1H"));
        Assert.Null(SoapRequest.ValueProblem("guid", "7c9e6679-7425-40de-944b-e07fc1f90ae7"));
        Assert.NotNull(SoapRequest.ValueProblem("guid", "nope"));
        Assert.Null(SoapRequest.ValueProblem("string", "anything"));
    }

    [Fact]
    public void Responses_are_formatted_and_faults_read()
    {
        string Read(string name) => File.ReadAllText(TestData("response-" + name + ".xml"));

        var add = SoapResponse.Parse(200, "text/xml; charset=utf-8", Read("add11"), TimeSpan.FromMilliseconds(3));
        Assert.False(add.IsFault);
        Assert.Equal("AddResponse", add.Tree?.Name);
        Assert.Equal("AddResult = 5.5", Assert.Single(add.Tree!.Children).Label);
        Assert.Contains("\n    <AddResponse", add.Formatted, StringComparison.Ordinal);
        Assert.Equal("200, 3 ms", add.Summary);

        var people = SoapResponse.Parse(200, null, Read("people11"), TimeSpan.Zero);
        var result = Assert.Single(people.Tree!.Children);
        Assert.Equal(["Person", "Person"], result.Children.Select(c => c.Name));
        Assert.Equal(["Name = Ada", "Age = 36", "Mood = Happy", "Tags"], result.Children[0].Children.Select(c => c.Label));
        Assert.Equal(["string = math", "string = engines"], result.Children[0].Children[3].Children.Select(c => c.Label));

        var fault11 = SoapResponse.Parse(500, null, Read("divide11"), TimeSpan.Zero);
        Assert.Equal((true, "s:Client", "Division by zero"), (fault11.IsFault, fault11.FaultCode, fault11.FaultReason));
        Assert.StartsWith("Fault s:Client: Division by zero", fault11.Summary, StringComparison.Ordinal);
        Assert.Contains(fault11.Tree!.Children, c => c.Name == "detail");

        var add12 = SoapResponse.Parse(200, "application/soap+xml", Read("add12"), TimeSpan.Zero);
        Assert.Equal(("AddResponse", false), (add12.Tree?.Name, add12.IsFault));

        var fault12 = SoapResponse.Parse(500, null, Read("divide12"), TimeSpan.Zero);
        Assert.Equal((true, "s:Sender", "Division by zero"), (fault12.IsFault, fault12.FaultCode, fault12.FaultReason));

        // Not XML: the text as it came.
        var html = SoapResponse.Parse(404, "text/html", "<html><body>Not found", TimeSpan.Zero);
        Assert.Equal(("<html><body>Not found", null, false), (html.Formatted, html.Tree, html.IsFault));
    }

    [Fact]
    public async Task Requests_are_posted_as_soap_and_metadata_is_fetched_over_http()
    {
        var ct = TestContext.Current.CancellationToken;
        MockHttpServer? server = null;
        server = new MockHttpServer(request =>
        {
            if (request.Method == "GET" && request.Path.EndsWith("Ws.svc", StringComparison.Ordinal))
            {
                return MockHttpResponse.Text(string.Empty, 400); // as CoreWCF answers a GET on a SOAP 1.2 endpoint
            }

            if (request.Method == "GET")
            {
                var name = request.Query switch
                {
                    "wsdl" => "wsdl.xml",
                    var q when q.StartsWith("wsdl=", StringComparison.Ordinal) => q[5..] + ".xml",
                    var q when q.StartsWith("xsd=", StringComparison.Ordinal) => q[4..] + ".xml",
                    _ => null,
                };
                return name is null
                    ? MockHttpResponse.NotFound()
                    : MockHttpResponse.Text(File.ReadAllText(TestData(name)).Replace("http://localhost:5280/", server!.BaseUrl + "/", StringComparison.Ordinal), contentType: "text/xml; charset=UTF-8");
            }

            var soap12 = request.Path.EndsWith("Ws.svc", StringComparison.Ordinal);
            return MockHttpResponse.Text(File.ReadAllText(TestData(soap12 ? "response-add12.xml" : "response-add11.xml")), contentType: soap12 ? "application/soap+xml; charset=utf-8" : "text/xml; charset=utf-8");
        });
        await using (server)
        {
            using var client = new WcfTestClient(new HttpClient());
            var document = await client.LoadAsync(server.BaseUrl + "/CalculatorService.svc", ct);
            Assert.Equal(5, document.DocumentCount);
            Assert.Equal(server.BaseUrl + "/CalculatorService.svc", document.Endpoints[0].Address);

            var basic = new SoapRequest(document, document.Endpoints[0], document.Endpoints[0].Operations[0]);
            var response = await client.SendAsync(basic, basic.BuildEnvelope(), ct);
            Assert.Equal((200, false, "AddResponse"), (response.StatusCode, response.IsFault, response.Tree?.Name));
            var posted = server.Requests.Last();
            Assert.Equal("POST", posted.Method);
            Assert.Equal("text/xml; charset=utf-8", posted.Header("Content-Type"));
            Assert.Equal("\"" + Contract + "/ICalculatorService/Add\"", posted.Header("SOAPAction"));
            Assert.Contains("<Add xmlns=\"" + Contract + "\"", posted.Body, StringComparison.Ordinal);

            var ws = new SoapRequest(document, document.Endpoints[1], document.Endpoints[1].Operations[0]);
            response = await client.SendAsync(ws, ws.BuildEnvelope(), ct);
            Assert.Equal("AddResponse", response.Tree?.Name);
            posted = server.Requests.Last();
            Assert.Equal("/CalculatorServiceWs.svc", posted.Path);
            Assert.Equal("application/soap+xml; charset=utf-8; action=\"" + Contract + "/ICalculatorService/Add\"", posted.Header("Content-Type"));
            Assert.Null(posted.Header("SOAPAction"));
            Assert.Contains("<a:To s:mustUnderstand=\"1\">" + server.BaseUrl + "/CalculatorServiceWs.svc</a:To>", posted.Body, StringComparison.Ordinal);

            // A running CoreWCF service is found from its project's endpoints and the address it listens on.
            var service = Path.Combine(ContainerToolsTests.RepoRoot, "samples", "Wcf", "CalculatorService", "CalculatorService.csproj");
            var found = await client.DiscoverAsync([(service, "info: Microsoft.Hosting.Lifetime[14]\n      Now listening on: " + server.BaseUrl + "\n")], ct);
            Assert.Equal([server.BaseUrl + "/CalculatorService.svc?wsdl"], found.Select(s => s.MetadataUrl));
        }
    }

    [Fact]
    public void Running_services_are_found_from_their_sources_and_output()
    {
        var service = Path.Combine(ContainerToolsTests.RepoRoot, "samples", "Wcf", "CalculatorService");
        Assert.True(WcfServiceDiscovery.IsCoreWcfProject(Path.Combine(service, "CalculatorService.csproj")));
        Assert.False(WcfServiceDiscovery.IsCoreWcfProject(Path.Combine(ContainerToolsTests.RepoRoot, "samples", "Wcf", "CalculatorClient", "CalculatorClient.csproj")));
        Assert.Equal(["/CalculatorService.svc", "/CalculatorServiceWs.svc"], WcfServiceDiscovery.EndpointPaths(service));
        Assert.Equal(
            [new Uri("http://localhost:5280"), new Uri("https://localhost:7280")],
            WcfServiceDiscovery.ListeningAddresses("Now listening on: https://[::]:7280\nNow listening on: http://0.0.0.0:5280\nNow listening on: http://192.0.2.1:5000\n"));
    }
}
