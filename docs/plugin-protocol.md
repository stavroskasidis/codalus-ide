# Shell ↔ plugin host protocol (v0.2, plugin API 1.0)

This is the transport between the shell and `codalus-pluginhost`: **JSON-RPC 2.0** over stdio, framed with LSP `Content-Length` headers, the same framing as `docs/protocol.md`. Requests flow in both directions. Parameters are a single camelCase object. Cancellation uses `$/cancelRequest`. The design decisions are in [ADR 0003](adr/0003-plugin-api.md). The C# contracts live in `src/Codalus.Plugins.Protocol`.

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
| `window/showInputBox` | host → shell | request | `{pluginId, title, prompt?, value?, browse?}` → `{value?: string}` (null when cancelled). Since API 0.1.1; `browse` (`folder` or `file`: the text is a path, with a Browse button) since API 1.1.0. |
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
| `workspace/packages` | host → shell | request | `{path}` → `{packages: [{id, requestedVersion?, resolvedVersion?, isTransitive, targetFrameworks}]}` (API 0.2.2) |
| `workspace/properties` | host → shell | request | `{path, names}` → `{values: {name: value}}`, evaluated with the active configuration (API 0.2.2) |
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
| `debug/registerVisualizer` | host → shell | notification | `{visualizerId, pluginId, title, types}`; `visualizerId` is `pluginId/id`, a type ending in `*` is a prefix (API 0.2.7) |
| `debug/unregisterVisualizer` | host → shell | notification | `{visualizerId}` (API 0.2.7) |
| `debug/visualize` | shell → host | request | `{visualizerId, expression, type?, value}` → `{text?, languageId?, columns?, rows?}` (API 0.2.7) |
| `debug/evaluate` | host → shell | request | `{expression}` → `{value, type?}`, in the paused program's selected frame; an error when it isn't paused or the evaluation fails (API 0.2.7) |
| `terminal/create` | host → shell | request | `{name?, profile?, workingDirectory?, program?, arguments?, environment?, show, reportOutput}` → `{terminalId, processId}`: a shell (profile id, null for the default) or, with `program`, that program in a terminal (API 0.2.3) |
| `terminal/sendText` | host → shell | request | `{terminalId, text}` → `{}` (as typed; `\r` runs a line) |
| `terminal/kill` | host → shell | request | `{terminalId}` → `{}` (ends the program and closes the terminal) |
| `terminal/output` | shell → host | notification | `{terminalId, text}` (UTF-8 decoded, escape sequences included; only with `reportOutput`; may arrive before the `terminal/create` response) |
| `terminal/exited` | shell → host | notification | `{terminalId, exitCode?}` |
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

### Hosting providers and secrets

Since API 0.2.6 (protocol 0.2), [ADR 0031](adr/0031-git-hosting-and-repository-features.md); messages in `HostingMessages.cs`. A provider is declared in the manifest (`contributes.hostingProviders`) and registered when its plugin activates (`onHostingProvider:<id>`). Every shell → host request names the provider (`providerId`); `repository` is `{id, owner, name, webUrl, host, remote?}` as the provider resolved it, and `$/cancelRequest` cancels any of them (the sign-in when the user cancels). Dates are ISO 8601.

