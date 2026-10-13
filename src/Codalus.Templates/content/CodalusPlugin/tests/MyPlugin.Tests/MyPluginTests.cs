using Codalus.Plugins.Testing;

namespace MyPlugin.Tests;

/// <summary>The plugin in memory, without the IDE (Codalus.Plugins.Testing).</summary>
public sealed class PluginTests
{
    [Fact]
    public async Task Greet_says_the_greeting_and_counts()
    {
        var context = new TestPluginContext("my-publisher.myplugin");
        await using var host = await PluginTestHost.ActivateAsync<Plugin>(context, TestContext.Current.CancellationToken);

        context.Settings.Set("myplugin.greeting", "Ahoy");
        Assert.Equal(1, await context.Commands.ExecuteAsync<int>("myplugin.greet"));

        Assert.Equal("Ahoy from MyPlugin!", Assert.Single(context.Window.Notifications).Message);
        Assert.True(context.Window.StatusBarItems["greetings"].Visible);
    }
}
