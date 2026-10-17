using System.Runtime.CompilerServices;
using Codalus.Projects.Host;
using Codalus.Rpc;
using Microsoft.Build.Locator;

// Usage: codalus-buildhost [--solution-dir <dir>]
// The solution directory decides which SDK is used (global.json), so the shell starts one host per solution.
var dirIndex = Array.IndexOf(args, "--solution-dir");
if (dirIndex >= 0 && dirIndex + 1 < args.Length && Directory.Exists(args[dirIndex + 1]))
{
    Directory.SetCurrentDirectory(args[dirIndex + 1]);
}

// stdout carries the protocol; MSBuild and SDK resolvers may write to Console, so that goes to stderr.
var stdin = Console.OpenStandardInput();
var stdout = Console.OpenStandardOutput();
Console.SetOut(Console.Error);

// The SDK `dotnet` resolves for the solution's folder (global.json included) comes first, then the others, newest
// first (ADR 0025). The shell starts the host on a runtime that can load it; if this one still can't, the newest SDK
// it can load is used, and the shell is told which SDK the solution asked for.
var globalJson = GlobalJsonSdk.Find(Directory.GetCurrentDirectory());
string? unresolved = null;
List<VisualStudioInstance> instances;
try
{
    instances = Query(Directory.GetCurrentDirectory());
}
catch (Exception ex) when (ex is not OutOfMemoryException)
{
    // A global.json that asks for an SDK that isn't installed: the locator throws, as dotnet fails there (M33). The
    // SDKs are listed from a folder without one instead, and the solution is told.
    unresolved = ex.Message.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
    try
    {
        instances = Query(AppContext.BaseDirectory);
    }
    catch (InvalidOperationException)
    {
        // No dotnet at all where the locator looks (DOTNET_ROOT, DOTNET_HOST_PATH, PATH): say so instead of crashing.
        await Console.Error.WriteLineAsync(unresolved is not null && globalJson is not null
            ? $"{unresolved} Install the SDK that {globalJson.Path} asks for, or change it (Help → Check Environment)."
            : "No .NET SDK was found: dotnet isn't in DOTNET_ROOT or on the PATH. Install the .NET SDK (Help → Check Environment) to open solutions.").ConfigureAwait(false);
        return 3;
    }
}

// Below the floor (M33, ADR 0030), an SDK's MSBuild doesn't run here: the newest SDK that does stands in, and the
// solution is told which SDK it asked for and why it isn't used.
var instance = instances.FirstOrDefault(i => SdkSelection.CanLoad(i.Version, Environment.Version) && SdkSelection.AboveFloor(i.Version));
if (instance is null)
{
    await Console.Error.WriteLineAsync(instances.Count == 0
        ? unresolved is not null && globalJson is not null
            ? $"{unresolved} Install the SDK that {globalJson.Path} asks for, or change it (Help → Check Environment)."
            : "No .NET SDK was found. Install the .NET SDK to open solutions."
        : !SdkSelection.AboveFloor(instances[0].Version) ? SdkSelection.BelowFloor(SdkName(instances[0]), GlobalJsonSdk.Find(Directory.GetCurrentDirectory())?.Path, null)
        : SdkSelection.Unloadable(instances[0].Version, null, Environment.Version)).ConfigureAwait(false);
    return 3;
}

// A global.json that asks for an SDK that isn't installed: dotnet fails there, and the locator offers the newest SDK
// (or throws, above).
var sdkWarning = globalJson is not null && (unresolved is not null || !globalJson.Accepts(SdkName(instances[0]))) ? SdkSelection.NotInstalled(globalJson, instance.Version)
    : unresolved is not null ? $"{unresolved} The build host uses the .NET SDK {instance.Version.ToString(3)} instead."
    : instance == instances[0] ? null
    : !SdkSelection.AboveFloor(instances[0].Version) ? SdkSelection.BelowFloor(SdkName(instances[0]), globalJson?.Path, instance.Version)
    : SdkSelection.Unloadable(instances[0].Version, instance.Version, Environment.Version);
if (sdkWarning is not null)
{
    await Console.Error.WriteLineAsync(sdkWarning).ConfigureAwait(false);
}

// The CLI that restores and runs tools in the solution's folder is the SDK global.json resolves, not necessarily the one loaded here.
Codalus.Projects.Host.Packages.DotnetProcess.SdkVersion = unresolved is null && instances.Count > 0 ? instances[0].Version : instance.Version;

// MSBuild before 17 builds in this process (ADR 0030), and its tasks start the SDK's own tools (csc on .NET Core 2.x or
// 3.x), which stop at start on a Linux distribution whose ICU is newer than the ones they know. They inherit this.
if (OperatingSystem.IsLinux() && instance.Version.Major < 5 && Codalus.Projects.Host.Packages.DotnetProcess.IcuMajor is { } icu
    && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CLR_ICU_VERSION_OVERRIDE"))
    && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DOTNET_SYSTEM_GLOBALIZATION_INVARIANT")))
{
    Environment.SetEnvironmentVariable("CLR_ICU_VERSION_OVERRIDE", icu.ToString(System.Globalization.CultureInfo.InvariantCulture));
}

WindowsDesktop.EnableWindowsTargeting();
NetFramework.UseReferenceAssemblies(); // non-SDK-style .NET Framework projects (M43a)
MSBuildLocator.RegisterInstance(instance);
return await RunAsync(stdin, stdout, instance.MSBuildPath, sdkWarning).ConfigureAwait(false);

static List<VisualStudioInstance> Query(string directory) =>
    MSBuildLocator.QueryVisualStudioInstances(new VisualStudioInstanceQueryOptions
        {
            DiscoveryTypes = DiscoveryType.DotNetSdk,
            WorkingDirectory = directory,
            AllowAllRuntimeVersions = true,
        })
        .ToList();

// The SDK's full version, from its folder (VisualStudioInstance.Version drops a prerelease label).
static string SdkName(VisualStudioInstance instance) => Path.GetFileName(Path.TrimEndingDirectorySeparator(instance.MSBuildPath));

// Separate method: Microsoft.Build types must not be loaded before RegisterInstance.
[MethodImpl(MethodImplOptions.NoInlining)]
static async Task<int> RunAsync(Stream input, Stream output, string msbuildPath, string? sdkWarning)
{
    // Before any project collection reads the environment (M43a): where the reference assemblies packages are.
    Codalus.Projects.Host.Packages.NuGetClient.PublishGlobalPackagesFolder(Directory.GetCurrentDirectory());
    await using var connection = new JsonRpcConnection(input, output);
    await using var server = new BuildHostServer(connection, msbuildPath) { SdkWarning = sdkWarning };
    server.RegisterHandlers();
    connection.Start();
    await Task.WhenAny(connection.Completion, server.ExitRequested).ConfigureAwait(false);
    return 0;
}
