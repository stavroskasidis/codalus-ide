using Codalus.Workbench.Testing;

namespace Codalus.Plugins.Tests;

[CollectionDefinition(Name)]
public sealed class DotNetTestGroup : ICollectionFixture<DotNetTestFixture>
{
    public const string Name = "dotnettest";
}

/// <summary>Discovery and runs on every framework of samples/Testing, VSTest and Microsoft.Testing.Platform (ADR 0010).</summary>
[Collection(DotNetTestGroup.Name)]
public sealed class DotNetTestPluginTests(DotNetTestFixture fixture)
{
    public static readonly TheoryData<string, string> Projects = new()
    {
        { "XunitV2Tests", "vstest" },
        { "XunitV3Tests", "mtp" },
        { "NUnitTests", "vstest" },
        { "MSTestVSTestTests", "vstest" },
        { "MSTestMtpTests", "mtp" },
        { "TUnitTests", "mtp" },
    };

    private static CancellationToken Timeout() =>
        CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken, new CancellationTokenSource(DotNetTestFixture.Timeout).Token).Token;

    private TestNode Method(string project, string method) => fixture.Node(project, project, "CalculatorTests", method);

    /// <summary>Writes the runs' output and the fixture log to the test output (shown when a test fails).</summary>
    private void Diagnostics()
    {
        foreach (var run in fixture.Tests.Runs)
        {
            TestContext.Current.TestOutputHelper?.WriteLine($"--- run {run.Name}: {run.Results.Count} results");
            TestContext.Current.TestOutputHelper?.WriteLine(run.Output);
        }

        TestContext.Current.TestOutputHelper?.WriteLine("--- log");
        TestContext.Current.TestOutputHelper?.WriteLine(fixture.Log);
    }

    [Theory]
    [MemberData(nameof(Projects))]
    public void Test_projects_are_evaluated_with_their_platform(string project, string platform)
    {
        var info = fixture.Solutions.Projects.Values.Single(p => p.Name == project);
        Assert.True(info.IsTestProject);
        Assert.Equal(platform, info.TestPlatform);
        Assert.False(fixture.Solutions.Projects.Values.Single(p => p.Name == "Calculator").IsTestProject);
    }

    [Theory]
    [MemberData(nameof(Projects))]
    public void Source_discovery_shows_every_test_method(string project, string platform)
    {
        _ = platform;
        var methods = fixture.Node(project, project, "CalculatorTests").Children.Select(c => c.Label).ToList();
        Assert.Equal(["Adds", "Fails", "Skipped", "Divides", "WritesOutput", "Hangs"], methods);
        var adds = Method(project, "Adds");
        Assert.EndsWith("/CalculatorTests.cs", adds.Uri, StringComparison.Ordinal);
        Assert.NotNull(adds.Range);
    }

    [Theory]
    [MemberData(nameof(Projects))]
    public async Task Running_a_class_reports_every_outcome(string project, string platform)
    {
        _ = platform;
        var cls = fixture.Node(project, project, "CalculatorTests");
        Assert.True(await fixture.Tests.RunAsync(TestRunKind.Run, [cls.Key], Timeout()), fixture.Log);
        Diagnostics();

        Assert.Equal(TestOutcome.Passed, Method(project, "Adds").AggregateOutcome());
        var fails = Method(project, "Fails");
        Assert.Equal(TestOutcome.Failed, fails.AggregateOutcome());
        var failure = fails.Leaves().Single().Result!;
        Assert.NotEmpty(failure.Messages);
        Assert.Contains("CalculatorTests", failure.Messages[0].StackTrace ?? string.Empty, StringComparison.Ordinal);
        Assert.Equal(TestOutcome.Skipped, Method(project, "Skipped").AggregateOutcome());
        var divides = Method(project, "Divides");
        Assert.Equal(2, divides.Leaves().Count());
        Assert.All(divides.Leaves(), l => Assert.Equal(TestOutcome.Passed, l.Outcome));
        Assert.Equal(TestOutcome.Passed, Method(project, "Hangs").AggregateOutcome());
        var output = Method(project, "WritesOutput").Leaves().Single().Result!;
        Assert.Equal(TestOutcome.Passed, output.Outcome);
        Assert.Contains("from", output.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Multi_targeted_projects_run_on_every_framework()
    {
        var method = fixture.Node("MultiTargetTests", "MultiTargetTests", "FrameworkTests", "ReportsFramework");
        Assert.True(await fixture.Tests.RunAsync(TestRunKind.Run, [method.Key], Timeout()), fixture.Log);
        Diagnostics();

        method = fixture.Node("MultiTargetTests", "MultiTargetTests", "FrameworkTests", "ReportsFramework");
        Assert.Equal(["net10.0", "net9.0"], method.Children.Select(c => c.Label).Order(StringComparer.Ordinal));
        Assert.All(method.Leaves(), l => Assert.Equal(TestOutcome.Passed, l.Outcome));
    }
}
