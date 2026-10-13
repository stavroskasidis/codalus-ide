# ADR 0057: Where plugins run in a remote window

Status: accepted (M21b, 2026-10-13)

## Context

A remote window (ADR 0050) moves the build, language and markup hosts, terminals, the debugger and the file system to the server. Plugins stayed in the window's plugin hosts on this machine. That suits plugins about the window: themes, keymaps, hosting accounts, the start page. It doesn't suit plugins that work on the workspace's files and tools. The .NET test plugin (ADR 0010) discovers tests by building them and runs them with VSTest and Microsoft.Testing.Platform as local processes, so in a remote window it found nothing to run, and the Test Explorer was on the list of what doesn't work remotely.

VS Code solves this with `extensionKind` (`ui` or `workspace`). An extension runs in the extension host next to the UI or in the one next to the workspace.

## Decision

**The manifest says where a plugin runs.** `"runsOn": "ui" | "workspace"` (plugin API 0.3.0):
- `ui` is the default and the behavior so far.
- A `workspace` plugin runs on the machine the workspace is on: this machine for a local window, the server for a remote one. Anything else in `runsOn` makes the manifest invalid.

**Workspace plugins have host groups of their own.** A workspace plugin's group is its usual one with `workspace:` before it (`workspace:builtin`, `workspace:default`, `workspace:dedicated:<id>`). Its host can then move without stopping the window's. Locally that's one more host process, started only when such a plugin activates.

**In a remote window**, `PluginManager.SetWorkspacePlacementAsync` receives a `WorkspacePlacement`:
- `RemoteSession.Place` starts the plugin host as it starts the other hosts. It runs `pluginhost/codalus-pluginhost.dll` from the bundle with `dotnet` there, through the session's launcher, so the host's JSON has its paths mapped (`MappedJsonStream`).
- `RemoteSession.ToBundlePath` turns each plugin folder into its copy in the bundle. The bundle (`DefaultRemoteOptions`) carries the plugin host and each usable workspace plugin's folder, as `plugins/<id>`, so only those plugins are copied.

Setting or clearing the placement retires the running workspace hosts. What they registered at run time goes (test controllers, hosting providers, visualizers), and their plugins are ready again. They activate on their next activation event in the new place: opening the remote solution, or the local one after Close Remote Connection.

**The .NET test plugin runs on the workspace.** In a remote window, the Test Explorer discovers, runs and debugs tests on the server: VSTest's console and the Microsoft.Testing.Platform apps are the server's local processes. Test locations come back with mapped paths, and debugging goes through the window's debugger, which already runs netcoredbg on the server.

## Consequences

- A workspace plugin must run on the server's OS and .NET. Its code is copied as it is: plugins are .NET assemblies, so a `runtime: dotnet` plugin with no native parts runs anywhere.
- Plugins loaded with `--plugin-dir` are bundled too when they're workspace plugins. A changed plugin folder changes the bundle's checksum, so it's copied again.
- Calls from a workspace plugin to the shell (commands, the proposed workspace and build APIs, the Test Explorer) cross the network. They're the same calls a local plugin makes, and the round trip is the session's.
- `PluginManager.Hosts` says which groups run and where, for the Plugins window and tests.

**Tests**:
- Manifest validation: `runsOn` values and host groups.
- `RemoteSession.BundlePath`.
- `RemoteShellTests.Tests_run_on_the_server_through_the_workspace_plugin`: the Test Explorer in a remote window runs samples/Testing's xUnit tests in a plugin host started through the server, and the host is retired on disconnect.
