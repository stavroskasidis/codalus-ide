using Codalus.Workbench.Plugins;

namespace Codalus.Plugins.Tests;

/// <summary>Bisect Plugins (M22c): halves, answers, the culprit and the user's list back at the end.</summary>
public sealed class PluginBisectTests
{
    [Theory]
    [InlineData("e")]
    [InlineData("a")]
    [InlineData("c")]
    [InlineData("h")]
    public void The_culprit_is_found_by_halves_and_the_original_list_comes_back(string culprit)
    {
        string[] plugins = ["a", "b", "c", "d", "e", "f", "g", "h"];
        var step = PluginBisect.Start(plugins, ["old"])!;
        var rounds = 0;
        while (step.State is { } state)
        {
            Assert.Contains("old", step.Disabled); // the user's own list stays off throughout
            Assert.Equal(state.TurnedOff.Length, step.Disabled.Length - 1);
            // The problem stays while the culprit is still on.
            step = PluginBisect.Answer(state, stillThere: !state.TurnedOff.Contains(culprit));
            rounds++;
        }

        Assert.Equal(culprit, step.Culprit);
        Assert.Equal(["old"], step.Disabled);
        Assert.Equal(3, rounds); // log2(8)
    }

    [Fact]
    public void One_plugin_needs_no_rounds_and_none_is_nothing_to_bisect()
    {
        var one = PluginBisect.Start(["only"], [])!;
        Assert.Equal("only", one.Culprit);
        Assert.Null(one.State);
        Assert.Null(PluginBisect.Start([], []));
    }

    [Fact]
    public void Stopping_puts_the_list_back_and_the_state_survives_restarts()
    {
        var file = Path.Combine(Path.GetTempPath(), $"codalus-bisect-{Guid.NewGuid():N}.json");
        try
        {
            var step = PluginBisect.Start(["a", "b", "c"], ["x"])!;
            PluginBisect.Save(file, step.State);
            var loaded = PluginBisect.Load(file)!;
            Assert.Equal(step.State!.Suspects, loaded.Suspects);
            Assert.Equal(["a"], loaded.TurnedOff);

            var stopped = PluginBisect.Stop(loaded);
            Assert.Equal(["x"], stopped.Disabled);
            Assert.Null(stopped.Culprit);
            PluginBisect.Save(file, stopped.State);
            Assert.False(File.Exists(file));
        }
        finally
        {
            File.Delete(file);
        }
    }
}
