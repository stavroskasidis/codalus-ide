# Shell ↔ plugin host protocol (v0.1)

This is the transport between the shell and `daedalus-pluginhost`: **JSON-RPC 2.0** over stdio, framed with LSP `Content-Length` headers, the same framing as `docs/protocol.md`. Requests flow in both directions. Parameters are a single camelCase object. Cancellation uses `$/cancelRequest`. The design decisions are in [ADR 0003](adr/0003-plugin-api.md). The C# contracts live in `src/Daedalus.Plugins.Protocol`.

## Lifecycle

| Method | Direction | Kind | Params → Result |
|---|---|---|---|
| `initialize` | shell → host | request | `{protocolVersion, shellVersion, hostGroup, settings: {key: value}}` → `{protocolVersion, hostVersion}` |
| `plugins/load` | shell → host | request | `{plugins: [{id, version, installPath, assembly}]}` → `{results: [{id, loaded, error?}]}` |
| `plugins/activate` | shell → host | request | `{id, reason}` → `{activated, error?}` |
| `plugins/deactivate` | shell → host | request | `{id}` → `{}` |
| `shutdown` | shell → host | request | `{}` → `{}` (deactivates every plugin) |
| `exit` | shell → host | notification | |

A protocol major mismatch in `initialize` is an error. On a minor mismatch, both sides use the lower minor.

## Commands

| Method | Direction | Kind | Params → Result |
|---|---|---|---|
| `commands/register` | host → shell | notification | `{pluginId, command}` |
| `commands/unregister` | host → shell | notification | `{pluginId, command}` |
| `commands/execute` | shell → host | request | `{command, args: any[]}` → `{result?: any}` |
| `commands/executeInShell` | host → shell | request | `{command, args: any[]}` → `{result?: any}` (any command, built-in or contributed) |

## Window

| Method | Direction | Kind | Params → Result |
|---|---|---|---|
| `window/showNotification` | host → shell | request | `{pluginId, severity: info\|warning\|error, message, actions: string[]}` → `{action?: string}`, resolved when the user picks an action or dismisses |
| `window/showQuickPick` | host → shell | request | `{pluginId, items: [{label, description?, detail?}], placeholder?}` → `{index?: number}` (null when cancelled). Since API 0.1.1. |
| `window/showInputBox` | host → shell | request | `{pluginId, title, prompt?, value?}` → `{value?: string}` (null when cancelled). Since API 0.1.1. |
| `output/append` | host → shell | notification | `{pluginId, channel, text}` (channels are created on first use) |
| `output/clear` | host → shell | notification | `{pluginId, channel}` |
| `output/show` | host → shell | notification | `{pluginId, channel}` |
| `statusBar/update` | host → shell | notification | `{pluginId, itemId, text, tooltip?, command?, alignment: left\|right, priority, visible}` |
| `statusBar/remove` | host → shell | notification | `{pluginId, itemId}` |
| `treeView/refresh` | host → shell | notification | `{viewId, handle?}` (`null` refreshes the whole view) |
| `treeView/getChildren` | shell → host | request | `{viewId, handle?}` → `{items: [{handle, label, description?, tooltip?, icon?, collapsible: none\|collapsed\|expanded, command?: {id, args}}]}` |
| `markdownView/set` | host → shell | notification | `{viewId, markdown}` |
| `log` | host → shell | notification | `{pluginId?, level: trace\|debug\|info\|warning\|error, message}` |

## Settings

| Method | Direction | Kind | Params |
|---|---|---|---|
| `settings/didChange` | shell → host | notification | `{changes: {key: value \| null}}` (`null` = reset to the default) |

The host keeps a settings cache, seeded by `initialize`, and plugin `Settings.Get<T>` reads from that cache.

## Errors

These are JSON-RPC error codes. `-32601` means the method was not found. Daedalus-specific codes:
- `-33000`: plugin not found
- `-33001`: plugin not activated
- `-33002`: activation failed
- `-33003`: command handler threw
