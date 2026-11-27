using Codalus.Shell.ViewModels;
using Codalus.Workbench.Sdks;

namespace Codalus.Shell.Infrastructure;

/// <summary>
/// ADR 0113's smoke step: Tools → .NET SDKs in the shipped app, on the machine's own .NET folders and the samples' copy
/// of Microsoft's release metadata (set for the whole run, <see cref="RunAsync"/>). The window lists the SDK the run
/// itself uses, under the folder marked as the one it uses, with its details and what's available; nothing is installed
/// or removed. Its picture is the website's <c>dotnet-sdks.png</c>.
/// </summary>
internal sealed partial class SmokeTest
{
    private async Task DotnetSdksStepAsync()
    {
        await shell.Workbench.Commands.ExecuteAsync("dotnetSdks.show", CancellationToken.None).ConfigureAwait(true);
        await WaitAsync(() => shell.DotnetSdksLoaded, "Tools → .NET SDKs opens and reads the .NET folders", 60).ConfigureAwait(true);
        var model = shell.DotnetSdksModel;
        model.Tab = DotnetSdksTab.Installed;
        Check(model.Rows.Any(r => r.Item is InstalledSdk), "the SDKs this run uses are listed");
        Check(model.Rows.FirstOrDefault(r => r.IsHeader) is { Installation.InUse: true }, "the folder the IDE uses comes first, marked");
        Check(model.Details?.Item is InstalledSdk && model.Details.Facts.Count > 0, "the selected SDK's details show");
        Check(model.ReleaseInfo.Length > 0, "the window says when the release information is from");
        model.Tab = DotnetSdksTab.Available;
        Check(!model.IsLoading, "the release metadata gives what's available");
        model.Tab = DotnetSdksTab.Installed;
        await model.PreviewTask.ConfigureAwait(true);
        await RenderedAsync(window).ConfigureAwait(true);
        Screenshot("dotnet-sdks.png");
    }
}
