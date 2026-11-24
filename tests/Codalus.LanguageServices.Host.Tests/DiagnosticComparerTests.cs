using Codalus.LanguageServices.Host;
using Codalus.LanguageServices.Protocol;
using Range = Codalus.LanguageServices.Protocol.Range;

namespace Codalus.LanguageServices.Host.Tests;

/// <summary>The background pass publishes a closed file again only when its diagnostics changed, tags included.</summary>
public sealed class DiagnosticComparerTests
{
    private static Diagnostic Deprecated(int line, params int[] tags) =>
        new(new Range(new Position(line, 0), new Position(line, 4)), DiagnosticSeverity.Warning, "CS0618", "csharp", "'Old' is obsolete")
        {
            Tags = tags.Length == 0 ? null : tags,
        };

    [Fact]
    public void Tags_are_compared_by_value_and_everything_else_still_counts()
    {
        var comparer = DiagnosticsService.DiagnosticComparer.Instance;
        Diagnostic[] before = [Deprecated(1, DiagnosticTag.Deprecated)];
        Diagnostic[] after = [Deprecated(1, DiagnosticTag.Deprecated)];
        Assert.NotEqual(before[0], after[0]); // the record compares the arrays by reference
        Assert.True(before.SequenceEqual(after, comparer));
        Assert.Equal(comparer.GetHashCode(before[0]), comparer.GetHashCode(after[0]));

        Assert.False(comparer.Equals(Deprecated(1, DiagnosticTag.Deprecated), Deprecated(2, DiagnosticTag.Deprecated)));
        Assert.False(comparer.Equals(Deprecated(1, DiagnosticTag.Deprecated), Deprecated(1, DiagnosticTag.Unnecessary)));
        Assert.False(comparer.Equals(Deprecated(1, DiagnosticTag.Deprecated), Deprecated(1)));
        Assert.True(comparer.Equals(Deprecated(1), Deprecated(1)));
    }
}
