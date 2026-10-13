# MyPlugin

A plugin for Codalus IDE.

- `src/MyPlugin/codalus-plugin.json`: the manifest (commands, menus, settings and the rest of the contribution points).
- `src/MyPlugin/MyPlugin.cs`: the code, activated when one of its commands first runs.
- `tests/MyPlugin.Tests`: unit tests on `Codalus.Plugins.Testing`, with no IDE.

Run it: open `MyPlugin.slnx` in Codalus IDE and choose **Debug → Run Plugin in Development Instance**, or build it and start `codalus --plugin-dir src/MyPlugin/bin/Debug/net10.0`.

The plugin guide: https://github.com/stavroskasidis/codalus-ide/blob/develop/docs/plugins.md
