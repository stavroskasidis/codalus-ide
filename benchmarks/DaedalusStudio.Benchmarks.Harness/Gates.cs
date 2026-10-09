using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DaedalusStudio.Benchmarks.Harness.Model;

namespace DaedalusStudio.Benchmarks.Harness;

/// <summary>An absolute budget from ADR 0002, checked on reference hardware.</summary>
public sealed record Budget(string Target, string Scenario, string Metric, string Stat, double Max)
{
    /// <summary>Only on this OS (<c>windows</c>, <c>linux</c>, <c>macos</c>), e.g. for black-box metrics.</summary>
    public string? Os { get; init; }

    /// <summary>Where the number comes from, e.g. "ADR 0002 #1".</summary>
    public string? Source { get; init; }

    /// <summary>Why the budget is reported but not enforced yet (e.g. "M3: the editor moves into the shell").</summary>
    public string? Pending { get; init; }
}

/// <summary>Relative regression gates (ADR 0002, "Enforcement in CI").</summary>
public sealed record RegressionPolicy
{
    public double LatencyP50 { get; init; } = 0.10;

    public double LatencyP99 { get; init; } = 0.20;

    public double Memory { get; init; } = 0.10;

    public double Size { get; init; } = 0.05;

    /// <summary>p99 of fewer samples is just the maximum; it is only compared from this many samples.</summary>
    public int MinSamplesForP99 { get; init; } = 20;

    /// <summary>Changes smaller than these absolute amounts are noise, whatever the percentage.</summary>
    public double FloorMs { get; init; } = 5;

    public double FloorMb { get; init; } = 5;

    public double FloorSizeMb { get; init; } = 0.1;

    /// <summary>The baseline is the median of at most this many previous runs.</summary>
    public int BaselineRuns { get; init; } = 10;
}

public sealed record BudgetFile(IReadOnlyList<Budget> Budgets, RegressionPolicy Regression);

public sealed record GateFinding(string Os, string Target, string Scenario, string Metric, string Stat, double Value, double Limit, bool Failed, string Rule)
{
    /// <summary>Over the limit, but the budget is not enforced yet.</summary>
    public bool Pending { get; init; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true)]
[JsonSerializable(typeof(BudgetFile))]
internal sealed partial class GatesJsonContext : JsonSerializerContext;

/// <summary><c>check</c> (absolute budgets) and <c>compare</c> (regressions against a baseline).</summary>
internal static class Gates
{
    public static BudgetFile Load(string path) =>
        JsonSerializer.Deserialize(File.ReadAllText(path), GatesJsonContext.Default.BudgetFile)
        ?? throw new InvalidDataException($"{path} is empty.");

    public static IReadOnlyList<GateFinding> Check(BudgetFile budgets, IReadOnlyList<ScenarioResult> results)
    {
        var findings = new List<GateFinding>();
        foreach (var budget in budgets.Budgets)
        {
            foreach (var result in results.Where(r => r.Target == budget.Target && r.Scenario == budget.Scenario
                                                      && (budget.Os is null || string.Equals(r.Machine.Os, budget.Os, StringComparison.OrdinalIgnoreCase))))
            {
                if (result.Summary.TryGetValue(budget.Metric, out var distribution) && Stat(distribution, budget.Stat) is { } value)
                {
                    var over = value > budget.Max;
                    findings.Add(new GateFinding(result.Machine.Os, result.Target, result.Scenario, budget.Metric, budget.Stat, value, budget.Max,
                        over && budget.Pending is null, budget.Pending is null ? budget.Source ?? "budget" : $"{budget.Source} (pending: {budget.Pending})")
                    {
                        Pending = over && budget.Pending is not null,
                    });
                }
            }
        }

        return findings;
    }

