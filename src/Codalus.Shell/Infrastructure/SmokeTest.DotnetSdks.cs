using Codalus.Plugins.Protocol;

namespace Codalus.Shell.Infrastructure;

/// <summary>
/// ADR 0111's smoke step: Tools → .NET SDKs in the shipped app, on the machine's own .NET folders and the samples' copy
/// of Microsoft's release metadata (set for the whole run, <see cref="RunAsync"/>). The window lists the SDK the run
/// itself uses, in the folder marked in use, and what's available; nothing is installed or removed. Its picture is the
/// website's <c>dotnet-sdks.png</c>.
/// </summary>
internal sealed partial class SmokeTest
{
    private async Task DotnetSdksStepAsync()
    {
        await shell.Workbench.Commands.ExecuteAsync("dotnetSdks.show", CancellationToken.None).ConfigureAwait(true);
        await WaitAsync(() => shell.DotnetSdksLoaded, "Tools → .NET SDKs opens and reads the .NET folders", 60).ConfigureAwait(true);
        var root = shell.DotnetSdksRoot();
        static ViewNodeDto? Find(ViewNodeDto node, string id) => node.Id == id ? node : node.Children?.Select(c => Find(c, id)).FirstOrDefault(c => c is not null);
        var sdks = Find(root, "sdks");
        Check(sdks?.Rows is { Length: > 0 }, "the SDKs this run uses are listed");
        Check(sdks!.Rows!.Any(r => r[3].EndsWith("(in use)", StringComparison.Ordinal)), "the folder the IDE uses is marked");
        Check(Find(root, "available")?.Rows is not null, "the release metadata gives what's available");
        Check(root.Children!.Any(c => c.Text?.Contains("release information of", StringComparison.Ordinal) == true), "the window says when the release information is from");
        await RenderedAsync(window).ConfigureAwait(true);
        Screenshot("dotnet-sdks.png");
    }
}
