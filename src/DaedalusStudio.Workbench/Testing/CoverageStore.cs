using System.Globalization;
using System.Xml.Linq;
using DaedalusStudio.Plugins.Protocol;

namespace DaedalusStudio.Workbench.Testing;

/// <summary>How much of a line the tests ran.</summary>
public enum LineCoverageState
{
    Covered,

    /// <summary>The line ran, but not all of its branches did.</summary>
    Partial,

    Uncovered,
}

/// <summary>Coverage of one line (zero-based <see cref="Line"/>).</summary>
public sealed record CoverageLine(int Line, int Hits, int BranchesCovered, int BranchesTotal)
{
    public LineCoverageState State =>
        Hits == 0 ? LineCoverageState.Uncovered
        : BranchesTotal > 0 && BranchesCovered < BranchesTotal ? LineCoverageState.Partial
        : LineCoverageState.Covered;
}

public enum CoverageNodeKind
{
    Assembly,
    Namespace,
    Class,
    Method,
}

/// <summary>Covered and coverable lines and branches, with their percentages (null when there is nothing to cover).</summary>
public readonly record struct CoverageCounts(int LinesCovered, int LinesTotal, int BranchesCovered, int BranchesTotal)
{
    public double? LinePercent => LinesTotal == 0 ? null : 100.0 * LinesCovered / LinesTotal;

    public double? BranchPercent => BranchesTotal == 0 ? null : 100.0 * BranchesCovered / BranchesTotal;

    public static CoverageCounts operator +(CoverageCounts a, CoverageCounts b) =>
        new(a.LinesCovered + b.LinesCovered, a.LinesTotal + b.LinesTotal, a.BranchesCovered + b.BranchesCovered, a.BranchesTotal + b.BranchesTotal);

    public static string Percent(double? value) => value is { } v ? v.ToString("0.#", CultureInfo.CurrentCulture) + " %" : "–";
}

/// <summary>A row of the Coverage Results tree: assembly, namespace, class or method.</summary>
public sealed class CoverageNode
{
    internal CoverageNode(CoverageNodeKind kind, string key, string label)
    {
        Kind = kind;
        Key = key;
        Label = label;
    }

    public CoverageNodeKind Kind { get; }

    /// <summary>Stable across reports (assembly, namespace, class and method names joined).</summary>
    public string Key { get; }

    public string Label { get; }

    public CoverageCounts Counts { get; internal set; }

    /// <summary>The document of a method or class, when known.</summary>
    public string? Uri { get; internal set; }

    /// <summary>The zero-based first line of a method.</summary>
    public int Line { get; internal set; }

    public List<CoverageNode> Children { get; } = [];
}

