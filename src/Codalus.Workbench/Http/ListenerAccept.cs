using System.Net;

namespace Codalus.Workbench.Http;

/// <summary>Takes an <see cref="HttpListener"/>'s next request until a token ends the wait (the IDE's local servers).</summary>
internal static class ListenerAccept
{
    /// <summary>
    /// The next request, or null once <paramref name="cancellationToken"/> is cancelled or the listener stops. A cancelled
    /// wait leaves the listener's own accept running: it ends when the listener closes, faulted with an
    /// <see cref="ObjectDisposedException"/> nothing awaited, which the finalizer reported as unobserved (the macOS smoke's
    /// command sweep, run 41197709812). It is observed here, and a request it takes meanwhile is refused.
    /// </summary>
    public static async Task<HttpListenerContext?> NextAsync(HttpListener listener, CancellationToken cancellationToken)
    {
        Task<HttpListenerContext> accept;
        try
        {
            accept = listener.GetContextAsync();
        }
        catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
        {
            return null;
        }

        try
        {
            return await accept.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or OperationCanceledException or InvalidOperationException)
        {
            _ = accept.ContinueWith(
                static late =>
                {
                    if (late.IsCompletedSuccessfully)
                    {
                        late.Result.Response.Abort();
                    }
                    else
                    {
                        _ = late.Exception;
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return null;
        }
    }
}
