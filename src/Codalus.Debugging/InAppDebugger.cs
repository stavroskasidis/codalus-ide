namespace Codalus.Debugging;

/// <summary>
/// The in-app debugger (ADR 0117): the Codalus debugger as a library in an app on an iPhone or iPad, where no other
/// process can be started. CoreCLR loads it as a profiler, Microsoft's documented way in for a debugger on mobile
/// platforms; it attaches to the app's own process, holds the app's start for the IDE, and serves DAP on a TCP port
/// the IDE reaches over the device's tunnel. The names the IDE's build and launch and the library agree on.
/// </summary>
public static class InAppDebugger
{
    /// <summary>The framework's name: <c>CodalusDebugger.framework</c>, in the app's <c>Frameworks</c>.</summary>
    public const string FrameworkName = "CodalusDebugger";

    /// <summary>The profiler class id CoreCLR asks the library's <c>DllGetClassObject</c> for (<c>DOTNET_PROFILER</c>).</summary>
    public const string ProfilerClassId = "{C0DA1005-1D3B-4C1E-9A60-0F1E5D0B0117}";

    /// <summary>The library's path in the app, as CoreCLR's loader takes it (<c>DOTNET_PROFILER_PATH</c>): known before the app is installed.</summary>
    public const string ProfilerPath = "@executable_path/Frameworks/" + FrameworkName + ".framework/" + FrameworkName;

    /// <summary>The TCP port the library serves DAP on, in the app's environment.</summary>
    public const string PortVariable = "CODALUS_DEBUG_PORT";

    /// <summary>
    /// How long, in seconds, the library holds the app's start for the IDE to connect and set its breakpoints (default
    /// <see cref="DefaultHoldSeconds"/>). iOS ends an app that hasn't finished launching in 20 seconds.
    /// </summary>
    public const string HoldVariable = "CODALUS_DEBUG_HOLD";

    /// <summary>The default hold: below iOS's launch watchdog (20 s), with room for the app's own start.</summary>
    public const int DefaultHoldSeconds = 12;

    /// <summary>The app's environment for the in-app debugger: the profiler's three settings, and the port.</summary>
    public static IReadOnlyList<KeyValuePair<string, string>> Environment(int port, int? holdSeconds = null) =>
    [
        new("DOTNET_ENABLE_PROFILING", "1"),
        new("DOTNET_PROFILER", ProfilerClassId),
        new("DOTNET_PROFILER_PATH", ProfilerPath),
        new(PortVariable, port.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        .. holdSeconds is { } hold ? [new KeyValuePair<string, string>(HoldVariable, hold.ToString(System.Globalization.CultureInfo.InvariantCulture))] : Array.Empty<KeyValuePair<string, string>>(),
    ];
}
