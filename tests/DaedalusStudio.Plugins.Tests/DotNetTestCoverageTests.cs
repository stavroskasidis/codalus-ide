using DaedalusStudio.Plugins.Protocol;
using DaedalusStudio.Workbench.Testing;

namespace DaedalusStudio.Plugins.Tests;

/// <summary>Run with Coverage (ADR 0011) on samples/Testing: Coverlet's collector (VSTest) and console (MTP).</summary>
[Collection(DotNetTestGroup.Name)]
public sealed class DotNetTestCoverageTests(DotNetTestFixture fixture)
{
    private static CancellationToken Timeout() =>
        CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken, new CancellationTokenSource(DotNetTestFixture.Timeout).Token).Token;

    [Theory]
    [InlineData("XunitV2Tests")]
    [InlineData("XunitV3Tests")]
    [InlineData("NUnitTests")]
    [InlineData("MSTestMtpTests")]
    public async Task Coverage_reports_lines_branches_and_methods_of_the_code_under_test(string project)
    {
        var reports = new List<TestRunCoverageParams>();
        void OnCoverage(object? sender, TestRunCoverageParams coverage)
        {
            lock (reports)
            {
                reports.Add(coverage);
            }
        }

        fixture.Tests.CoverageReported += OnCoverage;
        try
        {
            var cls = fixture.Node(project, project, "CalculatorTests");
            Assert.True(await fixture.Tests.RunAsync(TestRunKind.Coverage, [cls.Key], Timeout()), fixture.Log);

            var runOutput = string.Join("\n", fixture.Tests.Runs.Select(r => r.Output));
            Assert.True(reports.Count(r => r.Files.Any(f => f.Uri.EndsWith("/Calculator.cs", StringComparison.OrdinalIgnoreCase))) == 1, $"{reports.Count} coverage reports. Run output: {runOutput} Log: {fixture.Log}");
            var report = reports.Single(r => r.Files.Any(f => f.Uri.EndsWith("/Calculator.cs", StringComparison.OrdinalIgnoreCase)));
            var file = report.Files.Single(f => f.Uri.EndsWith("/Calculator.cs", StringComparison.OrdinalIgnoreCase));
            var source = File.ReadAllLines(fixture.File("src", "Calculator", "Calculator.cs"));
            int LineOf(string text) => Array.FindIndex(source, l => l.Contains(text, StringComparison.Ordinal));
            LineCoverageDto? At(string text) => file.Lines.FirstOrDefault(l => l.Line == LineOf(text));

            Assert.True(At("=> a + b") is { Hits: > 0 }, runOutput);
            Assert.True(At("return a * 3") is { Hits: 0 }, "Unused is not covered");
            Assert.True(At("if (b == 0)") is { BranchesTotal: 2, BranchesCovered: 1 }, "the zero branch is not taken");
            Assert.DoesNotContain(report.Files, f => f.Uri.Contains("CalculatorTests.cs", StringComparison.Ordinal)); // tests are not measured
            var unused = Assert.Single(report.Methods, m => m.Method.StartsWith("Unused", StringComparison.Ordinal));
            Assert.Equal(("Calculator", "Arithmetic", 0), (unused.Namespace, unused.Class, unused.LinesCovered));
            Assert.Equal("Calculator", unused.Assembly);
        }
        finally
        {
            fixture.Tests.CoverageReported -= OnCoverage;
        }
    }
}
