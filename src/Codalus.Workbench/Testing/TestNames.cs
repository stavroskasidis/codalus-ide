namespace Codalus.Workbench.Testing;

/// <summary>
/// Fully qualified names of test items, for matching tests from files (TRX results, the classic IDE's playlists) to the
/// tree. A .NET controller's tree is project › namespace › class › method (› framework) (› case); the name is
/// <c>Namespace.Class.Method</c>, and cases also match by display name.
/// </summary>
public static class TestNames
{
    /// <summary><c>Namespace.Class.Method</c> for a method or case node (null for groups above methods).</summary>
    public static string? FullyQualifiedName(TestNode node)
    {
        var path = new List<TestNode>();
        for (var n = node; n is not null; n = n.Parent)
        {
            path.Insert(0, n);
        }

        // [project, namespace, class, method, ...]
        if (path.Count < 4)
        {
            return null;
        }

        var ns = path[1].Label.StartsWith('(') ? null : path[1].Label;
        var cls = path[2].Label;
        var method = path[3].Label;
        return ns is null ? $"{cls}.{method}" : $"{ns}.{cls}.{method}";
    }

    /// <summary>The class's full name (<c>Namespace.Class</c>) for nodes at or below a class.</summary>
    public static string? ClassName(TestNode node) =>
        FullyQualifiedName(node) is { } fqn ? fqn[..fqn.LastIndexOf('.')] : null;

    /// <summary>
    /// Whether <paramref name="leaf"/> is the test a file names: the same method, and for cases (several tests under a
    /// method) the same display name.
    /// </summary>
    public static bool Matches(TestNode leaf, string fullyQualifiedName, string? displayName)
    {
        if (FullyQualifiedName(leaf) is not { } fqn)
        {
            return false;
        }

        var name = StripArguments(fullyQualifiedName).Replace('+', '.');
        if (!string.Equals(fqn, name, StringComparison.Ordinal))
        {
            return false;
        }

        var isCase = leaf.Parent is { } parent && parent.Children.Count > 1 && Depth(leaf) > 4;
        return !isCase || displayName is null || displayName.EndsWith(leaf.Label, StringComparison.Ordinal) || leaf.Label.EndsWith(StripPrefix(displayName), StringComparison.Ordinal);
    }

    private static int Depth(TestNode node)
    {
        var depth = 0;
        for (var n = node; n is not null; n = n.Parent)
        {
            depth++;
        }

        return depth;
    }

    private static string StripArguments(string name) => name.IndexOf('(', StringComparison.Ordinal) is var paren and >= 0 ? name[..paren] : name;

    /// <summary><c>Ns.Class.Method(1, 2)</c> → <c>Method(1, 2)</c>.</summary>
    private static string StripPrefix(string displayName)
    {
        var paren = displayName.IndexOf('(', StringComparison.Ordinal);
        var head = paren >= 0 ? displayName[..paren] : displayName;
        var dot = head.LastIndexOf('.');
        return dot >= 0 ? displayName[(dot + 1)..] : displayName;
    }
}
