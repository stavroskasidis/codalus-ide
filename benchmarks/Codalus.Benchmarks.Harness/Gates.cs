using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Codalus.Benchmarks.Harness.Model;

namespace Codalus.Benchmarks.Harness;

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

    /// <summary>
    /// Latency and memory fail only against at least this many runs on the same runner CPU model; with fewer, a
    /// regression is a warning (ADR 0002, amendment 2026-10-17). Binary size needs one run, or an accepted value.
    /// </summary>
    public int MinComparableRuns { get; init; } = 3;

    /// <summary>
    /// Latency fails only against at least this many runs on the same runner CPU, enough to measure its spread (ADR 0002,
    /// amendment 2026-10-27); with fewer, it warns. Memory keeps <see cref="MinComparableRuns"/>.
    /// </summary>
    public int MinComparableLatencyRuns { get; init; } = 5;

    /// <summary>
    /// Latency fails only over the median of the runs on the same CPU by more than this many of their standard deviations
    /// (1.4826 × the median absolute deviation, which a single outlier doesn't move), as well as over its threshold
    /// (ADR 0002, amendment 2026-10-27): a hosted runner's latency varies 11 to 27% between runs of the same code on
    /// the same CPU, so a fixed +10% failed on noise. 0 turns the band off.
    /// </summary>
    public double LatencyNoiseSigmas { get; init; } = 3;

    /// <summary>
    /// Whether latency normalized by the reference application's time (fewer than <see cref="MinComparableRuns"/> runs on
    /// the same CPU) fails from <see cref="MinComparableRuns"/> such runs. Off: on CI the ratio of Codalus's startup to
    /// VS Code's ranges from 0.13 to 0.19 between runs, wider than the thresholds, so a normalized comparison warns.
    /// </summary>
    public bool FailNormalized { get; init; }

    /// <summary>
    /// Scenarios whose numbers are compared and reported, a regression as a warning, but never fail: a scenario CI hasn't
    /// measured has no budget yet (benchmarks/README.md), so it gates nothing until CI's measurements set its budgets
    /// (the everyday flows' UI-thread stalls, M56).
    /// </summary>
    public IReadOnlyList<string> InformativeScenarios { get; init; } = [];

    /// <summary>The reference application, measured in the same run: its numbers are reported but never fail.</summary>
    public string ReferenceTarget { get; init; } = "vscode";

    /// <summary>The reference application's scenario and metric that latency is divided by when runs are on different CPUs.</summary>
    public string ReferenceScenario { get; init; } = "cold-start";

    public string ReferenceMetric { get; init; } = "startup_selfreported_ms";
}

public sealed record BudgetFile(IReadOnlyList<Budget> Budgets, RegressionPolicy Regression);

/// <summary>
/// A value the maintainer accepted for one metric (<c>benchmarks/accepted.json</c>, ADR 0002, amendment 2026-10-17).
/// Baseline runs from before <see cref="Date"/> don't count for that metric; until newer runs exist, the regression gate
/// compares with <see cref="Value"/> instead of their median.
/// </summary>
public sealed record AcceptedValue(string Os, string Target, string Scenario, string Metric, double Value, DateOnly Date, string Reason)
{
    public string Stat { get; init; } = "p50";

    /// <summary>
    /// For latency and memory: the runner CPU the value was measured on. On another CPU the value isn't a reference,
    /// but the earlier runs still don't count.
    /// </summary>
    public string? Cpu { get; init; }

    /// <summary>Where the value was measured: the commit and the CI run.</summary>
    public string? Commit { get; init; }

    public string? Run { get; init; }
}

public sealed record AcceptedFile(IReadOnlyList<AcceptedValue> Accepted);

public sealed record GateFinding(string Os, string Target, string Scenario, string Metric, string Stat, double Value, double Limit, bool Failed, string Rule)
{
    /// <summary>Over the limit, but the budget is not enforced yet.</summary>
    public bool Pending { get; init; }

    /// <summary>Over the limit, but not enough comparable runs to fail, or the reference application's own number.</summary>
    public bool Warning { get; init; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true)]
[JsonSerializable(typeof(BudgetFile))]
[JsonSerializable(typeof(AcceptedFile))]
internal sealed partial class GatesJsonContext : JsonSerializerContext;

/// <summary><c>check</c> (absolute budgets) and <c>compare</c> (regressions against a baseline).</summary>
internal static class Gates
{
    public static BudgetFile Load(string path) =>
        JsonSerializer.Deserialize(File.ReadAllText(path), GatesJsonContext.Default.BudgetFile)
        ?? throw new InvalidDataException($"{path} is empty.");

