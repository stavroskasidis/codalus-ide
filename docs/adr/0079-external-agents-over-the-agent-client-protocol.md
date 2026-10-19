# 0079. External agents over the Agent Client Protocol, and their exception to the AI boundary

- **Status:** accepted (M48, 2026-10-18; D34)
- **Date:** 2026-10-18
- **Deciders:** maintainer (D34: the exception below is the maintainer's choice)
- **Amends:** [ADR 0062](0062-ai-provider-api-and-data-boundaries.md) (`AiService` is no longer the only code whose data reaches a model), [ADR 0063](0063-agent-mode-safety-model.md) (an external agent's requests pass the same checks as agent mode's tools; `codalus mcp` gets a pipe per session)

## Context

AI Chat's agent mode (ADR 0063) runs the loop in the workbench: the model is the user's chosen provider, reached only through `AiService`, so every byte of workspace content that reaches a model has passed the boundary of ADR 0062 (exclusions, redaction, the size budget, the request log). Coding agents are now programs of their own: pi (through pi-acp), Claude (through its ACP adapter), Codex, Gemini CLI, GitHub Copilot, OpenCode, Goose. The Agent Client Protocol (ACP, [agentclientprotocol.com](https://agentclientprotocol.com/protocol/overview)) is how an editor runs one: the editor starts the agent as a process and speaks JSON-RPC on its stdio; the agent streams its messages, thoughts, plans and tool calls back, and asks the editor to read and write files, run commands and allow what it is about to do. The maintainer asked for an ACP client in AI Chat, next to the built-in agent (roadmap M48).

Such an agent calls its own model provider, with its own account and data policy. What it sends there is out of the IDE's reach: a file it reads through the IDE goes to its provider, and so does what it reads by itself. CLAUDE.md's rule, "`AiService` is the only code that may send workspace content to a model", can't fully hold for it. The maintainer chose to allow it as a recorded exception (D34), with conditions, rather than to leave external agents out.

## Options

| Question | Options | Verdict |
|---|---|---|
| Whether external agents may run at all | No (keep the boundary absolute); yes, as a bounded exception | **Yes, as an exception** (D34): off by default, user settings only, trusted workspaces only, and labeled whenever one is chosen. The built-in agent stays the default and keeps the full boundary. |
| The client library | The community .NET ACP packages (dotacp and others); our own on `Codalus.Rpc` | **Our own.** The packages are unofficial, and dotacp depends on StreamJsonRpc, which serializes by reflection (the shell is Native AOT). The client is `Codalus.Rpc`'s `JsonRpcConnection` with source-generated `System.Text.Json`, and types written by hand from the official schema (protocol version 1, schema 1.12.0). |
| The transport | ACP's stdio (one JSON message per line); our `Content-Length` framing | **ACP's stdio.** `JsonRpcConnection` gains newline-delimited framing (`JsonRpcFraming.NewlineDelimited`) and a configurable cancel notification (`$/cancel_request` with `requestId`). The agent runs under `SupervisedProcess` with no restarts: its session lives in the process. |
| Where agents are configured | Workspace settings; user settings | **User settings only** (`ai.agents`), as `ai.mcp.servers` (ADR 0063): a cloned repository can't start a process. |
| How many processes | One per IDE; one per chat session | **One per chat session**, stopped on New Chat, when another agent is picked, when the workspace closes or changes, and on exit. |
| What the agent may ask of the IDE | Nothing (it works on the disk itself); the protocol's file, terminal and permission requests | **The requests, through the IDE's checks**, each advertised in `initialize`: `fs/read_text_file`, `fs/write_text_file`, `terminal/*`, `session/request_permission`; compaction and notices. Not advertised: terminal sign-in (`auth.terminal`) and elicitation (`TODO(issue)`). |
| How the agent gets the IDE's tools | Not at all; `codalus mcp` only when `ai.mcpServer.enabled` is on; `codalus mcp` with a pipe for the session | **A pipe for the session** (below): every agent gets the same tools, and the general pipe stays off unless the user turns it on. |
| Approving permissions | The agent's options as offered; without always-allow | **Without always-allow** (ADR 0063: no always-allow yet): Allow Once, Reject and Always Reject, Reject the default. |

## Decision

### The exception (D34), and what changes in ADR 0062 and 0063

- **Off by default.** No agent is configured (`ai.agents` is `{}`), and an agent runs only while AI assistance is on (`ai.enabled`, which only the user's settings turn on and a workspace can turn off).
- **User settings only.** `ai.agents` is read from the user's settings layer; a workspace's `.codalus/settings.json` can't add one. AI Chat's "Add agent…" (approved by the maintainer on 2026-10-19) writes a known agent's entry, pinned to the ACP registry's version in a committed list (`AcpAgentCatalog`, never fetched), to the user's settings after a dialog that shows the exact command; nothing is downloaded or run until a chat with it starts.
- **Trusted workspaces only.** Picking an agent in a folder that isn't trusted (or with no folder) shows why in the chat, and nothing starts.
- **Labeled.** Whenever an external agent is chosen, AI Chat shows: "External agent: uses its own provider and data policy. Codalus checks what it asks the IDE for." Each of its turns says which agent answered.
- **What still holds** (the IDE's side of the boundary): what the IDE gives the agent passes the same gate as everything `AiService` sends. The context chips go through `AiService.PrepareAsync` (exclusions, redaction, budget) before they are put in the prompt; `fs/read_text_file` refuses paths outside the workspace (links included) and excluded files and returns redacted text; terminal output the agent reads is redacted; `codalus mcp`'s tools leave excluded files out and redact. Every prompt, request and tool call is a line of the AI Output channel (the agent, the method, the path or command, never content).
- **What doesn't hold:** the agent reads files by itself too, writes them by itself if it wants, and sends whatever it has to its provider. Only edits it makes through `fs/write_text_file` are reviewed before they land; whether it does depends on the agent. An edit it reports as a diff without that request is shown read-only in its step, and an open editor reloads the file. The user guide says so.
- **ADR 0062** changes: `AiService` remains the only code that sends workspace content to a *provider the IDE talks to*; an external agent is the one other path, bounded as above. CLAUDE.md's rule names this ADR.
- **ADR 0063** changes: an external agent's requests use agent mode's checks (`WorkspacePaths.Resolve`, the exclusions, `SecretRedactor`, the refactoring preview, the approval dialog with Reject the default, `ai.agent.commandTimeoutSeconds`), and `codalus mcp` gains `--agent-session`.

### The client (`Codalus.Workbench/Ai/Acp/`)

- `AcpProtocol.cs`: the messages, written from `schema/v1/schema.json` of agent-client-protocol 1.12.0, with `AcpJsonContext` (source-generated). Unions (content blocks, session updates, tool call content) are read by their discriminator into flattened types.
- `AcpClient`: starts the agent (`SupervisedProcess`, newline-delimited, no restarts, no shutdown request: closing stdin ends it; a Windows `.cmd` such as `npx.cmd` runs through `cmd.exe`), sends `initialize` with protocol version 1 and refuses another version, and wraps `authenticate`, `session/new`, `session/prompt`, `session/set_mode` and `session/cancel`. A prompt completes only after the updates sent before its answer are handled.
- `AcpAgentSession`: one chat session. `session/new` sends `cwd` = the workspace root, no `additionalDirectories`, and `mcpServers` = `codalus mcp`. Updates become the turn: message chunks, thoughts (collapsed), the plan, tool calls and their updates as steps (kind, title, status, locations, diffs, output), available commands (offered in the input as `/commands`), the current mode (a picker), notices and compaction notices. Stop sends `session/cancel`, answers pending permissions `cancelled`, closes pending approvals, kills the turn's commands, marks unfinished steps cancelled, and waits 10 seconds for the agent's `cancelled` before ending the turn anyway. A crash ends the turn with the agent's last lines of standard error; the next question starts a new process.
- `AcpAgents`: `ai.agents` (user settings only), the gate (`WhyNot`), and the one running session.

### Serving the agent's requests

- **`fs/read_text_file`:** the editor's buffer when the file is open (unsaved changes included), else the disk; `line` (1-based) and `limit` honored; refused outside the workspace (links included) and for excluded files; redacted.
- **`fs/write_text_file`:** the refactoring preview of the whole-file change; the request is answered only after Apply (into the editor's buffer, opening the file when it isn't open: unsaved, one undo step; a new file is created on the disk) or Reject (a JSON-RPC error that says the user rejected it). Same path rules as reading.
- **`session/request_permission`:** the approval dialog with the tool call's title, kind, raw input and diffs. Allow Once, Reject and Always Reject (the agent's `allow_once`, `reject_once` and `reject_always`); `allow_always` isn't offered; Reject is the default and Esc rejects; `cancelled` when the turn is.
- **`terminal/*`:** each command asks first with its exact command line and folder (inside the workspace), as `run_command` does; it runs through the IDE's process launcher (`IProcessLauncher`; a command without arguments that has spaces goes through `/bin/sh -c` or `cmd /c`), its output kept within the agent's byte limit (cut from the start), shown live in the step and redacted, stopped after `ai.agent.commandTimeoutSeconds`, killed when the turn is cancelled, and freed on `terminal/release`.
- **`authenticate`:** when `session/new` answers `auth_required`, the agent's sign-in methods are shown and the chosen one runs (`authenticate`); the dialog says the agent manages its own sign-in and Codalus doesn't see or keep its credentials. Codalus stores nothing of it (anything it would keep goes through `SecretStorage`). Terminal sign-in isn't advertised (`TODO(issue)`).
- **`elicitation/create`:** not advertised (`TODO(issue)`).

### `codalus mcp` for every session, and how its pipe is scoped

- The entry is `codalus mcp --user-data-dir <dir> --agent-session <token>` with the running executable's absolute path (`dotnet` and the shell's `codalus.dll` when it runs that way; `--user-data-dir` only when the IDE has one).
- **The pipe:** for each session the IDE opens its own pipe, `<instance pipe>-a<token>` (the token is 12 random hexadecimal characters, short because a Unix pipe is a socket file with a path limit), and closes it when the session ends (New Chat, another agent, the workspace closing, a crash, exit). `codalus mcp --agent-session <token>` relays to that pipe instead of the general one.
- **Scope:** the pipe accepts the current user only (`PipeOptions.CurrentUserOnly`, as the single-instance and the general MCP pipes), lives exactly as long as the session, serves the same tools as the general pipe with the same exclusions, redaction and the build's approval, and logs each call with the agent's name. It is open even when `ai.mcpServer.enabled` is off: that setting still governs the general pipe, which other programs reach without a session. The token names the pipe; the access check is the operating system's.

## Consequences

- External agents work with any ACP agent the user installs; Codalus ships, installs and pins none (the user guide has pinned example entries for pi, Claude, Codex and Gemini).
- The IDE can't vouch for what an external agent sends to its provider: the label says so, the user guide explains the limits, and the built-in agent stays the default with the full boundary.
- `Codalus.Rpc` can now speak newline-delimited JSON-RPC; MCP's client and server keep their own small writers for now.
- Tests never reach a real agent: a stand-in agent (`tests/Codalus.AcpAgent.Fake`, its script in `tests/Shared/StandInAcpAgent.cs`) plays scripted sessions.
- Deferred (`TODO(issue)`): HTTP MCP servers in `session/new` (when the agent advertises `mcpCapabilities.http`), elicitation, terminal sign-in, `session/load` and resuming a conversation, config options, images and embedded resources in prompts, agents in remote windows (they would run on this machine).
