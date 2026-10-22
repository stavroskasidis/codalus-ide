using System.Buffers.Binary;
using System.Net;
using Avalonia.Headless.XUnit;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Aspire;

namespace Codalus.Shell.Tests;

/// <summary>
/// The Aspire window's console of the selected resource, against a stand-in resource service: a stream that ends while
/// the resource runs (seen for a project just started: the dashboard's stream ended after 64 ms with nothing, and the
/// window stayed empty) is followed again, with growing waits and a bounded number of times, the lines already shown
/// left out of its replay; not once the resource stops, and again when it starts.
/// </summary>
public sealed class AspireLogWatchTests
{
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(20);

    private static AspireResource Resource(string state) =>
        new("frontend-abc", "Project", "frontend", state, null, [], [], [], [], false);

    /// <summary>
    /// The resource service's <c>WatchResourceConsoleLogs</c>: each call's lines from <paramref name="lines"/> (by the
    /// call's number, from 1), as one message, then the stream's end with an OK status.
    /// </summary>
    private sealed class StandInResourceService(Func<int, string[]> lines) : HttpMessageHandler
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("/aspire.v1.DashboardService/WatchResourceConsoleLogs", request.RequestUri!.AbsolutePath);
            var call = Interlocked.Increment(ref _calls);
            var body = new MemoryStream();
            if (lines(call) is { Length: > 0 } texts)
            {
                // WatchResourceConsoleLogsUpdate { repeated ConsoleLogLine log_lines = 1; }, ConsoleLogLine { text = 1; line_number = 3; }
                var update = new ProtoWriter();
                for (var i = 0; i < texts.Length; i++)
                {
                    update.Bytes(1, [.. new ProtoWriter().String(1, texts[i]).ToArray(), 3 << 3, (byte)(i + 1)]);
                }

                var message = update.ToArray();
                var header = new byte[5];
                BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(1), (uint)message.Length);
                body.Write(header);
                body.Write(message);
                body.Position = 0;
            }

            var response = new HttpResponseMessage(HttpStatusCode.OK) { Version = HttpVersion.Version20, Content = new StreamContent(body) };
            response.TrailingHeaders.Add("grpc-status", "0");
            return Task.FromResult(response);
        }
    }

    private static (AspireViewModel Window, AspireResourceClient Client) Watch(StandInResourceService service, string state, params TimeSpan[] delays)
    {
        var client = new AspireResourceClient(new Uri("http://localhost:1"), "key", service);
        var window = new AspireViewModel(_ => { }) { LogRetryDelays = delays };
        window.Attach("AppHost", client);
        window.Apply(new AspireResourceChange([Resource(state)], null, null));
        window.Selected = window.Resources.Single();
        return (window, client);
    }

    [AvaloniaFact]
    public async Task A_running_resources_stream_that_ends_at_once_is_followed_again_until_its_lines_come()
    {
        // The first stream ends with nothing; the second has the console; later ones replay it and end.
        var service = new StandInResourceService(call => call == 1 ? [] : ["Now listening on: http://localhost:5000", "\u001b[32minfo\u001b[0m: started"]);
        var (window, client) = Watch(service, "Running", Short, Short);
        using var _ = client;
        using var __ = window;

        await ShellFixture.PumpUntil(() => window.Logs.Length > 0, describe: () => $"the console stays empty after {service.Calls} streams");
        Assert.True(service.Calls >= 2);

        // Bounded: the stream with lines, then two more that only replay them (the delays run out): five in all, the
        // console shown once.
        await ShellFixture.PumpUntil(() => service.Calls == 5, describe: () => $"{service.Calls} streams");
        await Task.Delay(300, TestContext.Current.CancellationToken);
        ShellFixture.Pump();
        Assert.Equal(5, service.Calls);
        Assert.Equal("Now listening on: http://localhost:5000\ninfo: started\n", window.Logs);
    }

    [AvaloniaFact]
    public async Task A_stopped_resources_stream_isnt_followed_again_until_it_starts()
    {
        var service = new StandInResourceService(call => call == 1 ? [] : ["started again"]);
        var (window, client) = Watch(service, "Exited", Short);
        using var _ = client;
        using var __ = window;

        await ShellFixture.PumpUntil(() => service.Calls == 1);
        await Task.Delay(300, TestContext.Current.CancellationToken);
        ShellFixture.Pump();
        Assert.Equal(1, service.Calls);
        Assert.Equal(string.Empty, window.Logs);

        // Started again (its restart command): its console is followed again.
        window.Apply(new AspireResourceChange(null, Resource("Running"), null));
        await ShellFixture.PumpUntil(() => window.Logs == "started again\n", describe: () => $"{service.Calls} streams, the console: {window.Logs}");
    }

    [AvaloniaFact]
    public async Task The_retries_stop_when_the_resource_stops_or_the_apphost_ends()
    {
        // Every stream ends at once; the wait before the next one is long.
        var service = new StandInResourceService(_ => []);
        var (window, client) = Watch(service, "Running", TimeSpan.FromSeconds(30));
        using var _ = client;
        using var __ = window;
        await ShellFixture.PumpUntil(() => service.Calls == 1);

        // The resource stops while the window waits: no more streams. Started again, it gets one.
        window.Apply(new AspireResourceChange(null, Resource("Exited"), null));
        await Task.Delay(300, TestContext.Current.CancellationToken);
        ShellFixture.Pump();
        Assert.Equal(1, service.Calls);
        window.Apply(new AspireResourceChange(null, Resource("Running"), null));
        await ShellFixture.PumpUntil(() => service.Calls == 2);

        // The AppHost ends while the window waits: no more streams.
        window.Detach();
        await Task.Delay(300, TestContext.Current.CancellationToken);
        ShellFixture.Pump();
        Assert.Equal(2, service.Calls);
    }
}
