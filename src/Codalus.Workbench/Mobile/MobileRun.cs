using Codalus.Projects.Protocol;
using Codalus.Workbench.Build;
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

/// <summary>
/// Runs a mobile app on an Android device or emulator (M20a, ADR 0064): the emulator starts when it isn't running,
/// the build host builds the device's framework and installs it (the workload's <c>Install</c> target with
/// <c>AdbTarget</c>), adb starts it, and its <c>logcat</c> is the program's output. Stop stops the app.
/// </summary>
public sealed class AndroidRunner(AndroidDevices devices, BuildService builds, RunService runs, ProjectManagementService projects)
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

        var serial = device.Serial ?? await devices.StartEmulatorAsync(device.Avd!, log, cancellationToken).ConfigureAwait(false);
        var properties = new Dictionary<string, string>(devices.Sdk.BuildProperties(), StringComparer.OrdinalIgnoreCase)
        {
            ["TargetFramework"] = framework,
            ["AdbTarget"] = "-s " + serial,
        };
        log($"Building {project.Name} ({framework}) and installing it on {device.Label}…");
        var result = await builds.BuildAsync(BuildTargets.Install, project.Path, properties, cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return null;
        }

        var applicationId = await ApplicationIdAsync(project, cancellationToken).ConfigureAwait(false);
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

    /// <summary>The app's package name: <c>ApplicationId</c>, or the project's name lowercased as the workload's default.</summary>
    private async Task<string> ApplicationIdAsync(ProjectInfo project, CancellationToken cancellationToken)
    {
        var result = await projects.GetPropertiesAsync(project.Path, ["ApplicationId"], null, cancellationToken).ConfigureAwait(false);
        return result.Properties.FirstOrDefault(p => p.Name == "ApplicationId")?.Value is { Length: > 0 } id
            ? id
            : "com.companyname." + project.Name.ToLowerInvariant();
    }
}