    /// <summary>The accepted values, or none when the file doesn't exist.</summary>
    public static IReadOnlyList<AcceptedValue> LoadAccepted(string path) =>
        !File.Exists(path)
            ? []
            : (JsonSerializer.Deserialize(File.ReadAllText(path), GatesJsonContext.Default.AcceptedFile)
               ?? throw new InvalidDataException($"{path} is empty.")).Accepted;

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
    /// Compares the latest result per OS × target × scenario with the baseline: earlier runs on the same OS (CI runners
    /// only with CI runners), one result per run, the latest <see cref="RegressionPolicy.BaselineRuns"/> of them. Only
    /// metrics with a known kind (latency <c>*_ms</c>, memory <c>memory_*</c>, binary size) are gated; first-launch and
    /// count metrics are not. ADR 0002, amendment 2026-10-17:
    /// <list type="bullet">
    /// <item>An accepted value (<paramref name="accepted"/>) drops the runs from before its date for its metric, and is
    /// the reference until newer runs exist.</item>
    /// <item>Binary size is deterministic: the median of the runs (one is enough), or the accepted value; it fails.</item>
    /// <item>Latency and memory follow the runner's CPU, so they compare with runs on the same CPU model, and fail from
    /// <see cref="RegressionPolicy.MinComparableRuns"/> of them. With fewer, a regression is a warning: latency is then
    /// divided by the reference application's time in each run (<see cref="RegressionPolicy.ReferenceMetric"/>; it fails
    /// only with <see cref="RegressionPolicy.FailNormalized"/>), and memory, which has nothing to normalize by (the GC
    /// sizes its generation-0 budget from the L3 cache, which VS Code's memory doesn't follow), compares with the runs
    /// there are.</item>
    /// <item>Latency fails only from <see cref="RegressionPolicy.MinComparableLatencyRuns"/> runs on the same CPU, and
    /// only over its threshold and <see cref="RegressionPolicy.LatencyNoiseSigmas"/> standard deviations of those runs
    /// (amendment 2026-10-27): a hosted runner's latency varies more between runs than the threshold.</item>
    /// <item>The reference application's own numbers measure the runner and its latest release: reported, never failed.</item>
    /// <item>So are the numbers of the <see cref="RegressionPolicy.InformativeScenarios"/>, until they have budgets.</item>
    /// </list>
    /// </summary>
    public static IReadOnlyList<GateFinding> Compare(
        RegressionPolicy policy, IReadOnlyList<ScenarioResult> current, IReadOnlyList<ScenarioResult> baseline, IReadOnlyList<AcceptedValue>? accepted = null)
    {
        var findings = new List<GateFinding>();
        foreach (var result in current)
        {
            var runs = baseline
                .Where(b => b.Machine.Os == result.Machine.Os && b.Machine.IsCi == result.Machine.IsCi && b.Target == result.Target && b.Scenario == result.Scenario)
                .GroupBy(b => (object?)b.Run ?? b) // one result per run: a confirmation replaces the first measurement
                .Select(g => g.MaxBy(b => b.StartedAt)!)
                .OrderByDescending(b => b.StartedAt)
                .ToList();
            var isReference = result.Target == policy.ReferenceTarget;
            var informative = policy.InformativeScenarios.Contains(result.Scenario, StringComparer.Ordinal);
            var currentReference = isReference ? null : ReferenceTime(policy, current, result.Machine.Os, run: null, anyRun: true);

            foreach (var (metric, distribution) in result.Summary)
            {
                foreach (var (stat, threshold, floor, kind) in Rules(policy, result.Scenario, metric, distribution))
                {
                    if (Stat(distribution, stat) is not { } value)
                    {
                        continue;
                    }

                    var acceptance = accepted?
                        .Where(a => a.Os == result.Machine.Os && a.Target == result.Target && a.Scenario == result.Scenario && a.Metric == metric && a.Stat == stat)
                        .MaxBy(a => a.Date);
                    var history = runs
                        .Where(r => acceptance is null || DateOnly.FromDateTime(r.StartedAt.UtcDateTime) > acceptance.Date)
                        .Select(r => (Result: r, Value: r.Summary.TryGetValue(metric, out var d) ? Stat(d, stat) : null))
                        .Where(r => r.Value is not null)
                        .Select(r => (r.Result, Value: r.Value!.Value))
                        .ToList();
                    var acceptedReference = acceptance is not null && (kind == MetricKind.Size || acceptance.Cpu is null || SameCpu(acceptance.Cpu, result.Machine.Cpu))
                        ? acceptance
                        : null;

                    double reference;
                    bool gating;
                    string rule;
                    var band = 0.0; // latency's noise band over the reference, when wider than its threshold
                    if (kind == MetricKind.Size)
                    {
                        var previous = history.Take(policy.BaselineRuns).Select(h => h.Value).ToList();
                        if (previous.Count > 0)
                        {
                            (reference, gating) = (Median(previous), true);
                            rule = Describe(threshold, $"the median of {previous.Count} run(s) ({reference:F1})");
                        }
                        else if (acceptedReference is not null)
                        {
                            (reference, gating) = (acceptedReference.Value, true);
                            rule = Describe(threshold, $"the value accepted on {acceptedReference.Date:yyyy-MM-dd} ({reference:F1})");
                        }
                        else
                        {
                            continue;
                        }
                    }
                    else
                    {
                        var sameCpu = history.Where(h => SameCpu(h.Result.Machine.Cpu, result.Machine.Cpu)).Take(policy.BaselineRuns).Select(h => h.Value).ToList();
                        var ratios = isReference || currentReference is null
                            ? []
                            : history
                                .Select(h => (h.Value, Reference: ReferenceTime(policy, baseline, result.Machine.Os, h.Result.Run, anyRun: false)))
                                .Where(h => h.Reference is > 0)
                                .Take(policy.BaselineRuns)
                                .Select(h => h.Value / h.Reference!.Value)
                                .ToList();
                        var needed = kind == MetricKind.Latency ? Math.Max(policy.MinComparableRuns, policy.MinComparableLatencyRuns) : policy.MinComparableRuns;
                        if (sameCpu.Count >= policy.MinComparableRuns)
                        {
                            (reference, gating) = (Median(sameCpu), !isReference && sameCpu.Count >= needed);
                            rule = Describe(threshold, $"the median of {sameCpu.Count} run(s) on this CPU ({reference:F1})");
                            if (kind == MetricKind.Latency && policy.LatencyNoiseSigmas > 0)
                            {
                                var sigma = 1.4826 * Median([.. sameCpu.Select(v => Math.Abs(v - reference))]);
                                band = policy.LatencyNoiseSigmas * sigma;
                                if (band > reference * threshold)
                                {
                                    rule += string.Create(CultureInfo.InvariantCulture, $" and {policy.LatencyNoiseSigmas:0.#} σ of their spread (σ {sigma:F1}, +{band / reference:P0})");
                                }
                            }
                        }
                        else if (acceptedReference is not null)
                        {
                            (reference, gating) = (acceptedReference.Value, !isReference);
                            rule = Describe(threshold, $"the value accepted on {acceptedReference.Date:yyyy-MM-dd} ({reference:F1})");
                        }
                        else if (kind == MetricKind.Latency && ratios.Count > 0)
                        {
                            var ratio = Median(ratios);
                            (reference, gating) = (ratio * currentReference!.Value, policy.FailNormalized && ratios.Count >= policy.MinComparableRuns);
                            rule = Describe(threshold,
                                $"the median of {ratios.Count} run(s) normalized by {policy.ReferenceTarget} {policy.ReferenceMetric} (× {ratio:F3} of {currentReference.Value:F0} = {reference:F1})");
                        }
                        else if (history.Count > 0)
                        {
                            var previous = sameCpu.Count > 0 ? sameCpu : history.Take(policy.BaselineRuns).Select(h => h.Value).ToList();
                            (reference, gating) = (Median(previous), false);
                            rule = Describe(threshold, $"the median of {previous.Count} run(s){(sameCpu.Count > 0 ? " on this CPU" : " on other CPUs")} ({reference:F1})");
                        }
                        else
                        {
                            continue;
                        }

                        if (isReference)
                        {
                            rule += "; the reference application: never fails";
                        }
                        else if (!gating && !informative)
                        {
                            rule += string.Create(CultureInfo.InvariantCulture, $"; fewer than {needed} runs on this CPU: a warning only");
                        }
                    }

                    if (informative && !isReference)
                    {
                        gating = false;
                        rule += "; informative until it has budgets: never fails";
                    }

                    var limit = Math.Max(reference * (1 + threshold), reference + band);
                    var over = value > limit && value - reference > floor;
                    findings.Add(new GateFinding(result.Machine.Os, result.Target, result.Scenario, metric, stat, value, limit, over && gating, rule)
                    {
                        Warning = over && !gating,
                    });
                }
            }
        }

        return findings;
    }

