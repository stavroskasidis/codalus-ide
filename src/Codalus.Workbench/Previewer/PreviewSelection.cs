using Codalus.LanguageServices.Protocol;
using Codalus.Projects.Protocol;

namespace Codalus.Workbench.Previewer;

/// <summary>
/// The previewer's selection both ways (ADR 0048): the XAML's logical tree (<c>codalus/xaml/logicalTree</c>) against the
/// running preview's (the agent's paths). A step is a type and its index among its siblings of that type, which is
/// what the XAML and the logical tree agree on: an element's children in XAML are its logical children, in order.
/// </summary>
public static class PreviewSelection
{
    /// <summary>The deepest node whose element contains <paramref name="position"/>, and its path from the XAML's root.</summary>
    public static (XamlLogicalNode Node, IReadOnlyList<PreviewPathStep> Path)? At(IReadOnlyList<XamlLogicalNode> roots, Position position)
    {
        if (roots.Count == 0 || !Contains(roots[0].Range, position))
        {
            return null;
        }

        var node = roots[0];
        var path = new List<PreviewPathStep> { new(node.Type, 0, node.Name) };
        while (true)
        {
            var next = node.Children.FirstOrDefault(c => Contains(c.Range, position));
            if (next is null)
            {
                return (node, path);
            }

            path.Add(new PreviewPathStep(next.Type, node.Children.TakeWhile(c => c != next).Count(c => c.Type == next.Type), next.Name));
            node = next;
        }
    }

    /// <summary>
    /// The node a hit in the preview lands on: the hit's path (from the previewed window) aligned at the XAML's root,
    /// then followed as deep as the XAML declares (a button's generated text lands on the button). Null when the root
    /// isn't on the path.
    /// </summary>
    public static XamlLogicalNode? NodeFor(IReadOnlyList<XamlLogicalNode> roots, IReadOnlyList<PreviewPathStep> hit)
    {
        if (roots.Count == 0)
        {
            return null;
        }

        var root = roots[0];
        var start = -1;
        for (var i = 0; i < hit.Count; i++)
        {
            if (hit[i].Type == root.Type)
            {
                start = i;
                break;
            }
        }

        if (start < 0)
        {
            return null;
        }

        var node = root;
        foreach (var step in hit.Skip(start + 1))
        {
            var next = node.Children.Where(c => c.Type == step.Type).Skip(step.Ordinal).FirstOrDefault();
            if (next is null)
            {
                break;
            }

            node = next;
        }

        return node;
    }

    private static bool Contains(LanguageServices.Protocol.Range range, Position position) =>
        Compare(range.Start, position) <= 0 && Compare(position, range.End) < 0;

    private static int Compare(Position a, Position b) => a.Line != b.Line ? a.Line.CompareTo(b.Line) : a.Character.CompareTo(b.Character);
}

/// <summary>
/// The XAML compiler's errors (Avalonia's AVLN…, M19a; WPF's markup compiler's MC…, M40a) from the last build, as
/// editor diagnostics of their <c>.axaml</c> and <c>.xaml</c> files: squiggles where the build put them, until the file
/// changes or the next build.
/// </summary>
public static class XamlBuildDiagnostics
{
    /// <summary>The diagnostics store's source for them.</summary>
    public const string Source = "axaml.build";

    public static IReadOnlyDictionary<string, Diagnostic[]> ByFile(IEnumerable<BuildDiagnosticInfo> diagnostics) =>
        diagnostics
            .Where(d => d.File is { } file && (file.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)))
            .GroupBy(d => d.File!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => g.Select(d =>
                {
                    var start = new Position(Math.Max(0, d.Line - 1), Math.Max(0, d.Column - 1));
                    var end = d.EndLine > 0 && (d.EndLine > d.Line || d.EndColumn > d.Column)
                        ? new Position(d.EndLine - 1, Math.Max(0, d.EndColumn - 1))
                        : start with { Character = start.Character + 1 };
                    return new Diagnostic(
                        new LanguageServices.Protocol.Range(start, end),
                        d.Severity == BuildSeverities.Error ? DiagnosticSeverity.Error : DiagnosticSeverity.Warning,
                        d.Code,
                        "build",
                        d.Message);
                }).DistinctBy(d => (d.Range.Start, d.Code, d.Message)).ToArray(),
                StringComparer.OrdinalIgnoreCase);
}
