using System.Runtime.InteropServices;
using Codalus.Projects.Protocol;
using Codalus.Workbench.Build;

namespace Codalus.Workbench.Tests;

/// <summary>
/// A run's plan made to read it has no effects; only the plan of a start goes through <see cref="RunService.ProgramPlan"/>,
/// which gives an Avalonia app a live session and ends the last one (2026-10-21: the dev certificate check's plan ended
/// the session of the run it raced).
/// </summary>
public sealed class RunPlanTests
{
    [Fact]
    public async Task Only_the_plan_of_a_start_goes_through_the_program_plan_hook()
    {
        var runs = new RunService(null!, new Output.OutputService(), _ => { })
        {
            Machine = new RuntimeSelection.Machine(Architecture.X64, OSPlatform.Linux, _ => false, _ => null, _ => null),
        };
        var calls = 0;
        runs.ProgramPlan = (_, plan) =>
        {
            calls++;
            return plan with { Environment = new Dictionary<string, string>(plan.Environment) { ["HOOKED"] = "1" } };
        };
        var info = new RunInfo("/src/App/App.csproj", "net10.0", "Exe", "/src/App/bin/App.dll", "/src/App/bin/App", null, "/src/App", null);
        var cancellationToken = TestContext.Current.CancellationToken;

        var read = await runs.PlanAsync(info, null, cancellationToken);
        Assert.Equal(0, calls);
        Assert.False(read.Environment.ContainsKey("HOOKED"));

        var launch = await runs.LaunchPlanAsync(info, null, cancellationToken);
        Assert.Equal(1, calls);
        Assert.Equal("1", launch.Environment["HOOKED"]);
        Assert.Equal((read.FileName, read.Arguments, read.WorkingDirectory), (launch.FileName, launch.Arguments, launch.WorkingDirectory));
    }
}
