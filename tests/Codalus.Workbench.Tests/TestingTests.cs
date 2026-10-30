using System.Xml.Linq;
using Codalus.Plugins.Protocol;
using Codalus.Workbench.Plugins;
using Codalus.Workbench.Testing;

namespace Codalus.Workbench.Tests;

/// <summary>The Test Explorer's model (ADR 0010): items, runs, results, filters, grouping, TRX and playlists.</summary>
public sealed class TestingTests : IDisposable
{
    private const string Controller = "codalus.dotnettest:dotnet";
    private readonly TestService _service = new();
    private readonly FakeConnection _connection = new();

    public TestingTests()
    {
        _service.ControllerCreated(new TestControllerParams("codalus.dotnettest", Controller, ".NET"), _connection);
        _service.ProfilesUpdated(new TestProfilesParams(Controller, [new TestProfileDto("p1", "Run", "run", true), new TestProfileDto("p2", "Debug", "debug", true)], CanRefresh: true));
        _service.ItemsUpdated(new TestItemsUpdateParams(Controller,
        [
            Item("P", null, "Tests.Unit"),
            Item("P|n:Tests", "P", "Tests"),
            Item("P|c:Tests.Calc", "P|n:Tests", "Calc"),
            Item("P|m:Adds", "P|c:Tests.Calc", "Adds", traits: [new TestTraitDto("Category", "Math")], uri: "file:///c:/src/Calc.cs", line: 10),
            Item("P|m:Fails", "P|c:Tests.Calc", "Fails", uri: "file:///c:/src/Calc.cs", line: 20),
            Item("P|m:Divides", "P|c:Tests.Calc", "Divides", uri: "file:///c:/src/Calc.cs", line: 30),
            Item("P|t:1", "P|m:Divides", "Divides(a: 6, b: 3)"),
            Item("P|t:2", "P|m:Divides", "Divides(a: 9, b: 3)"),
            Item("Q", null, "Other.Tests"),
            Item("Q|n:Other", "Q", "Other"),
            Item("Q|c:Other.Slow", "Q|n:Other", "Slow"),
            Item("Q|m:Waits", "Q|c:Other.Slow", "Waits"),
        ], []));
    }

    public void Dispose() => _service.Dispose();

    private static TestItemDto Item(string id, string? parent, string label, TestTraitDto[]? traits = null, string? uri = null, int? line = null) =>
        new(id, parent, label, null, uri, line is { } l ? new RangeDto(l, 4, l + 3, 5) : null, traits, null, null);

    private TestNode Node(string id) => _service.Find(TestNode.KeyOf(Controller, id))!;

    private void Report(string runId, params (string Id, string State, double? Ms)[] states) =>
        _service.RunUpdated(new TestRunUpdateParams(runId, [.. states.Select(s => new TestStateDto(s.Id, s.State, s.Ms, s.State == TestStates.Failed ? [new TestMessageDto("boom", "4", "5", "   at Tests.Calc.Fails() in C:\\src\\Calc.cs:line 21", null, null)] : null))], [], []));

    private void CompleteRun(string runId = "r1")
    {
        _service.RunStarted(new TestRunStartedParams(Controller, runId, "Run", null, "run"));
        Report(runId, ("P|m:Adds", TestStates.Passed, 3), ("P|m:Fails", TestStates.Failed, 12), ("P|t:1", TestStates.Passed, 1), ("P|t:2", TestStates.Skipped, null), ("Q|m:Waits", TestStates.Passed, 1500));
        _service.RunUpdated(new TestRunUpdateParams(runId, [], [new TestOutputDto("P|m:Adds", "hello\n")], []));
        _service.RunEnded(runId);
    }

    [Fact]
    public async Task A_disposed_service_raises_no_more_changes()
    {
        var service = new TestService();
        var raised = 0;
        service.Changed += (_, _) => Interlocked.Increment(ref raised);
        service.ControllerCreated(new TestControllerParams("x", Controller, ".NET"), _connection); // schedules a notification
        service.Dispose();
        service.ControllerDisposed(Controller); // and so would this, after the window closed
        await Task.Delay(TimeSpan.FromMilliseconds(500), TestContext.Current.CancellationToken);
        Assert.Equal(0, Volatile.Read(ref raised));
    }

