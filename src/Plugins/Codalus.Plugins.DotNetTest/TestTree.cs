using Codalus.Plugins.Proposed;

namespace Codalus.Plugins.DotNetTest;

/// <summary>What a test item stands for, so a run request can be resolved to runner tests.</summary>
internal sealed record ItemInfo(ItemKind Kind, string Project, string? Namespace = null, string? Type = null, string? Method = null, string? TargetFramework = null, string? RunnerKey = null)
{
    /// <summary>Whether <paramref name="test"/> is under this item.</summary>
    public bool Contains(RunnerTest test) => Kind switch
    {
        ItemKind.Project => test.Project == Project,
        ItemKind.Namespace => test.Project == Project && test.Type is { } type && (ManagedNames.Namespace(type) ?? string.Empty) == (Namespace ?? string.Empty),
        ItemKind.Type => test.Project == Project && test.Type == Type,
        ItemKind.Method => test.Project == Project && test.Type == Type && test.Method == Method && (TargetFramework is null || test.TargetFramework == TargetFramework),
        ItemKind.Test => test.Project == Project && test.Key == RunnerKey && test.TargetFramework == TargetFramework,
        ItemKind.Unknown => test.Project == Project && test.Type is null,
        _ => false,
    };
}

internal enum ItemKind
{
    Project,
    Namespace,
    Type,
    Method,
    Test,

    /// <summary>Runner tests without managed names.</summary>
    Unknown,
}

/// <summary>Everything known about one test project: source candidates and runner tests.</summary>
internal sealed class ProjectTests(string path, string name)
{
    public string Path { get; } = path;

    public string Name { get; set; } = name;

    public string? Platform { get; set; }

    /// <summary>Frameworks to test (all, or the one chosen in <c>testing.targetFramework</c>).</summary>
    public IReadOnlyList<string> Frameworks { get; set; } = [];

    /// <summary>Source candidates per document URI.</summary>
    public Dictionary<string, IReadOnlyList<TestCandidate>> Candidates { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Runner tests per framework ("" when the project has one).</summary>
    public Dictionary<string, IReadOnlyList<RunnerTest>> RunnerTests { get; } = new(StringComparer.Ordinal);

    /// <summary>The last discovery problem, shown on the project item.</summary>
    public string? Error { get; set; }

    /// <summary>
    /// Why the project's tests can't run on this machine (M40a): every target framework targets Windows and this isn't
    /// Windows. Its tests show from source, and a run says why it skips them.
    /// </summary>
    public string? WindowsOnly { get; set; }

    public IEnumerable<RunnerTest> AllRunnerTests => RunnerTests.Values.SelectMany(t => t);
}

/// <summary>
/// Builds the controller's items from <see cref="ProjectTests"/> (ADR 0010): project → namespace → class → method, with
/// theory cases, data rows and per-framework results under their method. Items keep their identity across rebuilds
/// (ids are stable), so only real changes reach the shell.
/// </summary>
internal sealed class TestTree(ITestController controller)
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, ItemInfo> _info = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _itemOfRunnerTest = new(StringComparer.Ordinal);

    // The keys of _itemOfRunnerTest by the runner test's project: a project's rebuild forgets its own instead of looking
    // through every runner test of the solution, for each project (M56).
    private readonly Dictionary<string, HashSet<string>> _runnerKeysOfProject = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How many runner tests are mapped to an item (tests).</summary>
    internal int MappedRunnerTests
    {
        get
        {
            lock (_gate)
            {
                return _itemOfRunnerTest.Count;
            }
        }
    }

    public ItemInfo? Info(string itemId)
    {
        lock (_gate)
        {
            return _info.GetValueOrDefault(itemId);
        }
    }

    /// <summary>The item that reports results for <paramref name="test"/>, if the tree has one.</summary>
    public TestItem? ItemFor(RunnerTest test)
    {
        string? id;
        lock (_gate)
        {
            id = _itemOfRunnerTest.GetValueOrDefault(RunnerKey(test));
        }

        return id is null ? null : controller.Find(id);
    }

    private static string RunnerKey(RunnerTest test) => test.Project + "|" + test.TargetFramework + "|" + test.Key;

