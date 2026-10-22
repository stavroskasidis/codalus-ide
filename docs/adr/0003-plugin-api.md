# 0003. Plugin API v0

- **Status:** accepted (M1 plan approval, 2026-10-08). API 1.0 is [ADR 0058](0058-plugin-api-1-0.md), which keeps this ADR's versioning policy.
- **Date:** 2026-10-08
- **Deciders:** maintainer

## Context

The project brief asks for a **versioned, out-of-process plugin API, designed from the start**. Built-in features (language services, Git, debugger UI) should be implemented on top of it where practical, which proves the API works. M1 delivers plugin host v0 with these pieces:
- a manifest
- activation events
- contribution points for commands, menus, tool windows and settings
- an API version policy

Constraints:
- A crashing or hung plugin must never take down the UI (principle 1).
- The shell is Native AOT, so it can't load plugin assemblies itself.
- The idle-memory budget is ≤ 250 MB for the whole process tree (ADR 0002).
- Plugins must not be able to block the UI thread (principle 2).

## Options considered

| Option | Verdict |
|---|---|
| In-process plugins (VS / MEF style) | Rejected. One faulty plugin takes the IDE down, and an AOT shell can't load arbitrary assemblies anyway. |
| One process per plugin | Strongest isolation, but each .NET host costs about 40–60 MB private, so 5 plugins would exceed the memory budget. Offered as an opt-in. |
| **Shared plugin host processes, grouped by trust level, with an opt-in dedicated host** | **Chosen.** |
| Plugin UI as arbitrary Avalonia controls rendered remotely | Rejected for v0. It needs a remote-UI protocol, which is complex, brittle across versions and hard to make accessible. Revisited in a later ADR, along with webviews (Avalonia 12 ships an open-source WebView). |
| **Declarative plugin UI (data views rendered by the shell)** | **Chosen for v0.** It works across processes and stays AOT-safe, themeable and accessible by construction. |

## Decision

### Process model
- Plugins run in **`daedalus-pluginhost`**: .NET 10 on the regular JIT runtime, talking JSON-RPC over stdio with LSP `Content-Length` framing. The host process is the same model as the language service host's, supervised by the shell's `SupervisedProcess`.
- **Host groups:**
  - `builtin` hosts the plugins that ship with Daedalus.
  - `default` hosts all third-party plugins.
  - A plugin that declares `"isolation": "dedicated"` gets its own host.

  Each plugin is loaded into its own collectible `AssemblyLoadContext`. `DaedalusStudio.Plugins.Abstractions` is shared from the default context so its types unify.
- **Lazy start.** A host process starts only when one of its plugins receives an activation event. With no `onStartup` plugins, no plugin host runs at all.
- **Failure handling.**
  - If a host exits or its connection drops, the shell notifies the user ("Plugin host *default* stopped unexpectedly"). It restarts the host with exponential backoff and re-activates the plugins that were active.
  - More than 5 crashes in 60 s disables the host group for the session, and a notification offers to view the log.
  - A plugin whose activation throws is marked failed, and the other plugins are unaffected.
- **Responsiveness.** Every call from the shell to a plugin is asynchronous and cancellable, with a timeout: activation 10 s, commands none (cancellable), tree requests 5 s. A slow plugin can delay its own feature, never the UI.

### Manifest (`daedalus-plugin.json`)

```json
{
  "id": "contoso.hello",
  "version": "1.2.0",
  "displayName": "Hello",
  "publisher": "contoso",
  "description": "Says hello.",
  "engines": { "daedalus": "^0.1.0" },
  "entry": { "runtime": "dotnet", "assembly": "Contoso.Hello.dll" },
  "isolation": "shared",
  "activationEvents": ["onCommand:hello.sayHello", "onView:hello.view"],
  "contributes": {
    "commands": [{ "id": "hello.sayHello", "title": "Say Hello", "category": "Hello", "enablement": "!isReadOnly" }],
    "menus": {
      "commandPalette": [{ "command": "hello.sayHello", "when": "true" }],
      "menuBar/view": [{ "command": "hello.sayHello", "group": "plugins", "order": 10 }],
      "toolWindow/title": [{ "command": "hello.refresh", "when": "view == hello.view" }]
    },
    "keybindings": [{ "command": "hello.sayHello", "key": "ctrl+alt+h", "mac": "cmd+alt+h", "when": "" }],
    "toolWindows": [{ "id": "hello.view", "title": "Hello", "location": "right", "kind": "tree" }],
    "settings": {
      "title": "Hello",
      "properties": {
        "hello.greeting": { "type": "string", "default": "Hello", "description": "Greeting text." },
        "hello.times": { "type": "integer", "default": 1, "minimum": 1, "maximum": 10, "scope": "user" }
      }
    }
  }
}
```

- `id` is `publisher.name`, lower case. `version` is SemVer.
- `engines.daedalus` is a SemVer range over the **plugin API version**.
- **Activation events:**
  - `onStartup`
  - `onCommand:<id>`
  - `onView:<toolWindowId>`
  - `onSettingChanged:<key>`

  Later milestones add `onLanguage:<id>`, `workspaceContains:<glob>` and `onSolutionOpened`.
