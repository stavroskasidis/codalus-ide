using System.Reflection;
using Codalus.Benchmarks.Harness;
using Codalus.Benchmarks.Harness.Model;

namespace Codalus.Benchmarks.Harness.Tests;

public class GatesTests
{
    private static readonly MachineInfo s_machine = new("linux", "Ubuntu", "x64", "CPU", 4, 16L << 30, null, null, IsCi: true);

    private static ScenarioResult Result(string scenario, int day, params (string Metric, double[] Values)[] samples) =>
        Result("shell", scenario, day, cpu: "CPU", run: null, samples);

    private static ScenarioResult Result(string target, string scenario, int day, string cpu, string? run, params (string Metric, double[] Values)[] samples)
    {
        var result = new ScenarioResult
        {
            Target = target,
            Scenario = scenario,
            Machine = s_machine with { Cpu = cpu },
            StartedAt = DateTimeOffset.UnixEpoch.AddDays(day),
            Method = "instrumented",
            Run = run,
        };
        foreach (var (metric, values) in samples)
        {
            result.AddRange(metric, values);
        }

        return result.Summarize();
    }

    [Fact]
    public void The_repository_budgets_file_parses_and_covers_adr_0002()
    {
        var root = typeof(GatesTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "CodalusRepoRoot").Value!;
        var budgets = Gates.Load(Path.Combine(root, "benchmarks", "budgets.json"));

        Assert.Contains(budgets.Budgets, b => b is { Target: "shell", Metric: "startup_interactive_ms", Stat: "p50", Max: 800 });
        Assert.Contains(budgets.Budgets, b => b is { Target: "shell", Metric: "memory_idle_private_mb", Max: 250 });
        Assert.Equal(0.05, budgets.Regression.Size);
        Assert.Equal(3, budgets.Regression.MinComparableRuns);
        Assert.False(budgets.Regression.FailNormalized);
        Assert.Equal("startup_selfreported_ms", budgets.Regression.ReferenceMetric);
    }

    [Fact]
    public void Check_flags_values_over_budget()
    {
        var budgets = new BudgetFile([new Budget("shell", "cold-start", "startup_interactive_ms", "p50", 800)], new RegressionPolicy());
        var findings = Gates.Check(budgets, [Result("cold-start", 0, ("startup_interactive_ms", [900, 950, 1000]))]);

        var finding = Assert.Single(findings);
        Assert.True(finding.Failed);
        Assert.Equal(950, finding.Value);
    }

    [Fact]
    public void Compare_uses_the_median_of_previous_runs_and_noise_floors()
    {
        var baseline = new[]
        {
            Result("cold-start", 1, ("startup_interactive_ms", [400]), ("memory_idle_private_mb", [100])),
            Result("cold-start", 2, ("startup_interactive_ms", [420]), ("memory_idle_private_mb", [102])),
            Result("cold-start", 3, ("startup_interactive_ms", [900]), ("memory_idle_private_mb", [101])), // an outlier night
        };

        // 470 ms vs. a 420 ms median: +11.9% and 50 ms over the floor. Memory: +4%, within the gate.
        var regressed = Gates.Compare(new RegressionPolicy(), [Result("cold-start", 4, ("startup_interactive_ms", [470]), ("memory_idle_private_mb", [105]))], baseline);
        Assert.True(regressed.Single(f => f.Metric == "startup_interactive_ms").Failed);
        Assert.False(regressed.Single(f => f.Metric == "memory_idle_private_mb").Failed);

        // +20% of a tiny number is under the noise floor.
        var small = Gates.Compare(new RegressionPolicy(), [Result("cold-start", 4, ("startup_interactive_ms", [12]))], [Result("cold-start", 1, ("startup_interactive_ms", [10]))]);
        Assert.False(Assert.Single(small).Failed);
    }

    [Fact]
    public void Compare_gates_binary_size_at_five_percent_and_skips_first_launch_and_counts()
    {
        var baseline = Result("binary-size", 1, ("shell_zip_mb", [20]));
        var bigger = Gates.Compare(new RegressionPolicy(), [Result("binary-size", 2, ("shell_zip_mb", [21.5]))], [baseline]);
        Assert.True(Assert.Single(bigger).Failed);

        var ignored = Gates.Compare(
            new RegressionPolicy(),
            [Result("cold-start", 2, ("startup_interactive_ms_first", [2000]), ("process_count", [9]))],
            [Result("cold-start", 1, ("startup_interactive_ms_first", [500]), ("process_count", [2]))]);
        Assert.Empty(ignored);
    }

