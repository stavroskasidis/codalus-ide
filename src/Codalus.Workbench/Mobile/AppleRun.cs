using Codalus.Projects.Protocol;
using Codalus.Workbench.Build;
using Codalus.Workbench.Projects;

namespace Codalus.Workbench.Mobile;

/// <summary>
/// Runs a mobile app on this Mac or an iOS simulator (M20c, ADR 0064), on macOS:
/// <list type="bullet">
/// <item>Mac Catalyst: the build host builds the <c>maccatalyst</c> framework, and the app bundle's executable runs as the program, its output in the Run output.</item>
/// <item>iOS simulator: the simulator boots, the build host builds the <c>ios</c> framework for the simulator, <c>simctl</c> installs the bundle and launches it attached to its console; Stop terminates it.</item>
/// </list>
/// </summary>
public sealed class AppleRunner(AppleDevices devices, BuildService builds, RunService runs, ProjectManagementService projects, SolutionService solutions)
{
    /// <summary>Builds and starts the app; null when the build failed (its errors are the build's).</summary>
    public async Task<RunningProgram?> RunAsync(ProjectInfo project, MobileDevice device, Action<string> log, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(log);
        if (!devices.Available)
        {
            throw new InvalidOperationException("iOS and Mac Catalyst apps build and run on a Mac.");
        }

        var framework = MobileProjects.FrameworkFor(project, device.Platform)
            ?? throw new InvalidOperationException($"{project.Name} has no {(device.Platform == "ios" ? "iOS" : "Mac Catalyst")} target framework.");
        if (device.Platform == "maccatalyst")
        {
            return await RunOnMacAsync(project, framework, log, cancellationToken).ConfigureAwait(false);
        }

        var udid = device.Serial ?? device.Avd ?? throw new InvalidOperationException($"{device.Label} has no simulator id.");
        if (!device.IsRunning)
        {
            log($"Booting {device.Label}…");
            await devices.BootAsync(udid, cancellationToken).ConfigureAwait(false);
        }

        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["TargetFramework"] = framework,
            ["RuntimeIdentifier"] = SimulatorRuntime(),
        };
        log($"Building {project.Name} ({framework}) for the simulator…");
        if (!(await builds.BuildAsync(BuildTargets.Build, project.Path, properties, cancellationToken).ConfigureAwait(false)).Succeeded)
        {
            return null;
        }

        var bundle = await AppBundleAsync(project, framework, cancellationToken).ConfigureAwait(false);
        var bundleId = await ApplicationIdAsync(project, cancellationToken).ConfigureAwait(false);
        log($"Installing {Path.GetFileName(bundle)} on {device.Label}…");
        await devices.InstallAsync(udid, bundle, cancellationToken).ConfigureAwait(false);
        var (file, arguments) = AppleDevices.LaunchCommand(udid, bundleId);
        var plan = new LaunchPlan(file, string.Empty, Path.GetDirectoryName(project.Path)!, new Dictionary<string, string>(), null) { ArgumentList = [.. arguments] };
        var program = runs.StartPlan(project.Path, project.Name, plan);
        program.Stopping = () => _ = devices.TerminateAsync(udid, bundleId, CancellationToken.None);
        log($"{bundleId} runs on {device.Label}.");
        return program;
    }

    private async Task<RunningProgram?> RunOnMacAsync(ProjectInfo project, string framework, Action<string> log, CancellationToken cancellationToken)
    {
        log($"Building {project.Name} ({framework})…");
        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["TargetFramework"] = framework };
        if (!(await builds.BuildAsync(BuildTargets.Build, project.Path, properties, cancellationToken).ConfigureAwait(false)).Succeeded)
        {
            return null;
        }

        var bundle = await AppBundleAsync(project, framework, cancellationToken).ConfigureAwait(false);
        var executable = BundleExecutable(bundle)
            ?? throw new InvalidOperationException($"{Path.GetFileName(bundle)} has no executable in Contents/MacOS.");
        var plan = new LaunchPlan(executable, string.Empty, Path.GetDirectoryName(project.Path)!, new Dictionary<string, string>(), null);
        return runs.StartPlan(project.Path, project.Name, plan);
    }

    /// <summary>The simulator's runtime identifier for this Mac's processor.</summary>
    internal static string SimulatorRuntime() =>
        System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "iossimulator-arm64" : "iossimulator-x64";

    /// <summary>The app bundle the build wrote next to the framework's output: <c>bin/Debug/net10.0-ios/iossimulator-arm64/X.app</c>.</summary>
    private async Task<string> AppBundleAsync(ProjectInfo project, string framework, CancellationToken cancellationToken)
    {
        var info = await solutions.GetRunInfoAsync(project.Path, cancellationToken, framework).ConfigureAwait(false);
        var folder = info.TargetPath is { Length: > 0 } target ? Path.GetDirectoryName(target) : null;
        return FindBundle(folder, Path.Combine(Path.GetDirectoryName(project.Path)!, "bin"), framework)
            ?? throw new InvalidOperationException($"The build of {project.Name} ({framework}) wrote no .app bundle.");
    }

    /// <summary>The newest <c>.app</c> in <paramref name="folder"/>, else under <paramref name="bin"/>'s folders of the framework.</summary>
    internal static string? FindBundle(string? folder, string bin, string framework)
    {
        IEnumerable<string> Bundles(string root, SearchOption option) =>
            Directory.Exists(root) ? Directory.EnumerateDirectories(root, "*.app", option) : [];

        var candidates = folder is not null ? Bundles(folder, SearchOption.TopDirectoryOnly).ToList() : [];
        if (candidates.Count == 0 && Directory.Exists(bin))
        {
            candidates = [.. Directory.EnumerateDirectories(bin, framework, SearchOption.AllDirectories).SelectMany(d => Bundles(d, SearchOption.AllDirectories))];
        }

        return candidates.OrderByDescending(Directory.GetLastWriteTimeUtc).FirstOrDefault();
    }

    /// <summary>A Mac app bundle's executable: <c>Contents/MacOS/&lt;name&gt;</c>, the bundle's name first.</summary>
    internal static string? BundleExecutable(string bundle)
    {
        var macos = Path.Combine(bundle, "Contents", "MacOS");
        if (!Directory.Exists(macos))
        {
            return null;
        }

        var named = Path.Combine(macos, Path.GetFileNameWithoutExtension(bundle));
        return File.Exists(named) ? named : Directory.EnumerateFiles(macos).FirstOrDefault();
    }

    /// <summary>The app's bundle id: <c>ApplicationId</c>, or the project's name lowercased as the workload's default.</summary>
    private async Task<string> ApplicationIdAsync(ProjectInfo project, CancellationToken cancellationToken)
    {
        var result = await projects.GetPropertiesAsync(project.Path, ["ApplicationId"], null, cancellationToken).ConfigureAwait(false);
        return result.Properties.FirstOrDefault(p => p.Name == "ApplicationId")?.Value is { Length: > 0 } id
            ? id
            : "com.companyname." + project.Name.ToLowerInvariant();
    }
}