    [Fact]
    public void Items_form_a_tree_and_can_be_moved_and_removed()
    {
        var roots = _service.Read(r => r.Select(n => n.Label).ToList());
        Assert.Equal(["Tests.Unit", "Other.Tests"], roots);
        Assert.Equal(["Divides(a: 6, b: 3)", "Divides(a: 9, b: 3)"], Node("P|m:Divides").Children.Select(c => c.Label));
        Assert.Equal("Tests.Unit › Tests › Calc › Adds", Node("P|m:Adds").FullLabel);

        // Moving an item to another parent, then removing a subtree.
        _service.ItemsUpdated(new TestItemsUpdateParams(Controller, [Item("P|m:Adds", "Q|c:Other.Slow", "Adds")], []));
        Assert.Equal("Other.Tests › Other › Slow › Adds", Node("P|m:Adds").FullLabel);
        _service.ItemsUpdated(new TestItemsUpdateParams(Controller, [], ["Q"]));
        Assert.Null(_service.Find(TestNode.KeyOf(Controller, "P|m:Adds")));
        Assert.Equal(["Tests.Unit"], _service.Read(r => r.Select(n => n.Label).ToList()));
    }

    [Fact]
    public void Results_aggregate_to_groups_and_keep_history_and_output()
    {
        _service.HistoryLength = 2;
        CompleteRun("r1");
        CompleteRun("r2");
        CompleteRun("r3");

        Assert.Equal(TestOutcome.Passed, Node("P|m:Adds").Outcome);
        Assert.Equal("hello\n", Node("P|m:Adds").Result!.Output);
        Assert.Equal(TimeSpan.FromMilliseconds(12), Node("P|m:Fails").Result!.Duration);
        Assert.Equal("boom", Node("P|m:Fails").Result!.Messages[0].Message);

        // Passed and skipped cases: the theory passed. Any failure fails the class.
        Assert.Equal(TestOutcome.Passed, Node("P|m:Divides").AggregateOutcome());
        Assert.Equal(TestOutcome.Failed, Node("P|c:Tests.Calc").AggregateOutcome());
        Assert.Equal(["r3", "r2"], _service.History(Node("P|m:Adds").Key).Select(h => h.RunId));
        Assert.Equal(2, _service.Runs.Count);
        Assert.Equal(1, _service.Runs[^1].Count(TestOutcome.Failed));
    }

    [Fact]
    public void Tests_still_running_when_a_run_ends_are_not_left_running()
    {
        _service.RunStarted(new TestRunStartedParams(Controller, "r1", "Run", null, "run"));
        Report("r1", ("P|m:Adds", TestStates.Enqueued, null), ("P|m:Fails", TestStates.Started, null));
        Assert.Equal(TestOutcome.Queued, Node("P|m:Adds").Outcome);
        Assert.Equal(TestOutcome.Running, Node("P|m:Fails").Outcome);

        _service.RunEnded("r1");

        Assert.Equal(TestOutcome.None, Node("P|m:Adds").Outcome);
        Assert.Equal(TestOutcome.None, Node("P|m:Fails").Outcome);
    }

