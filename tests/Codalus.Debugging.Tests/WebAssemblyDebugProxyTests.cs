using Codalus.Tests;

namespace Codalus.Debugging.Tests;

/// <summary>
/// When js-debug can't open a Blazor app's WebAssembly debugging proxy, the Debug Console says what the app answers
/// there (Windows CI, run 41555852375: "Could not open ws://…/ws-proxy" after a minute, and nothing else said why).
/// </summary>
public sealed class WebAssemblyDebugProxyTests
{
    private const string Browser = "browser=ws%3A%2F%2F127.0.0.1%3A56951%2Fdevtools%2Fbrowser%2F3c2b73a6";

    [Fact]
    public void The_address_comes_from_js_debugs_error()
    {
        var line = "$ [Error]: Unable to launch browser: \"Could not open ws://localhost:56849/_framework/debug/ws-proxy?" + Browser + "\"";

        Assert.Equal("ws://localhost:56849/_framework/debug/ws-proxy?" + Browser, WebAssemblyDebugProxy.UnopenedAddress(line));
        Assert.Null(WebAssemblyDebugProxy.UnopenedAddress("Unable to launch browser: \"Could not attach to main target\""));
    }

    [Fact]
    public async Task A_proxy_the_app_couldnt_start_is_described_by_the_apps_error()
    {
        await using var app = new MockHttpServer(_ => MockHttpResponse.Text(
            "System.TimeoutException: Failed to start the debug proxy within the timeout period of 10 seconds.\r\n   at Microsoft.AspNetCore.Builder.DebugProxyLauncher.LaunchAndGetUrl()", 500));

        var said = await WebAssemblyDebugProxy.DescribeAsync(Address(app), TestContext.Current.CancellationToken);

        Assert.Equal($"The app's WebAssembly debugging proxy ({app.BaseUrl}/_framework/debug/ws-proxy) answers 500 Status: System.TimeoutException: Failed to start the debug proxy within the timeout period of 10 seconds.", said);
        var request = Assert.Single(app.Requests);
        Assert.Equal("GET", request.Method);
        Assert.Equal("/_framework/debug/ws-proxy", request.Path);
        Assert.Equal(Browser, request.Query);
        Assert.Null(request.Header("Upgrade"));
        Assert.Equal("text/plain", request.Header("Accept"));
    }

    [Fact]
    public async Task A_proxy_the_app_started_is_described_by_its_redirect_which_isnt_followed()
    {
        await using var app = new MockHttpServer(_ => MockHttpResponse.Text(string.Empty, 302) with
        {
            Headers = new Dictionary<string, string> { ["Location"] = "ws://127.0.0.1:61234/ws-proxy?" + Browser },
        });

        var said = await WebAssemblyDebugProxy.DescribeAsync(Address(app), TestContext.Current.CancellationToken);

        Assert.Equal($"The app's WebAssembly debugging proxy ({app.BaseUrl}/_framework/debug/ws-proxy) answers 302 Status, to ws://127.0.0.1:61234/ws-proxy: the app started the proxy.", said);
        Assert.Single(app.Requests);
    }

    [Fact]
    public async Task An_app_that_doesnt_answer_is_described_too()
    {
        int port;
        await using (var gone = new MockHttpServer(_ => MockHttpResponse.NotFound()))
        {
            port = new Uri(gone.BaseUrl).Port;
        }

        var said = await WebAssemblyDebugProxy.DescribeAsync($"ws://127.0.0.1:{port}/_framework/debug/ws-proxy?{Browser}", TestContext.Current.CancellationToken);

        Assert.StartsWith($"The app's WebAssembly debugging proxy (http://127.0.0.1:{port}/_framework/debug/ws-proxy) didn't answer: ", said, StringComparison.Ordinal);
    }

    private static string Address(MockHttpServer app) => app.BaseUrl.Replace("http://", "ws://", StringComparison.Ordinal) + "/_framework/debug/ws-proxy?" + Browser;
}
