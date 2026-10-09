# Shell ↔ plugin host protocol (v0.2)

This is the transport between the shell and `daedalus-pluginhost`: **JSON-RPC 2.0** over stdio, framed with LSP `Content-Length` headers, the same framing as `docs/protocol.md`. Requests flow in both directions. Parameters are a single camelCase object. Cancellation uses `$/cancelRequest`. The design decisions are in [ADR 0003](adr/0003-plugin-api.md). The C# contracts live in `src/DaedalusStudio.Plugins.Protocol`.

## Lifecycle

| Method | Direction | Kind | Params → Result |
|---|---|---|---|
| `initialize` | shell → host | request | `{protocolVersion, shellVersion, hostGroup, settings: {key: value}}` → `{protocolVersion, hostVersion}` |
| `plugins/load` | shell → host | request | `{plugins: [{id, version, installPath, assembly, entryType?, enableProposedApi}]}` → `{results: [{id, loaded, error?}]}` |
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

## Proposed APIs

Since protocol 0.2 (API 0.2.1). Only plugins loaded with `enableProposedApi` use them; see [ADR 0010](adr/0010-test-platform-integration.md) and `ProposedMessages.cs`. Ranges are zero-based `{startLine, startCharacter, endLine, endCharacter}`; documents are `file:` URIs; projects are paths.

| Method | Direction | Kind | Params → Result |
|---|---|---|---|
| `workspace/getSolution` | host → shell | request | `{}` → `{solution?: {path, trusted, configuration?, platform?, sdkPath?, projects: [{path, name, state, targetFrameworks, outputType?, isTestProject, testPlatform?: vstest\|mtp, compileFiles, projectReferences}]}}` |
| `workspace/projectOutput` | host → shell | request | `{path, targetFramework?}` → `{path, targetFramework?, targetPath?, runCommand?, runArguments?, runWorkingDirectory?}` |
| `workspace/testCandidates` | host → shell | request | `{uris}` → `{documents: [{uri, candidates: [{kind: class\|method, name, type, namespace?, range, selectionRange, parameterized}]}]}` |
| `workspace/typeOutline` | host → shell | request | `{uri, line, character}` → `{outline?: {name, namespace?, nestedName, isStatic, hasParameterlessConstructor, methods: [{name, returnType, parameters: [{name, type}], isStatic, isAsync, line}], selectedMethod?}}` |
| `workspace/solutionChanged` | shell → host | notification | `{}` (coalesced) |
| `workspace/documentsChanged` | shell → host | notification | `{uris}` (edited or saved, batched) |
| `build/start` | host → shell | request | `{projects?}` → `{succeeded, canceled, errors, warnings}` (null projects: the solution) |
| `build/finished` | shell → host | notification | `{target, project?, result}` (every build) |
| `debug/launch` | host → shell | request | `{program, arguments, workingDirectory, environment?}` → `{sessionId, processId}` |
| `debug/attach` | host → shell | request | `{processId}` → `{sessionId, processId}` |
| `debug/stop` | host → shell | request | `{sessionId}` → `{}` |
| `debug/ended` | shell → host | notification | `{sessionId, exitCode?}` |
| `tests/createController` | host → shell | notification | `{pluginId, controllerId, label}` (`controllerId` is `pluginId:id`) |
| `tests/disposeController` | host → shell | notification | `{controllerId}` |
| `tests/updateItems` | host → shell | notification | `{controllerId, items: [{id, parentId?, label, description?, uri?, range?, traits?: [{name, value?}], sortText?, error?}], removed: [id]}` (items after their parents; a removed id takes its descendants) |
| `tests/updateProfiles` | host → shell | notification | `{controllerId, profiles: [{id, label, kind: run\|debug\|coverage, isDefault}], canRefresh}` |
| `tests/run` | shell → host | request | `{controllerId, profileId, requestId, include?: [id], exclude: [id]}` → `{runIds}`; `$/cancelRequest` cancels the run |
| `tests/refresh` | shell → host | request | `{controllerId}` → `{}` |
| `tests/runStarted` | host → shell | notification | `{controllerId, runId, name, requestId?, profileKind?}` |
| `tests/runUpdate` | host → shell | notification | `{runId, states: [{itemId, state: enqueued\|started\|passed\|failed\|errored\|skipped, durationMs?, messages?: [{message, expected?, actual?, stackTrace?, uri?, range?}]}], output: [{itemId?, text}], attachments: [{uri, displayName?}]}` |
| `tests/runCoverage` | host → shell | notification | `{runId, files: [{uri, lines: [{line, hits, branchesCovered, branchesTotal}]}], methods: [{assembly, namespace?, class, method, uri?, line, linesCovered, linesTotal, branchesCovered, branchesTotal}]}` |
| `tests/runEnded` | host → shell | notification | `{runId}` |

`tests/run` completes when the profile's handler completes and returns the runs it created. Their `tests/runEnded` may be processed after the response (notifications and responses are dispatched separately), so the shell waits for each.

## Errors

These are JSON-RPC error codes. `-32601` means the method was not found. Daedalus-specific codes:
- `-33000`: plugin not found
- `-33001`: plugin not activated
- `-33002`: activation failed
- `-33003`: command handler threw
- `-33004`: no tree data provider for the view
- `-33005`: a proposed API that the shell doesn't offer
