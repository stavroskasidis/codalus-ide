using System.Text.Json;
using Codalus.Benchmarks.Harness;
using Codalus.Benchmarks.Harness.Model;

// codalus-bench list
// codalus-bench run [--targets a,b] [--scenarios x,y] [--runs N] [--allow-input] [--results DIR]
// codalus-bench report [--results DIR] [--out FILE]
// codalus-bench check [--results DIR] [--budgets FILE]                  absolute budgets (reference hardware)
// codalus-bench compare --baseline DIR [--results DIR] [--accepted FILE] [--confirm]   regression gate (CI)
var repoRoot = FindRepoRoot();
var command = args.FirstOrDefault() ?? "help";
string? Option(string name) => Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;
var resultsDir = Option("--results") ?? Path.Combine(repoRoot, "benchmarks", "results");

switch (command)
{
    case "list":
        foreach (var target in TargetCatalog.All(repoRoot))
        {
            Console.WriteLine($"{target.Name,-10} {(target.Available ? "available" : "missing  ")} {target.Executable}");
        }

        Console.WriteLine($"scenarios: {string.Join(", ", ScenarioRunner.Scenarios)}");
        return 0;

    case "run":
    {
        var targetNames = Option("--targets")?.Split(',') ?? ["all"];
        var scenarioNames = Option("--scenarios")?.Split(',') ?? ["all"];
        var allowInput = args.Contains("--allow-input");
        if (allowInput && OperatingSystem.IsWindows())
        {
            Console.WriteLine("--allow-input: target windows will be brought to the foreground and receive synthetic keystrokes.");
            Console.WriteLine("Do not use the computer while the run is in progress. Input is only ever sent while the target window is in the foreground.");
        }

        var runs = Option("--runs") is { } r ? int.Parse(r, System.Globalization.CultureInfo.InvariantCulture) : (int?)null;
        var runner = new ScenarioRunner(repoRoot, new RunOptions(runs, allowInput, Console.Out, args.Contains("--keep-work")));
        var targets = TargetCatalog.All(repoRoot).Where(t => targetNames.Contains("all") || targetNames.Contains(t.Name)).ToList();
        var scenarios = ScenarioRunner.Scenarios.Where(s => scenarioNames.Contains("all") || scenarioNames.Contains(s)).ToList();
        var failures = 0;
        foreach (var target in targets)
        {
            if (!target.Available)
            {
                Console.WriteLine($"skip {target.Name}: not installed ({target.Executable})");
                continue;
            }

            foreach (var scenario in scenarios.Where(s => ScenarioRunner.Applies(target, s)))
            {
                Console.WriteLine($"== {target.Name} / {scenario}");
                try
                {
                    var result = await runner.RunAsync(target, scenario, CancellationToken.None);
                    var dir = Path.Combine(resultsDir, result.Machine.Os, target.Name);
                    Directory.CreateDirectory(dir);
                    var file = Path.Combine(dir, $"{scenario}-{result.StartedAt:yyyyMMdd-HHmmss}.json");
                    await File.WriteAllTextAsync(file, JsonSerializer.Serialize(result, ResultsJsonContext.Default.ScenarioResult));
                    Console.WriteLine($"   -> {Path.GetRelativePath(repoRoot, file)}");
                }
                catch (Exception ex) when (ex is InvalidOperationException or IOException or TimeoutException or System.ComponentModel.Win32Exception)
                {
                    failures++;
                    Console.Error.WriteLine($"   FAILED: {ex.Message}");
                }
            }
        }

        return failures == 0 ? 0 : 1;
    }

    case "report":
    {
        var markdown = Report.Markdown(Report.LoadLatest(resultsDir));
        var output = Option("--out");
        if (output is null)
        {
            Console.WriteLine(markdown);
        }
        else
        {
            if (Path.GetDirectoryName(Path.GetFullPath(output)) is { } folder)
            {
                Directory.CreateDirectory(folder);
            }

            await File.WriteAllTextAsync(output, markdown);
            Console.WriteLine($"wrote {output}");
        }

        return 0;
    }

    case "check":
    {
        var budgets = Gates.Load(Option("--budgets") ?? Path.Combine(repoRoot, "benchmarks", "budgets.json"));
        var findings = Gates.Check(budgets, Report.LoadLatest(resultsDir));
        Console.WriteLine(Gates.Format("Budgets (ADR 0002)", findings));
        return findings.Any(f => f.Failed) ? 1 : 0;
    }

    case "compare":
    {
        if (Option("--baseline") is not { } baselineDir)
        {
            Console.Error.WriteLine("compare needs --baseline DIR (results of previous runs, e.g. downloaded nightly artifacts).");
            return 2;
        }

        var policy = Gates.Load(Option("--budgets") ?? Path.Combine(repoRoot, "benchmarks", "budgets.json")).Regression;
        var accepted = Gates.LoadAccepted(Option("--accepted") ?? Path.Combine(repoRoot, "benchmarks", "accepted.json"));
        var baseline = Gates.LoadAll(baselineDir);
        var latest = Report.LoadLatest(resultsDir);
        var findings = Gates.Compare(policy, latest, baseline, accepted);

        // Runner noise: a regression only counts if a fresh run of the same scenario confirms it.
        if (args.Contains("--confirm") && findings.Any(f => f.Failed))
        {
            var runner = new ScenarioRunner(repoRoot, new RunOptions(null, AllowInput: false, Console.Out, KeepWork: false));
            foreach (var (targetName, scenario) in findings.Where(f => f.Failed && f.Os == Codalus.Benchmarks.Harness.Platform.Machine.Describe().Os)
                         .Select(f => (f.Target, f.Scenario)).Distinct())
            {
                if (TargetCatalog.All(repoRoot).FirstOrDefault(t => t.Name == targetName && t.Available) is not { } target)
                {
                    continue;
                }

                Console.WriteLine($"== confirming {targetName} / {scenario}");
                var confirmation = await runner.RunAsync(target, scenario, CancellationToken.None);
                var dir = Path.Combine(resultsDir, confirmation.Machine.Os, target.Name);
                Directory.CreateDirectory(dir);
                await File.WriteAllTextAsync(Path.Combine(dir, $"{scenario}-{confirmation.StartedAt:yyyyMMdd-HHmmss}-confirm.json"),
                    JsonSerializer.Serialize(confirmation, ResultsJsonContext.Default.ScenarioResult));
                // The rest of the run goes along: latency may be normalized by the reference application's time in it.
                var second = Gates.Compare(policy, [confirmation, .. latest.Where(r => r.Target != targetName || r.Scenario != scenario)], baseline, accepted)
                    .Where(s => s.Target == targetName && s.Scenario == scenario)
                    .ToList();
                findings = [.. findings.Select(f => f.Target == targetName && f.Scenario == scenario && f.Failed
                    ? f with { Failed = second.Any(s => s.Os == f.Os && s.Metric == f.Metric && s.Stat == f.Stat && s.Failed), Rule = f.Rule + " (confirmation run)" }
                    : f)];
            }
        }

        // Which runners were compared: latency and memory follow the CPU and its L3 cache.
        var preamble = latest.Select(r => r.Machine.Os).Distinct().Order().Select(os =>
        {
            var machine = latest.First(r => r.Machine.Os == os).Machine;
            var runs = baseline.Where(b => b.Machine.Os == os).GroupBy(b => b.Run ?? b.StartedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture))
                .Select(g => g.First().Machine).ToList();
            var sameCpu = runs.Count(m => Gates.SameCpu(m.Cpu, machine.Cpu));
            return $"- {os}: {machine.Cpu.Trim()}, L3 {Codalus.Benchmarks.Harness.Platform.Machine.FormatCache(machine.L3CacheBytes)}; " +
                   $"baseline: {runs.Count} run(s), {sameCpu} on this CPU; {accepted.Count(a => a.Os == os)} accepted value(s).";
        }).ToList();
        var report = Gates.Format("Regression gate", findings, preamble);
        Console.WriteLine(report);

        // In GitHub Actions, warnings also show as annotations on the run.
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true")
        {
            foreach (var f in findings.Where(f => f.Warning))
            {
                Console.WriteLine($"::warning title=Benchmarks ({f.Os})::{f.Target} {f.Scenario} {f.Metric} {f.Stat}: {f.Value:F1} over {f.Limit:F1} ({f.Rule})");
            }
        }

        if (Option("--out") is { } output)
        {
            await File.WriteAllTextAsync(output, report);
        }

        return findings.Any(f => f.Failed) ? 1 : 0;
    }

    default:
        Console.WriteLine("usage: codalus-bench (list | run [--targets a,b] [--scenarios x,y] [--runs N] [--allow-input] [--results DIR] | report [--results DIR] [--out FILE] | check [--budgets FILE] | compare --baseline DIR [--accepted FILE] [--confirm] [--out FILE])");
        return command == "help" ? 0 : 2;
}

static string FindRepoRoot()
{
    for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
    {
        if (File.Exists(Path.Combine(dir.FullName, "Codalus.slnx")))
        {
            return dir.FullName;
        }
    }

    return Directory.GetCurrentDirectory();
}