    [Fact]
    public async Task Running_sends_the_selection_to_the_controller_and_waits_for_its_runs()
    {
        _connection.OnRun = request =>
        {
            Assert.Equal("p1", request.ProfileId);
            Assert.Equal(["P|m:Fails"], request.Include!);
            _ = Task.Run(async () =>
            {
                await Task.Delay(100);
                _service.RunStarted(new TestRunStartedParams(Controller, "late", "Run", request.RequestId, "run"));
                Report("late", ("P|m:Fails", TestStates.Passed, 1));
                _service.RunEnded("late");
            });
            return ["late"];
        };

        Assert.True(await _service.RunAsync(TestRunKind.Run, [Node("P|m:Fails").Key], TestContext.Current.CancellationToken));

        // The run's results were applied before RunAsync returned, although they arrived after the response.
        Assert.Equal(TestOutcome.Passed, Node("P|m:Fails").Outcome);
        Assert.False(await _service.RunAsync(TestRunKind.Coverage, null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Run_failed_and_repeat_last_choose_the_right_tests()
    {
        CompleteRun();
        var requests = new List<string[]?>();
        _connection.OnRun = request =>
        {
            requests.Add(request.Include);
            return [];
        };

        await _service.RunFailedAsync();
        await _service.RepeatLastAsync();
        await _service.RunNotRunAsync();

        Assert.Equal(["P|m:Fails"], requests[0]!);
        Assert.Equal(["P|m:Fails"], requests[1]!);
        Assert.Null(requests.ElementAtOrDefault(2)); // every test has a result: nothing to run
    }

    [Fact]
    public void Find_at_returns_the_innermost_item_on_a_line()
    {
        Assert.Equal("Adds", _service.FindAt("file:///c:/src/Calc.cs", 11)!.Label);
        Assert.Null(_service.FindAt("file:///c:/src/Calc.cs", 15));
        Assert.Equal(3, _service.InDocument("file:///c:/src/Calc.cs").Count);
    }

    [Fact]
    public void The_document_index_finds_what_a_walk_of_the_trees_finds()
    {
        // Editors ask for a document's items: from the controllers' indexes, not a walk of every test (M56). After random
        // additions, moves, changes of document and removals, in two controllers of which one goes away, the index gives
        // the walk's items in the walk's order (editors keep the first of the items that start on a line).
        using var service = new TestService();
        service.ControllerCreated(new TestControllerParams("x", "c1", "One"), _connection);
        service.ControllerCreated(new TestControllerParams("x", "c2", "Two"), _connection);
        string?[] documents = ["file:///c:/src/A.cs", "file:///C:/SRC/a.cs", "file:///c:/src/B.cs", null];
        var random = new Random(56);
        for (var step = 0; step < 600; step++)
        {
            var controller = random.Next(4) == 0 ? "c2" : "c1";
            var id = random.Next(40);
            if (random.Next(6) == 0)
            {
                service.ItemsUpdated(new TestItemsUpdateParams(controller, [], ["n" + id]));
            }
            else
            {
                // A parent has a smaller number: no cycles.
                var parent = id == 0 || random.Next(4) == 0 ? null : "n" + random.Next(id);
                var line = random.Next(6);
                var range = random.Next(5) == 0 ? null : new RangeDto(line, 0, line + random.Next(3), 0);
                service.ItemsUpdated(new TestItemsUpdateParams(controller, [new TestItemDto("n" + id, parent, "N" + id, null, documents[random.Next(documents.Length)], range, null, null, null)], []));
            }

            if (step == 450)
            {
                service.ControllerDisposed("c2");
            }

            foreach (var document in new[] { "file:///c:/src/a.cs", "file:///c:/src/B.cs" })
            {
                var walked = service.Read(roots => roots.SelectMany(r => r.SelfAndDescendants())
                    .Where(n => n.Range is not null && string.Equals(n.Uri, document, StringComparison.OrdinalIgnoreCase)).ToList());
                Assert.Equal(walked, service.InDocument(document));
                for (var line = 0; line < 9; line++)
                {
                    var innermost = walked.Where(n => n.Range!.StartLine <= line && line <= n.Range.EndLine).OrderBy(n => n.Range!.EndLine - n.Range.StartLine).FirstOrDefault();
                    Assert.Same(innermost, service.FindAt(document, line));
                }
            }
        }
    }

    [Fact]
    public void A_documents_version_changes_with_its_items_and_their_outcomes_only()
    {
        // Editors refresh their glyphs and CodeLens when their document's version changed (M56). The class is declared in
        // Calc.cs: its CodeLens sums up the tests under it, wherever they are declared.
        const string calc = "file:///c:/src/Calc.cs";
        const string other = "file:///c:/src/Other.cs";
        _service.ItemsUpdated(new TestItemsUpdateParams(Controller, [Item("P|c:Tests.Calc", "P|n:Tests", "Calc", uri: calc, line: 5), Item("Q|m:Waits", "Q|c:Other.Slow", "Waits", uri: other, line: 3)], []));
        var seen = new Dictionary<string, long> { [calc] = _service.DocumentVersion(calc), [other] = _service.DocumentVersion(other) };
        string Changed()
        {
            var changed = seen.Keys.Where(uri => _service.DocumentVersion(uri) != seen[uri]).ToList();
            foreach (var uri in changed)
            {
                seen[uri] = _service.DocumentVersion(uri);
            }

            return string.Join(", ", changed.Select(uri => uri[(uri.LastIndexOf('/') + 1)..]));
        }

        _service.RunStarted(new TestRunStartedParams(Controller, "r1", "Run", null, "run"));
        Report("r1", ("Q|m:Waits", TestStates.Passed, 1));
        Assert.Equal("Other.cs", Changed());
        Report("r1", ("P|m:Adds", TestStates.Started, null));
        Assert.Equal("Calc.cs", Changed());

        // Output changes neither: glyphs and CodeLens don't show it. The run's end leaves Adds not run.
        _service.RunUpdated(new TestRunUpdateParams("r1", [], [new TestOutputDto("P|m:Adds", "hello\n")], []));
        Assert.Equal(string.Empty, Changed());
        _service.RunEnded("r1");
        Assert.Equal("Calc.cs", Changed());

        // A test of the class declared elsewhere (a partial class), then a test moved to Calc.cs: both documents.
        _service.ItemsUpdated(new TestItemsUpdateParams(Controller, [Item("P|m:Elsewhere", "P|c:Tests.Calc", "Elsewhere", uri: other, line: 9)], []));
        Assert.Equal("Calc.cs, Other.cs", Changed());
        _service.ItemsUpdated(new TestItemsUpdateParams(Controller, [Item("Q|m:Waits", "Q|c:Other.Slow", "Waits", uri: calc, line: 40)], []));
        Assert.Equal("Calc.cs, Other.cs", Changed());
        Assert.Equal(["Calc", "Adds", "Fails", "Divides", "Waits"], _service.InDocument(calc).Select(n => n.Label));
        Assert.Equal(["Elsewhere"], _service.InDocument(other).Select(n => n.Label));

        // Removing a project: its tests' document. The controller going away: every document.
        _service.ItemsUpdated(new TestItemsUpdateParams(Controller, [], ["Q"]));
        Assert.Equal("Calc.cs", Changed());
        _service.ControllerDisposed(Controller);
        Assert.Equal("Calc.cs, Other.cs", Changed());
        Assert.Empty(_service.InDocument(calc));

        // A controller created again (a plugin that came back) drops the old one's items from every document too.
        _service.ControllerCreated(new TestControllerParams("codalus.dotnettest", Controller, ".NET"), _connection);
        _service.ItemsUpdated(new TestItemsUpdateParams(Controller, [Item("P|c:Tests.Calc", null, "Calc", uri: calc, line: 5)], []));
        Changed();
        _service.ControllerCreated(new TestControllerParams("codalus.dotnettest", Controller, ".NET"), _connection);
        Assert.Equal("Calc.cs, Other.cs", Changed());
        Assert.Empty(_service.InDocument(calc));
    }

    [Fact]
    public void An_item_whose_parent_is_itself_or_under_it_becomes_a_root()
    {
        // Ids are unique per controller, but nothing makes a plugin keep to it: a parent that is the item itself, or an
        // item under it, made a cycle, and the editors' walk up the tree for the document's items never left it.
        const string uri = "file:///c:/src/Loop.cs";
        using var service = new TestService();
        service.ControllerCreated(new TestControllerParams("x", "c", "C"), _connection);
        service.ItemsUpdated(new TestItemsUpdateParams("c", [Item("self", "self", "Self", uri: uri, line: 1), Item("a", null, "A", uri: uri, line: 3), Item("b", "a", "B", uri: uri, line: 4)], []));
        service.ItemsUpdated(new TestItemsUpdateParams("c", [Item("a", "b", "A", uri: uri, line: 3)], []));

        Assert.Equal(["Self", "A", "B"], service.InDocument(uri).Select(n => n.Label));
        Assert.Equal(["Self", "A"], service.Read(roots => roots.Select(r => r.Label).ToList()));
        Assert.Same(service.Find(TestNode.KeyOf("c", "a")), service.Find(TestNode.KeyOf("c", "b"))!.Parent);
    }

    [Fact]
    public void A_documents_items_cost_what_the_document_has()
    {
        // Each open editor asks for its document's items at each change: the index answers from that document's items,
        // where a walk of every test took 12-25 ms for 100K tests (M56). 1,000 questions to a forest of 100,000 tests:
        // seconds for a walk, milliseconds for the index.
        using var service = new TestService();
        service.ControllerCreated(new TestControllerParams("x", "c", "C"), _connection);
        var items = new List<TestItemDto>();
        for (var project = 0; project < 100; project++)
        {
            items.Add(Item("p" + project, null, "P" + project));
            for (var test = 0; test < 1000; test++)
            {
                items.Add(Item($"p{project}|{test}", "p" + project, "T" + test, uri: $"file:///c:/src/P{project}/T{test / 10}.cs", line: test % 10));
            }
        }

        service.ItemsUpdated(new TestItemsUpdateParams("c", [.. items], []));
        var clock = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 1000; i++)
        {
            Assert.Equal(10, service.InDocument($"file:///c:/src/P{i % 100}/T{i % 7}.cs").Count);
        }

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2), $"1,000 documents' items took {clock.Elapsed.TotalMilliseconds:0} ms");
    }

