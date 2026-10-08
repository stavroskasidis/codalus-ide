using System.Runtime.CompilerServices;
using Microsoft.Build.Locator;

namespace Daedalus.LanguageServices.Host;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        // MSBuild must be located before any type from Microsoft.Build is loaded,
        // so everything that touches MSBuild lives behind a non-inlined call.
        if (!MSBuildLocator.IsRegistered)
        {
            MSBuildLocator.RegisterDefaults();
        }

        return await RunAsync(args).ConfigureAwait(false);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<int> RunAsync(string[] args)
    {
        _ = args;
        using var stdin = Console.OpenStandardInput();
        using var stdout = Console.OpenStandardOutput();
        // stdout carries the protocol; diagnostics go to stderr only.
        Console.SetOut(Console.Error);

        await using var server = new HostServer(stdout, stdin);
        await server.RunAsync().ConfigureAwait(false);
        return 0;
    }
}