| Method | Direction | Kind | Params → Result |
|---|---|---|---|
| `hosting/registerProvider` | host → shell | notification | `{pluginId, providerId}` |
| `hosting/unregisterProvider` | host → shell | notification | `{pluginId, providerId}` |
| `hosting/deviceCode` | host → shell | notification | `{providerId, userCode, verificationUri, verificationUriComplete?, expiresAt, message?}` while a sign-in waits for the user |
| `secrets/get` | host → shell | request | `{pluginId, key}` → `{value?}` |
| `secrets/store` | host → shell | request | `{pluginId, key, value}` → `{}` (the OS credential store, under the account `<pluginId>/<key>`) |
| `secrets/delete` | host → shell | request | `{pluginId, key}` → `{}` |
| `hosting/resolve` | shell → host | request | `{providerId, remotes: [{remote, url}]}` → `{repository?}`: the first remote (in order) on the provider's host |
| `hosting/account` | shell → host | request | `{providerId}` → `{account?: {name, host, displayName?, url?}}` from the stored session |
| `hosting/signIn` | shell → host | request | `{providerId, token?, integrated?}` → `{name, host, displayName?, url?}`: the device flow, a personal access token, or with `integrated` (since API 1.2.0) the operating system's account |
| `hosting/signOut` | shell → host | request | `{providerId}` → `{}` |
| `hosting/repositoryInfo` | shell → host | request | `{providerId, repository}` → `{defaultBranch, webUrl, isPrivate}` |
| `hosting/pullRequests` | shell → host | request | `{providerId, repository, filter: all\|mine\|reviewRequested}` → `{pullRequests: [pullRequest]}` |
| `hosting/pullRequest` | shell → host | request | `{providerId, repository, number}` → `{pullRequest, description, threads: [{id, path, line, startLine?, isResolved, isOutdated, url?, comments: [{author, body, createdAt, url?}]}], comments: [comment]}` |
| `hosting/checkout` | shell → host | request | `{providerId, repository, pullRequest}` → `{remote, sourceRef, localBranch, track}` |
| `hosting/createPullRequest` | shell → host | request | `{providerId, repository, title, description, sourceBranch, targetBranch, isDraft}` → `pullRequest` |
| `hosting/branchStatus` | shell → host | request | `{providerId, repository, branch, commit?}` → `{state: success\|failure\|pending\|none, checks: [{name, state, description?, url?}], url?}` |
| `hosting/workItems` | shell → host | request | `{providerId, repository, assignedToMe, text?, limit}` → `{workItems: [{number, title, reference, webUrl, state?, type?, assignedTo?}]}` |
| `hosting/owners` | shell → host | request | `{providerId}` → `{owners: [{id, name, kind: user\|organization\|group\|project}]}` |
| `hosting/publish` | shell → host | request | `{providerId, owner, name, description?, isPrivate}` → `{cloneUrl, webUrl, sshUrl?}` |

The `pluginId` of a `secrets/*` request must be one of that host's own plugins, and never an id under `codalus.shell.` (the shell's own, where external agents' keys are; ADR 0083): the shell refuses it otherwise (0.6.0). Plugins in the same host group can still name each other's ids.

A `pullRequest` is `{number, title, author, sourceBranch, targetBranch, webUrl, reference, isDraft, state, createdAt?, updatedAt?, sourceCommit?, sourceRepository?, sourceCloneUrl?, reviewers}`; `sourceRepository` is set for a fork's pull request. Thread paths are relative to the repository, with forward slashes; lines are one-based in the pull request's version of the file.

### Language model providers

Proposed (M28a), [ADR 0062](adr/0062-ai-provider-api-and-data-boundaries.md); messages in `LanguageModelMessages.cs`. A provider is declared in the manifest (`contributes.languageModelProviders: [{id, label, description?, local?}]`) and registered when its plugin activates (`onLanguageModelProvider:<id>`, implicit), which the shell does only once the user has chosen it. Every shell → host request names the provider; `$/cancelRequest` cancels any of them (Stop in AI Chat).

| Method | Direction | Kind | Params → Result |
|---|---|---|---|
| `languageModels/registerProvider` | host → shell | notification | `{pluginId, providerId}` |
| `languageModels/unregisterProvider` | host → shell | notification | `{pluginId, providerId}` |
| `languageModels/models` | shell → host | request | `{providerId}` → `{models: [{id, name, contextWindow?, maxOutputTokens?, supportsTools}]}` |
| `languageModels/status` | shell → host | request | `{providerId}` → `{isReady, message?}` without a network call (a key is stored, the endpoint is set) |
| `languageModels/configure` | shell → host | request | `{providerId}` → `{isReady, message?}` after the provider asked the user for what it needs (its own input boxes) |
| `languageModels/chat` | shell → host | request | `{providerId, requestId, modelId, messages, tools?, maxOutputTokens?, stop?, options?}` → `{finishReason: stop\|length\|toolCalls\|refused, inputTokens?, outputTokens?, partCount}` |
| `languageModels/chatPart` | host → shell | notification | `{requestId, text?, toolCall?: {id, name, arguments}}` while the chat runs |

A message is `{role: system\|user\|assistant\|tool, content, toolCalls?: [{id, name, arguments}], toolCallId?, isError}`; a tool is `{name, description, parameters}` with a JSON schema; `arguments` is a JSON object as text. The host posts every `chatPart` before it answers `chat`, but the shell may handle the answer first, so it waits until it has `partCount` parts. Errors: `-33020` no such provider, `-33021` the service failed (the message says why), `-33022` the provider needs configuring (a missing or refused key).

## API 1.0 (M22a)

