using System.Globalization;

namespace Codalus.Workbench.Testing;

/// <summary>How the Test Explorer arranges tests.</summary>
public enum TestGrouping
{
    /// <summary>The controllers' tree: project, namespace, class, method, cases.</summary>
    Hierarchy,

    /// <summary>Project, then tests.</summary>
    Project,

    /// <summary>Failed, Passed, Skipped, Not Run, then tests.</summary>
    Outcome,

    /// <summary>Each trait (name: value), then tests; tests without traits under "No traits".</summary>
    Trait,

    /// <summary>Slow (≥ 1 s), Medium (≥ 100 ms), Fast, Not run, then tests.</summary>
    Duration,
}

/// <summary>
/// The Test Explorer's filter: whitespace-separated terms, all of which must match. <c>outcome:failed</c>,
/// <c>project:Name</c>, <c>trait:Name</c> and <c>trait:Name=Value</c> match fields; other terms match the test's full
/// name (case-insensitive substring). Quotes group words: <c>"two words"</c>. A leading <c>-</c> negates a term.
/// Repeating a field ORs its values (<c>outcome:failed outcome:skipped</c>).
/// </summary>
public sealed class TestFilter
{
    private readonly List<(string Field, string Value, bool Negated)> _terms = [];

    // The terms by field and negation, grouped once: each group must match (or, negated, must not).
    private (bool Negated, (string Field, string Value, bool Negated)[] Terms)[] _groups = [];

    public static TestFilter Empty { get; } = new();

    public bool IsEmpty => _terms.Count == 0;

    public static TestFilter Parse(string? text)
    {
        var filter = new TestFilter();
        foreach (var token in Tokens(text ?? string.Empty))
        {
            var negated = token.StartsWith('-') && token.Length > 1;
            var term = negated ? token[1..] : token;
            var colon = term.IndexOf(':', StringComparison.Ordinal);
            var field = colon > 0 ? term[..colon].ToLowerInvariant() : string.Empty;
            if (field is "outcome" or "project" or "trait")
            {
                filter._terms.Add((field, term[(colon + 1)..], negated));
            }
            else
            {
                filter._terms.Add((string.Empty, term, negated));
            }
        }

        filter._groups = [.. filter._terms.GroupBy(t => (t.Field, t.Negated)).Select(g => (g.Key.Negated, g.ToArray()))];
        return filter;
    }

    private static IEnumerable<string> Tokens(string text)
    {
        var current = new System.Text.StringBuilder();
        var quoted = false;
        foreach (var c in text)
        {
            if (c == '"')
            {
                quoted = !quoted;
            }
            else if (char.IsWhiteSpace(c) && !quoted)
            {
                if (current.Length > 0)
                {
                    yield return current.ToString();
                    current.Clear();
                }
            }
            else
            {
                current.Append(c);
            }
        }

        if (current.Length > 0)
        {
            yield return current.ToString();
        }
    }

    /// <summary>Whether a test (leaf) matches.</summary>
    public bool Matches(TestNode leaf)
    {
        foreach (var (negated, terms) in _groups)
        {
            var any = terms.Any(t => Term(leaf, t.Field, t.Value));
            if (negated ? any : !any)
            {
                return false;
            }
        }

        return true;
    }

    private static bool Term(TestNode leaf, string field, string value) => field switch
    {
        "outcome" => OutcomeMatches(leaf.Outcome, value),
        "project" => leaf.Root.Label.Contains(value, StringComparison.OrdinalIgnoreCase),
        "trait" => TraitMatches(leaf, value),
        _ => leaf.FullLabel.Contains(value, StringComparison.OrdinalIgnoreCase)
             || (leaf.Description?.Contains(value, StringComparison.OrdinalIgnoreCase) ?? false),
    };

    private static bool OutcomeMatches(TestOutcome outcome, string value) => value.ToLowerInvariant() switch
    {
        "failed" or "fail" => outcome.IsFailure(),
        "error" or "errored" => outcome == TestOutcome.Errored,
        "passed" or "pass" => outcome == TestOutcome.Passed,
        "skipped" or "skip" or "ignored" => outcome == TestOutcome.Skipped,
        "notrun" or "none" => outcome == TestOutcome.None,
        "running" => outcome is TestOutcome.Running or TestOutcome.Queued,
        _ => false,
    };

