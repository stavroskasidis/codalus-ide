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
    public static bool Matches(TestNode leaf, string fullyQualifiedName, string? displayName) =>
        FullyQualifiedName(leaf) is { } fqn && string.Equals(fqn, Normalize(fullyQualifiedName), StringComparison.Ordinal) && MatchesCase(leaf, displayName);

    /// <summary>
    /// <paramref name="leaves"/> by fully qualified name, to match many names at once (a TRX file's results, a playlist's
    /// tests): <see cref="Matches"/> for each name and each test built every test's name again, which froze the window for
    /// seconds on 5,000 results against 5,000 tests (M56).
    /// </summary>
    public static TestNameIndex Index(IEnumerable<TestNode> leaves) => new(leaves);

    /// <summary>A file's name as the tree's: without arguments, nested classes with dots.</summary>
    internal static string Normalize(string fullyQualifiedName) => StripArguments(fullyQualifiedName).Replace('+', '.');

    /// <summary>Whether <paramref name="leaf"/>, of the name a file gives, is its test: a case (one of several under a method) by display name.</summary>
    internal static bool MatchesCase(TestNode leaf, string? displayName)
    {
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

/// <summary>Tests by fully qualified name, each name's in tree order (<see cref="TestNames.Index"/>).</summary>
public sealed class TestNameIndex
{
    private readonly Dictionary<string, List<TestNode>> _byName = new(StringComparer.Ordinal);

    internal TestNameIndex(IEnumerable<TestNode> leaves)
    {
        foreach (var leaf in leaves)
        {
            if (TestNames.FullyQualifiedName(leaf) is not { } name)
            {
                continue;
            }

            if (!_byName.TryGetValue(name, out var named))
            {
                _byName[name] = named = [];
            }

            named.Add(leaf);
        }
    }

    /// <summary>The tests a file's name selects, in tree order: those <see cref="TestNames.Matches"/> accepts.</summary>
    public IEnumerable<TestNode> Matches(string fullyQualifiedName, string? displayName) =>
        _byName.TryGetValue(TestNames.Normalize(fullyQualifiedName), out var named) ? named.Where(leaf => TestNames.MatchesCase(leaf, displayName)) : [];
}
