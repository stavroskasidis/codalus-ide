# 0092. The IDE's tools for agents, and a compact AI Chat

- **Status:** accepted (0.12, 2026-11-04)
- **Date:** 2026-11-04
- **Deciders:** maintainer (the grilling of 2026-11-04, below)
- **Amends:** [ADR 0063](0063-agent-mode-safety-model.md) (more tools, `ai.agent.ideActions`, `codalus mcp` lists the running IDE's tools), [ADR 0079](0079-external-agents-over-the-agent-client-protocol.md) (images in prompts, tool titles redacted, `codalus mcp`'s tools), [ADR 0062](0062-ai-provider-api-and-data-boundaries.md) (AI Chat sends only what a question @-mentions)

## Context

The maintainer used AI Chat with an external agent on a large solution and reported four problems:

1. **AI Chat was verbose, with no way to reduce it.** Every tool call was its own row with the agent's raw title. A PowerShell script the agent ran showed whole, including a personal access token it had read. Thinking was a full-width box.
2. **The agent didn't use the IDE.** Every agent got `codalus mcp` on its session's pipe (ADR 0079), but the server had five tools: the solution, the open files, the Error List, the build output and a build. The agent couldn't run a test, debug, start the program or see its browser.
3. **The context chips** (Selection, Current file, Open files, Errors, Build output, Solution) had little value for an agent that can read for itself. What would help is code intelligence: find a symbol, its references and its callers through the compiler, the way code-graph tools index code so that an agent searches them instead of grepping.
4. Anything else that improves the experience.

## The maintainer's decisions (the grilling, 2026-11-04)

| Question | Options | Verdict |
|---|---|---|
| Delivery | ADR and phased PRs; one PR; a plan only | **One PR**, with the ADR, tests, user guide and website. |
| Chat density | Compact with a toggle; compact only; fold after the turn | **Compact by default, with a toggle**: `ai.chat.density` (`compact` or `detailed`) and a toolbar button. |
| Approving IDE actions | A setting that asks once per chat by default; build and tests free, run and debug ask; always ask | **`ai.agent.ideActions`**: `ask`, `askOncePerSession` (the default) or `allow`. |
| Where code intelligence comes from | Roslyn plus Code Search's index; a new persistent code graph; both, with the graph later | **Roslyn and Code Search's index** (ADR 0078). No new index. |
| What replaces the chips | IDE state plus @-mentions; @-mentions only; chips that send references | **@-mentions only.** No automatic context. |
| Debugger control | Full session control; inspect only; run and capture | **Full session control.** |
| Who gets the tools | The built-in agent and the general pipe; external agents only; everyone, plus plugins | **Everyone, and plugins can add tools** (a proposed API). |
| Extras | Steer agents to the IDE's tools; a per-turn changes summary; queued messages; pasted images; and (the maintainer's own) the browser of a web app under the debugger | **All of them.** Fixing the redaction leak was a given. |

## Decision

### One catalog of IDE tools (`Codalus.Workbench/Ai/Ide/IdeTools.cs`)

`IdeTools.BuiltIn` is the only list of tools. Each has a name, a description for the model, a JSON schema and a kind:
- **Read:** never asks.
- **Action:** builds, tests, runs, debugs or drives the browser, and follows `ai.agent.ideActions`.
- **Plugin:** asks every time, as an MCP server's tool does (ADR 0063).

The shell serves the catalog through one dispatcher, `ShellHost.IdeToolAsync`, which every route uses:
- an external agent's session pipe (ADR 0079), still only while the agent may run;
- the general `codalus mcp` pipe, still only while `ai.mcpServer.enabled` is on in the user's settings;
- AI Chat's built-in agent. It gets every tool whose name its own tools don't already use; its own `read_file`, `search_text`, `get_diagnostics`, `get_build_output` and `build` stay as they were.

The dispatcher applies these checks:
- paths resolve inside the workspace (`WorkspacePaths.Resolve`, links included);
- files the boundary excludes (ADR 0062) are left out of every listing and refused when named;
- every answer is redacted and cut to 30,000 characters;
- every call is a line of the AI Output channel: the tool and the caller, never the content.

The tools are:

