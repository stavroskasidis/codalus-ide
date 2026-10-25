using Codalus.Projects.Protocol;
using Codalus.Workbench.Output;
using Codalus.Workbench.Projects;

namespace Codalus.Workbench.Wcf;

/// <summary>
/// WCF service references (M42, ADR 0069), the classic IDE's "WCF Web Service Reference": the build host runs the
/// user's <c>dotnet-svcutil</c> (trusted solutions only) and keeps the reference in
/// <c>Connected Services/&lt;name&gt;/</c> with a <c>ConnectedService.json</c> as the classic IDE writes it. Its output goes
/// to the Service References channel. UI-agnostic; events arrive on any thread.
/// </summary>
public sealed class WcfReferenceService
{
    public const string OutputChannelId = "codalus.serviceReferences";

    /// <summary>The collection types the classic IDE offers, the first its default.</summary>
    public static readonly IReadOnlyList<string> CollectionTypes =
    [
        "System.Array",
        "System.Collections.Generic.List`1",
        "System.Collections.ObjectModel.Collection`1",
        "System.Collections.ObjectModel.ObservableCollection`1",
        "System.Collections.Generic.LinkedList`1",
        "System.Collections.ArrayList",
    ];

    /// <summary>The dictionary types the classic IDE offers, the first its default.</summary>
    public static readonly IReadOnlyList<string> DictionaryTypes =
    [
        "System.Collections.Generic.Dictionary`2",
        "System.Collections.Generic.SortedDictionary`2",
        "System.Collections.Generic.SortedList`2",
        "System.Collections.Hashtable",
        "System.Collections.SortedList",
        "System.Collections.Specialized.HybridDictionary",
        "System.Collections.Specialized.ListDictionary",
        "System.Collections.Specialized.OrderedDictionary",
    ];

    private static readonly TimeSpan s_runTimeout = TimeSpan.FromMinutes(10);
    private static WcfJsonContext Json => WcfJsonContext.Default;

    private readonly SolutionService _solutions;
    private readonly OutputService _output;

    public WcfReferenceService(SolutionService solutions, OutputService output)
    {
        _solutions = solutions;
        _output = output;
        solutions.WcfOutput += (_, o) => Channel.AppendLine(o.Level == "error" && !o.Line.StartsWith('>') ? "error: " + o.Line : o.Line);
    }

    public OutputChannel Channel => _output.GetOrCreate(OutputChannelId, "Service References");

    /// <summary>A reference was added, updated or removed in the project.</summary>
    public event EventHandler<string>? Changed;

    public Task<WcfToolStatusResult> StatusAsync(string project, CancellationToken cancellationToken) =>
        _solutions.RequestAsync(WcfReferenceMethods.Status, new WcfToolStatusParams(project), Json.WcfToolStatusParams, Json.WcfToolStatusResult, cancellationToken);

    /// <summary>Installs <c>dotnet-svcutil</c> as a local tool (or restores the manifest's tools).</summary>
    public Task<WcfToolStatusResult> InstallAsync(string project, CancellationToken cancellationToken) =>
        _solutions.RequestAsync(WcfReferenceMethods.Install, new WcfToolInstallParams(project), Json.WcfToolInstallParams, Json.WcfToolStatusResult, cancellationToken, s_runTimeout);

    public async Task<IReadOnlyList<WcfReferenceInfo>> ListAsync(string project, CancellationToken cancellationToken) =>
        (await _solutions.RequestAsync(WcfReferenceMethods.List, new WcfReferencesParams(project), Json.WcfReferencesParams, Json.WcfReferencesResult, cancellationToken).ConfigureAwait(false)).References;

    public async Task<WcfReferenceRunResult> AddAsync(WcfReferenceParams parameters, CancellationToken cancellationToken)
    {
        var result = await _solutions.RequestAsync(WcfReferenceMethods.Add, parameters, Json.WcfReferenceParams, Json.WcfReferenceRunResult, cancellationToken, s_runTimeout).ConfigureAwait(false);
        Changed?.Invoke(this, parameters.Project);
        return result;
    }

    /// <summary>Generates the reference again, with <paramref name="parameters"/>' options (a null input keeps its own).</summary>
    public async Task<WcfReferenceRunResult> UpdateAsync(WcfReferenceParams parameters, CancellationToken cancellationToken)
    {
        var result = await _solutions.RequestAsync(WcfReferenceMethods.Update, parameters, Json.WcfReferenceParams, Json.WcfReferenceRunResult, cancellationToken, s_runTimeout).ConfigureAwait(false);
        Changed?.Invoke(this, parameters.Project);
        return result;
    }

    public async Task<IReadOnlyList<WcfReferenceInfo>> RemoveAsync(string project, string name, CancellationToken cancellationToken)
    {
        var result = await _solutions.RequestAsync(WcfReferenceMethods.Remove, new WcfReferenceRemoveParams(project, name), Json.WcfReferenceRemoveParams, Json.WcfReferencesResult, cancellationToken).ConfigureAwait(false);
        Changed?.Invoke(this, project);
        return result.References;
    }

    /// <summary>The classic IDE's default name: <c>ServiceReference1</c>, or the next number free.</summary>
    public static string DefaultName(IEnumerable<string> existing)
    {
        var taken = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
        for (var i = 1; ; i++)
        {
            var name = "ServiceReference" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!taken.Contains(name))
            {
                return name;
            }
        }
    }
}
