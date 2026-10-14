using System.Xml.Linq;
using Codalus.Tests;
using Codalus.Workbench.Wcf;

namespace Codalus.Workbench.Tests;

/// <summary>
/// The WCF Test Client's engine (M42): samples/Wcf's WSDL (as CoreWCF publishes it at <c>?singleWsdl</c>), requests
/// built from it, and calls answered by a stand-in service on loopback.
/// </summary>
public sealed class WcfTestClientTests
{
    private const string Ns = "http://codalus.dev/samples/calculator";

    private static string Wsdl => File.ReadAllText(Path.Combine(ContainerToolsTests.RepoRoot, "samples", "Wcf", "contracts", "Calculator.wsdl"));

    [Fact]
    public void The_wsdl_lists_the_endpoint_and_its_operations_with_their_parameters()
    {
        var document = WsdlDocument.Parse(Wsdl);
        var endpoint = Assert.Single(document.Endpoints);
        Assert.Equal(("CalculatorService", "1.1", false, null), (endpoint.Service, endpoint.SoapVersion, endpoint.Addressing, endpoint.Unsupported));
        Assert.EndsWith("/CalculatorService.svc", endpoint.Address, StringComparison.Ordinal);
        Assert.Equal(["Add", "Greet", "Distance"], endpoint.Operations.Select(o => o.Name));

        var add = endpoint.Operations[0];
        Assert.Equal(Ns + "/ICalculatorService/Add", add.Action);
        Assert.Equal(("Add", Ns), (add.RequestElement, add.RequestNamespace));
        Assert.Equal([("a", "int"), ("b", "int")], add.Parameters.Select(p => (p.Name, p.Type)));

        // A data contract's members, in its namespace.
        var distance = endpoint.Operations[2];
        var from = distance.Parameters[0];
        Assert.Equal(("from", "Point", true), (from.Name, from.Type, from.IsComplex));
        Assert.Equal([("X", "double", Ns), ("Y", "double", Ns)], from.Children.Select(c => (c.Name, c.Type, c.Namespace)));
    }

    [Fact]
    public void A_sample_request_has_every_parameter_with_a_value_of_its_type()
    {
        var distance = WsdlDocument.Parse(Wsdl).Endpoints[0].Operations.Single(o => o.Name == "Distance");
        var request = XElement.Parse(WcfTestClient.SampleRequest(distance));
        XNamespace ns = Ns;
        Assert.Equal(ns + "Distance", request.Name);
        Assert.Equal("0", request.Element(ns + "from")!.Element(ns + "X")!.Value);
        Assert.Equal("0", request.Element(ns + "to")!.Element(ns + "Y")!.Value);
    }