/// <summary>
/// The latest coverage (ADR 0011), for the editor's coloring and the Coverage Results window. A run's reports are
/// merged (hits summed, branches maxed per line); the first report of another run replaces them. Thread-safe;
/// <see cref="Changed"/> is raised on any thread.
/// </summary>
public sealed class CoverageStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Dictionary<int, CoverageLine>> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string Assembly, string? Namespace, string Class, string Method), MethodCoverageDto> _methods = [];
    private IReadOnlyList<CoverageNode>? _tree;
    private bool _isVisible = true;

    /// <summary>Coverage arrived, was cleared, or <see cref="IsVisible"/> changed.</summary>
    public event EventHandler? Changed;

    /// <summary>The run whose coverage is shown.</summary>
    public string? RunId { get; private set; }

    /// <summary>When the shown coverage was collected (null: none).</summary>
    public DateTimeOffset? CollectedAt { get; private set; }

    public bool HasCoverage
    {
        get
        {
            lock (_gate)
            {
                return _files.Count > 0 || _methods.Count > 0;
            }
        }
    }

    /// <summary>Whether the editor colors covered and uncovered lines.</summary>
    public bool IsVisible
    {
        get => Volatile.Read(ref _isVisible);
        set
        {
            if (Interlocked.Exchange(ref _isVisible, value) != value)
            {
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public void Add(TestRunCoverageParams report, DateTimeOffset? now = null)
    {
        lock (_gate)
        {
            if (!string.Equals(RunId, report.RunId, StringComparison.Ordinal))
            {
                _files.Clear();
                _methods.Clear();
                RunId = report.RunId;
            }

            CollectedAt = now ?? DateTimeOffset.Now;
            foreach (var file in report.Files)
            {
                if (!_files.TryGetValue(file.Uri, out var lines))
                {
                    _files[file.Uri] = lines = [];
                }

                foreach (var line in file.Lines)
                {
                    lines[line.Line] = lines.TryGetValue(line.Line, out var existing)
                        ? new CoverageLine(line.Line, existing.Hits + line.Hits, Math.Max(existing.BranchesCovered, line.BranchesCovered), Math.Max(existing.BranchesTotal, line.BranchesTotal))
                        : new CoverageLine(line.Line, line.Hits, line.BranchesCovered, line.BranchesTotal);
                }
            }

            foreach (var method in report.Methods)
            {
                var key = (method.Assembly, method.Namespace, method.Class, method.Method);
                _methods[key] = _methods.TryGetValue(key, out var existing)
                    ? existing with
                    {
                        LinesCovered = Math.Max(existing.LinesCovered, method.LinesCovered),
                        LinesTotal = Math.Max(existing.LinesTotal, method.LinesTotal),
                        BranchesCovered = Math.Max(existing.BranchesCovered, method.BranchesCovered),
                        BranchesTotal = Math.Max(existing.BranchesTotal, method.BranchesTotal),
                        Uri = existing.Uri ?? method.Uri,
                    }
                    : method;
            }

            _tree = null;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        lock (_gate)
        {
            _files.Clear();
            _methods.Clear();
            _tree = null;
            RunId = null;
            CollectedAt = null;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The covered and coverable lines of a document, by line.</summary>
    public IReadOnlyList<CoverageLine> LinesOf(string uri)
    {
        lock (_gate)
        {
            return _files.TryGetValue(uri, out var lines) ? [.. lines.Values.OrderBy(l => l.Line)] : [];
        }
    }

    /// <summary>Totals over every document.</summary>
    public CoverageCounts Totals
    {
        get
        {
            lock (_gate)
            {
                var counts = default(CoverageCounts);
                foreach (var line in _files.Values.SelectMany(f => f.Values))
                {
                    counts += new CoverageCounts(line.Hits > 0 ? 1 : 0, 1, line.BranchesCovered, line.BranchesTotal);
                }

                return counts;
            }
        }
    }

    /// <summary>Assembly → namespace → class → method, sorted by name, each with its totals.</summary>
    public IReadOnlyList<CoverageNode> Tree()
    {
        lock (_gate)
        {
            return _tree ??= BuildTree(_methods.Values);
        }
    }

    private static List<CoverageNode> BuildTree(IEnumerable<MethodCoverageDto> methods)
    {
        var assemblies = new List<CoverageNode>();
        foreach (var byAssembly in methods.GroupBy(m => m.Assembly, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            var assembly = new CoverageNode(CoverageNodeKind.Assembly, byAssembly.Key, byAssembly.Key);
            foreach (var byNamespace in byAssembly.GroupBy(m => m.Namespace ?? string.Empty, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
            {
                var nsKey = assembly.Key + "/" + byNamespace.Key;
                var ns = new CoverageNode(CoverageNodeKind.Namespace, nsKey, byNamespace.Key.Length == 0 ? "(global namespace)" : byNamespace.Key);
                foreach (var byClass in byNamespace.GroupBy(m => m.Class, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
                {
                    var cls = new CoverageNode(CoverageNodeKind.Class, nsKey + "/" + byClass.Key, byClass.Key);
                    foreach (var method in byClass.OrderBy(m => m.Line).ThenBy(m => m.Method, StringComparer.Ordinal))
                    {
                        var node = new CoverageNode(CoverageNodeKind.Method, cls.Key + "/" + method.Method, method.Method)
                        {
                            Counts = new CoverageCounts(method.LinesCovered, method.LinesTotal, method.BranchesCovered, method.BranchesTotal),
                            Uri = method.Uri,
                            Line = method.Line,
                        };
                        cls.Children.Add(node);
                        cls.Counts += node.Counts;
                        cls.Uri ??= method.Uri;
                    }

                    cls.Line = cls.Children.Count > 0 ? cls.Children.Min(c => c.Line) : 0;
                    ns.Children.Add(cls);
                    ns.Counts += cls.Counts;
                }

                assembly.Children.Add(ns);
                assembly.Counts += ns.Counts;
            }

            assemblies.Add(assembly);
        }

        return assemblies;
    }

    /// <summary>
    /// The coverage as a Cobertura report: a package per assembly, a class per class and document. Each line of a
    /// document belongs to the method that starts closest above it.
    /// </summary>
    public XDocument ToCobertura()
    {
        lock (_gate)
        {
            var totals = Totals;
            var packages = new XElement("packages");
            foreach (var byAssembly in _methods.Values.GroupBy(m => m.Assembly, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                var classes = new XElement("classes");
                var packageCounts = default(CoverageCounts);
                foreach (var byClass in byAssembly.GroupBy(m => (m.Namespace, m.Class, m.Uri)).OrderBy(g => g.Key.Namespace + "." + g.Key.Class, StringComparer.Ordinal))
                {
                    var path = byClass.Key.Uri is { } uri && System.Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && parsed.IsFile ? parsed.LocalPath : byClass.Key.Uri ?? string.Empty;
                    var name = byClass.Key.Namespace is { Length: > 0 } ns ? ns + "." + byClass.Key.Class : byClass.Key.Class;
                    var owned = OwnedLines(byClass.Key.Uri, byClass.Key.Namespace, byClass.Key.Class);
                    var methodsElement = new XElement("methods");
                    foreach (var method in byClass.OrderBy(m => m.Line))
                    {
                        var lines = owned.TryGetValue(method.Method, out var l) ? l : [];
                        var counts = Count(lines);
                        methodsElement.Add(new XElement(
                            "method",
                            new XAttribute("name", method.Method),
                            new XAttribute("signature", string.Empty),
                            Rates(counts),
                            new XElement("lines", lines.Select(LineElement))));
                    }

                    var allLines = owned.Values.SelectMany(l => l).OrderBy(l => l.Line).ToList();
                    var classCounts = Count(allLines);
                    packageCounts += classCounts;
                    classes.Add(new XElement(
                        "class",
                        new XAttribute("name", name),
                        new XAttribute("filename", path),
                        Rates(classCounts),
                        new XAttribute("complexity", 0),
                        methodsElement,
                        new XElement("lines", allLines.Select(LineElement))));
                }

                packages.Add(new XElement("package", new XAttribute("name", byAssembly.Key), Rates(packageCounts), new XAttribute("complexity", 0), classes));
            }

            return new XDocument(
                new XDeclaration("1.0", "utf-8", null),
                new XElement(
                    "coverage",
                    Rates(totals),
                    new XAttribute("lines-covered", totals.LinesCovered),
                    new XAttribute("lines-valid", totals.LinesTotal),
                    new XAttribute("branches-covered", totals.BranchesCovered),
                    new XAttribute("branches-valid", totals.BranchesTotal),
                    new XAttribute("complexity", 0),
                    new XAttribute("version", "1.9"),
                    new XAttribute("timestamp", (CollectedAt ?? DateTimeOffset.Now).ToUnixTimeSeconds()),
                    new XElement("sources"),
                    packages));
        }
    }

    /// <summary>The lines of a class's document assigned to its methods (each line to the nearest method above it).</summary>
    private Dictionary<string, List<CoverageLine>> OwnedLines(string? uri, string? ns, string cls)
    {
        var owned = new Dictionary<string, List<CoverageLine>>(StringComparer.Ordinal);
        if (uri is null || !_files.TryGetValue(uri, out var lines))
        {
            return owned;
        }

        var starts = _methods.Values
            .Where(m => string.Equals(m.Uri, uri, StringComparison.OrdinalIgnoreCase))
            .OrderBy(m => m.Line)
            .ToList();
        foreach (var line in lines.Values)
        {
            var owner = starts.LastOrDefault(m => m.Line <= line.Line) ?? starts.FirstOrDefault();
            if (owner is not null && owner.Namespace == ns && owner.Class == cls)
            {
                if (!owned.TryGetValue(owner.Method, out var list))
                {
                    owned[owner.Method] = list = [];
                }

                list.Add(line);
            }
        }

        foreach (var list in owned.Values)
        {
            list.Sort((a, b) => a.Line.CompareTo(b.Line));
        }

        return owned;
    }

    private static CoverageCounts Count(IEnumerable<CoverageLine> lines)
    {
        var counts = default(CoverageCounts);
        foreach (var line in lines)
        {
            counts += new CoverageCounts(line.Hits > 0 ? 1 : 0, 1, line.BranchesCovered, line.BranchesTotal);
        }

        return counts;
    }

    private static XAttribute[] Rates(CoverageCounts counts) =>
    [
        new("line-rate", Rate(counts.LinesCovered, counts.LinesTotal)),
        new("branch-rate", Rate(counts.BranchesCovered, counts.BranchesTotal)),
    ];

    private static string Rate(int covered, int total) =>
        (total == 0 ? 1.0 : (double)covered / total).ToString("0.####", CultureInfo.InvariantCulture);

    private static XElement LineElement(CoverageLine line)
    {
        var element = new XElement(
            "line",
            new XAttribute("number", line.Line + 1),
            new XAttribute("hits", line.Hits),
            new XAttribute("branch", line.BranchesTotal > 0 ? "True" : "False"));
        if (line.BranchesTotal > 0)
        {
            var percent = (100 * line.BranchesCovered / line.BranchesTotal).ToString(CultureInfo.InvariantCulture);
            element.Add(new XAttribute("condition-coverage", $"{percent}% ({line.BranchesCovered}/{line.BranchesTotal})"));
        }

        return element;
    }
}
