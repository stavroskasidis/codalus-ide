# Codalus.Plugins.Abstractions

The plugin API of [Codalus IDE](https://github.com/stavroskasidis/codalus-ide), a free, open-source IDE for .NET on Windows, macOS and Linux. A plugin references this package and implements `ICodalusPlugin`; it runs in a plugin host process, never in the IDE's, so a slow or crashing plugin can't freeze the UI.

The quickest start is the template, which makes a plugin with its manifest, a command, a setting and unit tests:

```bash
dotnet new install Codalus.Templates
dotnet new codalus-plugin -n Contoso.Greeter --publisher contoso
```

By hand, reference the package without its runtime assets (the IDE provides them) and put a `codalus-plugin.json` manifest beside the project:

```xml
<PackageReference Include="Codalus.Plugins.Abstractions" Version="1.1.0" ExcludeAssets="runtime" />
```

```csharp
using Codalus.Plugins;

public sealed class Plugin : ICodalusPlugin
{
    public Task ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        context.Subscriptions.Add(context.Commands.Register("greeter.greet", async (_, ct) =>
        {
            await context.Window.ShowNotificationAsync(NotificationSeverity.Info, "Hello from Greeter!", cancellationToken: ct);
            return 0;
        }));
        return Task.CompletedTask;
    }
}
```

The API is versioned and frozen at 1.x: minor releases only add, and a member goes away only in a major release, after it has been marked obsolete (warning `CODALUS0001`) for at least two minor releases. The package targets `net10.0`.

- [Writing plugins](https://github.com/stavroskasidis/codalus-ide/blob/develop/docs/plugins.md): the manifest, contribution points, running, debugging and publishing
- [API reference](https://github.com/stavroskasidis/codalus-ide/blob/develop/docs/user-guide/plugin-api.md): every type and member
- [Codalus.Plugins.Testing](https://www.nuget.org/packages/Codalus.Plugins.Testing): unit tests for plugins, with no IDE

MIT licensed.
