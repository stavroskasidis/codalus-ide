using System.Runtime.CompilerServices;
using DaedalusStudio.Projects.Host;
using DaedalusStudio.Rpc;
using Microsoft.Build.Locator;

// Usage: daedalus-buildhost [--solution-dir <dir>]
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
var instances = MSBuildLocator.QueryVisualStudioInstances(new VisualStudioInstanceQueryOptions
    {
        DiscoveryTypes = DiscoveryType.DotNetSdk,
        WorkingDirectory = Directory.GetCurrentDirectory(),
        AllowAllRuntimeVersions = true,
    })
    .ToList();
var instance = instances.FirstOrDefault(i => SdkSelection.CanLoad(i.Version, Environment.Version));
if (instance is null)
{
    await Console.Error.WriteLineAsync(instances.Count == 0
        ? "No .NET SDK was found. Install the .NET SDK to open solutions."
        : SdkSelection.Unloadable(instances[0].Version, null, Environment.Version)).ConfigureAwait(false);
    return 3;
}

var sdkWarning = instance == instances[0] ? null : SdkSelection.Unloadable(instances[0].Version, instance.Version, Environment.Version);
if (sdkWarning is not null)
{
    await Console.Error.WriteLineAsync(sdkWarning).ConfigureAwait(false);
}

MSBuildLocator.RegisterInstance(instance);
return await RunAsync(stdin, stdout, instance.MSBuildPath, sdkWarning).ConfigureAwait(false);

// Separate method: Microsoft.Build types must not be loaded before RegisterInstance.
[MethodImpl(MethodImplOptions.NoInlining)]
static async Task<int> RunAsync(Stream input, Stream output, string msbuildPath, string? sdkWarning)
{
    await using var connection = new JsonRpcConnection(input, output);
    await using var server = new BuildHostServer(connection, msbuildPath) { SdkWarning = sdkWarning };
    server.RegisterHandlers();
    connection.Start();
    await Task.WhenAny(connection.Completion, server.ExitRequested).ConfigureAwait(false);
    return 0;
}
