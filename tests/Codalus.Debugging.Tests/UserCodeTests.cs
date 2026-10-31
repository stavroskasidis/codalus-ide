using Codalus.Workbench.Debugging;

namespace Codalus.Debugging.Tests;

/// <summary>
/// Whether a module is the solution's code is asked once per module for the session (M56): the data tip asked for each
/// member of an object it listed, and each asking copied and sorted every module and scanned every project.
/// </summary>
public sealed class UserCodeTests
{
    [Fact]
    public async Task A_modules_answer_is_kept_until_the_solutions_projects_change()
    {
        await using var debug = new DebugService(() => null);
        var asked = 0;
        debug.IsUserModule = path =>
        {
            Interlocked.Increment(ref asked);
            return path!.EndsWith("App.dll", StringComparison.Ordinal);
        };

        for (var i = 0; i < 1000; i++)
        {
            Assert.True(debug.IsUserCode("/src/App/bin/App.dll"));
            Assert.False(debug.IsUserCode("/dotnet/System.Private.CoreLib.dll"));
        }

        Assert.Equal(2, asked);
        Assert.Same(debug.Modules, debug.Modules); // sorted once, not at each read

        debug.UserCodeChanged();
        Assert.True(debug.IsUserCode("/src/App/bin/App.dll"));
        Assert.Equal(3, asked);
    }

    [Fact]
    public async Task An_answer_made_while_the_projects_change_is_not_kept()
    {
        // Asked off the UI thread while the projects change: the answer made from the old ones would have been kept until
        // the next change, written after the change had cleared what was known.
        await using var debug = new DebugService(() => null);
        var asked = 0;
        debug.IsUserModule = _ =>
        {
            if (Interlocked.Increment(ref asked) == 1)
            {
                debug.UserCodeChanged(); // the solution's projects change while this answer is made
            }

            return true;
        };

        Assert.True(debug.IsUserCode("/src/App/bin/App.dll"));
        Assert.True(debug.IsUserCode("/src/App/bin/App.dll"));
        Assert.Equal(2, asked); // asked again: the first answer wasn't kept
        Assert.True(debug.IsUserCode("/src/App/bin/App.dll"));
        Assert.Equal(2, asked);
    }
}
