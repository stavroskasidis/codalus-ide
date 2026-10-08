using System.Globalization;
using System.Text;
using System.Text.Json;
using Daedalus.Benchmarks.Harness.Model;

namespace Daedalus.Benchmarks.Harness;

/// <summary>Builds Markdown tables (used in the ADRs) from the latest result file per OS × target × scenario.</summary>
internal static class Report
{
    private static readonly string[] s_targetOrder = ["avalonia", "gpui", "vscode", "rider", "vs"];

    public static IReadOnlyList<ScenarioResult> LoadLatest(string resultsDir)
    {
        if (!Directory.Exists(resultsDir))
        {
            return [];
        }

        return Directory.EnumerateFiles(resultsDir, "*.json", SearchOption.AllDirectories)
            .Select(path => JsonSerializer.Deserialize(File.ReadAllText(path), ResultsJsonContext.Default.ScenarioResult))
            .OfType<ScenarioResult>()
            .GroupBy(r => (r.Machine.Os, r.Target, r.Scenario))
            .Select(g => g.OrderByDescending(r => r.StartedAt).First())
            .ToList();
    }

    public static string Markdown(IReadOnlyList<ScenarioResult> results)
    {
        var sb = new StringBuilder();
        foreach (var os in results.Select(r => r.Machine.Os).Distinct().Order())
        {
            var machine = results.First(r => r.Machine.Os == os).Machine;
            sb.Append(CultureInfo.InvariantCulture, $"## {os}\n\n");
            sb.Append(CultureInfo.InvariantCulture,
                $"Machine: {machine.Cpu}, {machine.LogicalCores} logical cores, {machine.MemoryBytes / 1073741824.0:F0} GB RAM, GPU {machine.Gpu ?? "n/a"}, display {machine.DisplayRefreshHz?.ToString(CultureInfo.InvariantCulture) ?? "n/a"} Hz, {machine.OsVersion}{(machine.IsCi ? " (CI runner)" : string.Empty)}.\n\n");

            foreach (var scenario in ScenarioRunner.Scenarios)
            {
                var rows = results.Where(r => r.Machine.Os == os && r.Scenario == scenario)
                    .OrderBy(r => Array.IndexOf(s_targetOrder, r.Target))
                    .ToList();
                if (rows.Count == 0)
                {
                    continue;
                }

                sb.Append(CultureInfo.InvariantCulture, $"### {scenario}\n\n");
                var metrics = rows.SelectMany(r => r.Summary.Keys).Distinct().Order(StringComparer.Ordinal).ToList();
                sb.Append("| metric | ").Append(string.Join(" | ", rows.Select(r => r.Target))).Append(" |\n");
                sb.Append("|---|").Append(string.Concat(rows.Select(_ => "---|"))).Append('\n');
                foreach (var metric in metrics)
                {
                    sb.Append("| ").Append(metric).Append(" | ");
                    sb.Append(string.Join(" | ", rows.Select(r => Cell(r, metric))));
                    sb.Append(" |\n");
                }

                var notes = rows.SelectMany(r => r.Notes.Select(n => $"{r.Target}: {n}"))
                    .Concat(rows.Where(r => r.DiscardedRuns > 0).Select(r => $"{r.Target}: {r.DiscardedRuns} run(s) discarded"))
                    .Distinct()
                    .ToList();
                if (notes.Count > 0)
                {
                    sb.Append('\n');
                    foreach (var note in notes)
                    {
                        sb.Append("- ").Append(note).Append('\n');
                    }
                }

                sb.Append('\n');
            }
        }

        sb.Append("Cells: p50 / p99 / max (n). Single-sample metrics show the value only. Times in ms, memory in MB.\n");
        return sb.ToString();
    }

    private static string Cell(ScenarioResult result, string metric)
    {
        if (!result.Summary.TryGetValue(metric, out var d))
        {
            return "–";
        }

        return d.N == 1
            ? Format(d.P50)
            : string.Create(CultureInfo.InvariantCulture, $"{Format(d.P50)} / {Format(d.P99)} / {Format(d.Max)} ({d.N})");
    }

    private static string Format(double value) =>
        value >= 100 ? value.ToString("F0", CultureInfo.InvariantCulture) : value.ToString("F1", CultureInfo.InvariantCulture);
}