    private static bool TraitMatches(TestNode leaf, string value)
    {
        var equals = value.IndexOf('=', StringComparison.Ordinal);
        var name = equals >= 0 ? value[..equals] : value;
        var expected = equals >= 0 ? value[(equals + 1)..] : null;
        for (var node = leaf; node is not null; node = node.Parent)
        {
            foreach (var trait in node.Traits)
            {
                if ((trait.Name.Equals(name, StringComparison.OrdinalIgnoreCase) || (expected is null && (trait.Value?.Equals(name, StringComparison.OrdinalIgnoreCase) ?? false)))
                    && (expected is null || string.Equals(trait.Value, expected, StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }
            }
        }

        return false;
    }
}

/// <summary>A node of the Test Explorer's arranged tree: a group, or a test item (<see cref="Node"/>).</summary>
public sealed class TestGroup
{
    internal TestGroup(string key, string label, TestNode? node)
    {
        Key = key;
        Label = label;
        Node = node;
    }

    /// <summary>Stable across rebuilds (expansion and selection are kept by it).</summary>
    public string Key { get; }

    public string Label { get; }

    /// <summary>The test item shown by this row; null for synthetic groups (outcome, trait, duration).</summary>
    public TestNode? Node { get; }

    public List<TestGroup> Children { get; } = [];

    /// <summary>The worst outcome of the tests under it.</summary>
    public TestOutcome Outcome { get; internal set; }

    /// <summary>The number of tests (leaves) under it.</summary>
    public int Tests { get; internal set; }

    /// <summary>The keys of the test items to run for this row.</summary>
    public IEnumerable<string> RunKeys() => Node is { } node ? [node.Key] : Children.SelectMany(c => c.RunKeys());
}

/// <summary>Arranges the tests for the Test Explorer: filter, playlist and grouping (ADR 0010).</summary>
public static class TestProjection
{
    /// <summary>
    /// The arranged tree. <paramref name="playlist"/> (node keys) keeps only the tests under those items. Call inside
    /// <see cref="TestService.Read{T}"/>.
    /// </summary>
    public static IReadOnlyList<TestGroup> Build(IReadOnlyList<TestNode> roots, TestGrouping grouping, TestFilter filter, IReadOnlySet<string>? playlist = null)
    {
        bool Included(TestNode leaf)
        {
            if (playlist is not null)
            {
                var inList = false;
                for (var node = leaf; node is not null && !inList; node = node.Parent)
                {
                    inList = playlist.Contains(node.Key);
                }

                if (!inList)
                {
                    return false;
                }
            }

            return filter.Matches(leaf);
        }

        if (grouping == TestGrouping.Hierarchy)
        {
            return [.. roots.Select(r => Mirror(r, Included)).OfType<TestGroup>()];
        }

        var leaves = roots.SelectMany(r => r.Leaves()).Where(Included).ToList();
        var groups = new Dictionary<string, TestGroup>(StringComparer.Ordinal);
        var order = new List<TestGroup>();
        foreach (var leaf in leaves)
        {
            foreach (var (key, label, sort) in Keys(leaf, grouping))
            {
                if (!groups.TryGetValue(key, out var group))
                {
                    groups[key] = group = new TestGroup("g:" + key, label, null);
                    group.Outcome = (TestOutcome)sort; // temporary sort key, replaced below
                    order.Add(group);
                }

                group.Children.Add(new TestGroup(group.Key + "|" + leaf.Key, FlatLabel(leaf), leaf) { Outcome = leaf.Outcome, Tests = 1 });
            }
        }

        var sorted = order.OrderBy(g => (int)g.Outcome).ThenBy(g => g.Label, StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var group in sorted)
        {
            group.Children.Sort((a, b) => string.Compare(a.Label, b.Label, StringComparison.OrdinalIgnoreCase));
            group.Tests = group.Children.Count;
            group.Outcome = group.Children.Max(c => c.Outcome);
        }

        return sorted;
    }

    private static TestGroup? Mirror(TestNode node, Func<TestNode, bool> included)
    {
        if (node.IsLeaf)
        {
            return included(node) ? new TestGroup(node.Key, node.Label, node) { Outcome = node.Outcome, Tests = 1 } : null;
        }

        var group = new TestGroup(node.Key, node.Label, node);
        foreach (var child in node.Children)
        {
            if (Mirror(child, included) is { } mirrored)
            {
                group.Children.Add(mirrored);
            }
        }

        if (group.Children.Count == 0)
        {
            return null;
        }

        // The worst outcome; a group whose tests all passed or were skipped (some passed) is Passed.
        var tests = 0;
        var worst = TestOutcome.None;
        var anyPassed = false;
        var allPassedOrSkipped = true;
        foreach (var child in group.Children)
        {
            tests += child.Tests;
            worst = child.Outcome > worst ? child.Outcome : worst;
            anyPassed |= child.Outcome == TestOutcome.Passed;
            allPassedOrSkipped &= child.Outcome is TestOutcome.Passed or TestOutcome.Skipped;
        }

        group.Tests = tests;
        group.Outcome = worst == TestOutcome.Skipped && anyPassed && allPassedOrSkipped ? TestOutcome.Passed : worst;
        return group;
    }

    /// <summary>The groups a test goes in, with a sort order.</summary>
    private static IEnumerable<(string Key, string Label, int Sort)> Keys(TestNode leaf, TestGrouping grouping)
    {
        switch (grouping)
        {
            case TestGrouping.Project:
                yield return ("p:" + leaf.Root.Key, leaf.Root.Label, 0);
                break;
            case TestGrouping.Outcome:
                var outcome = leaf.Outcome;
                (string Label, int Sort) bucket = outcome switch
                {
                    TestOutcome.Failed or TestOutcome.Errored => ("Failed", 0),
                    TestOutcome.Running or TestOutcome.Queued => ("Running", 1),
                    TestOutcome.Passed => ("Passed", 3),
                    TestOutcome.Skipped => ("Skipped", 4),
                    _ => ("Not Run", 2),
                };
                yield return ("o:" + bucket.Label, bucket.Label, bucket.Sort);
                break;
            case TestGrouping.Trait:
                var traits = new List<(string, string)>();
                for (var node = leaf; node is not null; node = node.Parent)
                {
                    traits.AddRange(node.Traits.Select(t => (t.Name, t.Value is null ? t.Name : $"{t.Name}: {t.Value}")));
                }

                if (traits.Count == 0)
                {
                    yield return ("t:", "No traits", 1);
                }

                foreach (var (_, label) in traits.Distinct())
                {
                    yield return ("t:" + label, label, 0);
                }

                break;
            case TestGrouping.Duration:
                (string Label, int Sort) speed = leaf.Result?.Duration switch
                {
                    null => ("Not run", 3),
                    { TotalSeconds: >= 1 } => ("Slow (≥ 1 s)", 0),
                    { TotalMilliseconds: >= 100 } => ("Medium (≥ 100 ms)", 1),
                    _ => ("Fast (< 100 ms)", 2),
                };
                yield return ("d:" + speed.Label, speed.Label, speed.Sort);
                break;
            default:
                yield return ("all", "All tests", 0);
                break;
        }
    }

    /// <summary>A test's label in flat groupings: class, method and case, without the project and namespace.</summary>
    public static string FlatLabel(TestNode leaf)
    {
        var parts = new List<string>();
        for (var node = leaf; node?.Parent is not null; node = node.Parent)
        {
            parts.Insert(0, node.Label);
        }

        // project › namespace › class › method › case: drop the namespace, and a method whose case repeats its name.
        if (parts.Count >= 3)
        {
            parts.RemoveAt(0);
        }

        if (parts.Count >= 2 && parts[^1].StartsWith(parts[^2], StringComparison.Ordinal))
        {
            parts.RemoveAt(parts.Count - 2);
        }

        return string.Join('.', parts);
    }

    /// <summary>The summary line: total, passed, failed, skipped, not run.</summary>
    public static string Summary(IReadOnlyList<TestNode> roots)
    {
        // One walk: the leaves were listed, then counted four times.
        int total = 0, passed = 0, failed = 0, skipped = 0;
        foreach (var root in roots)
        {
            foreach (var leaf in root.Leaves())
            {
                total++;
                switch (leaf.Outcome)
                {
                    case TestOutcome.Passed:
                        passed++;
                        break;
                    case TestOutcome.Failed or TestOutcome.Errored:
                        failed++;
                        break;
                    case TestOutcome.Skipped:
                        skipped++;
                        break;
                }
            }
        }

        var culture = CultureInfo.CurrentCulture;
        return string.Format(
            culture,
            "{0} tests · {1} passed · {2} failed · {3} skipped · {4} not run",
            total,
            passed,
            failed,
            skipped,
            total - passed - failed - skipped); // not run, queued or running
    }
}
