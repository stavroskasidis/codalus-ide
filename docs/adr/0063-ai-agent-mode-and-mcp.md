# 0063. Agent mode and MCP

- **Status:** accepted (M28d)
- **Date:** 2026-10-14
- **Deciders:** maintainer (D7; M28's design: agent mode bounded by trust, MCP client and server)

## Context

M28's design asks for two things.
- **Agent mode:** multi-step work in which the model reads, edits and runs things. Every file change goes through the preview, every command through an approval prompt, and all of it is bounded by workspace trust.
- **MCP** both ways: connect the user's MCP servers, and expose Codalus' own context to external agents.

M28a's provider API streams text only. An agent needs tool calls: the model asks for a tool, the IDE runs it, and the result goes back.

## Decision

**Tool calls in the provider API** (still proposed).
- `AiChatRequest.Tools` lists the tools (name, description, JSON schema of the input).
- `IAiModelProvider.ChatEventsAsync` streams `AiEvent`s: text, or a tool call with an id, a name and its JSON arguments. The default implementation wraps `ChatAsync`, so text-only providers still work outside agent mode.
- `AiMessage` gains the assistant's tool calls and a tool result's call id.
- The built-in providers map these onto OpenAI's `tools`/`tool_calls` and Anthropic's `tool_use`/`tool_result`.
- Over the protocol, `ai/chat` with `events: true` sends `ai/event` notifications: `{index, text}` or `{index, toolCall}`, numbered and counted as chunks are.

**The agent** (`AiAgent`, workbench `Ai/`) is a loop of at most 25 steps. Each step sends the conversation; the model answers with text, tool calls, or both. The IDE runs the calls and answers with their results. The loop ends when the model answers without a tool call, the user stops it, or the steps run out. Its tools:
- `list_files` and `read_file`, under the workspace's root. A file the data boundary refuses (secrets, ignored files) is refused to the agent too, with the reason as the tool's result.
- `search_text` finds a literal in the workspace's files, skipping the same files.
- `get_problems` returns a file's diagnostics, or the workspace's.
- `edit_file` replaces exact text in a file, or writes a whole file. **Every edit is shown in the refactoring preview** and applied only when the user chooses Apply. A rejected edit is the tool's result, and the model is told.
- `run_command` runs a command in the workspace's folder. **Every command needs the user's approval** in a dialog that shows it, and only in a trusted workspace. Its output and exit code are the result, cut to 8,000 characters, with a 2-minute limit.
- `build` builds the solution and returns its errors.
- With no workspace open, or an untrusted one, only `read_file`, `list_files`, `search_text` and `get_problems` are offered.

**MCP client.**
- The setting `ai.mcpServers` holds `{ name: { command, args, env } }`, stdio servers like Claude Desktop's and VS Code's. A server starts when agent mode first needs it, and is stopped with the window.
- Its tools join the agent's as `<server>__<tool>`. **Every call needs the user's approval**: Allow Once, or Allow for This Session for that server. It runs the user's own program with the arguments the model chose.
- The client speaks JSON-RPC over stdio: `initialize`, `tools/list`, `tools/call`, with text content.

**MCP server.**
- `codalus mcp` is an stdio MCP server for external agents (Claude Code, other IDEs' agents). It forwards to the running Codalus IDE of that user-data folder, over the single-instance pipe.
- It offers `get_solution` (projects and their frameworks), `get_problems` (a file's or every file's), `get_test_results` (the Test Explorer's last results), and `build` (a build, then its errors).
- With no IDE running, each tool says so. It reads only what the IDE has; it edits nothing.

## Consequences

- Agent mode is as capable as its tools. It edits through exact replacement, so a model that misquotes the text gets "not found" and must read the file again.
- The approvals are prompts, not a sandbox. A command the user approves runs with their rights.
- MCP over HTTP (streamable HTTP, SSE) and MCP resources and prompts are later work.
