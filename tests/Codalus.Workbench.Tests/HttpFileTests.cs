using System.Net;
using System.Net.Sockets;
using System.Text;
using Codalus.Workbench.Http;

namespace Codalus.Workbench.Tests;

/// <summary><c>.http</c> files (M15a): parsing, variables, environments and sending requests to a local server.</summary>
public sealed class HttpFileTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "codalus-http-" + Guid.NewGuid().ToString("N"));

    public HttpFileTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private const string Sample = """
        @host = http://localhost:5000
        @api = {{host}}/api

        # A comment
        GET {{api}}/todos
        Accept: application/json

        ###

        # @name create
        POST {{api}}/todos HTTP/1.1
        Content-Type: application/json

        {
          "title": "x"
        }

        ### Query lines
        GET https://example.test/search
            ?q=one
            &page=2
        """;

    [Fact]
    public void Requests_variables_and_bodies_are_parsed()
    {
        var document = HttpFileParser.Parse(Sample);
        Assert.Equal(3, document.Requests.Count);

        var list = document.Requests[0];
        Assert.Equal(("GET", "{{api}}/todos", 5), (list.Method, list.Url, list.RequestLine));
        Assert.Equal("application/json", Assert.Single(list.Headers).Value);
        Assert.Null(list.Body);
        Assert.Equal(["host", "api"], list.Variables.Select(v => v.Key));

        var create = document.Requests[1];
        Assert.Equal("create", create.Name);
        Assert.Equal("POST", create.Method);
        Assert.Equal("{{api}}/todos", create.Url);
        Assert.Equal("{\n  \"title\": \"x\"\n}", create.Body);
        Assert.Same(create, document.At(14));
        Assert.Equal("https://example.test/search?q=one&page=2", document.Requests[2].Url);
        Assert.True(HttpFileParser.IsRequestLine("GET {{api}}/todos"));
        Assert.True(HttpFileParser.IsRequestLine("https://example.test"));
        Assert.False(HttpFileParser.IsRequestLine("Accept: application/json"));
        Assert.False(HttpFileParser.IsRequestLine("@host = x"));
    }

    [Fact]
    public void Variables_resolve_from_the_file_the_environment_and_dynamic_values()
    {
        var document = HttpFileParser.Parse(Sample);
        var unresolved = new List<string>();
        var environment = new Dictionary<string, string> { ["token"] = "secret", ["host"] = "http://ignored" };
        var url = HttpVariables.Resolve("{{api}}/todos?t={{token}}&id={{$guid}}&n={{missing}}", document.Requests[0].Variables, environment, _ => null, unresolved);

        Assert.StartsWith("http://localhost:5000/api/todos?t=secret&id=", url, StringComparison.Ordinal);
        Assert.EndsWith("&n={{missing}}", url, StringComparison.Ordinal);
        Assert.True(Guid.TryParse(url.Split("id=")[1].Split('&')[0], out _));
        Assert.Equal(["missing"], unresolved);
        Assert.Equal("3", HttpVariables.JsonPath("""{"items":[{"id":3}]}""", "$.items[0].id"));
        Assert.Equal("Ada", HttpVariables.JsonPath("""{"name":"Ada"}""", "$.name"));
        Assert.Null(HttpVariables.JsonPath("""{"name":"Ada"}""", "$.missing"));
    }

    [Fact]
    public void Environments_merge_shared_values_and_user_overrides()
    {
        var project = Path.Combine(_folder, "Project");
        Directory.CreateDirectory(project);
        File.WriteAllText(Path.Combine(_folder, "http-client.env.json"), """
            { "$shared": { "version": "v1" }, "dev": { "host": "http://localhost:1" }, "prod": { "host": "https://prod.test" } }
            """);
        File.WriteAllText(Path.Combine(_folder, "http-client.env.json.user"), """{ "dev": { "token": "mine" } }""");

        var environments = HttpEnvironments.Load(Path.Combine(project, "api.http"));
        Assert.Equal(["dev", "prod"], environments.Names);
        var dev = environments.For("dev");
        Assert.Equal(("v1", "http://localhost:1", "mine"), (dev["version"], dev["host"], dev["token"]));
        Assert.Equal("https://prod.test", environments.For("prod")["host"]);
        Assert.Same(HttpEnvironments.None, HttpEnvironments.Load(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "x.http"), Path.GetTempPath()));
    }

    [Fact]
    public async Task Requests_are_sent_with_bodies_and_request_variables()
    {
        var port = FreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        listener.Start();
        var received = new List<string>();
        var server = Task.Run(async () =>
        {
            for (var i = 0; i < 2; i++)
            {
                var context = await listener.GetContextAsync();
                using var reader = new StreamReader(context.Request.InputStream);
                var body = await reader.ReadToEndAsync();
                lock (received)
                {
                    received.Add($"{context.Request.HttpMethod} {context.Request.Url!.PathAndQuery} {context.Request.ContentType} {body}");
                }

                var reply = context.Request.HttpMethod == "POST" ? """{"id":42,"title":"x"}""" : "pong";
                context.Response.StatusCode = context.Request.HttpMethod == "POST" ? 201 : 200;
                context.Response.ContentType = context.Request.HttpMethod == "POST" ? "application/json" : "text/plain";
                context.Response.Headers["X-Test"] = "yes";
                var bytes = Encoding.UTF8.GetBytes(reply);
                await context.Response.OutputStream.WriteAsync(bytes);
                context.Response.Close();
            }
        }, TestContext.Current.CancellationToken);

        var file = Path.Combine(_folder, "api.http");
        var text = $$$"""
            @host = http://localhost:{{{port}}}

            # @name create
            POST {{host}}/todos
            Content-Type: application/json

            {"title":"x"}

            ###

            GET {{host}}/todos/{{create.response.body.$.id}}?h={{create.response.headers.X-Test}}
            """;
        await File.WriteAllTextAsync(file, text, TestContext.Current.CancellationToken);
        using var http = new HttpService();
        var completed = 0;
        http.Completed += (_, _) => Interlocked.Increment(ref completed);

        var created = await http.SendAsync(file, text, 4, TestContext.Current.CancellationToken);
        Assert.Null(created?.Error);
        Assert.Equal(201, created!.Response!.StatusCode);
        Assert.Equal("application/json", created.Response.ContentType);
        Assert.Contains(created.Response.Headers, h => h.Key == "X-Test" && h.Value == "yes");
        Assert.Equal("create", created.Name);

        var fetched = await http.SendAsync(file, text, 11, TestContext.Current.CancellationToken);
        Assert.Equal("pong", fetched?.Response?.Body);
        await server.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.Equal($"POST /todos application/json {{\"title\":\"x\"}}", received[0]);
        Assert.Equal("GET /todos/42?h=yes  ", received[1]);
        Assert.Equal(2, http.History.Count);
        Assert.Same(fetched, http.History[0]);
        Assert.Equal(2, completed);

        // Unknown variables are reported without sending; a bad URL too.
        var unknown = await http.SendAsync(file, "GET {{nope}}/x", 1, TestContext.Current.CancellationToken);
        Assert.Contains("nope", unknown?.Error, StringComparison.Ordinal);
        Assert.Null(unknown?.Response);
        Assert.Null(await http.SendAsync(file, "# nothing here", 1, TestContext.Current.CancellationToken));
    }

    private static int FreePort()
    {
        using var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        return ((IPEndPoint)socket.LocalEndpoint).Port;
    }
}