    [Fact]
    public void P99_is_only_compared_with_enough_samples()
    {
        var few = Enumerable.Repeat(10.0, 5).Append(100).ToArray();
        Assert.DoesNotContain(Gates.Compare(new RegressionPolicy(), [Result("self-driven", 2, ("selfdriven_typing_ms", few))], [Result("self-driven", 1, ("selfdriven_typing_ms", [10, 10]))]), f => f.Stat == "p99");

        var many = Enumerable.Repeat(10.0, 30).ToArray();
        Assert.Contains(Gates.Compare(new RegressionPolicy(), [Result("self-driven", 2, ("selfdriven_typing_ms", many))], [Result("self-driven", 1, ("selfdriven_typing_ms", many))]), f => f.Stat == "p99");
    }

    private static readonly DateOnly s_epoch = DateOnly.FromDateTime(DateTime.UnixEpoch);

    private static AcceptedValue Accepted(string metric, double value, int day, string? cpu = null, string scenario = "binary-size") =>
        new("linux", "shell", scenario, metric, value, s_epoch.AddDays(day), "test") { Cpu = cpu };

    [Fact]
    public void The_repository_accepted_values_parse_and_each_says_why()
    {
        var root = typeof(GatesTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "CodalusRepoRoot").Value!;
        var accepted = Gates.LoadAccepted(Path.Combine(root, "benchmarks", "accepted.json"));

        Assert.All(accepted, a =>
        {
            Assert.True(a.Os is "linux" or "windows" or "macos", a.Os);
            Assert.False(string.IsNullOrWhiteSpace(a.Reason));
            Assert.True(a.Value > 0);
            Assert.Equal("p50", a.Stat);
        });
        Assert.Contains(accepted, a => a is { Os: "linux", Target: "shell", Scenario: "binary-size", Metric: "lshost_mb" } && Math.Abs(a.Value - 93.5) < 0.05);
        Assert.Contains(accepted, a => a is { Os: "windows", Metric: "lshost_mb" } && Math.Abs(a.Value - 92.6) < 0.05);
        Assert.Contains(accepted, a => a is { Os: "macos", Metric: "pluginhost_mb" } && Math.Abs(a.Value - 4.7) < 0.05);
        Assert.Empty(Gates.LoadAccepted(Path.Combine(root, "benchmarks", "no-such-file.json")));
    }

    [Fact]
    public void An_accepted_value_replaces_the_runs_before_it_until_newer_runs_exist()
    {
        var before = Result("binary-size", 1, ("lshost_mb", [73.9]));
        var accepted = new[] { Accepted("lshost_mb", 93.5, day: 2) };

        // Without it, the language host is 26% over its only run.
        Assert.True(Assert.Single(Gates.Compare(new RegressionPolicy(), [Result("binary-size", 3, ("lshost_mb", [93.5]))], [before])).Failed);

        var same = Assert.Single(Gates.Compare(new RegressionPolicy(), [Result("binary-size", 3, ("lshost_mb", [93.5]))], [before], accepted));
        Assert.False(same.Failed);
        Assert.Contains("accepted", same.Rule, StringComparison.Ordinal);

        // Still strict: 6% over the accepted value fails.
        Assert.True(Assert.Single(Gates.Compare(new RegressionPolicy(), [Result("binary-size", 3, ("lshost_mb", [99.2]))], [before], accepted)).Failed);

        // A newer run takes over from the accepted value, and the runs before it still don't count.
        var newer = Result("binary-size", 4, ("lshost_mb", [80]));
        var overNewer = Assert.Single(Gates.Compare(new RegressionPolicy(), [Result("binary-size", 5, ("lshost_mb", [93.5]))], [before, newer], accepted));
        Assert.True(overNewer.Failed);
        Assert.Contains("median of 1 run(s) (80.0)", overNewer.Rule, StringComparison.Ordinal);

        // An acceptance older than every run changes nothing.
        Assert.True(Assert.Single(Gates.Compare(new RegressionPolicy(), [Result("binary-size", 3, ("lshost_mb", [93.5]))], [before], [Accepted("lshost_mb", 93.5, day: -1)])).Failed);
    }

