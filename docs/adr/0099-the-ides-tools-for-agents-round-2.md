# 0099. The IDE's tools for agents, round 2

- **Status:** accepted (0.13, 2026-11-13)
- **Date:** 2026-11-13
- **Deciders:** maintainer (D50, D52)
- **Amends:** [ADR 0092](0092-the-ides-tools-for-agents-and-a-compact-ai-chat.md) (more tools in the same catalog and dispatcher; agents outside the IDE asked once per folder; progress for long calls)
- **Milestone:** [M60](../roadmap.md#m60--the-ides-tools-for-agents-round-2)

## Context

Since ADR 0092, every agent (agent mode, an external agent's own `codalus mcp` pipe, an agent outside the IDE through the general pipe) gets the IDE's code intelligence, builds, tests, runs, the debugger and a web app's browser. For the rest of the IDE they fell back on the CLI or plain text edits: refactoring, Hot Reload, profiling, containers and Aspire, Git, NuGet and migrations. An agent outside the IDE reached it whenever `ai.mcpServer.enabled` was on, with nothing asked per folder, and a minutes-long call gave its client no sign of life.

## Decisions (D52, settled at M60's plan as recommended)

| Question | Verdict |
|---|---|
| How do edits made by tools (rename, code actions, format, replace) get approved? | **As actions under `ai.agent.ideActions`**, not the preview: they are the compiler's or the user's own edit engines, not text the agent wrote. Each is one undo step in an open editor; a closed file is written. A change that would touch a file outside the workspace or excluded from AI is refused whole, and the answer lists every file changed. |
| Which new tools are Read and which are Action? | **Read** changes nothing and runs none of the workspace's code: code actions listed, Git's status, diffs, history, blame and conflicts, packages listed and searched, services and their logs, commands and settings listed, a picture of the IDE. **Action** changes files, the repository, the program or the IDE, or runs the workspace's code: listing and scripting migrations among them, since `dotnet ef` builds and runs the startup project. |
| Does driving a desktop app need an approval of its own? | **No**: an action like the others. (The desktop-app tools are deferred; see below.) |
| Which Git operations are offered? | **Status, diff, log, history, blame, stage, unstage, commit, stash, check out, conflicts and their resolution.** Never push, pull, fetch, reset, rebase, amend, a forced operation or deleting a branch. |
| The consent question for agents outside the IDE | **Once per folder**, on top of `ai.mcpServer.enabled`: Allow (until the window closes), Always for This Folder (the user's state, never the repository), Don't Allow (the default). Unanswered for two minutes, the call is refused and the next one asks again. Listing the tools never asks. |
| The headless mode (`codalus mcp --folder`) | **Not built in this milestone** (`TODO(issue)`): a window-less IDE that takes actions only under `allow` needs its own trust gate and lifetime; the agents that need it today run beside a window. |

## Decision

- **One catalog, by group.** `IdeTools.BuiltIn` is round 1's tools and then `IdeToolsRound2.All`: code changes (`IdeCodeChangeTools`), Hot Reload and profiling (`IdeRuntimeTools`), services (`IdeServiceTools`), packages and migrations (`IdePackageTools`), Git (`IdeGitTools`) and the IDE (`IdeShellTools`). The shell serves each group in `ShellHost.IdeTools.<Group>.cs`, behind round 1's dispatcher and its checks: paths confined to the workspace, excluded files left out, answers redacted and cut to size, a line in the AI Output channel per call (now with its duration).
- **The services the windows use.** Each tool goes through the service its window uses (the language service's rename and code actions, Replace in Files' engine, Apply Code Changes, the profiler and heap snapshots, `GitService`, the Package Manager's `PackageService`, the Entity Framework window's flow, the Containers and Aspire windows, the command registry), so the IDE shows every change where the user would look for it.
- **Progress.** `codalus mcp` sends MCP's `notifications/progress` every 10 seconds while a call runs, when the client sent a progress token.
- **Setups to copy.** **Set Up Agents Outside the IDE…** (`ai.mcpServer.setup`) shows each known agent's setup with this install's path (`McpSetups`: Claude Code, Codex and Gemini CLI as commands, GitHub Copilot CLI's file and `.mcp.json`'s JSON), each with a Copy button, and offers to turn MCP Server on. MCP Server's description in Options › AI points at it.

## Consequences

- An agent renames, applies fixes, formats, commits, adds packages and migrations, restarts services and profiles through the IDE, in its windows, instead of the CLI or text edits.
- Files the tools write are read again by the C# language host before the next code-change tool, so a second call never works on stale text; files an agent writes with its own tools are still picked up by the host's file watcher.
- Not built here, each a `TODO(issue)`: the desktop app being built driven through the live agent (its windows, control tree and input; Avalonia and WPF), the headless mode, and a data tip-style approval text per new tool (they use the generic "run {tool}." text).