Progress, declarative views and custom editors ([plugins.md](plugins.md#progress)):

| Method | Direction | Kind | Params → Result |
|---|---|---|---|
| `window/progressStart` | host → shell | notification | `{pluginId, id, title, cancellable}`: a task in the status bar |
| `window/progressReport` | host → shell | notification | `{pluginId, id, message?, percent?}` |
| `window/progressEnd` | host → shell | notification | `{pluginId, id}` |
| `window/progressCancel` | shell → host | notification | `{pluginId, id}`: the user cancelled the task |
| `view/set` | host → shell | notification | `{pluginId, viewId, root}`: a declarative view's content; `root` is a view node |
| `view/event` | shell → host | request | `{viewId, elementId, kind: click\|change\|select\|activate, value?, row?}` → `{}` |
| `customEditor/open` | shell → host | request | `{viewType, viewId, path, text}` → `{}`: the plugin fills `viewId` with `view/set` |
| `customEditor/close` | shell → host | notification | `{viewId}` |
| `customEditor/setText` | host → shell | notification | `{pluginId, viewId, text}`: the document's new text (it shows as modified) |

A view node is `{kind, id?, text?, style?, value?, placeholder?, checked?, disabled, options?, columns?, rows?: string[][], children?, orientation?}`, `kind` one of `stack`, `text`, `button`, `textbox`, `checkbox`, `dropdown`, `table`, `toolbar` and `separator`.

File system providers:

| Method | Direction | Kind | Params → Result |
|---|---|---|---|
| `fs/register`, `fs/unregister` | host → shell | notification | `{pluginId, scheme}` |
| `fs/changed` | host → shell | notification | `{scheme, paths, kind: created\|changed\|deleted}` |
| `fs/stat` | shell → host | request | `{scheme, path}` → `{exists, isDirectory, size, modified}` |
| `fs/readDirectory` | shell → host | request | `{scheme, path}` → `{entries: [{name, isDirectory, size, modified}]}` |
| `fs/readFile` | shell → host | request | `{scheme, path}` → `{content}` (base64) |
| `fs/writeFile` | shell → host | request | `{scheme, path, content}` → `{}` |
| `fs/createDirectory` | shell → host | request | `{scheme, path}` → `{}` |
| `fs/delete` | shell → host | request | `{scheme, path, recursive}` → `{}` |
| `fs/rename` | shell → host | request | `{scheme, path, target, overwrite}` → `{}` |

Authentication providers:

| Method | Direction | Kind | Params → Result |
|---|---|---|---|
| `auth/register` | host → shell | notification | `{pluginId, providerId, label}` |
| `auth/unregister`, `auth/sessionsChanged` | host → shell | notification | `{pluginId, providerId, label}` |
| `auth/getSession` | host → shell | request | `{pluginId, providerId, scopes, createIfNone}` → `{session?}`: the shell asks the user's consent when the provider is another plugin's, activates the provider's plugin and asks it |
| `auth/getSessions` | shell → host | request | `{providerId, scopes?}` → `{sessions: [session]}` |
| `auth/createSession` | shell → host | request | `{providerId, scopes}` → `{session}` |
| `auth/removeSession` | shell → host | request | `{providerId, sessionId}` → `{}` |

A `session` is `{id, accessToken, account, scopes}`.

Source control providers:

| Method | Direction | Kind | Params → Result |
|---|---|---|---|
| `scm/register` | host → shell | notification | `{pluginId, providerId, label, root}` |
| `scm/update` | host → shell | notification | `{providerId, groups: [{id, label, resources: [{path, status, tooltip?}]}], inputPlaceholder?, acceptLabel?, status?}` |
| `scm/unregister` | host → shell | notification | `{pluginId, providerId, label, root}` |
| `scm/accept` | shell → host | request | `{providerId, message}` → `{}` |
| `scm/original` | shell → host | request | `{providerId, path}` → `{text?}` |

## Errors

These are JSON-RPC error codes. `-32601` means the method was not found. Codalus-specific codes:
- `-33000`: plugin not found
- `-33001`: plugin not activated
- `-33002`: activation failed
- `-33003`: command handler threw
- `-33004`: no tree data provider for the view
- `-33005`: a proposed API that the shell doesn't offer
- `-33010`: not signed in to the hosting provider, or the session expired (API 0.2.6)
- `-33011`: the hosting provider doesn't support the request
- `-33012`: the host refused or failed (the message says why)
- `-33013`: no hosting provider with that id is registered
- `-33100`: no such file or directory (file system providers, API 1.0)
- `-33101`: access denied, or a read-only file system
- `-33102`: the provider failed (the message says why)
