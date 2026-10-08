# Writing plugins

Plugins extend Daedalus Studio with commands, menus, key bindings, settings, tool windows, notifications, output channels and status bar items. This guide walks through the sample in [`samples/plugins/HelloWorld`](../samples/plugins/HelloWorld). The design and its reasons are in [ADR 0003](adr/0003-plugin-api.md), and the wire protocol is in [plugin-protocol.md](plugin-protocol.md).

**API version 0.1.0.** The API is pre-1.0: a minor release may break plugins. See the [changelog](#changelog).

## How plugins run

- A plugin is a .NET 10 class library. It runs in a **plugin host** process (`daedalus-pluginhost`), never in the IDE process, so a slow or crashing plugin can't freeze the UI. Built-in plugins share one host, and third-party plugins share another. A plugin can ask for a host of its own with `"isolation": "dedicated"`.
- Everything a user can see before your code runs comes from the **manifest** (`daedalus-plugin.json`): commands in the palette and menus, key bindings, tool windows and settings, including the Settings UI.
- Your code is loaded on the first **activation event**: a contributed command is run, a contributed tool window is opened, a setting changes, or startup. Until then no host process runs at all.
- If a host crashes, Daedalus restarts it and reactivates its plugins, and the user is notified. If it keeps crashing, the host is stopped for the session and its plugins are marked failed.

## Project setup

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <EnableDynamicLoading>true</EnableDynamicLoading>
  </PropertyGroup>
  <ItemGroup>
    <!-- The host provides the API at runtime, so it is not copied next to the plugin. -->
    <PackageReference Include="Daedalus.Plugins.Abstractions" Version="0.1.0" ExcludeAssets="runtime" />
  </ItemGroup>
  <ItemGroup>
    <None Update="daedalus-plugin.json" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
</Project>
```

The `Daedalus.Plugins.Abstractions` package is not published yet (the repository is private until M7). Until then, reference the project directly, as the sample does.

## The manifest

```json
{
  "id": "daedalus-samples.hello",
  "version": "1.0.0",
  "displayName": "Hello World",
  "publisher": "daedalus-samples",
  "engines": { "daedalus": "^0.1.0" },
  "entry": { "runtime": "dotnet", "assembly": "HelloWorld.dll" },
  "activationEvents": [ "onSettingChanged:hello.announce" ],
  "contributes": {
    "commands": [ { "id": "hello.sayHello", "title": "Say Hello", "category": "Hello" } ],
    "menus": {
      "menuBar/tools": [ { "command": "hello.sayHello", "group": "plugins", "order": 1 } ],
      "toolWindow/title": [ { "command": "hello.refresh", "when": "view == hello.view" } ]
    },
    "keybindings": [ { "key": "ctrl+alt+h", "mac": "cmd+alt+h", "command": "hello.sayHello" } ],
    "toolWindows": [ { "id": "hello.view", "title": "Hello", "location": "right", "kind": "tree", "icon": "info" } ],
    "settings": {
      "title": "Hello World",
      "properties": {
        "hello.greeting": { "type": "string", "default": "Hello", "description": "The greeting to use." },
        "hello.times": { "type": "integer", "default": 1, "minimum": 1, "maximum": 5 }
      }
    }
  }
}
```

| Field | Meaning |
|---|---|
| `id` | `publisher.name`, lower case. Unique. |
| `version` | SemVer. |
| `engines.daedalus` | An npm-style range over the **plugin API version** (`^0.1.0`, `~0.1.2`, `>=0.1.0 <0.3.0`, `0.1.x`, `a \|\| b`). Plugins whose range doesn't include the running API are not loaded; the user is told why. |
| `entry` | `runtime` is `dotnet`; `assembly` is the plugin DLL next to the manifest. If it contains more than one public `IDaedalusPlugin`, name one with `type`. |
| `activationEvents` | `onStartup`, `onCommand:<id>`, `onView:<toolWindowId>`, `onSettingChanged:<key>`. Contributed commands and tool windows activate the plugin implicitly, so most plugins list nothing here. Prefer lazy events: `onStartup` costs every user startup time. |
| `isolation` | `shared` (default) or `dedicated` (a host process of its own). |
| `enableProposedApi` | Use proposed APIs. Only honored when Daedalus runs with `--enable-proposed-api`. |

### Contribution points

- **commands:** `id`, `title`, `category` (shown as "Category: Title"), `enablement` (a when-clause; disabled commands are hidden from the palette and greyed in menus).
- **menus:** locations `commandPalette` (only to restrict visibility with `when`; every command is in the palette by default), `menuBar/file|view|tools|help`, and `toolWindow/title` (buttons above a tool window). Entries take `command`, `group`, `order` and `when`. Groups are separated in menus.
- **keybindings:** `key` plus optional `mac`, `linux` and `win` overrides, `command`, `when` and `args`. Chords are space-separated (`ctrl+k ctrl+h`). `primary` means ⌘ on macOS and Ctrl elsewhere. Users override bindings in the Keyboard Shortcuts editor; user bindings win over plugin bindings, which win over defaults.
- **toolWindows:** `id`, `title`, `location` (`left`, `right`, `bottom`), `kind` (`tree` or `markdown`) and `icon`.
- **settings:** `title` (the Settings UI category) and `properties`, each with `type` (`string`, `boolean`, `integer`, `number`, `string[]`), `default`, `description`, `enum` and `enumDescriptions`, `minimum`/`maximum`, `scope` (`user`, or `workspace` to allow per-workspace overrides) and `order`. Invalid values in settings.json fall back to the default and are reported in the Settings UI.

### When-clauses

`&&`, `||`, `!`, parentheses, `==`, `!=`, `=~` (regex), `<`, `<=`, `>`, `>=`, and bare keys (truthy). Context keys the shell sets: `isWindows`, `isMac`, `isLinux`, `isDevelopment`, `theme` (`light`, `dark`, `highContrast`), `focusedView` (the focused tool window id, or the kind of the focused document), `activeDocumentKind`, `inCommandPalette`, `toastsVisible`. In `toolWindow/title`, `view` is the tool window's id.

## The code

```csharp
public sealed class HelloPlugin : IDaedalusPlugin
{
    public Task ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        var output = context.Window.CreateOutputChannel("Greetings");
        context.Subscriptions.Add(context.Commands.Register("hello.sayHello", async (_, ct) =>
        {
            var greeting = context.Settings.GetValue("hello.greeting", "Hello");
            output.AppendLine($"{greeting}, Daedalus!");
            await context.Window.ShowNotificationAsync(NotificationSeverity.Info, $"{greeting}, Daedalus!", ["Show Output"], ct);
            return null;
        }));
        context.Subscriptions.Add(context.Window.RegisterTreeDataProvider("hello.view", new GreetingTree(context.Settings)));
        return Task.CompletedTask;
    }
}
```

- `IPluginContext` gives you `Commands`, `Window`, `Settings`, `Log` and `Subscriptions`. Add every registration to `Subscriptions`; they are disposed on deactivation.
- **Commands** take and return JSON-serializable values (`CommandInvocation.Arg<T>(i)` reads arguments). `Commands.ExecuteAsync` runs any command, including the shell's built-ins below.
- **Window:** notifications with actions (the task completes with the chosen action, or null), output channels, status bar items, tree data providers and `SetMarkdown` for Markdown views.
- **Settings:** `GetValue<T>(key, fallback)` and a `Changed` event (`e.Affects("section")`).
- **Log** goes to Output → Plugins.

### Tree views

Implement `ITreeDataProvider<T>`: `GetChildrenAsync(element)` (null means the roots) and `GetTreeItem(element)`, which returns a `TreeItem` with a label, description, tooltip, icon, collapsible state and a command to run when the item is clicked. Raise `Changed` with an element (or null for everything) to refresh. The host maps your elements to handles; you never see the UI.

### Markdown views

`Window.SetMarkdown(viewId, markdown)` supports headings, paragraphs, bulleted and numbered lists, block quotes, fenced code, horizontal rules, `**bold**`, `*italic*`, `` `code` `` and links. Link targets are `https://…` (opened in the browser) or `command:<id>` (runs a command).

