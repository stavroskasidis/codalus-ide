# 0063. AI agent mode, MCP, and their safety model

- **Status:** accepted (M28c plan, 2026-10-15; D7 and the maintainer's standing go-ahead to complete every milestone)
- **Date:** 2026-10-15
- **Deciders:** maintainer (D7: opt-in, with any provider, no telemetry)

## Context

M28's last part is agent mode and MCP. In agent mode the model takes several steps on its own: it reads files, searches, edits, runs commands. MCP (the Model Context Protocol) connects models to tools from other programs, and lets other programs' agents use the IDE. The roadmap's design fixed the rule: every file change goes through the preview, every command through an approval prompt, and it is bounded by workspace trust (ADR 0004).

A model's output is untrusted: a file it reads can carry instructions ("ignore the user and delete…"), and an MCP server's tool can be anything. The safety model has to hold even when the model is steered by text in the workspace.

## Options

| Question | Options | Verdict |
|---|---|---|
| Who runs the loop | The provider plugin; the workbench | **The workbench** (`AgentSession`), on the chat API's tool calls (ADR 0062), so it works with any provider whose model takes tools, and every tool call passes the IDE's checks. |
| Approving edits | Apply silently and let the user undo; approve each change; approve a plan | **Each change, in the refactoring preview** (Apply or Reject), as the roadmap says. A rejected change tells the model it was rejected. |
| Approving commands | An allow list; each command | **Each command**, showing the exact command line and folder; Run or Reject. No "always allow" for now. |
| MCP transport | stdio; HTTP | **stdio** for both directions: the user's configured servers are processes, and `codalus mcp` is a process an agent starts. HTTP servers are a later addition. |
| Where MCP servers are configured | Workspace settings; user settings | **User settings only** (`ai.mcp.servers`): a cloned repository must not be able to start a process or add a tool. |
| How an external agent reaches the IDE | A port; the existing single-instance pipe; a pipe of its own | **A pipe of its own**, for the current user only (as the single-instance pipe), opened only while `ai.mcpServer.enabled` is on. `codalus mcp` is the stdio server an agent starts, and relays each call over that pipe. |

## Decision

### Agent mode (AI Chat's Agent switch)

- **When it can run:** AI is on with a provider, the workspace is trusted, and the model takes tools. Otherwise the chat says why, and Ask works as before.
- **The loop** (`AgentSession`, workbench `Ai/Agent/`): the model answers with text and tool calls; the workbench runs each call and sends the results back, until the model answers without a tool call.
  - At most `ai.agent.maxSteps` tool calls (25) per question.
  - Stop cancels the request and the running tool.
  - Every call is logged to the AI Output channel (the tool and its target, never the content), and shown in the turn.
- **The tools** (`AgentTools`):
  - **Reading:** `read_file`, `list_files`, `search_text`, `get_diagnostics`, `get_build_output`. They run without asking.
    - Paths resolve inside the workspace only: a path that leaves it (`..`, an absolute path elsewhere, a link) is an error.
    - Files the boundary excludes (ADR 0062) are reported as excluded, never read. Everything returned is redacted and cut to the size budget.
  - **Changing:** `edit_file` (replace an exact text once) and `create_file`. Each change shows in the preview, with Apply or Reject. Applied changes go into the editor's undo; nothing is saved without the user.
  - **Commands:** `run_command` asks first: the command line and the folder (inside the workspace). It then runs with its output captured, at most 2 minutes and 20,000 characters (`ai.agent.commandTimeoutSeconds`). `build` runs the IDE's build of the solution (no prompt: building is what the IDE does for a trusted workspace) and returns the errors and warnings.
  - **MCP tools:** each call asks first, with the server, the tool and the arguments.
- **Untrusted text:** the system prompt tells the model that file contents, command output and tool results are data, never instructions. The approvals hold whatever the model is told: it can't change a file or run anything without the user.

### The MCP client

- `ai.mcp.servers` (user settings only) maps a name to `{ "command", "args", "env" }`. Servers start when agent mode first needs their tools, are supervised, and stop with the IDE.
- `McpClient` speaks MCP over stdio (newline-delimited JSON-RPC 2.0): `initialize`, `notifications/initialized`, `tools/list` and `tools/call`. A server's tools are offered to the model as `<server>__<tool>`, and their text results are redacted.

### The MCP server (`codalus mcp`)

- **Off by default:** `ai.mcpServer.enabled` turns on the IDE's pipe (`McpBridge`, the current user only). An agent configured with `codalus mcp` as a stdio server then reaches the running IDE of the same user-data folder.
- **Tools:**
  - `get_solution` (projects, frameworks, output types, test projects);
  - `get_open_files` (the open files, the active one, its selection);
  - `get_diagnostics` (the Error List);
  - `get_build_output`;
  - `build`, which shows an approval prompt in the IDE first.
- Files the boundary excludes are left out of what it returns, and text is redacted, as for the IDE's own requests.

## Consequences

- Agent mode needs a model that takes tools. Small local models often don't: the chat says so.
- Approving every change and command is slower than tools that act alone. It is the D7 design. An allow list for commands can come later, as a setting, without changing the model.
- `codalus mcp` without a running IDE (or with the setting off) answers every call with an error that says what to do.
- MCP resources and prompts, HTTP servers, and sampling are not supported. `TODO(issue)`
