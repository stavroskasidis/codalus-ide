using DaedalusStudio.LanguageServices.Protocol;

namespace DaedalusStudio.LanguageServices.Host.Tests;

/// <summary>samples/Web in the real build and language hosts.</summary>
public sealed class WebFixture : LanguageFixture
{
    protected override (string Sample, string Solution, int Projects) Sample => ("Web", "Web.slnx", 1);
}

/// <summary><c>daedalus/aspnet/endpoints</c> (M15a): minimal APIs with groups and controller attribute routes.</summary>
public sealed class AspNetEndpointsTests(WebFixture web) : IClassFixture<WebFixture>
{
    private static int Line(string file, string marker) =>
        Array.FindIndex(File.ReadAllLines(file), l => l.Contains($"// [{marker}]", StringComparison.Ordinal));

    [Fact]
    public async Task Minimal_apis_groups_and_controllers_are_found()
    {
        var result = await web.Client.AspNetEndpointsAsync(null, TestContext.Current.CancellationToken);
        var endpoints = Assert.IsType<AspNetEndpointsResult>(result).Endpoints;
        string Describe(AspNetEndpoint e) => $"{e.HttpMethod} {e.Route} {e.Kind} {e.Handler}";
        var all = endpoints.Select(Describe).ToList();
        var program = web.File("WebApi", "Program.cs");
        var orders = web.File("WebApi", "Controllers", "OrdersController.cs");

        Assert.Contains("GET / minimal lambda", all);
        Assert.Contains("GET /weatherforecast minimal Forecasts.Get", all);
        Assert.Contains("GET /api/todos minimal lambda", all);
        Assert.Contains("GET /api/todos/{id:int} minimal lambda", all);
        Assert.Contains("POST /api/todos minimal lambda", all);
        Assert.Contains("GET,HEAD /ping minimal lambda", all);
        Assert.Contains("GET /api/Orders controller OrdersController.List", all);
        Assert.Contains("GET /api/Orders/{id} controller OrdersController.Get", all);
        Assert.Contains("DELETE /api/Orders/{id} controller OrdersController.Delete", all);
        Assert.Contains("GET /status controller OrdersController.Status", all);
        Assert.Equal(10, endpoints.Length);
        Assert.All(endpoints, e => Assert.Equal("WebApi", e.Project));

        // Handlers navigate: a lambda to its line, a method group to the method, an action to its name.
        Assert.Equal(Line(program, "todos-get"), endpoints.Single(e => e.Route == "/api/todos/{id:int}").Location.Range.Start.Line);
        Assert.Equal(Line(program, "forecast-handler"), endpoints.Single(e => e.Route == "/weatherforecast").Location.Range.Start.Line);
        var status = endpoints.Single(e => e.Route == "/status");
        Assert.Equal(Line(orders, "orders-status"), status.Location.Range.Start.Line);
        Assert.EndsWith("OrdersController.cs", DocumentUris.ToPath(status.Location.Uri), StringComparison.Ordinal);

        // One project by its path.
        var one = await web.Client.AspNetEndpointsAsync(web.File("WebApi", "WebApi.csproj"), TestContext.Current.CancellationToken);
        Assert.Equal(10, one?.Endpoints.Length);
        var none = await web.Client.AspNetEndpointsAsync(web.File("Other", "Other.csproj"), TestContext.Current.CancellationToken);
        Assert.Empty(none?.Endpoints ?? []);
    }
}
