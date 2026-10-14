using System.Net;
using System.Net.Sockets;
using Codalus.Debugging;
using Codalus.Projects.Protocol;
using Codalus.Rpc;
using Codalus.Workbench.Build;
using Codalus.Workbench.Debugging;
using Codalus.Workbench.Projects;

namespace Codalus.Workbench.Mobile;

/// <summary>.NET MAUI and other mobile projects (M20, ADR 0064): their target frameworks per platform.</summary>
public static class MobileProjects
{
    /// <summary>The platforms of a target framework: <c>net10.0-android</c> → <c>android</c>; null for any other.</summary>
    public static string? PlatformOf(string targetFramework)
    {
        var dash = targetFramework.IndexOf('-', StringComparison.Ordinal);
        if (dash < 0)
        {
            return null;
        }

        var platform = targetFramework[(dash + 1)..].ToLowerInvariant();
        var digits = platform.IndexOfAny(['0', '1', '2', '3', '4', '5', '6', '7', '8', '9']);
        platform = digits > 0 ? platform[..digits] : platform;
        return platform is "android" or "ios" or "maccatalyst" ? platform : null;
    }

    /// <summary>Whether the project is an app for a mobile platform (an executable with an Android, iOS or Mac Catalyst framework).</summary>
    public static bool IsMobileApp(ProjectInfo project) =>
        project.OutputType is { } type && (type.Equals("Exe", StringComparison.OrdinalIgnoreCase) || type.Equals("WinExe", StringComparison.OrdinalIgnoreCase))
        && project.TargetFrameworks.Any(f => PlatformOf(f) is not null);

    /// <summary>The project's target framework for <paramref name="platform"/>, or null.</summary>
    public static string? FrameworkFor(ProjectInfo project, string platform) =>
        project.TargetFrameworks.FirstOrDefault(f => PlatformOf(f) == platform);
}

/// <summary>The Mono debug adapter (<c>codalus-monodbg</c>, M20b, ADR 0065).</summary>
public static class MonoDebugger
{
    public const string EnvironmentVariable = "CODALUS_MONODBG";

    /// <summary>Resolution order: <c>CODALUS_MONODBG</c> (an apphost or a .dll), then <c>monodbg/</c> next to the shell.</summary>
    public static DebugAdapterStartInfo? Resolve()
    {
        var candidates = new List<string>();
        if (Environment.GetEnvironmentVariable(EnvironmentVariable) is { Length: > 0 } fromEnv)
        {
            candidates.Add(fromEnv);
        }

        var exe = OperatingSystem.IsWindows() ? "codalus-monodbg.exe" : "codalus-monodbg";
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "monodbg", exe));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "monodbg", "codalus-monodbg.dll"));
        return candidates.FirstOrDefault(File.Exists) is { } path && ProcessLaunchInfo.FromPath(path) is var launch
            ? new DebugAdapterStartInfo(launch.FileName, launch.Arguments)
            : null;
    }
}

/// <summary>
/// Runs a mobile app on an Android device or emulator (M20a, ADR 0064): the emulator starts when it isn't running,
/// the build host builds the device's framework and installs it (the workload's <c>Install</c> target with
/// <c>AdbTarget</c>), adb starts it, and its <c>logcat</c> is the program's output. Stop stops the app.
/// </summary>
public sealed class AndroidRunner(AndroidDevices devices, BuildService builds, RunService runs, ProjectManagementService projects, SolutionService solutions)
{
    /// <summary>Builds, installs and starts the app; null when the build failed (its errors are the build's).</summary>
    public async Task<RunningProgram?> RunAsync(ProjectInfo project, MobileDevice device, Action<string> log, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(log);
        var framework = MobileProjects.FrameworkFor(project, "android")
            ?? throw new InvalidOperationException($"{project.Name} has no Android target framework.");
        if (devices.Adb is null)
        {
            throw new InvalidOperationException("The Android SDK wasn't found. Install it (Help → Check Environment), or set ANDROID_HOME to its folder.");
        }

        if (device.Problem is { } problem)
        {
            throw new InvalidOperationException(problem == "unauthorized"
                ? $"{device.Label} hasn't allowed USB debugging from this computer: accept the prompt on the device."
                : $"{device.Label} is {problem}.");
        }

        var installed = await InstallAsync(project, framework, device, log, cancellationToken).ConfigureAwait(false);
        if (installed is not var (serial, applicationId))
        {
            return null;
        }

        await devices.StopAsync(serial, applicationId, cancellationToken).ConfigureAwait(false);
        await devices.LaunchAsync(serial, applicationId, cancellationToken).ConfigureAwait(false);
        var pid = await devices.ProcessIdAsync(serial, applicationId, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"{applicationId} started on {device.Label} but exited at once: see the device's log (adb logcat).");
        log($"{applicationId} runs on {device.Label} (process {pid}).");

        var logcat = devices.Logcat(serial, pid);
        var plan = new LaunchPlan(logcat.FileName, string.Empty, Path.GetDirectoryName(project.Path)!, new Dictionary<string, string>(), null)
        {
            ArgumentList = [.. logcat.ArgumentList],
        };
        var program = runs.StartPlan(project.Path, project.Name, plan);
        program.Stopping = () => _ = devices.StopAsync(serial, applicationId, CancellationToken.None);
        return program;
    }