    [Fact]
    public void Latency_and_memory_compare_with_runs_on_the_same_cpu()
    {
        const string fast = "AMD EPYC 9V45 96-Core Processor";
        const string slow = "AMD EPYC 7763 64-Core Processor";
        var baseline = new[]
        {
            Result("shell", "cold-start", 1, fast, "1", ("startup_interactive_ms", [220]), ("memory_idle_private_mb", [200])),
            Result("shell", "cold-start", 2, fast, "2", ("startup_interactive_ms", [225]), ("memory_idle_private_mb", [201])),
            Result("shell", "cold-start", 3, fast, "3", ("startup_interactive_ms", [215]), ("memory_idle_private_mb", [199])),
            Result("shell", "cold-start", 4, slow, "4", ("startup_interactive_ms", [320]), ("memory_idle_private_mb", [240])),
            Result("shell", "cold-start", 5, slow, "5", ("startup_interactive_ms", [330]), ("memory_idle_private_mb", [242])),
            Result("shell", "cold-start", 6, slow, "6", ("startup_interactive_ms", [325]), ("memory_idle_private_mb", [238])),
        };

        // The slow runner's 330 ms is no regression against the slow runs (the median of all six would fail it).
        var onSlow = Gates.Compare(new RegressionPolicy(), [Result("shell", "cold-start", 7, slow, null, ("startup_interactive_ms", [330]), ("memory_idle_private_mb", [240]))], baseline);
        Assert.All(onSlow, f => Assert.False(f.Failed || f.Warning));
        Assert.Contains("median of 3 run(s) on this CPU (325.0)", onSlow.Single(f => f.Metric == "startup_interactive_ms").Rule, StringComparison.Ordinal);

        // On the fast runner, 270 ms is 23% over its own runs and fails; memory too. Windows pads and capitalizes CPU names.
        var onFast = Gates.Compare(new RegressionPolicy(),
            [Result("shell", "cold-start", 7, "  AMD EPYC 9V45 96-CORE PROCESSOR   ", null, ("startup_interactive_ms", [270]), ("memory_idle_private_mb", [230]))], baseline);
        Assert.All(onFast, f => Assert.True(f.Failed));
    }

    [Fact]
    public void Fewer_than_three_runs_on_the_cpu_warn_instead_of_failing()
    {
        var baseline = new[]
        {
            Result("shell", "editor-100k", 1, "EPYC 7763", "1", ("memory_file_private_mb", [810])),
            Result("shell", "editor-100k", 2, "EPYC 7763", "2", ("memory_file_private_mb", [835])),
            Result("shell", "editor-100k", 3, "Xeon 8573C", "3", ("memory_file_private_mb", [880])),
        };

        // 940 MB on a runner with a bigger L3: 12.6% over the median of the runs, none of them on this CPU.
        var finding = Assert.Single(Gates.Compare(new RegressionPolicy(), [Result("shell", "editor-100k", 4, "Xeon 6973P-C", null, ("memory_file_private_mb", [940]))], baseline));
        Assert.False(finding.Failed);
        Assert.True(finding.Warning);
        Assert.Contains("fewer than 3 runs on this CPU", finding.Rule, StringComparison.Ordinal);
        Assert.Contains("| warn |", Gates.Format("Regression gate", [finding]), StringComparison.Ordinal);
        Assert.Contains("0 failed, 1 warning(s)", Gates.Format("Regression gate", [finding]), StringComparison.Ordinal);

        // A run and its confirmations are one run: three results on this CPU, but from one run, still only warn.
        var oneRun = Enumerable.Range(0, 3).Select(i => Result("shell", "editor-100k", 3, "Xeon 6973P-C", "3", ("memory_file_private_mb", [880 + i]))).ToList();
        var confirmed = Assert.Single(Gates.Compare(new RegressionPolicy(), [Result("shell", "editor-100k", 4, "Xeon 6973P-C", null, ("memory_file_private_mb", [990]))], oneRun));
        Assert.True(confirmed.Warning);
        Assert.False(confirmed.Failed);
    }

