using System.Globalization;
using System.Xml.Linq;
using DaedalusStudio.Plugins.Proposed;

namespace DaedalusStudio.Plugins.DotNetTest;

/// <summary>
/// Coverlet, shipped in the plugin folder (ADR 0011): the VSTest data collector (<c>coverage/collector</c>) and the
/// console tool (<c>coverage/console</c>) that wraps Microsoft.Testing.Platform applications. Reports are Cobertura XML.
/// </summary>
internal static class CoverageTools
{
    private static string PluginFolder => Path.GetDirectoryName(typeof(CoverageTools).Assembly.Location)!;

    public static string CollectorPath => Path.Combine(PluginFolder, "coverage", "collector", "coverlet.collector.dll");

    /// <summary>
    /// The collector's folder for test hosts of <paramref name="targetFramework"/>: the current one (net8.0) from .NET 8,
    /// the last netstandard2.0 one before (M33, ADR 0030), which loads into .NET Core 2.0 to .NET 7 test hosts.
    /// </summary>
    public static string CollectorFolder(string? targetFramework) =>
        OlderFrameworks.NetCoreVersion(targetFramework) is { Major: < 8 }
            ? Path.Combine(PluginFolder, "coverage", "collector-netstandard2.0")
            : Path.GetDirectoryName(CollectorPath)!;

    public static string ConsolePath => Path.Combine(PluginFolder, "coverage", "console", "coverlet.console.dll");

    /// <summary>No include filter by default: Coverlet instruments the assemblies next to the tests that have symbols.</summary>
    public static IReadOnlyList<string> DefaultIncludes(IReadOnlyList<TestTarget> targets) => [];

    /// <summary>The test assemblies themselves are not measured.</summary>
    public static IEnumerable<string> DefaultExcludes(IReadOnlyList<TestTarget> targets) =>
        targets.Select(t => $"[{Path.GetFileNameWithoutExtension(t.AssemblyPath)}]*").Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether the project references the Coverlet MTP extension (then <c>--coverlet</c> works).</summary>
    public static bool ProjectHasCoverletMtp(TestTarget target) =>
        File.Exists(Path.Combine(Path.GetDirectoryName(target.AssemblyPath)!, "coverlet.MTP.dll"))
        || File.Exists(Path.Combine(Path.GetDirectoryName(target.AssemblyPath)!, "coverlet.mtp.dll"));

    /// <summary>
    /// Runs the test application under <c>coverlet.console</c>: it instruments the assemblies next to the tests, starts the
    /// application, and writes a Cobertura report when it exits.
    /// </summary>
    /// <summary>Where the Cobertura report of one project and framework goes.</summary>
    public static string OutputFile(TestTarget target, CoverageOptions coverage) =>
        Path.Combine(coverage.OutputDirectory, target.ProjectName + "." + (target.TargetFramework ?? "default") + ".cobertura.xml");

    public static (string Program, List<string> Arguments) WrapWithConsole(TestTarget target, string program, IReadOnlyList<string> arguments, CoverageOptions coverage)
    {
        var output = OutputFile(target, coverage);
        var wrapped = new List<string>
        {
            ConsolePath,
            target.AssemblyPath,
            "--target", program,
            "--targetargs", string.Join(' ', arguments.Select(Quote)),
            "--format", "cobertura",
            "--output", output,
            "--exclude-by-attribute", "GeneratedCode",
            "--exclude-by-attribute", "CompilerGenerated",
            "--exclude-by-attribute", "ExcludeFromCodeCoverage",
        };
        foreach (var include in coverage.Include)
        {
            wrapped.AddRange(["--include", include]);
        }

        foreach (var exclude in coverage.Exclude.Concat(DefaultExcludes([target])))
        {
            wrapped.AddRange(["--exclude", exclude]);
        }

        return (DotnetHost.Path, wrapped);
    }