    [Theory]
    [InlineData("", 5)]
    [InlineData("adds", 1)]
    [InlineData("outcome:failed", 1)]
    [InlineData("outcome:passed outcome:failed", 4)]
    [InlineData("-outcome:passed", 2)]
    [InlineData("project:Other", 1)]
    [InlineData("trait:Category=Math", 1)]
    [InlineData("trait:Math", 1)]
    [InlineData("\"a: 9\"", 1)]
    [InlineData("calc outcome:passed", 2)]
    public void Filters_match_names_outcomes_projects_and_traits(string filter, int expected)
    {
        CompleteRun();
        var parsed = TestFilter.Parse(filter);
        Assert.Equal(expected, _service.Read(roots => roots.SelectMany(r => r.Leaves()).Count(parsed.Matches)));
    }

    [Fact]
    public void The_hierarchy_keeps_the_ancestors_of_matching_tests()
    {
        CompleteRun();
        var groups = _service.Read(roots => TestProjection.Build(roots, TestGrouping.Hierarchy, TestFilter.Parse("divides")));

        var project = Assert.Single(groups);
        Assert.Equal("Tests.Unit", project.Label);
        Assert.Equal(2, project.Tests);
        Assert.Equal(TestOutcome.Passed, project.Outcome);
        var method = project.Children[0].Children[0].Children[0];
        Assert.Equal("Divides", method.Label);
        Assert.Equal(["P|t:1", "P|t:2"], method.Children.Select(c => c.Node!.Id));
        Assert.Equal([Node("P").Key], project.RunKeys());
    }

