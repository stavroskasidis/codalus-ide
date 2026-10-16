# 0052. The Live Visual Tree and Live Property Explorer for running Avalonia apps

- **Status:** accepted (M19b, 2026-10-13)
- **Date:** 2026-10-13
- **Deciders:** maintainer

## Context

M19a gave Avalonia XAML editing and a previewer of the XAML being edited. While an app runs, developers ask what a classic IDE's live tools answer:
- which element is that;
- which template made it;
- why does it have this margin, and from which style;
- what if it were blue.

Avalonia's own DevTools (`Avalonia.Diagnostics`, F12 in the app) answer some of this. But they need a package reference and code in the app, they draw inside it, and they don't know the IDE's XAML files. The previewer's agent (ADR 0048) already reaches Avalonia from outside: a .NET startup hook, reflection only, a socket back to the IDE.

## Decision

**One agent, two modes.** `DaedalusStudio.Previewer.Agent` (netstandard2.0, shipped in `debughost/`) is also the live agent.
- With `DAEDALUS_LIVE_AGENT_PORT` and `_TOKEN` it serves a running app. The previewer's variables keep its designer-host mode.
- Start Debugging and Start Without Debugging add it, through `DOTNET_STARTUP_HOOKS`, to a project that references Avalonia (when `debug.liveVisualTree` is on, the default).
- Reflection only, so one agent serves every Avalonia 11 and 12 app. It does nothing without the variables, and the app's child processes don't inherit them.

**The protocol** is the previewer agent's: tab-separated request lines with an id, and answers `id ok …` or `id error …`. Lists are records separated by `\u001e`, fields by `\u001f`. The agent also sends lines of its own, `event …`, for an element picked in the app and for windows that open or close. Commands:
- `windows`;
- `tree <id> logical|visual`;
- `props <id>`;
- `set <id> <property> <value>`;
- `highlight <id>|none`;
- `pick on|off`;
- `path <id>`.

Elements have ids that are stable while they live (a weak table), so the IDE refreshes without losing its selection.

**What the tools show.**
- **The Live Visual Tree** shows the windows and their elements. By default that's the logical tree (the XAML's elements, a classic IDE's "Just My XAML"); a toggle switches to the visual tree, which adds templates' parts.
- **Select Element in the App** turns on pick mode: the next click in the app selects that element.
- Selecting an element highlights it in the app with an adorner (outside its layout).
- **Go to XAML** finds the element's line through its logical path from the window, the same path the previewer's selection uses, resolved by the C# host's XAML syntax (M19a, ADR 0047). The window's `x:Class` names the file.
- **The Live Property Explorer** lists the selected element's Avalonia properties: value, type and the value's source (local, style, style trigger, template, inherited, animation, default), from Avalonia's `GetDiagnostic`. A local value can be edited: the agent parses it the way XAML would for the property's type (enums, numbers, `Parse` methods, brushes and colors) and sets it as a local value.

**Templates.** The New Project dialog lists the Avalonia templates (`Avalonia.Templates`) when they're installed, and offers to install them with `dotnet new install`.

## Consequences

- No package or code in the user's app, and no Avalonia version lock. The startup hook needs .NET Core 3.0 or later, which every Avalonia 11 app is.
- Edits are runtime only: the IDE doesn't write them back into the XAML. A classic IDE does the same, and XAML Hot Reload is the separate, later candidate (M19's remaining work).
- Remote windows (M21) don't get the agent yet. The socket is the IDE's loopback; a remote app would need the server's tunnel. `TODO(issue)`.
- The adorner is drawn in the app's adorner layer, so an app without one (a custom window without `AdornerDecorator`) gets no highlight. The tree and properties still work.
