using Codalus.Workbench.Debugging;

namespace Codalus.Workbench.Tests;

/// <summary>Breakpoint filters (M14b): Visual Studio's syntax over threads and processes.</summary>
public sealed class BreakpointFilterTests
{
    private static readonly BreakpointFilterContext Worker = new(12, "Worker", 4321, "app");

    [Theory]
    [InlineData("ThreadId = 12", true)]
    [InlineData("ThreadId == 13", false)]
    [InlineData("ThreadName = \"Worker\"", true)]
    [InlineData("threadname=\"worker\"", false)] // names compare exactly, keywords don't
    [InlineData("ThreadName != \"Worker\"", false)]
    [InlineData("ProcessId = 4321 & ThreadId = 12", true)]
    [InlineData("ProcessName = \"app.exe\" && ThreadId = 1", false)]
    [InlineData("ThreadId = 1 || ThreadName = \"Worker\"", true)]
    [InlineData("!(ThreadId = 12)", false)]
    [InlineData("ProcessName = app", true)]
    public void Filters_match_the_thread_and_process(string filter, bool expected)
    {
        Assert.Equal(expected, BreakpointFilter.Parse(filter).Matches(Worker));
    }

    [Theory]
    [InlineData("Thread = 1", "Use ThreadId")]
    [InlineData("ThreadId 1", "'=' or '!=' is missing")]
    [InlineData("ThreadName = \"open", "closing")]
    [InlineData("(ThreadId = 1", "')' is missing")]
    [InlineData("ThreadId = 1 extra", "Unexpected")]
    public void Malformed_filters_say_why(string filter, string message)
    {
        Assert.False(BreakpointFilter.TryParse(filter, out _, out var error));
        Assert.Contains(message, error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_filter_makes_the_debugger_handle_the_breakpoint()
    {
        Assert.False(new BreakpointSpec("/a.cs", 3).NeedsClientHandling);
        Assert.True(new BreakpointSpec("/a.cs", 3, Filter: "ThreadId = 1").NeedsClientHandling);
    }
}
