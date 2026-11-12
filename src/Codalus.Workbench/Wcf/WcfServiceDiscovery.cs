using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Codalus.Workbench.Wcf;

/// <summary>A running service's metadata address, and the project it runs from.</summary>
public sealed record DiscoveredService(string Project, string MetadataUrl)
{
    public string Label => $"{Path.GetFileNameWithoutExtension(Project)} — {MetadataUrl}";
}

/// <summary>
/// Finds the metadata of running CoreWCF services (M42, ADR 0069): the endpoints a CoreWCF project adds
/// (<c>AddServiceEndpoint&lt;TService, TContract&gt;(binding, "/Service.svc")</c>, read from its sources), at the addresses
/// the running program listens on ("Now listening on: …"), asked for <c>?wsdl</c>. Only loopback addresses are asked.
/// </summary>
public static partial class WcfServiceDiscovery
{
    /// <summary>A project that references CoreWCF (<c>CoreWCF.Primitives</c>, <c>CoreWCF.Http</c>…).</summary>
    public static bool IsCoreWcfProject(string projectFile)
    {
        try
        {
            return File.Exists(projectFile) && CoreWcfReference().IsMatch(File.ReadAllText(projectFile));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>The relative endpoint addresses a project's sources add, in order, without duplicates.</summary>
    public static IReadOnlyList<string> EndpointPaths(string projectDirectory)
    {
        var paths = new List<string>();
        foreach (var file in SourceFiles(projectDirectory))
        {
            string text;
            try
            {
                text = File.ReadAllText(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (Match match in EndpointCall().Matches(text))
            {
                var path = match.Groups["path"].Value;
                if (!paths.Contains(path, StringComparer.Ordinal))
                {
                    paths.Add(path);
                }
            }
        }

        return paths;
    }

    private static IEnumerable<string> SourceFiles(string directory)
    {
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 8 };
        return Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, "*.cs", options).Where(f =>
            {
                var relative = Path.GetRelativePath(directory, f).Replace('\\', '/');
                return !relative.StartsWith("bin/", StringComparison.OrdinalIgnoreCase) && !relative.StartsWith("obj/", StringComparison.OrdinalIgnoreCase);
            }).Take(2000)
            : [];
    }

    /// <summary>
    /// The listening addresses in a program's output (<c>Now listening on: http://[::]:5280</c>), with wildcard hosts
    /// made <c>localhost</c>; loopback ones only.
    /// </summary>
    public static IReadOnlyList<Uri> ListeningAddresses(string output)
    {
        var addresses = new List<Uri>();
        foreach (Match match in Listening().Matches(output))
        {
            var text = match.Groups[1].Value.Trim();
            text = WildcardHost().Replace(text, "${scheme}://localhost");
            if (Uri.TryCreate(text, UriKind.Absolute, out var uri) && (uri.IsLoopback || uri.Host == "localhost") && !addresses.Contains(uri))
            {
                addresses.Add(uri);
            }
        }

        // http first: the metadata of a plain BasicHttpBinding endpoint is on http.
        return [.. addresses.OrderBy(a => a.Scheme == "http" ? 0 : 1)];
    }

    /// <summary>
    /// The metadata addresses that answer with a WSDL document: each endpoint path under the first listening address
    /// where it does. <paramref name="fetch"/> reads a URL (with a short time limit). A request that ran out of that
    /// limit is not an address without metadata: something listened and was slow, as a CoreWCF service is with its
    /// first WSDL, which it builds when first asked (over 5 seconds on a busy machine, then 4.6 seconds for the same
    /// address: Windows CI, run 43204581362). Those addresses are asked again with <paramref name="fetchSlow"/> (a
    /// longer limit) once the quick pass is over, so the addresses that answer at once aren't kept waiting.
    /// </summary>
    public static async Task<IReadOnlyList<string>> ProbeAsync(
        IReadOnlyList<Uri> addresses,
        IReadOnlyList<string> paths,
        Func<Uri, CancellationToken, Task<string>> fetch,
        CancellationToken cancellationToken,
        Func<Uri, CancellationToken, Task<string>>? fetchSlow = null)
    {
        var found = new string?[paths.Count];
        var ranOut = new List<string>[paths.Count];
        for (var i = 0; i < paths.Count; i++)
        {
            ranOut[i] = [];
            foreach (var address in addresses)
            {
                var url = new Uri(address, paths[i].StartsWith('/') ? paths[i] : "/" + paths[i]).GetLeftPart(UriPartial.Path) + "?wsdl";
                var answer = await AskAsync(url, fetch, cancellationToken).ConfigureAwait(false);
                if (answer == Answer.Wsdl)
                {
                    found[i] = url;
                    break;
                }

                if (answer == Answer.RanOut)
                {
                    ranOut[i].Add(url);
                }
            }
        }

        if (fetchSlow is not null)
        {
            for (var i = 0; i < paths.Count; i++)
            {
                foreach (var url in found[i] is null ? ranOut[i] : [])
                {
                    if (await AskAsync(url, fetchSlow, cancellationToken).ConfigureAwait(false) == Answer.Wsdl)
                    {
                        found[i] = url;
                        break;
                    }
                }
            }
        }

        return [.. found.OfType<string>()];
    }

    private enum Answer
    {
        Wsdl,
        None,
        RanOut,
    }

    private static async Task<Answer> AskAsync(string url, Func<Uri, CancellationToken, Task<string>> fetch, CancellationToken cancellationToken)
    {
        try
        {
            var text = await fetch(new Uri(url), cancellationToken).ConfigureAwait(false);
            return XDocument.Parse(text).Root?.Name == XName.Get("definitions", WsdlReader.WsdlNamespace) ? Answer.Wsdl : Answer.None;
        }
        catch (OperationCanceledException)
        {
            // The request's own limit, unless the whole discovery was cancelled: then nothing is asked again.
            return cancellationToken.IsCancellationRequested ? Answer.None : Answer.RanOut;
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or XmlException or IOException)
        {
            // Not there, or no metadata (HttpGetEnabled off): try the next address.
            return Answer.None;
        }
    }

    [GeneratedRegex(@"<PackageReference\s+Include\s*=\s*""CoreWCF\.", RegexOptions.IgnoreCase)]
    private static partial Regex CoreWcfReference();

    /// <summary><c>AddServiceEndpoint&lt;S, C&gt;(binding, "/path")</c> or <c>AddServiceEndpoint(typeof(S), typeof(C), binding, "/path")</c>.</summary>
    [GeneratedRegex(@"AddServiceEndpoint\s*(?:<[^>]*>)?\s*\((?:[^;""]|""[^""]*"")*?,\s*(?:new\s+Uri\s*\(\s*)?""(?<path>[^""]+)""")]
    private static partial Regex EndpointCall();

    [GeneratedRegex(@"Now listening on:\s*(\S+)")]
    private static partial Regex Listening();

    [GeneratedRegex(@"^(?<scheme>https?)://(\[::\]|0\.0\.0\.0|\+|\*)")]
    private static partial Regex WildcardHost();
}
