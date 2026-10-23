using System.Globalization;

namespace Codalus.Workbench.Debugging;

/// <summary>How Start Debugging runs a Blazor Web App's server (#75, ADR 0040).</summary>
public enum HotReloadDebugMode
{
    /// <summary>Not a Blazor Web App's server: the choice doesn't apply.</summary>
    NotApplicable,

    /// <summary>The server runs under <c>dotnet watch</c>, netcoredbg attaches to it, and the page joins the session.</summary>
    Watched,

    /// <summary>The server runs under <c>dotnet watch</c> and only the page is debugged (a platform where the attach isn't reliable).</summary>
    PageOnly,

    /// <summary>netcoredbg launches the server, as before 0.6: no Hot Reload while debugging.</summary>
    Launched,
}

/// <summary>The choice, and why a Blazor Web App isn't <see cref="HotReloadDebugMode.Watched"/> (a line for the Debug Console).</summary>
public sealed record HotReloadDebugDecision(HotReloadDebugMode Mode, string? Reason = null);

/// <summary>
/// Whether Start Debugging gives a Blazor Web App Hot Reload (#75): its server under <c>dotnet watch</c> with the debugger
/// attached, so edits to its WebAssembly components apply in the browser while both are debugged. A pure decision.
/// </summary>
public static class HotReloadDebugChoice
{
    /// <summary>
    /// Platforms where attaching to the server <c>dotnet watch</c> starts isn't reliable, with why: the page is debugged
    /// alone there (with Hot Reload). Empty: the spike passed on Linux; Windows and macOS are proven by CI's shell tests.
    /// An entry gets a TODO(issue) and comes only from CI or spike evidence.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> UnreliablePlatforms = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>The reason this platform debugs the page alone (<see cref="UnreliablePlatforms"/>), or null.</summary>
    public static string? CurrentPlatformFallback =>
        OperatingSystem.IsWindows() ? UnreliablePlatforms.GetValueOrDefault("windows")
        : OperatingSystem.IsMacOS() ? UnreliablePlatforms.GetValueOrDefault("macos")
        : UnreliablePlatforms.GetValueOrDefault("linux");

    /// <param name="isWebAppServer">The project is a Blazor Web App's server (it hosts a WebAssembly client project).</param>
    /// <param name="settingOn">The <c>debug.hotReload</c> setting.</param>
    /// <param name="unsupportedReason">Why Hot Reload can't run the project at all (its framework), or null.</param>
    /// <param name="sdkVersion">The .NET SDK the solution resolves (<c>dotnet watch</c>'s), or null when it couldn't be read.</param>
    /// <param name="startupBreakpoint">An enabled breakpoint's file in the server's startup code (Program.cs, Startup.cs), or null.</param>
    /// <param name="platformFallback">Why this platform debugs the page alone (<see cref="CurrentPlatformFallback"/>), or null.</param>
    public static HotReloadDebugDecision Decide(bool isWebAppServer, bool settingOn, string? unsupportedReason, string? sdkVersion, string? startupBreakpoint, string? platformFallback = null)
    {
        if (!isWebAppServer)
        {
            return new(HotReloadDebugMode.NotApplicable);
        }

        if (!settingOn)
        {
            return new(HotReloadDebugMode.Launched);
        }

        if (unsupportedReason is not null)
        {
            return new(HotReloadDebugMode.Launched, unsupportedReason);
        }

        if (!IsValidatedSdk(sdkVersion))
        {
            // TODO(issue): widen once CI validates another band (SDK 9, 10.0.1xx-3xx): their dotnet watch words its
            // messages and treats the hosted app differently.
            return new(HotReloadDebugMode.Launched, sdkVersion is null
                ? "Hot Reload while debugging a Blazor Web App needs the .NET SDK 10.0.400 or later, and the solution's SDK version couldn't be read: debugging without Hot Reload."
                : $"Hot Reload while debugging a Blazor Web App needs the .NET SDK 10.0.400 or later; the solution uses {sdkVersion}: debugging without Hot Reload.");
        }

        // TODO(issue): debug the server's startup code in this mode too, once dotnet/sdk has a supported way to learn
        // the app's process and to add a startup hook beside its agent (`dotnet run -e` replaces DOTNET_STARTUP_HOOKS).
        if (startupBreakpoint is not null)
        {
            return new(HotReloadDebugMode.Launched, $"{Path.GetFileName(startupBreakpoint)} has breakpoints, so the server starts under the debugger to stop in its startup code: debugging without Hot Reload (remove them, or Restart, to debug with Hot Reload).");
        }

        return platformFallback is not null ? new(HotReloadDebugMode.PageOnly, platformFallback) : new(HotReloadDebugMode.Watched);
    }

    /// <summary>Whether <c>dotnet watch</c> of this SDK was validated for the mode: 10.0.400 or later (11 previews included).</summary>
    public static bool IsValidatedSdk(string? version)
    {
        if (version is null)
        {
            return false;
        }

        var core = version.Split('-', '+')[0].Split('.');
        if (core.Length < 3 || !int.TryParse(core[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major)
            || !int.TryParse(core[2], NumberStyles.None, CultureInfo.InvariantCulture, out var patch))
        {
            return false;
        }

        return major > 10 || major == 10 && patch >= 400;
    }

    /// <summary>
    /// The first enabled breakpoint's file in the server's startup code: Program.cs or Startup.cs in the project's folder
    /// (code that runs before the debugger can attach to the server <c>dotnet watch</c> starts).
    /// </summary>
    public static string? StartupBreakpoint(string projectPath, IEnumerable<BreakpointSpec> breakpoints)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(projectPath));
        return breakpoints
            .Where(b => b.Enabled)
            .Select(b => b.Path)
            .FirstOrDefault(path => Path.GetFileName(path) is { } name
                && (name.Equals("Program.cs", StringComparison.OrdinalIgnoreCase) || name.Equals("Startup.cs", StringComparison.OrdinalIgnoreCase))
                && string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), folder, OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase));
    }
}