- **Contributions are declarative.** Menus, palette entries, key bindings, tool-window placeholders and settings, including their defaults, schema and Settings UI, all exist **without loading plugin code**. Invoking a contributed command or opening a contributed view fires the activation event, and the shell waits for activation before dispatching.
- **Menu locations:** `commandPalette`, `menuBar/<file|edit|view|tools|help>`, `toolWindow/title` and `statusBar`. Ordering uses `group` and `order`. `when` clauses use the shell's context keys.
- **Settings schema:** a JSON-Schema subset (`type`: string, boolean, integer, number or array of strings; `default`, `enum`, `enumDescriptions`, `minimum`, `maximum`, `description`, `scope` user or workspace).

### API (`DaedalusStudio.Plugins.Abstractions`, NuGet, SemVer)

```csharp
public interface IDaedalusPlugin
{
    Task ActivateAsync(IPluginContext context, CancellationToken cancellationToken);
    Task DeactivateAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public interface IPluginContext
{
    PluginInfo Plugin { get; }            // id, version, install path
    ICommands Commands { get; }           // Register(id, handler) -> IDisposable; ExecuteAsync(id, args)
    IWindow Window { get; }               // ShowNotificationAsync, CreateOutputChannel, CreateStatusBarItem,
                                          // RegisterTreeDataProvider<T>, SetMarkdown(viewId, markdown)
    ISettings Settings { get; }           // GetValue<T>(key, fallback), Changed event (Affects)
    IPluginLog Log { get; }               // Write(level, message) + Debug/Info/Warning/Error extensions
    IList<IDisposable> Subscriptions { get; } // disposed on deactivation
}
```

- **Tree views** are declarative. `ITreeDataProvider<T>` returns children and `TreeItem` descriptions: label, description, tooltip, icon name, collapsible state, and a command with arguments. The host maps elements to opaque handles. `Changed` triggers a refresh.
- **Icons** are names from the shell's built-in icon set (for example `file`, `folder`, `gear`, `play`, `info`, `warning`), so plugins match the theme, including high contrast.
- Command arguments and results are JSON values: `JsonElement` on the wire, with typed helpers in the SDK.

### Wire protocol

`docs/plugin-protocol.md` specifies it. It is **language-agnostic** JSON-RPC, so plugins in other languages could implement it later. The .NET SDK (`Abstractions` plus the host) is the only supported implementation in v0. Requests flow both ways: the shell activates plugins, executes commands and asks for tree children, and plugins register commands, show notifications, write output and set status items.

### Versioning policy
- **The plugin API version** is the version of `DaedalusStudio.Plugins.Abstractions`. The **protocol version** (major.minor) is negotiated in `initialize`, and the shell supports the current protocol major plus the previous one.
- **0.x (MVP):** a minor release may contain breaking changes. Each one is listed in `docs/plugins.md#changelog`, and the shell refuses plugins whose `engines.daedalus` range doesn't match.
- **From 1.0:** minor releases are additive only, and breaking changes happen only in a major release. Deprecated members keep working for at least two minor releases, marked `[Obsolete("…", DiagnosticId = "DAEDALUS0001")]`, and are listed in the changelog.
- **Load-time check:** a plugin whose `engines.daedalus` range is incompatible is not loaded. It appears as *incompatible* in the plugin list, with the reason in a notification.
- **Proposed APIs** live in `DaedalusStudio.Plugins.Abstractions.Proposed`. A plugin must set `"enableProposedApi": true` to use them, and they are only available in development builds (`--enable-proposed-api`).

### Trust and installation (v0)
- Plugins are **trusted code**, as in VS and VS Code: there is no sandbox inside the host process. Out-of-process isolation protects the UI's stability, not the user's security.
- Plugins are loaded from:
  - built-ins next to the shell (`plugins/`)
  - the user plugin directory (`<data>/plugins/<id>/`)
  - folders passed with `--plugin-dir` (development)

  A marketplace, signing and a permission model are future ADRs.

### Built-ins on the API
- In M1, the **Welcome** start page (tree view, commands, a setting, a status item) is a built-in plugin that uses only the public API.
- From M2 on, new built-in features default to plugins when they don't sit on a latency-critical path:
  - project templates (M2); amended by [ADR 0082](0082-new-project-wizard.md) (0.6.0): project templates moved into the shell as the New Project wizard, because a wizard needs UI the declarative views don't offer, and D2 put richer plugin UI after 1.0
  - the Error List population (M3/M4)
  - the Git UI (M6)
- The editor, language-service plumbing and debugger core stay in the shell or dedicated hosts for latency reasons. That justification is recorded per feature.

## Consequences
- Plugins can't freeze or crash the UI, and their cost is bounded by host groups and lazy start.
- Rich custom plugin UI is impossible in v0. Plugins use tree and Markdown views, notifications, the Output window and the status bar. A remote-UI or webview ADR must precede any richer surface, by M6 at the latest (the Git diff viewer is built-in). *Update 2026-10-10:* that ADR wasn't written by M6, so rich UI was built into the shell meanwhile (the NuGet manager, M9). The maintainer has since settled the direction (roadmap D2): declarative views first, frozen in API 1.0 (M22), and webviews after 1.0. The ADR comes with that work.
- Built-in plugins share the `builtin` host, so a crash in one restarts the others. This is acceptable because they are first-party and tested, and any of them can move to a dedicated host.
- The protocol and API are public contracts from M1 onward. Changes follow the policy above, and the plugin changelog becomes part of every milestone checkpoint.
