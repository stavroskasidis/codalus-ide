using System.Xml;
using Codalus.Shell.ViewModels;

namespace Codalus.Shell;

/// <summary>
/// WCF (M42, ADR 0066): the WCF Test Client document. WCF service references are part of the Service References page
/// (<see cref="OpenServiceReferences"/>).
/// </summary>
public sealed partial class ShellHost
{
    private const string WcfTestClientId = "wcf.testClient";

    private static readonly HttpClient s_wcfHttp = new() { Timeout = TimeSpan.FromSeconds(100) };

    /// <summary>Opens the WCF Test Client, loading <paramref name="address"/> (an address or a WSDL file) when given.</summary>
    public WcfTestClientViewModel OpenWcfTestClient(string? address = null)
    {
        WcfTestClientViewModel client;
        if (Layout.FindDocument<SolutionManagementDocument>(WcfTestClientId) is { ViewModel: WcfTestClientViewModel existing } document)
        {
            client = existing;
            Layout.OpenDocument(document, focus: true);
        }
        else
        {
            client = new WcfTestClientViewModel(new Workbench.Wcf.WcfTestClient(s_wcfHttp), WcfTestClientActionAsync);
            Layout.OpenDocument(new SolutionManagementDocument(WcfTestClientId, "WCF Test Client", client), focus: true);
        }

        if (address is { Length: > 0 })
        {
            client.Address = address;
            _ = client.RunAsync("load");
        }

        return client;
    }

    private async Task WcfTestClientActionAsync(WcfTestClientViewModel client, string action)
    {
        try
        {
            switch (action)
            {
                case "browse":
                    if (Window is not null && await Window.PickFileAsync("Choose a WSDL document", "WSDL documents", ["*.wsdl", "*.xml"]).ConfigureAwait(true) is { } picked)
                    {
                        client.Address = picked;
                        await WcfTestClientActionAsync(client, "load").ConfigureAwait(true);
                    }

                    break;
                case "load":
                    client.SetMetadata(await client.Client.LoadAsync(client.Address, CancellationToken.None).ConfigureAwait(true));
                    break;
                case "invoke" when client.Selected is { } selected:
                    client.Status = null;
                    client.SetResponse(await client.Client.InvokeAsync(selected.Endpoint, selected.Operation, client.Request, CancellationToken.None).ConfigureAwait(true));
                    break;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or FormatException or XmlException or IOException or HttpRequestException or TaskCanceledException or UnauthorizedAccessException)
        {
            client.Error = ex.Message;
        }
    }
}
