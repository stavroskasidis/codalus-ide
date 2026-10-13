using Codalus.Plugins;

namespace MyPlugin;

/// <summary>
/// The plugin. The manifest (codalus-plugin.json) declares what the IDE shows before this code loads; the plugin host
/// activates it the first time one of its commands runs. See https://github.com/stavroskasidis/codalus-ide/blob/develop/docs/plugins.md.
/// </summary>
public sealed class Plugin : ICodalusPlugin
{
    private int _greetings;

    public Task ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        var status = context.Window.CreateStatusBarItem("greetings", StatusBarAlignment.Right);
        context.Subscriptions.Add(status);

        context.Subscriptions.Add(context.Commands.Register("myplugin.greet", async (_, ct) =>
        {
            var greeting = context.Settings.GetValue("myplugin.greeting", "Hello");
            _greetings++;
            status.Text = $"$(info) {_greetings}";
            status.Show();
            await context.Window.ShowNotificationAsync(NotificationSeverity.Info, $"{greeting} from MyPlugin!", cancellationToken: ct);
            return _greetings;
        }));

        context.Log.Info("MyPlugin activated.");
        return Task.CompletedTask;
    }
}
