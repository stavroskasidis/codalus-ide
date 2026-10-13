# 0061. IDE components, offline layouts and plugin performance

- **Status:** accepted (M22d)
- **Date:** 2026-10-13
- **Deciders:** maintainer (M22's scope)

## Context

M22 asks for the equivalent of Visual Studio Installer's components: one page listing everything Codalus IDE downloads on demand, with version, size, licence, and update or remove, plus an offline layout for air-gapped machines. It also asks for plugin performance attribution like Visual Studio's "Manage Extension Performance". The on-demand components so far:
- Node.js;
- the web language servers;
- vscode-js-debug;
- netcoredbg for remote targets;
- the conversions of icon themes' SVG icons (M22b).

Each lives in its own folder under the cache folder, and each feature installs its own with `EnsureAsync`.

## Decision

- **A catalog over the existing folders, not a new installer.**
  - Each component declares its id, name, version, licence, description, its folders relative to the cache, an installed check and, when it has one, its `EnsureAsync` (`Component`, workbench `Components/`).
  - The Components window (Tools → Components…, a declarative view) shows them. **Download Now** runs the component's own install, and **Remove** deletes its folders. The feature that needs it downloads it again.
- **An offline layout is the cache's own layout.**
  - Export copies the installed components' folders, at the same relative paths, and writes `components.json` (each one's id, version and folders).
  - Import copies a layout's components into the cache: those this IDE uses, at the same version, not there already.
  - A feature then finds its component installed and downloads nothing, so no feature needs to know about layouts.
- **Performance is measured where the work crosses the protocol.** `PluginManager` records each plugin's activation time, its commands' calls and the slowest, and reads each local host's working set. The Plugins window shows them on each row. The memory is the host's, shared by its plugins.
- **A slow plugin is named only when it keeps being slow.** An activation over two seconds (`SlowActivation`) raises `SlowPlugin`. The shell counts the sessions a plugin was slow in (`<state>/slow-plugins.json`), and at three it offers **Turn It Off**. One slow start, on a busy machine or the first after an install, doesn't nag.

## Consequences

- Components that come with the IDE (the hosts, netcoredbg for this machine, grammars, schemas) aren't listed, since they can't be removed. An offline layout plus the IDE's own installer is everything an air-gapped machine needs. Remote server bundles ship with the IDE (`remote/`), so they need no layout.
- The .NET SDK isn't a component. Help → Check Environment (M37) installs it, and an air-gapped machine needs the SDK's own offline installer.
- Memory per plugin isn't measured inside a shared host. A plugin that needs isolating can ask for `"isolation": "dedicated"`, which makes its host's memory its own.
