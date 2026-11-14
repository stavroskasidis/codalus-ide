using System.Text.Json;
using Avalonia.Headless.XUnit;
using Codalus.Shell.Infrastructure;

namespace Codalus.Shell.Tests;

/// <summary>
/// The smoke test, ended before its last step (its window closed by hand, a shutdown it didn't ask for), fails: a local
/// macOS run, 2026-11-12, had its window closed during testing.run, exited with 0 and reported the 54 steps that had run
/// as a pass.
/// </summary>
public sealed class SmokeTestInterruptedTests
{
    [AvaloniaFact]
    public async Task An_exit_before_the_run_ends_fails_it_and_says_so_in_the_report()
    {
        await using var f = ShellFixture.Create();
        var smoke = new SmokeTest(f.Window, f.Shell, f.CopySample("BuildAndRun"));

        Assert.Equal(3, smoke.Interrupted());

        using var report = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(f.Shell.Paths.Logs, "smoke-test.json"), TestContext.Current.CancellationToken));
        Assert.False(report.RootElement.GetProperty("passed").GetBoolean());
        var step = Assert.Single(report.RootElement.GetProperty("steps").EnumerateArray());
        Assert.Equal(("interrupted", "failed"), (step.GetProperty("id").GetString(), step.GetProperty("outcome").GetString()));
        Assert.Contains("between steps", step.GetProperty("error").GetString(), StringComparison.Ordinal);
        Assert.Contains("FAILED", await File.ReadAllTextAsync(Path.Combine(f.Shell.Paths.Logs, "smoke-test.md"), TestContext.Current.CancellationToken), StringComparison.Ordinal);

        // Once ended (by the run, or by this), a later exit changes nothing.
        Assert.Null(smoke.Interrupted());
    }
}
