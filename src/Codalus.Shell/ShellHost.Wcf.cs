using Codalus.Projects.Protocol;
using Codalus.Rpc;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Localization;
using Codalus.Workbench.Wcf;

namespace Codalus.Shell;

/// <summary>
/// WCF (M42, ADR 0069): Add Service Reference through the user's dotnet-svcutil (in the Service References document),
/// the running CoreWCF services' metadata, and the WCF Test Client document. The references are the build host's work;
/// the test client's is the workbench's (plain HTTP, no System.ServiceModel, so the shell stays Native AOT).
/// </summary>
public sealed partial class ShellHost
{
    public WcfReferenceService WcfReferences { get; private set; } = null!;

    /// <summary>dotnet-svcutil's state for a project, or why it couldn't be known.</summary>
    private async Task<WcfToolStatusResult> WcfToolStatusAsync(string project)
    {
        try
        {
            using var limit = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            return await WcfReferences.StatusAsync(project, limit.Token).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or JsonRpcRemoteException or JsonRpcConnectionLostException or OperationCanceledException)
        {
            return new WcfToolStatusResult(false, null, null, null, false, ex is OperationCanceledException ? "dotnet-svcutil didn't answer within 2 minutes." : ex.Message);
        }
    }

    /// <summary>Installs dotnet-svcutil as a local tool of the solution, with the user's consent.</summary>
    private async Task InstallSvcutilAsync(ServiceReferencesViewModel form)
    {
        var what = form.WcfStatus is { ManifestListsTool: true }
            ? Localizer.Get("msg.wcf.restoreTheNetTools", "Restore the .NET tools (dotnet tool restore)? dotnet-svcutil is in the tool manifest.")
            : Localizer.Get("msg.wcf.installDotnetSvcutilAsALocalTool", "Install dotnet-svcutil as a local tool of this solution? It is downloaded from your NuGet sources (it's MIT-licensed, from the dotnet/wcf repository).");
        if (!await Dialogs.ConfirmAsync(Localizer.Get("msg.wcf.installDotnetSvcutil", "Install dotnet-svcutil"), what, Localizer.Get("msg.webTools.install", "Install")).ConfigureAwait(true))
        {
            return;
        }

        try
        {
            form.WcfStatus = await WcfReferences.InstallAsync(form.Project, CancellationToken.None).ConfigureAwait(true);
            form.Result = form.NeedsWcfTool ? null : "dotnet-svcutil is ready.";
            if (form.NeedsWcfTool)
            {
                form.Error = form.WcfStatus.Message ?? "dotnet-svcutil still can't run; see the Service References output.";
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or JsonRpcRemoteException or JsonRpcConnectionLostException)
        {
            form.Error = ex.Message;
        }
    }

    /// <summary>
    /// The metadata addresses of the CoreWCF services that run: programs started with Run (their output) and, while
    /// debugging, the solution's CoreWCF projects with the debugged program's output.
    /// </summary>
    public async Task<IReadOnlyList<DiscoveredService>> DiscoverWcfServicesAsync()
    {
        var programs = Runs.Running.Where(p => !p.HasExited).Select(p => (p.Project, p.Channel?.GetText() ?? string.Empty)).ToList();
        if (Debugger.IsActive && DebugLog.GetText() is { Length: > 0 } output)
        {
            foreach (var project in Solutions.Projects.Keys.Where(WcfServiceDiscovery.IsCoreWcfProject))
            {
                programs.Add((project, output));
            }
        }

        using var client = new WcfTestClient();
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            return await Task.Run(() => client.DiscoverAsync(programs, limit.Token), limit.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return [];
        }
    }

    /// <summary>The WCF Test Client, on <paramref name="address"/> (a metadata URL or a WSDL file) when given.</summary>
    public WcfTestClientViewModel OpenWcfTestClient(string? address = null)
    {
        if (Layout.FindDocument<SolutionManagementDocument>("wcfTestClient") is { ViewModel: WcfTestClientViewModel existing } document)
        {
            Layout.OpenDocument(document, focus: true);
            if (address is not null && address != existing.Address)
            {
                existing.Address = address;
                _ = existing.LoadAsync();
            }

            return existing;
        }

        var viewModel = new WcfTestClientViewModel(new WcfTestClient(), address, DiscoverWcfServicesAsync, BrowseWsdlAsync) { Font = EditorFont };
        Layout.OpenDocument(new SolutionManagementDocument("wcfTestClient", "WCF Test Client", viewModel), focus: true);
        if (address is not null)
        {
            _ = viewModel.LoadAsync();
        }

        return viewModel;
    }

    private async Task<string?> BrowseWsdlAsync() =>
        Window is null ? null : await Window.PickFileAsync(Localizer.Get("msg.wcf.pickWsdl", "Choose a WSDL file"), Localizer.Get("msg.wcf.wsdlDocuments", "WSDL documents"), ["*.wsdl", "*.xml"]).ConfigureAwait(true);
}
