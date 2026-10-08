using System.Runtime.CompilerServices;
using Daedalus.Projects.Host;
using Daedalus.Rpc;
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

var instance = MSBuildLocator.QueryVisualStudioInstances(new VisualStudioInstanceQueryOptions
    {
        DiscoveryTypes = DiscoveryType.DotNetSdk,
        WorkingDirectory = Directory.GetCurrentDirectory(),
    })
    .OrderByDescending(i => i.Version)
    .FirstOrDefault();
if (instance is null)
{
    await Console.Error.WriteLineAsync("No .NET SDK was found. Install the .NET SDK to open solutions.").ConfigureAwait(false);
    return 3;
}

MSBuildLocator.RegisterInstance(instance);
return await RunAsync(stdin, stdout, instance.MSBuildPath).ConfigureAwait(false);

// Separate method: Microsoft.Build types must not be loaded before RegisterInstance.
[MethodImpl(MethodImplOptions.NoInlining)]
static async Task<int> RunAsync(Stream input, Stream output, string msbuildPath)
{
    await using var connection = new JsonRpcConnection(input, output);
    await using var server = new BuildHostServer(connection, msbuildPath);
    server.RegisterHandlers();
    connection.Start();
    await Task.WhenAny(connection.Completion, server.ExitRequested).ConfigureAwait(false);
    return 0;
}