    private static string Describe(double threshold, FormattableString over) =>
        string.Create(CultureInfo.InvariantCulture, $"+{threshold:P0} over ") + over.ToString(CultureInfo.InvariantCulture);

    /// <summary>The reference application's time (p50) in one run: <paramref name="run"/>'s, or the latest anywhere in <paramref name="results"/>.</summary>
    private static double? ReferenceTime(RegressionPolicy policy, IReadOnlyList<ScenarioResult> results, string os, string? run, bool anyRun)
    {
        if (!anyRun && run is null)
        {
            return null; // a result of no known run can't be paired
        }

        return results
            .Where(r => r.Machine.Os == os && r.Target == policy.ReferenceTarget && r.Scenario == policy.ReferenceScenario && (anyRun || r.Run == run))
            .OrderByDescending(r => r.StartedAt)
            .Select(r => r.Summary.TryGetValue(policy.ReferenceMetric, out var d) ? d.P50 : (double?)null)
            .FirstOrDefault(v => v is > 0);
    }

    /// <summary>The same CPU model: Windows pads the name with spaces, and some runners report it in capitals.</summary>
    internal static bool SameCpu(string a, string b) =>
        string.Equals(NormalizeCpu(a), NormalizeCpu(b), StringComparison.OrdinalIgnoreCase);

