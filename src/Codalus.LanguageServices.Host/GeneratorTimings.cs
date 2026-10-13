using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Codalus.LanguageServices.Host;

/// <summary>
/// What each source generator costs (M34c), logged after a background pass when <c>CODALUS_GENERATOR_TIMINGS=1</c>:
/// the generators of every project the pass compiled run once more, from scratch and one project at a time, and each
/// one's time is logged. For measuring only: it doubles the generators' work.
/// </summary>
internal static class GeneratorTimings
{
    public static bool Enabled { get; } = Environment.GetEnvironmentVariable("CODALUS_GENERATOR_TIMINGS") == "1";

    public static async Task LogAsync(IReadOnlyList<Project> projects, Action<string> log, CancellationToken cancellationToken)
    {
        var totals = new Dictionary<string, (TimeSpan Elapsed, int Projects)>(StringComparer.Ordinal);
        var perProject = new List<(string Project, TimeSpan Elapsed)>();
        foreach (var project in projects)
        {
            var generators = project.AnalyzerReferences.SelectMany(r => r.GetGenerators(project.Language)).ToList();
            if (generators.Count == 0 || await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false) is not { } compilation)
            {
                continue;
            }

            // The compilation without what the generators added.
            var sources = project.Documents.Select(d => d.FilePath).OfType<string>().ToHashSet(Paths.Comparer);
            var input = compilation.RemoveSyntaxTrees(compilation.SyntaxTrees.Where(t => !sources.Contains(t.FilePath)));
            var additional = new List<AdditionalText>();
            foreach (var document in project.AdditionalDocuments)
            {
                additional.Add(new Text(document.FilePath ?? document.Name, await document.GetTextAsync(cancellationToken).ConfigureAwait(false)));
            }

            GeneratorDriver driver = CSharpGeneratorDriver.Create(
                generators,
                additional,
                (CSharpParseOptions?)project.ParseOptions,
                project.AnalyzerOptions.AnalyzerConfigOptionsProvider,
                new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: false));
            driver = driver.RunGenerators(input, cancellationToken);
            var timing = driver.GetTimingInfo();
            perProject.Add((project.Name, timing.ElapsedTime));
            foreach (var generator in timing.GeneratorTimes)
            {
                var name = generator.Generator.GetGeneratorType().Name;
                var (elapsed, count) = totals.GetValueOrDefault(name);
                totals[name] = (elapsed + generator.ElapsedTime, count + 1);
            }
        }

        log($"Source generators, run again from scratch over {perProject.Count} project(s): "
            + string.Join(", ", totals.OrderByDescending(t => t.Value.Elapsed).Select(t => $"{t.Key} {t.Value.Elapsed.TotalMilliseconds:0} ms in {t.Value.Projects}"))
            + ". Slowest projects: " + string.Join(", ", perProject.OrderByDescending(p => p.Elapsed).Take(8).Select(p => $"{p.Project} {p.Elapsed.TotalMilliseconds:0} ms")) + ".");
    }

    private sealed class Text(string path, Microsoft.CodeAnalysis.Text.SourceText text) : AdditionalText
    {
        public override string Path => path;

        public override Microsoft.CodeAnalysis.Text.SourceText GetText(CancellationToken cancellationToken = default) => text;
    }
}
