using System.Text.Json;
using Codalus.Workbench.Debugging;
using Codalus.Workbench.Settings;
using Codalus.Workbench.Testing;

namespace Codalus.Plugins.Tests;

/// <summary>Cancelling and debugging test runs through the .NET test plugin (ADR 0010).</summary>
[Collection(DotNetTestGroup.Name)]
public sealed class DotNetTestRunControlTests(DotNetTestFixture fixture)
{
    private static readonly JsonElement s_hangEnvironment = JsonSerializer.SerializeToElement(new[] { "CODALUS_FIXTURE_HANG=1" });

    private static CancellationToken Timeout() =>
        CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken, new CancellationTokenSource(DotNetTestFixture.Timeout).Token).Token;

    private static int Line(string file, string marker) =>
        Array.FindIndex(File.ReadAllLines(file), l => l.Contains($"// [{marker}]", StringComparison.Ordinal)) + 1;

    [Theory]
    [InlineData("XunitV2Tests")]
    [InlineData("XunitV3Tests")]
    [InlineData("NUnitTests")]
    [InlineData("MSTestMtpTests")]
    public async Task Cancelling_stops_a_hanging_test(string project)
    {
        var settings = fixture.Workbench.Settings;
        await settings.SetAsync("testing.environment", s_hangEnvironment, SettingScope.User, Timeout());
        Task? run = null;
        try
        {
            var hangs = fixture.Node(project, project, "CalculatorTests", "Hangs");
            var before = hangs.Leaves().Single().Result;
            run = fixture.Tests.RunAsync(TestRunKind.Run, [hangs.Key], Timeout());

            // Running (or at least queued) before cancelling, so the cancellation reaches the test process. A run that
            // ends first never started it (a failed build or discovery): waiting on for the test hid that for 4 minutes
            // (runs 39979261891, 40375629247).
            await fixture.WaitUntilAsync(() => run.IsCompleted || fixture.Node(project, project, "CalculatorTests", "Hangs").AggregateOutcome() is TestOutcome.Running or TestOutcome.Queued, Timeout());
            if (run.IsCompleted)
            {
                Assert.Fail($"The run ended without starting the test. Run output: {(fixture.Tests.Runs is [.., var last] ? last.Output : "none")} Log: {fixture.Log}");
            }

            await Task.Delay(1000, Timeout());
            var started = DateTime.UtcNow;
            fixture.Tests.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(60), Timeout());

            Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(30), "The run did not stop promptly after Cancel.");
            // An earlier run may have passed it; this run must not have.
            var after = fixture.Node(project, project, "CalculatorTests", "Hangs").Leaves().Single().Result;
            Assert.True(ReferenceEquals(after, before) || after?.Outcome != TestOutcome.Passed, $"The cancelled run reported {after?.Outcome}.");
            Assert.False(fixture.Tests.IsRunning);
        }
        finally
        {
            // A failure before the cancel leaves the hanging test running, and the next project's case then found a run
            // in progress (macOS CI, run 40568001945): the run ends here whatever happened.
            if (run is { IsCompleted: false })
            {
                fixture.Tests.Cancel();
                await run.WaitAsync(TimeSpan.FromSeconds(60), CancellationToken.None).ContinueWith(_ => { }, TaskScheduler.Default);
            }

            await settings.SetAsync("testing.environment", (JsonElement?)null, SettingScope.User, CancellationToken.None);
        }
    }

    /// <summary>
    /// M55: under the Codalus debugger a test being debugged takes an edit, as a launched program does (the test host
    /// starts under the debugger, VSTest's and the testing platform's alike). Stopped before <c>Arithmetic.Add</c> runs,
    /// an edit to it applies, and the test then fails.
    /// </summary>
    [Theory]
    [InlineData("XunitV2Tests")]
    [InlineData("XunitV3Tests")]
    public async Task A_test_being_debugged_under_the_Codalus_debugger_takes_an_edit(string project)
    {
        var file = fixture.File("tests", project, "CalculatorTests.cs");
        var line = Line(file, "breakpoint");
        var calculator = fixture.File("src", "Calculator", "Calculator.cs");
        var original = await File.ReadAllTextAsync(calculator, TestContext.Current.CancellationToken);
        var stopped = new TaskCompletionSource<StopInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnStopped(object? sender, StopInfo stop) => stopped.TrySetResult(stop);
        var adapter = fixture.Debug.AdapterSetting;
        fixture.Debug.AdapterSetting = () => DebugService.CodalusAdapter;
        await using var edits = new DebugHotReload(fixture.Debug, () => fixture.Languages.RunningClient("csharp"));
        var text = original;
        var version = 1;
        await fixture.Languages.OpenAsync(new Workbench.Languages.LanguageDocument(calculator, "csharp", () => (text, version)));
        fixture.Debug.Breakpoints.Toggle(file, line);
        fixture.Debug.Stopped += OnStopped;
        try
        {
            var run = fixture.Tests.RunAsync(TestRunKind.Debug, [fixture.Node(project, project, "CalculatorTests", "Adds").Key], Timeout());
            await stopped.Task.WaitAsync(TimeSpan.FromSeconds(90), Timeout());
            await fixture.WaitUntilAsync(() => edits.IsActive, Timeout());

            var lines = text.Split('\n');
            var end = new LanguageServices.Protocol.Position(lines.Length - 1, lines[^1].Length);
            text = original.Replace("a + b;", "a + b + 1;", StringComparison.Ordinal);
            version++;
            await fixture.Languages.ChangeAsync(calculator, version, [new LanguageServices.Protocol.TextDocumentContentChangeEvent(new LanguageServices.Protocol.Range(new LanguageServices.Protocol.Position(0, 0), end), text)]);
            var outcome = await edits.ApplyAsync(Timeout());
            Assert.True(outcome.Kind == HotReloadOutcome.Applied, $"{outcome.Kind}: {outcome.Message} {string.Join("; ", outcome.Diagnostics.Select(d => d.Id + " " + d.Message))}");

            fixture.Debug.Breakpoints.Toggle(file, line);
            await fixture.Debug.ContinueAsync();
            await run.WaitAsync(TimeSpan.FromMinutes(2), Timeout());
            Assert.Equal(TestOutcome.Failed, fixture.Node(project, project, "CalculatorTests", "Adds").AggregateOutcome());
            await fixture.WaitUntilAsync(() => !fixture.Debug.IsActive, Timeout());
        }
        finally
        {
            fixture.Debug.Stopped -= OnStopped;
            if (fixture.Debug.Breakpoints.At(file, line) is not null)
            {
                fixture.Debug.Breakpoints.Toggle(file, line);
            }

            if (fixture.Debug.IsActive)
            {
                await fixture.Debug.StopAsync();
            }

            fixture.Debug.AdapterSetting = adapter;
            await fixture.Languages.CloseAsync(calculator);
        }
    }

    [Theory]
    [InlineData("XunitV2Tests")]
    [InlineData("XunitV3Tests")]
    public async Task Debugging_a_test_stops_at_its_breakpoint(string project)
    {
        Assert.SkipWhen(DotNetTestFixture.Netcoredbg is null && Environment.GetEnvironmentVariable("CI") != "true", "netcoredbg is not installed (eng/get-netcoredbg.sh).");
        var file = fixture.File("tests", project, "CalculatorTests.cs");
        var line = Line(file, "breakpoint");
        var stopped = new TaskCompletionSource<StopInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnStopped(object? sender, StopInfo stop) => stopped.TrySetResult(stop);
        fixture.Debug.Breakpoints.Toggle(file, line);
        fixture.Debug.Stopped += OnStopped;
        try
        {
            var adds = fixture.Node(project, project, "CalculatorTests", "Adds");
            var run = fixture.Tests.RunAsync(TestRunKind.Debug, [adds.Key], Timeout());

            try
            {
                await stopped.Task.WaitAsync(TimeSpan.FromSeconds(90), Timeout());
            }
            catch (TimeoutException)
            {
                throw new TimeoutException($"No stop. Debug state: {fixture.Debug.State}. Log: {fixture.Log}");
            }
            var frame = fixture.Debug.Frames.First(f => f.Source?.Path is not null);
            Assert.Equal(Path.GetFileName(file), Path.GetFileName(frame.Source!.Path));
            Assert.Equal(line, frame.Line);
            await fixture.Debug.ContinueAsync();
            await run.WaitAsync(TimeSpan.FromMinutes(2), Timeout());

            Assert.Equal(TestOutcome.Passed, fixture.Node(project, project, "CalculatorTests", "Adds").AggregateOutcome());
            await fixture.WaitUntilAsync(() => !fixture.Debug.IsActive, Timeout());
        }
        finally
        {
            fixture.Debug.Stopped -= OnStopped;
            fixture.Debug.Breakpoints.Toggle(file, line);
            if (fixture.Debug.IsActive)
            {
                await fixture.Debug.StopAsync();
            }
        }
    }
}
