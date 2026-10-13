using System.Reflection;
using Codalus.Benchmarks.Harness;
using Codalus.Benchmarks.Harness.Model;

namespace Codalus.Benchmarks.Harness.Tests;

public class GatesTests
{
    private static readonly MachineInfo s_machine = new("linux", "Ubuntu", "x64", "CPU", 4, 16L << 30, null, null, IsCi: true);

    private static ScenarioResult Result(string scenario, int day, params (string Metric, double[] Values)[] samples)
    {
        var result = new ScenarioResult
        {
            Target = "shell",
            Scenario = scenario,
            Machine = s_machine,
            StartedAt = DateTimeOffset.UnixEpoch.AddDays(day),
            Method = "instrumented",
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
}
