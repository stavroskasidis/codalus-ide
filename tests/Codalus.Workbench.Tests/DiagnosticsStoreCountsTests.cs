using Codalus.LanguageServices.Protocol;
using Codalus.Workbench.Languages;
using Range = Codalus.LanguageServices.Protocol.Range;

namespace Codalus.Workbench.Tests;

/// <summary>The store's error and warning counts, kept as files change, equal a count of everything it holds.</summary>
public sealed class DiagnosticsStoreCountsTests
{
    private static Diagnostic Item(int severity) => new(new Range(new Position(0, 0), new Position(0, 1)), severity, "X1", "test", "x");

    private static (int, int) Walk(DiagnosticsStore store) =>
        (store.All.Sum(f => f.Items.Count(d => d.Severity == DiagnosticSeverity.Error)), store.All.Sum(f => f.Items.Count(d => d.Severity == DiagnosticSeverity.Warning)));

    [Fact]
    public void Replacing_emptying_and_clearing_keep_the_counts()
    {
        var store = new DiagnosticsStore();
        store.Set("/a.cs", "csharp", 1, [Item(DiagnosticSeverity.Error), Item(DiagnosticSeverity.Warning), Item(DiagnosticSeverity.Information)]);
        store.Set("/a.cs", "eslint", 1, [Item(DiagnosticSeverity.Warning)]);
        store.Set("/b.cs", "csharp", 1, [Item(DiagnosticSeverity.Error)]);
        Assert.Equal((2, 2), store.Counts);

        store.Set("/a.cs", "csharp", 2, [Item(DiagnosticSeverity.Warning)]); // replaced
        Assert.Equal((1, 2), store.Counts);
        store.Set("/b.cs", "csharp", 2, []); // emptied
        store.Set("/c.cs", "csharp", 2, []); // nothing there
        Assert.Equal((0, 2), store.Counts);
        store.Clear("eslint"); // one source of a file
        Assert.Equal((0, 1), store.Counts);
        Assert.Equal(Walk(store), store.Counts);
        store.Clear();
        Assert.Equal((0, 0), store.Counts);
    }

    [Fact]
    public async Task Concurrent_changes_leave_the_counts_of_what_is_held()
    {
        var store = new DiagnosticsStore();
        await Parallel.ForAsync(0, 2_000, TestContext.Current.CancellationToken, (i, _) =>
        {
            var path = $"/f{i % 37}.cs";
            if (i % 101 == 0)
            {
                store.Clear(i % 2 == 0 ? "csharp" : null);
            }
            else
            {
                store.Set(path, i % 3 == 0 ? "eslint" : "csharp", i, [.. Enumerable.Range(0, i % 5).Select(k => Item(k % 2 == 0 ? DiagnosticSeverity.Error : DiagnosticSeverity.Warning))]);
            }

            return ValueTask.CompletedTask;
        });
        Assert.Equal(Walk(store), store.Counts);
    }
}
