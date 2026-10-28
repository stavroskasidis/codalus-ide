# Codalus.Plugins.Testing

Headless tests for [Codalus IDE](https://github.com/stavroskasidis/codalus-ide) plugins. `TestPluginContext` is an in-memory `IPluginContext`: it runs a plugin with no IDE and no plugin host, records what the plugin registers and shows, and drives its commands, views, trees and providers. So a plugin's tests are ordinary unit tests, with any test framework.

```csharp
var context = new TestPluginContext("contoso.greeter");
context.Window.NotificationResponder = n => n.Actions.FirstOrDefault(); // the user's answer
await using var host = await PluginTestHost.ActivateAsync<GreeterPlugin>(context);

context.Settings.Set("greeter.greeting", "Ahoy");                      // raises Settings.Changed
Assert.Equal(1, await context.Commands.ExecuteAsync<int>("greeter.greet"));
Assert.Equal("Ahoy!", Assert.Single(context.Window.Notifications).Message);
```

- **Commands** run with their arguments and results through JSON, as over the plugin protocol; the IDE's commands the plugin calls are answered by `OnShellCommand`.
- **The window** answers notifications, quick picks and input boxes, and records output channels, status bar items, views, trees and tasks with their progress.
- **Providers** the plugin registered (file systems, authentication, source control) are there to call.
- **Settings and secrets** are in memory. Disposing the `PluginTestHost` deactivates the plugin.

`dotnet new codalus-plugin` (from [Codalus.Templates](https://www.nuget.org/packages/Codalus.Templates)) makes a plugin with tests on this package. The package's version follows the plugin API's ([Codalus.Plugins.Abstractions](https://www.nuget.org/packages/Codalus.Plugins.Abstractions)) and targets `net10.0`.

- [Testing plugins](https://github.com/stavroskasidis/codalus-ide/blob/develop/docs/plugins.md#testing-plugins)

MIT licensed.