    [Fact]
    public void Flat_groupings_bucket_tests()
    {
        CompleteRun();
        var byOutcome = _service.Read(roots => TestProjection.Build(roots, TestGrouping.Outcome, TestFilter.Empty));
        Assert.Equal(["Failed", "Passed", "Skipped"], byOutcome.Select(g => g.Label));
        Assert.Equal(["Calc.Fails"], byOutcome[0].Children.Select(c => c.Label));
        Assert.Equal(["Calc.Adds", "Calc.Divides(a: 6, b: 3)", "Slow.Waits"], byOutcome[1].Children.Select(c => c.Label));

        var byDuration = _service.Read(roots => TestProjection.Build(roots, TestGrouping.Duration, TestFilter.Empty));
        Assert.Equal(["Slow (≥ 1 s)", "Fast (< 100 ms)", "Not run"], byDuration.Select(g => g.Label));

        var byTrait = _service.Read(roots => TestProjection.Build(roots, TestGrouping.Trait, TestFilter.Empty));
        Assert.Equal(["Category: Math", "No traits"], byTrait.Select(g => g.Label));

        var byProject = _service.Read(roots => TestProjection.Build(roots, TestGrouping.Project, TestFilter.Empty, playlist: new HashSet<string> { Node("Q").Key }));
        Assert.Equal(["Other.Tests"], byProject.Select(g => g.Label));
        Assert.Equal(TestOutcome.Passed, byProject[0].Outcome);
    }

    [Fact]
    public void Trx_files_round_trip()
    {
        CompleteRun();
        var leaves = _service.Read(roots => roots.SelectMany(r => r.Leaves()).ToList());
        var document = Trx.Export(leaves, "Codalus IDE", DateTimeOffset.Now);
        var reread = XDocument.Parse(document.ToString());

        var imported = Trx.Import(reread, "import");
        Assert.Equal(5, imported.Count);
        var fails = Assert.Single(imported, r => r.FullyQualifiedName == "Tests.Calc.Fails");
        Assert.Equal(TestOutcome.Failed, fails.Result.Outcome);
        Assert.Equal("boom", fails.Result.Messages[0].Message);
        Assert.Contains("Calc.cs:line 21", fails.Result.Messages[0].StackTrace, StringComparison.Ordinal);
        Assert.Equal("hello\n", Assert.Single(imported, r => r.FullyQualifiedName == "Tests.Calc.Adds").Result.Output);

        // Applied to a fresh model, results land on the same tests (cases by display name).
        using var other = new TestService();
        other.ControllerCreated(new TestControllerParams("x", Controller, ".NET"), _connection);
        other.ItemsUpdated(new TestItemsUpdateParams(Controller, [Item("P", null, "Tests.Unit"), Item("P|n:Tests", "P", "Tests"), Item("P|c:Tests.Calc", "P|n:Tests", "Calc"), Item("P|m:Fails", "P|c:Tests.Calc", "Fails"), Item("P|m:Divides", "P|c:Tests.Calc", "Divides"), Item("P|t:1", "P|m:Divides", "Divides(a: 6, b: 3)"), Item("P|t:2", "P|m:Divides", "Divides(a: 9, b: 3)")], []));
        var unmatched = other.ApplyImported("ci.trx", imported, (leaf, result) => TestNames.Matches(leaf, result.FullyQualifiedName, result.DisplayName));
        Assert.Equal(2, unmatched.Count); // Adds and Waits are not in the fresh tree
        Assert.Equal(TestOutcome.Failed, other.Find(TestNode.KeyOf(Controller, "P|m:Fails"))!.Outcome);
        Assert.Equal(TestOutcome.Passed, other.Find(TestNode.KeyOf(Controller, "P|t:1"))!.Outcome);
        Assert.Equal(TestOutcome.Skipped, other.Find(TestNode.KeyOf(Controller, "P|t:2"))!.Outcome);
    }

