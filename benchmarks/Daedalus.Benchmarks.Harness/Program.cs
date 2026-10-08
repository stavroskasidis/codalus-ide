using System.Text.Json;
using Daedalus.Benchmarks.Harness;
using Daedalus.Benchmarks.Harness.Model;

// daedalus-bench list
// daedalus-bench run [--targets a,b] [--scenarios x,y] [--runs N] [--allow-input] [--results DIR]
// daedalus-bench report [--results DIR] [--out FILE]
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
        var runner = new ScenarioRunner(repoRoot, new RunOptions(runs, allowInput, Console.Out));
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

    default:
        Console.WriteLine("usage: daedalus-bench (list | run [--targets a,b] [--scenarios x,y] [--runs N] [--allow-input] [--results DIR] | report [--results DIR] [--out FILE])");
        return command == "help" ? 0 : 2;
}

static string FindRepoRoot()
{
    for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
    {
        if (File.Exists(Path.Combine(dir.FullName, "Daedalus.slnx")))
        {
            return dir.FullName;
        }
    }

    return Directory.GetCurrentDirectory();
}
