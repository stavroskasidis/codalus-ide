using System.Globalization;
using System.Net;

namespace Codalus.Workbench.Http;

/// <summary>
/// The prefixes of the IDE's local <see cref="HttpListener"/> servers, on a port
/// <see cref="Containers.ContainerLaunch.FreePort"/> found free on 127.0.0.1: the IDE execution server (Aspire) and the
/// browser refresh server (Hot Reload). They are reached at <c>localhost</c>, and off Windows they hold 127.0.0.1 too.
/// </summary>
/// <remarks>
/// Off Windows the managed listener binds only the first address a host name resolves to. Where <c>localhost</c>
/// resolves to <c>::1</c> first (CI's Linux runner; WSL and a Mac resolved 127.0.0.1 first), a server on
/// <c>http://localhost:port/</c> held <c>[::1]:port</c> and left 127.0.0.1's same port free. Aspire's orchestrator then
/// put the frontend's proxy on 127.0.0.1 at that port, and its URL, <c>http://localhost:port</c>, tried <c>::1</c> first
/// and reached the IDE's server, which answered 401 (Linux CI, run 43821705690); a browser opening the resource's URL got
/// the same. So the server also listens on <c>http://127.0.0.1:port/</c>, which holds that address, and fails to start
/// (taking another port) when it is taken. The <c>localhost</c> prefix stays, and so does the address the servers give
/// out: the managed listener matches a request's <c>Host</c> to a prefix's host exactly, and the orchestrator sends
/// <c>localhost</c>. On Windows http.sys holds a port exclusively on every address, and lets an unelevated user
/// register only <c>localhost</c>.
/// </remarks>
internal static class LoopbackPrefixes
{
    public static void Add(HttpListener listener, int port)
    {
        listener.Prefixes.Add(Url("localhost", port));
        if (!OperatingSystem.IsWindows())
        {
            listener.Prefixes.Add(Url("127.0.0.1", port));
        }
    }

    private static string Url(string host, int port) => string.Create(CultureInfo.InvariantCulture, $"http://{host}:{port}/");
}
