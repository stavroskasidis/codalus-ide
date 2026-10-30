using System.IO.Pipes;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Codalus.LanguageServices.Protocol;
using Codalus.Projects.Protocol;
using Codalus.Workbench.Build;
using Codalus.Workbench.Debugging;

namespace Codalus.Workbench.Tests;

/// <summary>
/// ADR 0090, the parts without a debugger: which projects run in the browser, when edits can reach it, the server's
/// environment, the IDE as the browser refresh server (a page proves itself with the secret it encrypts, gets an edit's
/// deltas and answers), and the pipe that tells a program's agent the updated types.
/// </summary>
public sealed class BrowserHotReloadTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ProjectInfo Project(string path, params ReferenceInfo[] references) =>
        new(path, Path.GetFileNameWithoutExtension(path), ProjectStates.Loaded, null, ["net10.0"], null, [], references, [], true);

    private static ReferenceInfo Package(string id) => new(ReferenceKinds.Package, id, "10.0.13", null, "net10.0", true);

    private static ReferenceInfo ProjectReference(string path) => new(ReferenceKinds.Project, Path.GetFileNameWithoutExtension(path), null, path, "net10.0", true);

    private static HotReloadModuleUpdate Update(Guid module, string project) => new(module, project, [1, 2], [3], [4], [0x02000002]);

    [Fact]
    public void The_browser_runs_the_clients_and_what_they_reference_not_the_servers_own_projects()
    {
        const string client = "/src/Client/Client.csproj", shared = "/src/Shared/Shared.csproj", model = "/src/Model/Model.csproj";
        const string server = "/src/Server/Server.csproj", data = "/src/Data/Data.csproj";
        var projects = new Dictionary<string, ProjectInfo>
        {
            [client] = Project(client, Package("Microsoft.AspNetCore.Components.WebAssembly"), ProjectReference(shared)),
            [shared] = Project(shared, ProjectReference(model)),
            [model] = Project(model),
            [data] = Project(data, ProjectReference(model)),
            [server] = Project(server, Package("Microsoft.AspNetCore.Components.WebAssembly.Server"), ProjectReference(client), ProjectReference(data)),
        };

        Assert.Equal([client, model, shared], BlazorProjects.BrowserProjectsOf(server, projects).Order(StringComparer.Ordinal));
        Assert.Empty(BlazorProjects.BrowserProjectsOf(data, projects));
    }

    [Fact]
    public void Edits_reach_the_browser_from_net10_clients_and_sdk_10_with_its_middleware()
    {
        Assert.Equal(10, BrowserHotReload.FrameworkMajor("net10.0"));
        Assert.Equal(11, BrowserHotReload.FrameworkMajor("net11.0-browser"));
        Assert.Null(BrowserHotReload.FrameworkMajor("net48"));
        Assert.Null(BrowserHotReload.FrameworkMajor("netstandard2.1"));

        // WPoseidon's case: SDK 10.0.303, below dotnet watch's 10.0.400 for debugging, is enough here.
        Assert.Null(BrowserHotReload.UnsupportedReason(["net10.0"], "10.0.303", "/sdk/middleware.dll"));
        Assert.Contains("older than 10", BrowserHotReload.UnsupportedReason(["net10.0", "net9.0"], "10.0.303", "/sdk/middleware.dll"), StringComparison.Ordinal);
        Assert.Contains("9.0.300", BrowserHotReload.UnsupportedReason(["net10.0"], "9.0.300", "/sdk/middleware.dll"), StringComparison.Ordinal);
        Assert.Null(BrowserHotReload.UnsupportedReason(["net10.0"], "10.0.403", "/sdk/middleware.dll"));

        // SDK 11's middleware forwards to dotnet watch's browser tools provider instead.
        Assert.Contains("doesn't support yet", BrowserHotReload.UnsupportedReason(["net10.0"], "11.0.100-rc.3.26522.148", "/sdk/middleware.dll"), StringComparison.Ordinal);
        Assert.Contains("couldn't be read", BrowserHotReload.UnsupportedReason(["net10.0"], null, null), StringComparison.Ordinal);
        Assert.Contains("no browser refresh middleware", BrowserHotReload.UnsupportedReason(["net10.0"], "10.0.303", null), StringComparison.Ordinal);

        Assert.Empty(BrowserHotReload.WebAssemblyCapabilities(9));
        Assert.DoesNotContain("AddFieldRva", BrowserHotReload.WebAssemblyCapabilities(10));
    }

    [Fact]
    public void The_middleware_is_found_under_dotnet_watchs_hotreload_folder()
    {
        var sdk = Path.Combine(Path.GetTempPath(), "codalus-sdk-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Assert.Null(BrowserHotReload.FindMiddleware(sdk));
            var folder = Path.Combine(sdk, "DotnetTools", "dotnet-watch", "10.0.303", "tools", "net10.0", "any", "hotreload", "net6.0");
            Directory.CreateDirectory(folder);
            var middleware = Path.Combine(folder, "Microsoft.AspNetCore.Watch.BrowserRefresh.dll");
            File.WriteAllBytes(middleware, []);
            Assert.Equal(middleware, BrowserHotReload.FindMiddleware(sdk));
        }
        finally
        {
            Directory.Delete(sdk, recursive: true);
        }
    }

    [Fact]
    public void The_servers_environment_loads_the_middleware_beside_the_plans_own_hooks_and_hosting_startups()
    {
        var planned = new Dictionary<string, string>
        {
            ["DOTNET_STARTUP_HOOKS"] = "/app/hook.dll",
            ["ASPNETCORE_HOSTINGSTARTUPASSEMBLIES"] = "Mine",
            ["ASPNETCORE_ENVIRONMENT"] = "Development",
        };
        var environment = BrowserHotReload.Environment(planned, "/sdk/Microsoft.AspNetCore.Watch.BrowserRefresh.dll", new Dictionary<string, string> { ["ASPNETCORE_AUTO_RELOAD_WS_ENDPOINT"] = "ws://localhost:1" });

        Assert.Equal("/sdk/Microsoft.AspNetCore.Watch.BrowserRefresh.dll" + Path.PathSeparator + "/app/hook.dll", environment["DOTNET_STARTUP_HOOKS"]);
        Assert.Equal("Mine;Microsoft.AspNetCore.Watch.BrowserRefresh", environment["ASPNETCORE_HOSTINGSTARTUPASSEMBLIES"]);
        Assert.Equal("debug", environment["DOTNET_MODIFIABLE_ASSEMBLIES"]);
        Assert.Equal("true", environment["__ASPNETCORE_BROWSER_TOOLS"]);
        Assert.Equal("ws://localhost:1", environment["ASPNETCORE_AUTO_RELOAD_WS_ENDPOINT"]);
        Assert.Equal("Development", environment["ASPNETCORE_ENVIRONMENT"]);
    }

    /// <summary>A page as aspnetcore-browser-refresh.js connects: its secret encrypted with the server's key as the subprotocol.</summary>
    private static async Task<(ClientWebSocket Socket, string Secret)> ConnectPageAsync(BrowserRefreshServer server)
    {
        var environment = server.Environment();
        using var key = RSA.Create();
        key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(environment[BrowserRefreshServer.KeyVariable]), out _);
        var secret = RandomNumberGenerator.GetBytes(32);
        var socket = new ClientWebSocket();
        socket.Options.AddSubProtocol(Uri.EscapeDataString(Convert.ToBase64String(key.Encrypt(secret, RSAEncryptionPadding.OaepSHA256))));
        await socket.ConnectAsync(new Uri(environment[BrowserRefreshServer.EndpointVariable]), Ct);
        return (socket, Convert.ToBase64String(secret));
    }

    private static async Task<JsonElement> ReceiveAsync(ClientWebSocket socket)
    {
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, Ct);
            message.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);
        return JsonElement.Parse(message.ToArray());
    }

    private static Task SendAsync(ClientWebSocket socket, string text) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, endOfMessage: true, Ct);

    [Fact]
    public async Task A_page_with_the_servers_secret_gets_an_edits_deltas_and_its_answer_is_the_result()
    {
        await using var server = BrowserRefreshServer.Start();
        Assert.Same(BrowserApplyResult.NoPage, await server.ApplyAsync([Update(Guid.NewGuid(), "/c.csproj")], TimeSpan.FromSeconds(5), Ct));

        var (page, secret) = await ConnectPageAsync(server);
        using var _ = page;
        await ShellLikeWait(() => server.Pages == 1);

        var module = Guid.NewGuid();
        var applying = server.ApplyAsync([Update(module, "/c.csproj")], TimeSpan.FromSeconds(30), Ct);
        var message = await ReceiveAsync(page);
        Assert.Equal("ApplyManagedCodeUpdates", message.GetProperty("type").GetString());
        Assert.Equal(secret, message.GetProperty("sharedSecret").GetString());
        var delta = message.GetProperty("deltas")[0];
        Assert.Equal(module, delta.GetProperty("moduleId").GetGuid());
        Assert.Equal(Convert.ToBase64String([1, 2]), delta.GetProperty("metadataDelta").GetString());
        Assert.Equal(0x02000002, delta.GetProperty("updatedTypes")[0].GetInt32());
        await SendAsync(page, """{"success":true,"log":[]}""");
        var result = await applying;
        Assert.Equal(1, result.Pages);
        Assert.Empty(result.Errors);

        // A page that can't apply it says why.
        applying = server.ApplyAsync([Update(module, "/c.csproj")], TimeSpan.FromSeconds(30), Ct);
        await ReceiveAsync(page);
        await SendAsync(page, """{"success":false,"log":[{"message":"ENC0001: not supported","severity":2}]}""");
        Assert.Equal(["ENC0001: not supported"], (await applying).Errors);

        // A page slow to apply it (under load, Windows: more than the wait) isn't a failure: its answer comes later.
        var late = new TaskCompletionSource<BrowserLateAnswer>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.LateAnswer += answer => late.TrySetResult(answer);
        var slow = await server.ApplyAsync([Update(module, "/c.csproj")], TimeSpan.FromMilliseconds(200), Ct);
        Assert.Equal(1, slow.Unanswered);
        Assert.Empty(slow.Errors);
        var updateId = (await ReceiveAsync(page)).GetProperty("updateId").GetInt32();
        await SendAsync(page, """{"success":true,"log":[]}""");
        Assert.Equal(new BrowserLateAnswer(updateId, null), await late.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct));

        // RefreshBrowser asks nothing back; capabilities are the page's string.
        await server.RefreshAsync(Ct);
        Assert.Equal("RefreshBrowser", (await ReceiveAsync(page)).GetProperty("type").GetString());
        var capabilities = server.CapabilitiesAsync(TimeSpan.FromSeconds(30), Ct);
        Assert.Equal("GetApplyUpdateCapabilities", (await ReceiveAsync(page)).GetProperty("type").GetString());
        await SendAsync(page, "Baseline AddMethodToExistingType");
        Assert.Equal("Baseline AddMethodToExistingType", await capabilities);
    }

    [Fact]
    public async Task A_connection_without_the_secret_is_refused()
    {
        await using var server = BrowserRefreshServer.Start();
        using var socket = new ClientWebSocket();
        await Assert.ThrowsAsync<WebSocketException>(() => socket.ConnectAsync(new Uri(server.Environment()[BrowserRefreshServer.EndpointVariable]), Ct));
        Assert.Equal(0, server.Pages);
    }

    [Fact]
    public async Task The_agent_pipe_tells_each_program_the_updated_types_and_returns_its_handlers_errors()
    {
        await using var agents = new HotReloadAgents();
        var (name, value) = agents.Variable();
        Assert.Equal(HotReloadAgents.PipeVariable, name);
        await using var program = new NamedPipeClientStream(".", value, PipeDirection.InOut, PipeOptions.Asynchronous);
        await program.ConnectAsync(Ct);
        await ShellLikeWait(() => agents.Count == 1);
        using var reader = new StreamReader(program, leaveOpen: true);
        await using var writer = new StreamWriter(program, leaveOpen: true) { AutoFlush = true, NewLine = "\n" };

        var module = Guid.NewGuid();
        var notifying = agents.NotifyAsync([Update(module, "/p.csproj")], TimeSpan.FromSeconds(30), Ct);
        Assert.Equal($"update {module:D}:{0x02000002}", await reader.ReadLineAsync(Ct));
        await writer.WriteLineAsync("done");
        Assert.Empty(await notifying);

        notifying = agents.NotifyAsync([Update(module, "/p.csproj")], TimeSpan.FromSeconds(30), Ct);
        await reader.ReadLineAsync(Ct);
        await writer.WriteLineAsync("error App.Handler.UpdateApplication: boom");
        Assert.Equal(["App.Handler.UpdateApplication: boom"], await notifying);

        // A program held at a breakpoint doesn't answer in time: the next edit still reaches it, and the late answer is dropped.
        Assert.Empty(await agents.NotifyAsync([Update(module, "/p.csproj")], TimeSpan.FromMilliseconds(100), Ct));
        notifying = agents.NotifyAsync([Update(module, "/p.csproj")], TimeSpan.FromSeconds(30), Ct);
        await reader.ReadLineAsync(Ct);
        await reader.ReadLineAsync(Ct);
        await writer.WriteLineAsync("done");
        await writer.WriteLineAsync("error second");
        Assert.Equal(["second"], await notifying);
    }

    private static async Task ShellLikeWait(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out.");
            await Task.Delay(20, Ct);
        }
    }
}
