using System.Net;
using Codalus.Workbench.Http;

namespace Codalus.Workbench.Tests;

/// <summary>The local servers' accept loop (the browser refresh server, Aspire's IDE execution server).</summary>
public sealed class ListenerAcceptTests
{
    /// <summary>
    /// A wait ended by its token, then the listener closed: the listener's own accept faults when it closes, and nothing
    /// observed it, so the finalizer reported an unobserved <see cref="ObjectDisposedException"/> (the macOS smoke's
    /// command sweep, run 41197709812).
    /// </summary>
    [Fact]
    public async Task A_cancelled_wait_leaves_no_unobserved_fault_when_the_listener_closes()
    {
        var unobserved = new List<Exception>();
        void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            if (e.Exception.InnerExceptions.Any(x => x is ObjectDisposedException { ObjectName: var name } && name.Contains(nameof(HttpListener), StringComparison.Ordinal)))
            {
                lock (unobserved)
                {
                    unobserved.Add(e.Exception);
                }
            }
        }

        TaskScheduler.UnobservedTaskException += OnUnobserved;
        try
        {
            for (var i = 0; i < 5; i++)
            {
                await CancelThenCloseAsync();
            }

            for (var i = 0; i < 3; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                await Task.Delay(50, TestContext.Current.CancellationToken);
            }
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= OnUnobserved;
        }

        lock (unobserved)
        {
            Assert.Empty(unobserved);
        }
    }

    [Fact]
    public async Task A_request_comes_back_and_a_closed_listener_gives_null()
    {
        var listener = Listen(out var port);
        var next = ListenerAccept.NextAsync(listener, TestContext.Current.CancellationToken);
        using var http = new HttpClient();
        var request = http.GetAsync(new Uri($"http://localhost:{port}/"), TestContext.Current.CancellationToken);
        var context = await next;
        Assert.NotNull(context);
        context.Response.StatusCode = 204;
        context.Response.Close();
        Assert.Equal(HttpStatusCode.NoContent, (await request).StatusCode);

        listener.Close();
        Assert.Null(await ListenerAccept.NextAsync(listener, TestContext.Current.CancellationToken));
    }

    // Not inlined: the task the wait left behind must be unreachable once it returns, for the collections above.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static async Task CancelThenCloseAsync()
    {
        var listener = Listen(out _);
        using var cancel = new CancellationTokenSource();
        var next = ListenerAccept.NextAsync(listener, cancel.Token);
        await cancel.CancelAsync();
        Assert.Null(await next);
        listener.Close();
        await Task.Delay(50, TestContext.Current.CancellationToken); // the listener's accept faults as it closes
    }

    private static HttpListener Listen(out int port)
    {
        using (var socket = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0))
        {
            socket.Start();
            port = ((IPEndPoint)socket.LocalEndpoint).Port;
        }

        var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        listener.Start();
        return listener;
    }
}
