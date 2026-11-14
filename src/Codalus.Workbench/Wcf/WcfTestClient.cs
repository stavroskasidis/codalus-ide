namespace Codalus.Workbench.Wcf;

/// <summary>
/// The WCF test client's engine (M42, ADR 0069): reads a service's metadata (a URL or a WSDL file, imports followed)
/// and sends requests built from it. Plain HTTP and <see cref="System.Xml.Linq"/>, in the shell's process.
/// </summary>
public sealed class WcfTestClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    public WcfTestClient(HttpClient? http = null)
    {
        _ownsHttp = http is null;
        _http = http ?? SoapClient.CreateHttpClient();
        Soap = new SoapClient(_http);
    }

    public SoapClient Soap { get; }

    /// <summary>Discovery's limit for each metadata request, which keeps a discovery over several addresses short.</summary>
    internal TimeSpan QuickLimit { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Its limit for asking again an address whose request ran out of <see cref="QuickLimit"/>: a service that listens
    /// and is slow with its first WSDL. Inside the 30 seconds the shell gives a whole discovery.
    /// </summary>
    internal TimeSpan SlowLimit { get; init; } = TimeSpan.FromSeconds(20);

    public Task<WsdlDocument> LoadAsync(string location, CancellationToken cancellationToken) =>
        WsdlReader.LoadAsync(location, Soap.FetchAsync, cancellationToken);

    /// <summary>Sends <paramref name="envelope"/> (the request's, or edited XML) to the request's endpoint.</summary>
    public Task<SoapResponse> SendAsync(SoapRequest request, string envelope, CancellationToken cancellationToken) =>
        Soap.SendAsync(request.Endpoint.Address, request.Endpoint.Soap, request.Operation.Action, envelope, cancellationToken);

    /// <summary>
    /// The metadata addresses of running CoreWCF services: each (project, program output) pair is a program that runs.
    /// </summary>
    public async Task<IReadOnlyList<DiscoveredService>> DiscoverAsync(IEnumerable<(string Project, string Output)> programs, CancellationToken cancellationToken)
    {
        var found = new List<DiscoveredService>();
        foreach (var (project, output) in programs)
        {
            if (!WcfServiceDiscovery.IsCoreWcfProject(project))
            {
                continue;
            }

            var addresses = WcfServiceDiscovery.ListeningAddresses(output);
            var paths = WcfServiceDiscovery.EndpointPaths(Path.GetDirectoryName(project)!);
            async Task<string> Fetch(Uri uri, TimeSpan time, CancellationToken ct)
            {
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
                limit.CancelAfter(time);
                return await Soap.FetchAsync(uri, limit.Token).ConfigureAwait(false);
            }

            var urls = await WcfServiceDiscovery.ProbeAsync(addresses, paths, (uri, ct) => Fetch(uri, QuickLimit, ct), cancellationToken, (uri, ct) => Fetch(uri, SlowLimit, ct)).ConfigureAwait(false);
            foreach (var url in urls)
            {
                found.Add(new DiscoveredService(project, url));
            }
        }

        return found;
    }

    public void Dispose()
    {
        if (_ownsHttp)
        {
            _http.Dispose();
        }
    }
}