    [Fact]
    public void Imports_message_security_and_other_transports_are_handled()
    {
        // ?wsdl with the types in an imported schema, as WCF writes it.
        var xsd = """
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" elementFormDefault="qualified" targetNamespace="urn:t">
              <xs:element name="Ping"><xs:complexType><xs:sequence><xs:element minOccurs="0" name="text" nillable="true" type="xs:string"/>
                <xs:element minOccurs="0" name="color" type="tns:Color" xmlns:tns="urn:t"/></xs:sequence></xs:complexType></xs:element>
              <xs:simpleType name="Color"><xs:restriction base="xs:string"><xs:enumeration value="Red"/><xs:enumeration value="Blue"/></xs:restriction></xs:simpleType>
            </xs:schema>
            """;
        var wsdl = """
            <wsdl:definitions xmlns:wsdl="http://schemas.xmlsoap.org/wsdl/" xmlns:soap12="http://schemas.xmlsoap.org/wsdl/soap12/" xmlns:soap="http://schemas.xmlsoap.org/wsdl/soap/"
                xmlns:wsp="http://schemas.xmlsoap.org/ws/2004/09/policy" xmlns:wsu="http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-utility-1.0.xsd"
                xmlns:sp="http://schemas.xmlsoap.org/ws/2005/07/securitypolicy" xmlns:wsaw="http://www.w3.org/2006/05/addressing/wsdl" xmlns:tns="urn:t" targetNamespace="urn:t" name="S">
              <wsp:Policy wsu:Id="Secure_policy"><wsp:ExactlyOne><wsp:All><sp:SymmetricBinding /><wsaw:UsingAddressing /></wsp:All></wsp:ExactlyOne></wsp:Policy>
              <wsp:Policy wsu:Id="Plain_policy"><wsp:ExactlyOne><wsp:All><wsaw:UsingAddressing /></wsp:All></wsp:ExactlyOne></wsp:Policy>
              <wsdl:types><xsd:schema xmlns:xsd="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:t/Imports"><xsd:import schemaLocation="?xsd=xsd0" namespace="urn:t"/></xsd:schema></wsdl:types>
              <wsdl:message name="PingIn"><wsdl:part name="parameters" element="tns:Ping"/></wsdl:message>
              <wsdl:portType name="IS"><wsdl:operation name="Ping"><wsdl:input wsaw:Action="urn:t/IS/Ping" message="tns:PingIn"/></wsdl:operation></wsdl:portType>
              <wsdl:binding name="Secure" type="tns:IS"><wsp:PolicyReference URI="#Secure_policy"/><soap12:binding transport="http://schemas.xmlsoap.org/soap/http"/>
                <wsdl:operation name="Ping"><soap12:operation soapAction="urn:t/IS/Ping" style="document"/></wsdl:operation></wsdl:binding>
              <wsdl:binding name="Plain" type="tns:IS"><wsp:PolicyReference URI="#Plain_policy"/><soap12:binding transport="http://schemas.xmlsoap.org/soap/http"/>
                <wsdl:operation name="Ping"><soap12:operation soapAction="urn:t/IS/Ping" style="document"/></wsdl:operation></wsdl:binding>
              <wsdl:binding name="Tcp" type="tns:IS"><soap12:binding transport="http://schemas.microsoft.com/soap/tcp"/>
                <wsdl:operation name="Ping"><soap12:operation soapAction="urn:t/IS/Ping" style="document"/></wsdl:operation></wsdl:binding>
              <wsdl:service name="S">
                <wsdl:port name="Secure" binding="tns:Secure"><soap12:address location="http://localhost/s.svc"/></wsdl:port>
                <wsdl:port name="Plain" binding="tns:Plain"><soap12:address location="http://localhost/s.svc/plain"/></wsdl:port>
                <wsdl:port name="Tcp" binding="tns:Tcp"><soap12:address location="net.tcp://localhost/s.svc"/></wsdl:port>
              </wsdl:service>
            </wsdl:definitions>
            """;
        var document = WsdlDocument.Parse(wsdl, location => location == "?xsd=xsd0" ? xsd : null);
        Assert.Equal(3, document.Endpoints.Count);
        Assert.Contains("message security", document.Endpoints[0].Unsupported, StringComparison.Ordinal);
        Assert.Contains("isn't HTTP", document.Endpoints[2].Unsupported, StringComparison.Ordinal);
        var plain = document.Endpoints[1];
        Assert.Equal(("1.2", true, null), (plain.SoapVersion, plain.Addressing, plain.Unsupported));
        var ping = Assert.Single(plain.Operations);
        Assert.Equal(("urn:t/IS/Ping", true), (ping.Action, ping.OneWay));
        var color = ping.Parameters.Single(p => p.Name == "color");
        Assert.Equal(["Red", "Blue"], color.Values);
        Assert.Contains("<color>Red</color>", WcfTestClient.SampleRequest(ping), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Operations_are_called_over_soap_and_faults_are_reported()
    {
        string? action = null;
        string? body = null;
        await using var server = new MockHttpServer(request =>
        {
            if (request.Method == "GET")
            {
                return request.Query.Contains("singleWsdl", StringComparison.Ordinal)
                    ? MockHttpResponse.Text(Wsdl, contentType: "text/xml; charset=utf-8")
                    : MockHttpResponse.NotFound();
            }

            action = request.Header("SOAPAction");
            body = request.Body;
            var b = XElement.Parse(request.Body).Descendants(XName.Get("b", Ns)).FirstOrDefault()?.Value;
            return b == "0"
                ? MockHttpResponse.Text("""<s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/"><s:Body><s:Fault><faultcode>s:Client</faultcode><faultstring>b can't be zero</faultstring></s:Fault></s:Body></s:Envelope>""", 500, "text/xml; charset=utf-8")
                : MockHttpResponse.Text($"""<s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/"><s:Body><AddResponse xmlns="{Ns}"><AddResult>5</AddResult></AddResponse></s:Body></s:Envelope>""", contentType: "text/xml; charset=utf-8");
        });

        using var http = new HttpClient();
        var client = new WcfTestClient(http);
        var metadata = await client.LoadAsync(server.BaseUrl + "/CalculatorService.svc", TestContext.Current.CancellationToken);
        Assert.Equal(server.BaseUrl + "/CalculatorService.svc?singleWsdl", client.Source);
        var endpoint = metadata.Endpoints[0];
        var add = endpoint.Operations[0];
        var request = WcfTestClient.SampleRequest(add).Replace("<a>0</a>", "<a>2</a>", StringComparison.Ordinal).Replace("<b>0</b>", "<b>3</b>", StringComparison.Ordinal);
        var address = server.BaseUrl + "/CalculatorService.svc";

        var response = await client.InvokeAsync(endpoint, add, request, TestContext.Current.CancellationToken, address);
        Assert.Equal(200, response.Status);
        Assert.False(response.IsFault);
        Assert.Equal("5", XElement.Parse(response.Body).Value);
        Assert.Equal($"\"{Ns}/ICalculatorService/Add\"", action);
        Assert.Contains("<s:Body><Add xmlns=", body, StringComparison.Ordinal);

        var fault = await client.InvokeAsync(endpoint, add, WcfTestClient.SampleRequest(add), TestContext.Current.CancellationToken, address);
        Assert.Equal((500, "b can't be zero"), (fault.Status, fault.Fault));

        await Assert.ThrowsAsync<FormatException>(() => client.InvokeAsync(endpoint, add, "<Add>", TestContext.Current.CancellationToken, address));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.LoadAsync(server.BaseUrl + "/missing?wsdl", TestContext.Current.CancellationToken));
    }
}