### Icons and status bar text

Icons are names from the shell's set, so they follow the theme, including high contrast: `search`, `gear`, `keyboard`, `color-mode`, `history`, `extensions`, `folder`, `file`, `book`, `check`, `circle`, `home`, `info`, `warning`, `error`, `bell`, `bell-dot`, `close`, `chevron-right`, `chevron-down`, `refresh`, `output`, `list`, `play`, `link-external`, `clear-all`, `lock`, `arrow-down`, `star`, `question`, `edit`, `discard`, `trash`, `layout`, `dot`. Status bar text may start with `$(icon)`, for example `"$(home) Start"`.

### Shell commands for plugins

| Command | Arguments |
|---|---|
| `workbench.view.<toolWindowId>` | optional `{ "preserveFocus": true }` |
| `workbench.openExternal` | an `http(s)` URL |
| `workbench.setSetting` | `key`, `value` (null resets) — writes the user's settings.json |
| `workbench.showCommandPalette`, `workbench.openSettings`, `workbench.openKeyboardShortcuts`, `workbench.selectTheme`, `workbench.showLogs`, `workbench.view.plugins` | none |

The built-in Welcome plugin (`src/Plugins/Daedalus.Plugins.Welcome`) is a complete example that reaches the shell only through these commands.

## Running and debugging

1. Build the plugin.
2. Start Daedalus with the build output: `daedalus --plugin-dir path/to/bin/Debug/net10.0`. `--plugin-dir` is repeatable and takes a plugin folder or a folder of plugins; it wins over installed copies with the same id.
3. Check **View → Plugins** for its state, and **Output → Plugins** for the activation log and anything you write to `context.Log`.
4. To debug, attach a .NET debugger to `daedalus-pluginhost`.

Installed plugins live in the user plugins folder (**Tools → Open User Plugins Folder**), one subfolder per plugin.

## Changelog

### 0.1.0 (M1)

The first API: manifest, activation events, commands, menus, key bindings, settings, notifications, output channels, status bar items, tree and Markdown views. Protocol version 0.1.
