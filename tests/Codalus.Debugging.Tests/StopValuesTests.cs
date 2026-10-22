using Codalus.Workbench.Debugging;

namespace Codalus.Debugging.Tests;

/// <summary>Values compared with the last stop, by evaluate name (D44, decision 12).</summary>
public sealed class StopValuesTests
{
    [Fact]
    public void A_value_is_changed_when_it_differs_from_the_last_stop_it_was_seen_at()
    {
        var values = new StopValues();
        values.NextStop();
        Assert.False(values.Record("app.Count", "3")); // nothing to compare with
        Assert.False(values.Record("app.Count", "3"));

        values.NextStop();
        Assert.True(values.Record("app.Count", "4"));
        Assert.False(values.Record("app.Name", "\"a\""));
        Assert.True(values.Record("app.Count", "4")); // still against the last stop, however often it's shown

        // Not looked at during a stop: compared with the stop it was last seen at.
        values.NextStop();
        values.NextStop();
        Assert.True(values.Record("app.Name", "\"b\""));
        Assert.False(values.Record("app.Count", "4"));

        values.Clear();
        values.NextStop();
        Assert.False(values.Record("app.Count", "5"));
    }
}
