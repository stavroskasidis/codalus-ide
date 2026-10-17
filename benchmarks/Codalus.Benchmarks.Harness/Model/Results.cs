using System.Text.Json;
using System.Text.Json.Serialization;

namespace Codalus.Benchmarks.Harness.Model;

/// <summary>
/// One result file: one target × one scenario on one machine. Metrics hold every sample so that
/// reports can be recomputed; <see cref="Summary"/> is derived from them.
/// </summary>
public sealed record ScenarioResult
{
    public required string Target { get; init; }

    public required string Scenario { get; init; }

    public required MachineInfo Machine { get; init; }

    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>"instrumented" (in-app events), "black-box" (OS-level probes) or "static".</summary>
    public required string Method { get; init; }

    public Dictionary<string, List<double>> Samples { get; init; } = [];

    public Dictionary<string, Distribution> Summary { get; init; } = [];

    public List<string> Notes { get; init; } = [];

    public int DiscardedRuns { get; set; }

    /// <summary>
    /// The CI run this result came from: the first folder under the baseline directory (<c>baseline/&lt;run id&gt;/</c>),
    /// set by <see cref="Gates.LoadAll"/>. Results of one run share their runner, so the reference application's numbers
    /// of the same run normalize latency (ADR 0002, amendment 2026-10-17). Not stored in the file.
    /// </summary>
    [JsonIgnore]
    public string? Run { get; init; }

    public void Add(string metric, double value)
    {
        if (!Samples.TryGetValue(metric, out var list))
        {
            Samples[metric] = list = [];
        }

        list.Add(value);
    }

    public void AddRange(string metric, IEnumerable<double> values)
    {
        foreach (var value in values)
        {
            Add(metric, value);
        }
    }

    public ScenarioResult Summarize()
    {
        Summary.Clear();
        foreach (var (metric, values) in Samples)
        {
            if (Distribution.Of(values) is { } distribution)
            {
                Summary[metric] = distribution;
            }
        }

        return this;
    }
}

/// <param name="L3CacheBytes">
/// The size of one last-level (L3) cache as the OS reports it, or null when unknown (results before 2026-10-17). The GC
/// sizes its generation-0 budget from it, so memory follows it across runner models (docs/benchmarks/net11-release-gate.md).
/// </param>
public sealed record MachineInfo(
    string Os,
    string OsVersion,
    string Architecture,
    string Cpu,
    int LogicalCores,
    long MemoryBytes,
    string? Gpu,
    double? DisplayRefreshHz,
    bool IsCi,
    long? L3CacheBytes = null);

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals)]
[JsonSerializable(typeof(ScenarioResult))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(string))]
internal sealed partial class ResultsJsonContext : JsonSerializerContext;