    /// <summary>
    /// Compares the latest result per OS × target × scenario with the baseline (same OS, and CI runners only with
    /// CI runners): the median, per statistic, of
    /// the latest <see cref="RegressionPolicy.BaselineRuns"/> baseline results. Only metrics with a known kind
    /// (latency <c>*_ms</c>, memory <c>memory_*</c>, binary size) are gated; first-launch and count metrics are not.
    /// </summary>
    public static IReadOnlyList<GateFinding> Compare(RegressionPolicy policy, IReadOnlyList<ScenarioResult> current, IReadOnlyList<ScenarioResult> baseline)
    {
        var findings = new List<GateFinding>();
        foreach (var result in current)
        {
            var history = baseline
                .Where(b => b.Machine.Os == result.Machine.Os && b.Machine.IsCi == result.Machine.IsCi && b.Target == result.Target && b.Scenario == result.Scenario)
                .OrderByDescending(b => b.StartedAt)
                .Take(policy.BaselineRuns)
                .ToList();
            if (history.Count == 0)
            {
                continue;
            }

            foreach (var (metric, distribution) in result.Summary)
            {
                foreach (var (stat, threshold, floor) in Rules(policy, result.Scenario, metric, distribution))
                {
                    var previous = history.Select(h => h.Summary.TryGetValue(metric, out var d) ? Stat(d, stat) : null).OfType<double>().ToList();
                    if (previous.Count == 0 || Stat(distribution, stat) is not { } value)
                    {
                        continue;
                    }

                    var reference = Median(previous);
                    var limit = reference * (1 + threshold);
                    var failed = value > limit && value - reference > floor;
                    findings.Add(new GateFinding(result.Machine.Os, result.Target, result.Scenario, metric, stat, value, limit, failed,
                        string.Create(CultureInfo.InvariantCulture, $"+{threshold:P0} over the median of {previous.Count} run(s) ({reference:F1})")));
                }
            }
        }

        return findings;
    }

    private static IEnumerable<(string Stat, double Threshold, double Floor)> Rules(RegressionPolicy policy, string scenario, string metric, Distribution distribution)
    {
        if (metric.EndsWith("_first", StringComparison.Ordinal))
        {
            yield break;
        }

        if (scenario == "binary-size" && metric.EndsWith("_mb", StringComparison.Ordinal))
        {
            yield return ("p50", policy.Size, policy.FloorSizeMb);
        }
        else if (metric.StartsWith("memory_", StringComparison.Ordinal))
        {
            yield return ("p50", policy.Memory, policy.FloorMb);
        }
        else if (metric.EndsWith("_ms", StringComparison.Ordinal))
        {
            yield return ("p50", policy.LatencyP50, policy.FloorMs);
            if (distribution.N >= policy.MinSamplesForP99)
            {
                yield return ("p99", policy.LatencyP99, policy.FloorMs);
            }
        }
    }

    private static double? Stat(Distribution distribution, string stat) => stat switch
    {
        "p50" => distribution.P50,
        "p95" => distribution.P95,
        "p99" => distribution.P99,
        "max" => distribution.Max,
        "mean" => distribution.Mean,
        _ => null,
    };

    private static double Median(List<double> values)
    {
        values.Sort();
        var middle = values.Count / 2;
        return values.Count % 2 == 1 ? values[middle] : (values[middle - 1] + values[middle]) / 2;
    }

    public static string Format(string title, IReadOnlyList<GateFinding> findings)
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"## {title}\n\n");
        if (findings.Count == 0)
        {
            sb.Append("Nothing to compare.\n");
            return sb.ToString();
        }

        sb.Append("| | os | target | scenario | metric | value | limit | rule |\n|---|---|---|---|---|---|---|---|\n");
        foreach (var f in findings.OrderByDescending(f => f.Failed).ThenBy(f => f.Os).ThenBy(f => f.Target).ThenBy(f => f.Scenario).ThenBy(f => f.Metric))
        {
            sb.Append(CultureInfo.InvariantCulture,
                $"| {(f.Failed ? "FAIL" : f.Pending ? "over (pending)" : "ok")} | {f.Os} | {f.Target} | {f.Scenario} | {f.Metric} {f.Stat} | {f.Value:F1} | {f.Limit:F1} | {f.Rule} |\n");
        }

        return sb.ToString();
    }

    /// <summary>Every result file under <paramref name="dir"/> (all runs, not only the latest).</summary>
    public static IReadOnlyList<ScenarioResult> LoadAll(string dir) =>
        !Directory.Exists(dir)
            ? []
            : [.. Directory.EnumerateFiles(dir, "*.json", SearchOption.AllDirectories)
                .Select(TryRead)
                .OfType<ScenarioResult>()];

    private static ScenarioResult? TryRead(string path)
    {
        try
        {
            return JsonSerializer.Deserialize(File.ReadAllText(path), ResultsJsonContext.Default.ScenarioResult);
        }
        catch (JsonException)
        {
            return null; // e.g. a summary or an unrelated JSON file
        }
    }
}
