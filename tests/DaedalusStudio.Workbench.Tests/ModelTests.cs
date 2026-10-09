using DaedalusStudio.Workbench.Notifications;
using DaedalusStudio.Workbench.Output;
using DaedalusStudio.Workbench.Search;
using DaedalusStudio.Workbench.StatusBar;

namespace DaedalusStudio.Workbench.Tests;

public class FuzzyMatcherTests
{
    [Fact]
    public void Returns_null_when_characters_are_missing() => Assert.Null(FuzzyMatcher.Match("xyz", "Show Command Palette"));

    [Fact]
    public void Ranks_word_start_matches_above_scattered_ones()
    {
        var wordStarts = FuzzyMatcher.Match("scp", "Show Command Palette")!;
        var scattered = FuzzyMatcher.Match("scp", "describe scope")!;
        Assert.True(wordStarts.Score > scattered.Score);
        Assert.Equal([0, 5, 13], wordStarts.Positions);
    }

    [Fact]
    public void Ranks_prefix_and_exact_matches_first()
    {
        var exact = FuzzyMatcher.Match("output", "Output")!;
        var prefix = FuzzyMatcher.Match("out", "Output: Show")!;
        var inner = FuzzyMatcher.Match("out", "View: Show Output")!;
        Assert.True(exact.Score > prefix.Score);
        Assert.True(prefix.Score > inner.Score);
    }

    [Fact]
    public void Empty_query_matches_everything() => Assert.NotNull(FuzzyMatcher.Match("  ", "anything"));
}

public class NotificationServiceTests
{
    [Fact]
    public async Task Choosing_an_action_completes_and_dismisses()
    {
        var service = new NotificationService();
        var removed = new List<long>();
        service.Removed += (_, n) => removed.Add(n.Id);

        var n = service.Warning("Plugin host stopped", "Show Log", "Restart");
        service.Choose(n, "Restart");

        Assert.Equal("Restart", await n.Result);
        Assert.Equal([n.Id], removed);
        Assert.Empty(service.Items);
    }

    [Fact]
    public async Task Dismissing_completes_with_null()
    {
        var service = new NotificationService();
        var n = service.Info("hello");
        service.Dismiss(n);
        Assert.Null(await n.Result);
    }
}

public class OutputAndStatusBarTests
{
    [Fact]
    public void Channels_are_created_once_and_trim_old_text()
    {
        var output = new OutputService();
        var channel = output.GetOrCreate("plugins", "Plugins");
        Assert.Same(channel, output.GetOrCreate("plugins"));

        var line = new string('x', 1023) + "\n";
        for (var i = 0; i < (OutputChannel.MaxLength / 1024) + 10; i++)
        {
            channel.Append(line);
        }

        Assert.True(channel.GetText().Length <= OutputChannel.MaxLength);
        Assert.EndsWith("\n", channel.GetText(), StringComparison.Ordinal);
        channel.Clear();
        Assert.Equal(string.Empty, channel.GetText());
    }

    [Fact]
    public void Status_items_are_ordered_towards_the_window_edge()
    {
        var status = new StatusBarService();
        status.Set(new StatusBarItem("b", "B", StatusBarAlignment.Left, 10, "s"));
        status.Set(new StatusBarItem("a", "A", StatusBarAlignment.Left, 100, "s"));
        status.Set(new StatusBarItem("y", "Y", StatusBarAlignment.Right, 100, "s"));
        status.Set(new StatusBarItem("z", "Z", StatusBarAlignment.Right, 1, "s"));
        status.Set(new StatusBarItem("hidden", "H", StatusBarAlignment.Right, 5, "s") { Visible = false });

        Assert.Equal(["a", "b"], status.GetItems(StatusBarAlignment.Left).Select(i => i.Id));
        Assert.Equal(["z", "y"], status.GetItems(StatusBarAlignment.Right).Select(i => i.Id));
        status.RemoveFrom("s");
        Assert.Empty(status.GetItems(StatusBarAlignment.Left));
    }
}