    private static string NormalizeCpu(string cpu) => string.Join(' ', cpu.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private enum MetricKind
    {
        Latency,
        Memory,
        Size,
    }

    private static IEnumerable<(string Stat, double Threshold, double Floor, MetricKind Kind)> Rules(RegressionPolicy policy, string scenario, string metric, Distribution distribution)
    {
        if (metric.EndsWith("_first", StringComparison.Ordinal))
        {
            yield break;
        }

        if (scenario == "binary-size" && metric.EndsWith("_mb", StringComparison.Ordinal))
        {
            yield return ("p50", policy.Size, policy.FloorSizeMb, MetricKind.Size);
        }
        else if (metric.StartsWith("memory_", StringComparison.Ordinal))
        {
            yield return ("p50", policy.Memory, policy.FloorMb, MetricKind.Memory);
        }
        else if (metric.EndsWith("_ms", StringComparison.Ordinal))
        {
            yield return ("p50", policy.LatencyP50, policy.FloorMs, MetricKind.Latency);
            if (distribution.N >= policy.MinSamplesForP99)
            {
                yield return ("p99", policy.LatencyP99, policy.FloorMs, MetricKind.Latency);
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

    public static string Format(string title, IReadOnlyList<GateFinding> findings, IEnumerable<string>? preamble = null)
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"## {title}\n\n");
        foreach (var line in preamble ?? [])
        {
            sb.Append(line).Append('\n');
        }

        if (findings.Count == 0)
        {
            sb.Append(preamble is null ? string.Empty : "\n").Append("Nothing to compare.\n");
            return sb.ToString();
        }

        var failed = findings.Count(f => f.Failed);
        var warnings = findings.Count(f => f.Warning);
        sb.Append(preamble is null ? string.Empty : "\n").Append(CultureInfo.InvariantCulture,
            $"{failed} failed, {warnings} warning(s), {findings.Count - failed - warnings} within the limits.\n\n");
        sb.Append("| | os | target | scenario | metric | value | limit | rule |\n|---|---|---|---|---|---|---|---|\n");
        foreach (var f in findings.OrderByDescending(f => f.Failed).ThenByDescending(f => f.Warning).ThenBy(f => f.Os).ThenBy(f => f.Target).ThenBy(f => f.Scenario).ThenBy(f => f.Metric))
        {
            sb.Append(CultureInfo.InvariantCulture,
                $"| {(f.Failed ? "FAIL" : f.Warning ? "warn" : f.Pending ? "over (pending)" : "ok")} | {f.Os} | {f.Target} | {f.Scenario} | {f.Metric} {f.Stat} | {f.Value:F1} | {f.Limit:F1} | {f.Rule} |\n");
        }

        return sb.ToString();
    }

    /// <summary>Every result file under <paramref name="dir"/> (all runs, not only the latest).</summary>
    /// <remarks>Each result's <see cref="ScenarioResult.Run"/> is its first folder under <paramref name="dir"/> (the run id).</remarks>
    public static IReadOnlyList<ScenarioResult> LoadAll(string dir) =>
        !Directory.Exists(dir)
            ? []
            : [.. Directory.EnumerateFiles(dir, "*.json", SearchOption.AllDirectories)
                .Select(path => TryRead(path, RunOf(dir, path)))
                .OfType<ScenarioResult>()];

    private static string? RunOf(string dir, string path)
    {
        var relative = Path.GetRelativePath(dir, path).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return relative.Length > 1 ? relative[0] : null;
    }

    private static ScenarioResult? TryRead(string path, string? run)
    {
        try
        {
            return JsonSerializer.Deserialize(File.ReadAllText(path), ResultsJsonContext.Default.ScenarioResult) is { } result
                ? result with { Run = run }
                : null;
        }
        catch (JsonException)
        {
            return null; // e.g. a summary or an unrelated JSON file
        }
    }
}
