using System.Text.Json;
using DaedalusStudio.Benchmarks.Harness;
using DaedalusStudio.Benchmarks.Harness.Model;

// daedalus-bench list
// daedalus-bench run [--targets a,b] [--scenarios x,y] [--runs N] [--allow-input] [--results DIR]
// daedalus-bench report [--results DIR] [--out FILE]
// daedalus-bench check [--results DIR] [--budgets FILE]                  absolute budgets (reference hardware)
// daedalus-bench compare --baseline DIR [--results DIR] [--confirm]      regression gate (CI)
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
        var baseline = Gates.LoadAll(baselineDir);
        var findings = Gates.Compare(policy, Report.LoadLatest(resultsDir), baseline);

        // Runner noise: a regression only counts if a fresh run of the same scenario confirms it.
        if (args.Contains("--confirm") && findings.Any(f => f.Failed))
        {
            var runner = new ScenarioRunner(repoRoot, new RunOptions(null, AllowInput: false, Console.Out, KeepWork: false));
            foreach (var (targetName, scenario) in findings.Where(f => f.Failed && f.Os == DaedalusStudio.Benchmarks.Harness.Platform.Machine.Describe().Os)
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
                var second = Gates.Compare(policy, [confirmation], baseline);
                findings = [.. findings.Select(f => f.Target == targetName && f.Scenario == scenario && f.Failed
                    ? f with { Failed = second.Any(s => s.Metric == f.Metric && s.Stat == f.Stat && s.Failed), Rule = f.Rule + " (confirmation run)" }
                    : f)];
            }
        }

        var report = Gates.Format("Regression gate", findings);
        Console.WriteLine(report);
        if (Option("--out") is { } output)
        {
            await File.WriteAllTextAsync(output, report);
        }

        return findings.Any(f => f.Failed) ? 1 : 0;
    }

    default:
        Console.WriteLine("usage: daedalus-bench (list | run [--targets a,b] [--scenarios x,y] [--runs N] [--allow-input] [--results DIR] | report [--results DIR] [--out FILE] | check [--budgets FILE] | compare --baseline DIR [--confirm] [--out FILE])");
        return command == "help" ? 0 : 2;
}

static string FindRepoRoot()
{
    for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
    {
        if (File.Exists(Path.Combine(dir.FullName, "DaedalusStudio.slnx")))
        {
            return dir.FullName;
        }
    }

    return Directory.GetCurrentDirectory();
}