    private static string Quote(string argument) =>
        argument.Length > 0 && !argument.Any(c => char.IsWhiteSpace(c) || c == '"') ? argument : "\"" + argument.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    /// <summary>The Cobertura reports of a run, merged (ADR 0011): hits summed per line, branches maxed per line.</summary>
    public static CoverageReport Merge(IEnumerable<string> coberturaFiles)
    {
        var lines = new Dictionary<string, Dictionary<int, (int Hits, int Covered, int Total)>>(StringComparer.OrdinalIgnoreCase);
        var methods = new Dictionary<(string Assembly, string Class, string Method), MethodAccumulator>();
        foreach (var file in coberturaFiles.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            XDocument document;
            try
            {
                document = XDocument.Load(file);
            }
            catch (Exception ex) when (ex is IOException or System.Xml.XmlException or UnauthorizedAccessException)
            {
                continue;
            }

            var sources = document.Root?.Element("sources")?.Elements("source").Select(s => s.Value).ToList() ?? [];
            foreach (var package in document.Descendants("package"))
            {
                var assembly = (string?)package.Attribute("name") ?? string.Empty;
                foreach (var cls in package.Descendants("class"))
                {
                    var path = Resolve((string?)cls.Attribute("filename"), sources);
                    var className = (string?)cls.Attribute("name") ?? string.Empty;
                    foreach (var method in cls.Element("methods")?.Elements("method") ?? [])
                    {
                        var key = (assembly, className, (string?)method.Attribute("name") ?? string.Empty);
                        if (!methods.TryGetValue(key, out var accumulator))
                        {
                            methods[key] = accumulator = new MethodAccumulator(path);
                        }

                        foreach (var line in method.Element("lines")?.Elements("line") ?? [])
                        {
                            accumulator.Add(Line(line));
                        }
                    }

                    if (path is null)
                    {
                        continue;
                    }

                    if (!lines.TryGetValue(path, out var fileLines))
                    {
                        lines[path] = fileLines = [];
                    }

                    foreach (var line in cls.Element("lines")?.Elements("line") ?? [])
                    {
                        var (number, hits, covered, total) = Line(line);
                        fileLines[number] = fileLines.TryGetValue(number, out var existing)
                            ? (existing.Hits + hits, Math.Max(existing.Covered, covered), Math.Max(existing.Total, total))
                            : (hits, covered, total);
                    }
                }
            }
        }

        var files = lines.Select(f => new FileCoverage(
            new Uri(f.Key).AbsoluteUri,
            [.. f.Value.OrderBy(l => l.Key).Select(l => new LineCoverage(l.Key - 1, l.Value.Hits, l.Value.Covered, l.Value.Total))])).ToList();
        var methodList = methods.Select(m =>
        {
            var (ns, cls) = SplitClass(m.Key.Class);
            return new MethodCoverage(
                m.Key.Assembly,
                ns,
                cls,
                MethodLabel(m.Key.Method),
                m.Value.Path is { } p ? new Uri(p).AbsoluteUri : null,
                Math.Max(0, m.Value.FirstLine - 1),
                m.Value.Lines.Count(l => l.Value.Hits > 0),
                m.Value.Lines.Count,
                m.Value.Lines.Values.Sum(l => l.Covered),
                m.Value.Lines.Values.Sum(l => l.Total));
        }).ToList();
        return new CoverageReport(files, methodList);
    }

    private static (int Number, int Hits, int Covered, int Total) Line(XElement line)
    {
        var number = int.TryParse((string?)line.Attribute("number"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;
        var hits = long.TryParse((string?)line.Attribute("hits"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var h) ? (int)Math.Min(h, int.MaxValue) : 0;
        var covered = 0;
        var total = 0;

        // condition-coverage="50% (1/2)"
        if ((string?)line.Attribute("condition-coverage") is { } condition && condition.IndexOf('(', StringComparison.Ordinal) is var open and >= 0)
        {
            var parts = condition[(open + 1)..].TrimEnd(')').Split('/');
            if (parts.Length == 2)
            {
                _ = int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out covered);
                _ = int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out total);
            }
        }

        return (number, hits, covered, total);
    }

    private static string? Resolve(string? fileName, IReadOnlyList<string> sources)
    {
        if (string.IsNullOrEmpty(fileName))
        {
            return null;
        }

        if (Path.IsPathRooted(fileName))
        {
            return Path.GetFullPath(fileName);
        }

        return sources.Select(s => Path.Combine(s, fileName)).FirstOrDefault(File.Exists) is { } found ? Path.GetFullPath(found) : null;
    }

    /// <summary><c>A.B.C/&lt;Nested&gt;d__1</c> → (<c>A.B</c>, <c>C</c>); compiler-generated parts are folded into their type.</summary>
    private static (string? Namespace, string Class) SplitClass(string name)
    {
        var clean = name.Split('/')[0];
        var dot = clean.LastIndexOf('.');
        return dot <= 0 ? (null, clean) : (clean[..dot], clean[(dot + 1)..]);
    }

    /// <summary>Cobertura method names are <c>Name</c> with a signature attribute; async/iterator state machines are <c>MoveNext</c> of a nested type.</summary>
    private static string MethodLabel(string method) => method;

    private sealed class MethodAccumulator(string? path)
    {
        public string? Path { get; } = path;

        public Dictionary<int, (int Hits, int Covered, int Total)> Lines { get; } = [];

        public int FirstLine => Lines.Count == 0 ? 0 : Lines.Keys.Min();

        public void Add((int Number, int Hits, int Covered, int Total) line) =>
            Lines[line.Number] = Lines.TryGetValue(line.Number, out var existing)
                ? (existing.Hits + line.Hits, Math.Max(existing.Covered, line.Covered), Math.Max(existing.Total, line.Total))
                : (line.Hits, line.Covered, line.Total);
    }
}
