using System.Globalization;

namespace Codalus.Workbench.Debugging;

/// <summary>
/// Where a debugged Blazor Web App's WebAssembly edits go (ADR 0090): <see cref="Projects"/> run in the browser, and
/// their deltas go to the pages through <see cref="Server"/>. Without a server (an app or SDK older than .NET 10) they
/// can't reach the browser, and an edit to them says it needs a restart. <see cref="Capabilities"/> is what the
/// browser's runtime can apply, which every edit of the session is limited to.
/// </summary>
public sealed record BrowserHotReload(IReadOnlyCollection<string> Projects, BrowserRefreshServer? Server, IReadOnlyList<string>? Capabilities, string? Reason = null)
{
    public const string MiddlewareAssembly = "Microsoft.AspNetCore.Watch.BrowserRefresh";
    public const string StartupHooksVariable = "DOTNET_STARTUP_HOOKS";
    public const string HostingStartupVariable = "ASPNETCORE_HOSTINGSTARTUPASSEMBLIES";
    public const string ModifiableAssembliesVariable = "DOTNET_MODIFIABLE_ASSEMBLIES";
    public const string BrowserToolsVariable = "__ASPNETCORE_BROWSER_TOOLS";

    /// <summary>
    /// The WebAssembly runtime's Hot Reload capabilities, as its agent reports them (<c>GetApplyUpdateCapabilities</c>):
    /// .NET 10's Mono is CoreCLR's set without <c>AddFieldRva</c>. A later runtime is taken to have at least as much
    /// until a test of it says more (<c>BrowserHotReloadDebugTests</c> compares this with a real page's answer).
    /// </summary>
    public static IReadOnlyList<string> WebAssemblyCapabilities(int major) => major < 10 ? [] :
    [
        "Baseline", "AddMethodToExistingType", "AddStaticFieldToExistingType", "NewTypeDefinition", "ChangeCustomAttributes",
        "AddInstanceFieldToExistingType", "GenericAddMethodToExistingType", "GenericUpdateMethod", "UpdateParameters", "GenericAddFieldToExistingType",
    ];

    /// <summary>
    /// The SDK's browser refresh middleware (<c>dotnet watch</c>'s, under <c>DotnetTools/dotnet-watch/…/hotreload/</c>
    /// in SDK 10), or null.
    /// </summary>
    public static string? FindMiddleware(string? sdkFolder)
    {
        var tools = sdkFolder is null ? null : Path.Combine(sdkFolder, "DotnetTools", "dotnet-watch");
        if (tools is null || !Directory.Exists(tools))
        {
            return null;
        }

        try
        {
            return Directory.EnumerateFiles(tools, MiddlewareAssembly + ".dll", SearchOption.AllDirectories)
                .FirstOrDefault(path => path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Contains("hotreload", StringComparer.Ordinal));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Why edits to a Blazor Web App's WebAssembly components can't reach the browser while it is debugged, or null when
    /// they can: its clients target .NET 10 or later (whose runtime has the agent) and the solution's SDK is 10 with the
    /// middleware. SDK 11's middleware only forwards to a "browser tools provider" that <c>dotnet watch</c> hosts and
    /// that serves the scripts itself, a protocol of its own (TODO(issue): support it once SDK 11 is out of preview).
    /// </summary>
    /// <param name="clientFrameworks">Each WebAssembly client's target framework (<c>net10.0</c>).</param>
    /// <param name="sdkVersion">The solution's SDK version, or null when it couldn't be read.</param>
    /// <param name="middleware">The SDK's middleware (<see cref="FindMiddleware"/>), or null.</param>
    public static string? UnsupportedReason(IReadOnlyList<string?> clientFrameworks, string? sdkVersion, string? middleware)
    {
        if (clientFrameworks.Any(f => FrameworkMajor(f) is not >= 10))
        {
            return "its WebAssembly components target a .NET older than 10, whose runtime in the browser can't take edits from the debugger";
        }

        if (sdkVersion is null || !int.TryParse(sdkVersion.Split('.')[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major))
        {
            return "the solution's .NET SDK version couldn't be read";
        }

        if (major != 10)
        {
            return major < 10
                ? $"the solution uses the .NET SDK {sdkVersion}, and edits reach the browser with SDK 10"
                : $"the solution uses the .NET SDK {sdkVersion}, whose browser tools Codalus IDE doesn't support yet (it supports SDK 10's)";
        }

        return middleware is null ? $"the .NET SDK {sdkVersion} has no browser refresh middleware ({MiddlewareAssembly})" : null;
    }

    /// <summary>The major version of a target framework (<c>net10.0</c>, <c>net11.0-browser</c>), or null.</summary>
    public static int? FrameworkMajor(string? framework)
    {
        if (framework is null || !framework.StartsWith("net", StringComparison.OrdinalIgnoreCase) || framework.Length < 4 || !char.IsAsciiDigit(framework[3]))
        {
            return null;
        }

        var digits = framework[3..].TakeWhile(char.IsAsciiDigit).ToArray();
        return framework.Contains('.', StringComparison.Ordinal) && int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var major) ? major : null;
    }

    /// <summary>
    /// The server's environment for the browser channel: the middleware as a startup hook (which loads it) and a hosting
    /// startup, this server's address and key, and the variables that make it serve the WebAssembly runtime its Hot
    /// Reload settings.
    /// </summary>
    public static Dictionary<string, string> Environment(IReadOnlyDictionary<string, string> planned, string middleware, IReadOnlyDictionary<string, string> server)
    {
        var environment = new Dictionary<string, string>(planned, StringComparer.Ordinal);
        PrependStartupHook(environment, middleware);
        var hosting = planned.TryGetValue(HostingStartupVariable, out var fromPlan) ? fromPlan : System.Environment.GetEnvironmentVariable(HostingStartupVariable);
        environment[HostingStartupVariable] = string.IsNullOrEmpty(hosting) ? MiddlewareAssembly : hosting + ";" + MiddlewareAssembly;
        environment[ModifiableAssembliesVariable] = "debug";
        environment[BrowserToolsVariable] = "true";
        foreach (var (name, value) in server)
        {
            environment[name] = value;
        }

        return environment;
    }

    /// <summary>Puts <paramref name="hook"/> first in <c>DOTNET_STARTUP_HOOKS</c>, keeping the plan's (or the IDE's) others.</summary>
    public static void PrependStartupHook(Dictionary<string, string> environment, string hook)
    {
        var existing = environment.TryGetValue(StartupHooksVariable, out var planned) ? planned : System.Environment.GetEnvironmentVariable(StartupHooksVariable);
        environment[StartupHooksVariable] = string.IsNullOrEmpty(existing) ? hook : hook + Path.PathSeparator + existing;
    }
}