    /// <summary>Rebuilds the items of the given projects and removes items of projects no longer listed.</summary>
    public void Update(IReadOnlyCollection<ProjectTests> projects)
    {
        lock (_gate)
        {
            var roots = new List<TestItem>();
            foreach (var project in projects.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
            {
                roots.Add(BuildProject(project));
            }

            controller.Items.Replace(roots);
            var live = projects.Select(p => p.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var key in _info.Where(i => !live.Contains(i.Value.Project)).Select(i => i.Key).ToList())
            {
                _info.Remove(key);
            }

            foreach (var project in _runnerKeysOfProject.Keys.Where(p => !live.Contains(p)).ToList())
            {
                ForgetRunnerTests(project);
            }
        }
    }

    // Under _gate: the project's runner tests leave the index (a rebuild maps them again).
    private void ForgetRunnerTests(string project)
    {
        if (_runnerKeysOfProject.Remove(project, out var mapped))
        {
            foreach (var key in mapped)
            {
                _itemOfRunnerTest.Remove(key);
            }
        }
    }

    private TestItem BuildProject(ProjectTests project)
    {
        var id = project.Path;
        var item = Item(id, project.Name, new ItemInfo(ItemKind.Project, project.Path));
        item.Description = project.Platform switch { "mtp" => "Microsoft.Testing.Platform", "vstest" => "VSTest", _ => null };
        item.Error = project.Error;
        item.Uri = new Uri(project.Path).AbsoluteUri;

        ForgetRunnerTests(project.Path);

        // Methods from source, then from runners; a runner test whose type and method are known joins its source method.
        var methods = new Dictionary<(string Type, string Method), MethodEntry>();
        var types = new Dictionary<string, (string? Uri, TestRange? Range)>(StringComparer.Ordinal);
        foreach (var (uri, candidates) in project.Candidates)
        {
            foreach (var candidate in candidates)
            {
                if (candidate.Kind == TestCandidate.ClassKind)
                {
                    types.TryAdd(candidate.Type, (uri, candidate.Range));
                }
                else
                {
                    var entry = methods.TryGetValue((candidate.Type, candidate.Name), out var existing) ? existing : methods[(candidate.Type, candidate.Name)] = new MethodEntry();
                    entry.Uri ??= uri;
                    entry.Range ??= candidate.Range;
                    entry.Parameterized |= candidate.Parameterized;
                    types.TryAdd(candidate.Type, (uri, null));
                }
            }
        }

        var unknown = new List<RunnerTest>();
        var multiFramework = project.RunnerTests.Keys.Count(k => k.Length > 0) > 1;
        foreach (var test in project.AllRunnerTests)
        {
            if (test.Type is null || test.Method is null)
            {
                unknown.Add(test);
                continue;
            }

            if (!methods.TryGetValue((test.Type, test.Method), out var entry))
            {
                methods[(test.Type, test.Method)] = entry = new MethodEntry();
            }

            entry.Tests.Add(test);
            if (entry.Uri is null && test.File is { } file)
            {
                entry.Uri = new Uri(file).AbsoluteUri;
                entry.Range = test.Line is { } line and > 0 ? new TestRange(line - 1, 0, line - 1, 0) : null;
            }

            types.TryAdd(test.Type, (entry.Uri, null));
        }

        var namespaces = new List<TestItem>();
        foreach (var nsGroup in methods.GroupBy(m => ManagedNames.Namespace(m.Key.Type) ?? string.Empty).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var nsId = $"{project.Path}|n:{nsGroup.Key}";
            var ns = Item(nsId, nsGroup.Key.Length == 0 ? "(global namespace)" : nsGroup.Key, new ItemInfo(ItemKind.Namespace, project.Path, Namespace: nsGroup.Key));
            var classes = new List<TestItem>();
            foreach (var typeGroup in nsGroup.GroupBy(m => m.Key.Type).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                var typeId = $"{project.Path}|c:{typeGroup.Key}";
                var type = Item(typeId, ManagedNames.TypeLabel(typeGroup.Key), new ItemInfo(ItemKind.Type, project.Path, Type: typeGroup.Key));
                if (types.TryGetValue(typeGroup.Key, out var location))
                {
                    type.Uri = location.Uri;
                    type.Range = location.Range;
                }

                var methodItems = new List<TestItem>();
                foreach (var (key, entry) in typeGroup.OrderBy(m => SortKey(m.Value), StringComparer.Ordinal).ThenBy(m => m.Key.Method, StringComparer.Ordinal))
                {
                    methodItems.Add(BuildMethod(project, key.Type, key.Method, entry, multiFramework));
                }

                type.Children.Replace(methodItems);
                classes.Add(type);
            }

            ns.Children.Replace(classes);
            namespaces.Add(ns);
        }

        if (unknown.Count > 0)
        {
            var other = Item($"{project.Path}|other", "Other tests", new ItemInfo(ItemKind.Unknown, project.Path));
            other.Children.Replace(unknown.Select(t => Leaf(project, t, t.DisplayName, multiFramework)));
            namespaces.Add(other);
        }

        item.Children.Replace(namespaces);
        return item;
    }

    private static string SortKey(MethodEntry entry) => entry.Range is { } range ? range.StartLine.ToString("D8", System.Globalization.CultureInfo.InvariantCulture) : "~";

    private TestItem BuildMethod(ProjectTests project, string typeName, string methodName, MethodEntry entry, bool multiFramework)
    {
        var id = $"{project.Path}|m:{typeName}.{methodName}";
        var method = Item(id, methodName, new ItemInfo(ItemKind.Method, project.Path, Type: typeName, Method: methodName));
        method.Uri = entry.Uri;
        method.Range = entry.Range;
        var tests = entry.Tests;
        method.Traits = tests.SelectMany(t => t.Traits).Distinct().ToList();

        // One runner test that is the method itself: the method item is the test.
        if (tests.Count == 1 && !multiFramework && !entry.Parameterized && IsPlain(tests[0], methodName))
        {
            Map(tests[0], id);
            method.Children.Replace([]);
            return method;
        }

        if (multiFramework)
        {
            var frameworks = new List<TestItem>();
            foreach (var group in tests.GroupBy(t => t.TargetFramework ?? string.Empty).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                var frameworkId = $"{id}|tfm:{group.Key}";
                var framework = Item(frameworkId, group.Key, new ItemInfo(ItemKind.Method, project.Path, Type: typeName, Method: methodName, TargetFramework: group.Key));
                var cases = group.ToList();
                if (cases.Count == 1 && !entry.Parameterized && IsPlain(cases[0], methodName))
                {
                    Map(cases[0], frameworkId);
                    framework.Children.Replace([]);
                }
                else
                {
                    framework.Children.Replace(cases.Select(t => Leaf(project, t, CaseLabel(t, typeName), multiFramework)));
                }

                frameworks.Add(framework);
            }

            method.Children.Replace(frameworks);
            return method;
        }

        method.Children.Replace(tests.Select(t => Leaf(project, t, CaseLabel(t, typeName), multiFramework)));
        return method;
    }

    private TestItem Leaf(ProjectTests project, RunnerTest test, string label, bool multiFramework)
    {
        var id = $"{project.Path}|t:{test.TargetFramework}|{test.Key}";
        var leaf = Item(id, label, new ItemInfo(ItemKind.Test, project.Path, Type: test.Type, Method: test.Method, TargetFramework: test.TargetFramework, RunnerKey: test.Key));
        leaf.Uri = test.File is { } file ? new Uri(file).AbsoluteUri : null;
        leaf.Range = test.Line is { } line and > 0 ? new TestRange(line - 1, 0, line - 1, 0) : null;
        leaf.Traits = test.Traits;
        leaf.Description = multiFramework ? test.TargetFramework : null;
        leaf.Children.Replace([]);
        Map(test, id);
        return leaf;
    }

    /// <summary>A runner test that is the method itself (not a theory case or data row).</summary>
    private static bool IsPlain(RunnerTest test, string methodName)
    {
        var display = test.DisplayName;
        return !display.Contains('(', StringComparison.Ordinal) && (display == methodName || display.EndsWith("." + methodName, StringComparison.Ordinal));
    }

    /// <summary>The case's label without its class prefix: <c>Divides(a: 6, b: 3, expected: 2)</c>.</summary>
    private static string CaseLabel(RunnerTest test, string typeName)
    {
        var display = test.DisplayName;
        var dotted = typeName.Replace('+', '.') + ".";
        return display.StartsWith(dotted, StringComparison.Ordinal) ? display[dotted.Length..] : display;
    }

    private void Map(RunnerTest test, string itemId)
    {
        var key = RunnerKey(test);
        _itemOfRunnerTest[key] = itemId;
        if (!_runnerKeysOfProject.TryGetValue(test.Project, out var keys))
        {
            _runnerKeysOfProject[test.Project] = keys = new HashSet<string>(StringComparer.Ordinal);
        }

        keys.Add(key);
    }

    private TestItem Item(string id, string label, ItemInfo info)
    {
        _info[id] = info;
        var item = controller.Find(id) ?? new TestItem(id, label);
        item.Label = label;
        return item;
    }

    private sealed class MethodEntry
    {
        public string? Uri { get; set; }

        public TestRange? Range { get; set; }

        public bool Parameterized { get; set; }

        public List<RunnerTest> Tests { get; } = [];
    }
}
