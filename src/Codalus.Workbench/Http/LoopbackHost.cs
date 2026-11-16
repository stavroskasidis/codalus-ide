namespace Codalus.Workbench.Http;

/// <summary>
/// The host the IDE's local <see cref="System.Net.HttpListener"/> servers listen on and are reached at: the IDE
/// execution server (Aspire) and the browser refresh server (Hot Reload), on a port
/// <see cref="Containers.ContainerLaunch.FreePort"/> found free on 127.0.0.1.
/// </summary>
/// <remarks>
/// On Linux and macOS the managed listener binds only the first address a host name resolves to, and <c>localhost</c>
/// resolves to <c>::1</c> first there: a server on <c>http://localhost:port/</c> held <c>[::1]:port</c> and left
/// 127.0.0.1's same port free. Aspire's orchestrator then put the frontend's proxy on 127.0.0.1 at that port, and its
/// URL, <c>http://localhost:port</c>, tried <c>::1</c> first and reached the IDE's server, which answered 401 (Linux CI,
/// run 43821705690). A browser opening the resource's URL got the same. So there the servers listen on 127.0.0.1 itself
/// and give that address out. On Windows http.sys holds a <c>localhost</c> port on every address, and lets an unelevated
/// user register only <c>localhost</c>, not <c>127.0.0.1</c>.
/// </remarks>
internal static class LoopbackHost
{
    public static string Name { get; } = OperatingSystem.IsWindows() ? "localhost" : "127.0.0.1";
}