| Group | Tools | Where the answers come from |
|---|---|---|
| The solution and the editor | `get_solution`, `get_open_files`, `get_diagnostics`, `get_build_output` | Solution service, editors, Error List, Build channel |
| Code intelligence | `find_symbols`, `search_code`, `get_document_outline`, `get_symbol_info`, `go_to_definition`, `find_references`, `find_implementations`, `call_hierarchy`, `type_hierarchy` | Code Search (the index, then live `workspace/symbol`), Find in Files' matching, and the language service's LSP and `codalus/*` requests (`codalus/references` for reference kinds, `codalus/symbolInfo`, `codalus/metadataText` for a referenced assembly's symbol) |
| Build and tests | `build`, `run_tests`, `get_test_results` | `BuildService`, Test Explorer (`TestFilter` for project and failed, a substring for the rest) |
| Running | `run_project`, `get_run_output`, `stop_run` | `RunService`, the run or debug channel |
| Debugging | `debug_start` (a project or one test), `debug_set_breakpoint` / `remove` / `list`, `debug_wait_for_stop`, `debug_get_state`, `debug_get_variables`, `debug_evaluate`, `debug_step`, `debug_continue` | `DebugService` and `BreakpointStore`; the user watches it all in the IDE's debugger windows |
| The browser | `browser_get_page`, `browser_screenshot`, `browser_console`, `browser_evaluate`, `browser_navigate` | `AgentBrowser` (below) |

A navigation tool takes a symbol by name (`Type.Member`, which Code Search's index resolves, then `workspace/symbol`), or a path, a 1-based line and the identifier on it. An agent rarely knows a column, and asking for one would send it back to reading files.

### Approvals: `ai.agent.ideActions`

- `askOncePerSession` (the default): the first action of a chat asks, with **Allow**, **Allow for This Chat** and **Reject** (Reject is the default). The grant is keyed by the caller: an external agent, the assistant, or `codalus mcp`. New Chat, another workspace and AI turned off forget it.
- `ask`: every action asks.
- `allow`: actions run without asking. Workspace trust still applies (ADR 0004).

The setting is read from the user's settings layer only, so a cloned repository's `.codalus/settings.json` can't allow actions for itself. Edits always go through the preview, and shell commands always ask (ADR 0063); neither is an IDE action. `debug_evaluate` is an action because an expression can call methods. Setting a breakpoint isn't, because it changes nothing in the program.

### `codalus mcp`

- `tools/list` asks the running IDE (`$tools/list` on the pipe), so plugins' tools are listed too. When the IDE doesn't answer, the built-in list is used.
- `initialize` answers MCP's `instructions` (`IdeTools.Instructions`): prefer `find_symbols` and the navigation tools to grep, `build` and `run_tests` to the CLI, and the debugger to prints. **Nothing is added to the agent's own prompt** (ADR 0083's rule for Claude stands). The tool descriptions say the same.
- A tool result may carry images (a browser screenshot) as MCP image content. The pipe's answer gains `isError` and `images`.

### The agent's browser (`AgentBrowser`)

The IDE had no Chrome DevTools Protocol client: browser debugging runs vscode-js-debug, and ADR 0090 rejected using its CDP proxy. So the agent gets a browser of its own:
- the Chromium that `debug.browser` finds, started by the IDE;
- a profile in `<state>/agent-browser`, with `--remote-debugging-port=0` on the loopback address only;
- opened at the address the web app the IDE runs or debugs listens on (`RunningProgram.Listening`, or the debug output's "Now listening on:");
- headless under `CODALUS_BROWSER_HEADLESS=1`, as browser debugging is.

The client is `System.Net.WebSockets` with hand-written JSON, so it stays AOT-clean. It reads the page's text or a selector's HTML, takes PNG screenshots, evaluates JavaScript (an awaited promise, its value as JSON) and keeps the last 500 console messages and uncaught errors. `browser_navigate` opens only loopback addresses.

The browser stops at New Chat, when the workspace changes, and at exit. A Blazor WebAssembly app's own debugging browser stays js-debug's: the agent's browser drives the app, while the server side stops at breakpoints in the IDE's debugger.

### Plugins' tools (proposed API)

- **Declaring:** `contributes.aiTools: [{ name, description, inputSchema }]`. The name is snake_case and can't be a built-in tool's. Agents see the tool before the plugin runs.
- **Activation:** the implicit event `onAiTool:<name>`, at the tool's first call.
- **Registering:** `context.Proposed().AiTools.RegisterTool(new MyTool())`, deriving from `AiTool`.
- **Protocol:** `aiTools/registerTool` and `aiTools/unregisterTool` (host to shell, notifications), and `aiTools/invoke` (shell to host, a request); errors `-33030` and `-33031`.
- **Safety:** every call asks; the approval names the plugin; the answer is redacted.
- **Example:** `samples/plugins/WordCount`.

### AI Chat

- **Density.** Compact (the default) folds a turn's steps into one line that opens them, for example "Read 3 files · searched twice · edited Program.cs · ran 2 commands". While the turn streams, the line ends with the step that runs now. Thinking is a short "Thought" line that opens on a click; it is never a box. Detailed opens the steps under the same line, which heads them so they don't read as part of the thought. MCP calls of the IDE's tools (`mcp__codalus__find_references`) show and count as the tool they name. Both modes show a step's long or multi-line title as its first line and put the whole of it in the step's details.
- **@-mentions.** The chips are gone, and only what a question mentions goes with it:
  - `@selection`, `@file` (the active file), `@open`, `@errors`, `@build` and `@solution`;
  - `@path` for a file of the workspace.
  
  Typing `@` lists the parts and the solution's files. Everything passes `AiService.PrepareAsync` as before (exclusions, redaction, the budget). Actions such as Explain, Fix and Generate Tests still choose their own context.
- **Queued questions.** While a turn runs, Enter (or the Queue button beside Stop) puts the question in a queue under the conversation. A queued question can be taken back into the input or removed. The first one goes when the turn ends, unless Stop paused the queue.
- **Images.** An image pasted (any bitmap the clipboard has, sent as PNG) or dropped (PNG, JPEG, GIF, WebP, at most 5 MB) goes with the question as an ACP image block. That happens only when the agent says it takes images (`promptCapabilities.image`); otherwise the chat says the images won't be sent. The built-in assistant takes none: the provider API carries text only.
- **The turn's changes.** Under an ended turn, "Changed N files" lists the files its steps changed: an external agent's diffs, and the built-in agent's edits, which `AgentToolResult.Diff` now records. Each file has:
  - its name, which opens it;
  - **Diff**, which compares the file with a copy of its text before the turn, written under `<state>/ai-changes`;
  - **Revert**, which asks first. It puts the text back into the open editor (unsaved, one undo step) or onto the disk; a file the turn created is deleted.
  
  A change the agent wrote by itself, outside the preview, says so.
- The external-agent label is one line, with its full text in its tooltip. The label still shows whenever an external agent is chosen (ADR 0079).

### Redaction

- `SecretRedactor` now recognizes:
  - Azure DevOps personal access tokens: the 52-character lowercase base32 shape (with a digit) and the 84-character shape with `AZDO`;
  - `$pat = "…"` and `*_PAT` names;
  - `Authorization: Basic|Bearer` values (with a digit or a base64 sign, so prose stays).
- An external agent's tool titles and raw input are redacted before they reach the chat, the approval and the log: the agent's title is often the command it runs.
- A command approval still shows the exact command line (ADR 0079's "what runs is what the user sees").

## Consequences

- An agent in Codalus can find, trace, build, test, run and debug through the IDE: compiler-accurate answers with the user's unsaved edits, in the IDE's own windows. Outside the IDE, the same tools are one setting away (`ai.mcpServer.enabled`).
- `askOncePerSession` lets an agent build and test after one click per chat. Teams that want every action approved set `ask`.
- The chat shows a fraction of what it did. Detailed is one click away, and the AI Output channel keeps the whole log.
- What goes with a question is explicit. A question without mentions sends only the question, a change from the default chips (selection, current file, errors and solution).
- **Tests** (Linux validated; Windows and macOS not yet):
  - Workbench: the catalog, the pipe's list and images, and the agent's browser in a real headless Chrome.
  - Shell: code intelligence on `samples/LanguageFeatures`; approvals and the workspace that can't allow itself on `samples/BuildAndRun`; `run_tests` on `samples/Testing`; a debug session with breakpoints, variables, evaluation and steps on `samples/Debugging` (netcoredbg); a plugin's tool end to end; compact and detailed; queued questions.
  - Plugins: manifest validation.
- Deferred (`TODO(issue)`):
  - a persistent code graph, if cold queries on very large solutions prove slow;
  - `notifications/tools/list_changed` when plugins come and go;
  - images for the built-in assistant (the provider API would need image parts);
  - the agent's browser for remote windows;
  - attaching the debugger to a running process (`AttachToProcessAsync` needs a picker).
