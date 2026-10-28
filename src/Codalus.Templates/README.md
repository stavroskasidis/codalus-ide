# Codalus.Templates

`dotnet new` templates for [Codalus IDE](https://github.com/stavroskasidis/codalus-ide), a free, open-source IDE for .NET on Windows, macOS and Linux.

```bash
dotnet new install Codalus.Templates
dotnet new codalus-plugin -n Contoso.Greeter --publisher contoso
```

`codalus-plugin` makes `Contoso.Greeter.slnx` with:

- `src/Contoso.Greeter`: the plugin, on [Codalus.Plugins.Abstractions](https://www.nuget.org/packages/Codalus.Plugins.Abstractions), with its manifest (`codalus-plugin.json`), a command in the Tools menu, a setting and a status bar item. `dotnet pack` makes the plugin's package for a Plugin Gallery.
- `tests/Contoso.Greeter.Tests`: its unit tests, on [Codalus.Plugins.Testing](https://www.nuget.org/packages/Codalus.Plugins.Testing).

| Option | |
|---|---|
| `--publisher` | The publisher part of the plugin's id (`publisher.name`, lower case). |
| `--skipTests` | Leave out the test project. |
| `--codalusVersion` | The version of the two API packages. |

In Codalus IDE, make the plugin the startup project and use **Debug → Debug Plugin in Development Instance**.

- [Writing plugins](https://github.com/stavroskasidis/codalus-ide/blob/develop/docs/plugins.md)

MIT licensed.