    [Fact]
    public void Latency_on_another_cpu_is_normalized_by_the_reference_application_of_the_same_run()
    {
        // Three runs on a fast CPU: VS Code starts in 1150 ms, Codalus highlights the file in 1600 ms (× 1.39).
        var baseline = new List<ScenarioResult>();
        foreach (var (run, vscode, shell) in new[] { ("1", 1150.0, 1600.0), ("2", 1200.0, 1680.0), ("3", 1140.0, 1580.0) })
        {
            var day = int.Parse(run, System.Globalization.CultureInfo.InvariantCulture);
            baseline.Add(Result("vscode", "cold-start", day, "fast", run, ("startup_selfreported_ms", [vscode])));
            baseline.Add(Result("shell", "editor-100k", day, "fast", run, ("file_highlight_ms", [shell])));
        }

        ScenarioResult[] Current(double vscode, double shell) =>
        [
            Result("vscode", "cold-start", 9, "slow", null, ("startup_selfreported_ms", [vscode])),
            Result("shell", "editor-100k", 9, "slow", null, ("file_highlight_ms", [shell])),
        ];

        // A slower runner: VS Code +30%, Codalus +30%. Normalized, nothing changed; VS Code itself is never failed.
        var slower = Gates.Compare(new RegressionPolicy(), Current(1500, 2080), baseline);
        var highlight = slower.Single(f => f.Metric == "file_highlight_ms");
        Assert.False(highlight.Failed || highlight.Warning);
        Assert.Contains("normalized by vscode startup_selfreported_ms", highlight.Rule, StringComparison.Ordinal);
        var reference = slower.Single(f => f.Target == "vscode");
        Assert.False(reference.Failed);
        Assert.True(reference.Warning);
        Assert.Contains("never fails", reference.Rule, StringComparison.Ordinal);

        // Codalus 25% slower than VS Code's ratio: a warning (no runs on this CPU), and a failure only when asked for.
        var regressed = Gates.Compare(new RegressionPolicy(), Current(1500, 2600), baseline).Single(f => f.Metric == "file_highlight_ms");
        Assert.True(regressed.Warning);
        Assert.False(regressed.Failed);
        Assert.True(Gates.Compare(new RegressionPolicy { FailNormalized = true }, Current(1500, 2600), baseline).Single(f => f.Metric == "file_highlight_ms").Failed);
        Assert.False(Gates.Compare(new RegressionPolicy { FailNormalized = true }, Current(1500, 2600), baseline.Skip(2).ToList()).Single(f => f.Metric == "file_highlight_ms").Failed);
    }

    [Fact]
    public void Binary_size_stays_strict_with_one_run_on_another_cpu()
    {
        var finding = Assert.Single(Gates.Compare(new RegressionPolicy(),
            [Result("shell", "binary-size", 2, "other", null, ("markuphost_mb", [3.6]))],
            [Result("shell", "binary-size", 1, "CPU", "1", ("markuphost_mb", [3.3]))]));
        Assert.True(finding.Failed);
        Assert.False(finding.Warning);
    }

    [Fact]
    public void Baseline_results_belong_to_the_run_of_their_first_folder()
    {
        var dir = Directory.CreateTempSubdirectory("codalus-bench-").FullName;
        try
        {
            var result = Result("binary-size", 1, ("shell_mb", [100]));
            var file = Path.Combine(dir, "38616576687", "linux", "shell", "binary-size.json");
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, System.Text.Json.JsonSerializer.Serialize(result, ResultsJsonContext.Default.ScenarioResult));
            File.WriteAllText(Path.Combine(dir, "loose.json"), System.Text.Json.JsonSerializer.Serialize(result, ResultsJsonContext.Default.ScenarioResult));

            var loaded = Gates.LoadAll(dir);
            Assert.Contains(loaded, r => r.Run == "38616576687");
            Assert.Contains(loaded, r => r.Run is null);
            Assert.DoesNotContain("38616576687", File.ReadAllText(file), StringComparison.Ordinal); // the run isn't stored
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