    /// <summary>Starts the emulator if needed, then builds the device's framework and installs it; null when the build failed.</summary>
    private async Task<(string Serial, string ApplicationId)?> InstallAsync(ProjectInfo project, string framework, MobileDevice device, Action<string> log, CancellationToken cancellationToken)
    {
        var serial = device.Serial ?? await devices.StartEmulatorAsync(device.Avd!, log, cancellationToken).ConfigureAwait(false);
        var properties = new Dictionary<string, string>(devices.Sdk.BuildProperties(), StringComparer.OrdinalIgnoreCase)
        {
            ["TargetFramework"] = framework,
            ["AdbTarget"] = "-s " + serial,
        };
        log($"Building {project.Name} ({framework}) and installing it on {device.Label}…");
        var result = await builds.BuildAsync(BuildTargets.Install, project.Path, properties, cancellationToken).ConfigureAwait(false);
        return result.Succeeded ? (serial, await ApplicationIdAsync(project, cancellationToken).ConfigureAwait(false)) : null;
    }

    /// <summary>
    /// Debugs the app on the device (M20b, ADR 0065): builds and installs it, forwards a free port, sets
    /// <c>debug.mono.connect</c> and marks it the debug app, starts it, and attaches the Mono adapter to its runtime's
    /// agent. The session's end stops the app and removes the forward, the property and the mark. False when the build failed.
    /// </summary>
    public async Task<bool> DebugAsync(ProjectInfo project, MobileDevice device, DebugService debugger, Action<string> log, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(debugger);
        ArgumentNullException.ThrowIfNull(log);
        var adapter = MonoDebugger.Resolve()
            ?? throw new InvalidOperationException("The Mono debug adapter wasn't found: it ships in monodbg/ next to Codalus IDE (or set " + MonoDebugger.EnvironmentVariable + ").");
        var framework = MobileProjects.FrameworkFor(project, "android")
            ?? throw new InvalidOperationException($"{project.Name} has no Android target framework.");
        if (devices.Adb is null)
        {
            throw new InvalidOperationException("The Android SDK wasn't found. Install it (Help → Check Environment), or set ANDROID_HOME to its folder.");
        }

        if (await InstallAsync(project, framework, device, log, cancellationToken).ConfigureAwait(false) is not var (serial, applicationId))
        {
            return false;
        }

        var port = FreePort();
        var portText = port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        await devices.StopAsync(serial, applicationId, cancellationToken).ConfigureAwait(false);
        await devices.AdbAsync(serial, ["forward", "tcp:" + portText, "tcp:" + portText], cancellationToken).ConfigureAwait(false);
        await devices.ShellAsync(serial, ["setprop", "debug.mono.connect", "port=" + portText], cancellationToken).ConfigureAwait(false);
        await devices.ShellAsync(serial, ["am", "set-debug-app", applicationId], cancellationToken).ConfigureAwait(false);

        async Task CleanUpAsync()
        {
            await devices.StopAsync(serial, applicationId, CancellationToken.None).ConfigureAwait(false);
            await devices.ShellAsync(serial, ["am", "clear-debug-app"], CancellationToken.None).ConfigureAwait(false);
            await devices.ShellAsync(serial, ["setprop", "debug.mono.connect", "''"], CancellationToken.None).ConfigureAwait(false);
            await devices.AdbAsync(serial, ["forward", "--remove", "tcp:" + portText], CancellationToken.None).ConfigureAwait(false);
        }

        try
        {
            // The app's runtime listens for the debugger for only two seconds as it starts: the adapter is already
            // trying to connect (every half second, through the forward) when the app starts.
            var output = await OutputFolderAsync(project, framework, cancellationToken).ConfigureAwait(false);
            log($"Starting {applicationId} on {device.Label} with the debugger (port {portText})…");
            var attaching = debugger.AttachMonoAsync(
                adapter,
                new MonoAttachArguments(port, Name: device.Label, Assemblies: output),
                applicationId,
                CleanUpAsync,
                cancellationToken);
            if (await Task.WhenAny(attaching, Task.Delay(TimeSpan.FromSeconds(2), cancellationToken)).ConfigureAwait(false) == attaching)
            {
                await attaching.ConfigureAwait(false); // the adapter didn't start: its error
            }

            await devices.LaunchAsync(serial, applicationId, cancellationToken).ConfigureAwait(false);
            await attaching.ConfigureAwait(false);
        }
        catch
        {
            await CleanUpAsync().ConfigureAwait(false);
            throw;
        }

        return true;
    }

    /// <summary>The build output of the device's framework: the app's assemblies and their PDBs, for the debugger's symbols.</summary>
    private async Task<string?> OutputFolderAsync(ProjectInfo project, string framework, CancellationToken cancellationToken)
    {
        var info = await solutions.GetRunInfoAsync(project.Path, cancellationToken, framework).ConfigureAwait(false);
        return info.TargetPath is { Length: > 0 } target ? Path.GetDirectoryName(target) : null;
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    /// <summary>The app's package name: <c>ApplicationId</c>, or the project's name lowercased as the workload's default.</summary>
    private async Task<string> ApplicationIdAsync(ProjectInfo project, CancellationToken cancellationToken)
    {
        var result = await projects.GetPropertiesAsync(project.Path, ["ApplicationId"], null, cancellationToken).ConfigureAwait(false);
        return result.Properties.FirstOrDefault(p => p.Name == "ApplicationId")?.Value is { Length: > 0 } id
            ? id
            : "com.companyname." + project.Name.ToLowerInvariant();
    }
}