    [Fact]
    public void Visual_Studio_playlists_select_tests()
    {
        var v1 = XDocument.Parse("<Playlist Version=\"1.0\"><Add Test=\"Tests.Calc.Adds\" /><Add Test=\"Other.Slow.Waits\" /></Playlist>");
        var v2 = XDocument.Parse("""
            <Playlist Version="2.0">
              <Rule Name="Includes" Match="Any">
                <Rule Match="All">
                  <Property Name="Solution" />
                  <Rule Match="Any">
                    <Rule Match="All">
                      <Property Name="Project" Value="Tests.Unit" />
                      <Rule Match="Any"><Property Name="Class" Value="Calc" /></Rule>
                    </Rule>
                  </Rule>
                </Rule>
              </Rule>
            </Playlist>
            """);

        var first = _service.Read(roots => Playlists.ImportVisualStudio(v1, roots));
        var second = _service.Read(roots => Playlists.ImportVisualStudio(v2, roots));

        Assert.Equal([Node("P|m:Adds").Key, Node("Q|m:Waits").Key], first);
        Assert.Equal(4, second.Count);
    }

    [Fact]
    public async Task Playlists_are_saved_relative_to_the_solution()
    {
        var folder = Path.Combine(Path.GetTempPath(), "codalus-playlists-" + Guid.NewGuid().ToString("N"));
        var solution = Path.Combine(folder, "App.slnx");
        try
        {
            var key = TestNode.KeyOf(Controller, Path.Combine(folder, "tests", "T.csproj") + "|m:A.B");
            await Playlists.SaveAsync(solution, new Playlist("Smoke: fast", [key]), TestContext.Current.CancellationToken);

            var text = await File.ReadAllTextAsync(Path.Combine(Playlists.Folder(solution), "Smoke_ fast.json"), TestContext.Current.CancellationToken);
            Assert.Contains(Playlists.SolutionDirToken, text, StringComparison.Ordinal);
            Assert.DoesNotContain(folder.Replace("\\", "\\\\", StringComparison.Ordinal), text, StringComparison.Ordinal);
            var loaded = Assert.Single(Playlists.Load(solution));
            Assert.Equal("Smoke: fast", loaded.Name);
            Assert.Equal([key], loaded.Tests);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Stack_trace_frames_with_locations_are_found()
    {
        var frames = StackTraces.Parse("   at Xunit.Assert.Equal() in /_/src/Assert.cs:line 9\r\n   at Tests.Calc.Fails() in C:\\src\\Calc.cs:line 21\n   at System.RuntimeMethodHandle.InvokeMethod()");

        Assert.Equal(3, frames.Count);
        Assert.Equal(("/_/src/Assert.cs", 9), (frames[0].File, frames[0].Line));
        Assert.Equal(("C:\\src\\Calc.cs", 21), (frames[1].File, frames[1].Line));
        Assert.Null(frames[2].File);
        Assert.Equal(frames[1], StackTraces.FirstUserFrame(frames, f => f.StartsWith("C:", StringComparison.Ordinal)));
    }

    private sealed class FakeConnection : ITestControllerConnection
    {
        public Func<TestRunParams, IReadOnlyList<string>> OnRun { get; set; } = _ => [];

        public Task<IReadOnlyList<string>> RunAsync(TestRunParams request, CancellationToken cancellationToken) => Task.FromResult(OnRun(request));

        public Task RefreshAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
