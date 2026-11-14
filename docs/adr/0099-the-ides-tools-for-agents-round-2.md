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
| Does driving a desktop app need an approval of its own? | **No**: an action like the others. (Deferred here; built in M60b, below.) |
| Which Git operations are offered? | **Status, diff, log, history, blame, stage, unstage, commit, stash, check out, conflicts and their resolution.** Never push, pull, fetch, reset, rebase, amend, a forced operation or deleting a branch. |
| The consent question for agents outside the IDE | **Once per folder**, on top of `ai.mcpServer.enabled`: Allow (until the window closes), Always for This Folder (the user's state, never the repository), Don't Allow (the default). Unanswered for two minutes, the call is refused and the next one asks again. Listing the tools never asks. |
| The headless mode (`codalus mcp --folder`) | **Not built in this milestone** (`TODO(issue)`): a window-less IDE that takes actions only under `allow` needs its own trust gate and lifetime; the agents that need it today run beside a window. Built in M60b (below), with its trust gate and lifetime. |

## Decision

- **One catalog, by group.** `IdeTools.BuiltIn` is round 1's tools and then `IdeToolsRound2.All`: code changes (`IdeCodeChangeTools`), Hot Reload and profiling (`IdeRuntimeTools`), services (`IdeServiceTools`), packages and migrations (`IdePackageTools`), Git (`IdeGitTools`) and the IDE (`IdeShellTools`). The shell serves each group in `ShellHost.IdeTools.<Group>.cs`, behind round 1's dispatcher and its checks: paths confined to the workspace, excluded files left out, answers redacted and cut to size, a line in the AI Output channel per call (now with its duration).
- **The services the windows use.** Each tool goes through the service its window uses (the language service's rename and code actions, Replace in Files' engine, Apply Code Changes, the profiler and heap snapshots, `GitService`, the Package Manager's `PackageService`, the Entity Framework window's flow, the Containers and Aspire windows, the command registry), so the IDE shows every change where the user would look for it.
- **Progress.** `codalus mcp` sends MCP's `notifications/progress` every 10 seconds while a call runs, when the client sent a progress token.
- **Setups to copy.** **Set Up Agents Outside the IDE…** (`ai.mcpServer.setup`) shows each known agent's setup with this install's path (`McpSetups`: Claude Code, Codex and Gemini CLI as commands, GitHub Copilot CLI's file and `.mcp.json`'s JSON), each with a Copy button, and offers to turn MCP Server on. MCP Server's description in Options › AI points at it.

## Consequences

- An agent renames, applies fixes, formats, commits, adds packages and migrations, restarts services and profiles through the IDE, in its windows, instead of the CLI or text edits.
- Files the tools write are read again by the C# language host before the next code-change tool, so a second call never works on stale text; files an agent writes with its own tools are still picked up by the host's file watcher.
- Not built here, each a `TODO(issue)`: the desktop app being built driven through the live agent (its windows, control tree and input; Avalonia and WPF) and the headless mode, both built in M60b (below), and a data tip-style approval text per new tool (they use the generic "run {tool}." text).

## What M60b built (2026-11-14)

On 2026-11-14 the maintainer decided that both deferred items are built before 0.13. Settled with them, as recommended: the desktop-app tools' actions follow `ai.agent.ideActions` with no approval of their own (D52's third row); reading the app's windows and controls is Read, and input sent to it is Action.

- **The desktop app being built** (`IdeDesktopAppTools`, served in `ShellHost.IdeTools.DesktopApp.cs`). The tools reach the app the IDE runs or debugs through the live session the Live Visual Tree uses (ADR 0052; WPF's agent, ADR 0072), which gained three commands in both agents: `controls` (each element of the logical tree with its type, name, text, state words and bounds in the window), `picture` (a PNG of the window, rendered by the app itself at 96 dpi, so a covered or off-screen window has one) and `input`. `windows` now carries each window's size.
  - `desktop_app_windows` (Read): the windows, with a picture of each (six at most).
  - `desktop_app_controls` (Read): the control tree, indented, with an optional filter that keeps the matches' ancestors.
  - `desktop_app_input` (Action): a click, text, keys or a scroll sent to a control by its id or name. Input never goes through the OS: a click is the control's automation peer (invoke, toggle, select, expand; the nearest control around it that has one), text is the text box's own text input (Avalonia's text input events, WPF's `AppendText` or `Text`), keys are routed key events raised on the control, a scroll calls its scroll viewer. The user's pointer doesn't move and their focus stays where it is. WPF reads modifier keys from the keyboard device, so a chord with Ctrl, Shift or Alt is refused there rather than sent without them.
  - **Off-screen.** `run_project` and `debug_start` take `offScreen`. On Linux with Xvfb on the PATH, the IDE starts one Xvfb (`-displayfd 1 -nolisten tcp`, a 1920x1080 screen), kept until it exits, and the program's plan gets its `DISPLAY` once (`RunService.ProgramPlan` is called once per start). Elsewhere, or without Xvfb, the windows open as usual and the answer says so: Windows and macOS have no window server an app can be sent to without a session of its own, and since input never uses the OS pointer or focus, an app on screen is not in the user's way. A virtual desktop on Windows and a second login session on macOS were not built.
- **The headless mode** (`codalus mcp --folder <path>`, `HeadlessIde`). The folder must be trusted in the user's trust store (or be inside a trusted folder); otherwise the process writes why to standard error and exits with code 2 before anything starts. The shell then runs on Avalonia's headless platform, the one the UI tests use (`Avalonia.Headless`, Skia drawing, a 50 ms render tick), with its main window shown on it, opens the folder and serves `IdeTools` through `McpStdioServer` with the same dispatcher (`ShellHost.IdeToolAsync`, caller `codalus mcp --folder`). It ends when standard input ends: the shell shuts down as a window's does and the process exits with 0.
  - **Nobody to ask.** Reads run; an action runs only when the user's `ai.agent.ideActions` is `allow` (an approval that would ask is refused with that reason, and "Always Allow in This Workspace" doesn't apply); a plugin's tool, which asks every time, is refused; every dialog and picker is cancelled as it opens.
  - **No per-folder question.** The general pipe's consent (above) doesn't apply: the agent started the IDE on a folder it named, and it needs no `ai.mcpServer.enabled`.
  - **Its own process.** It takes no single-instance requests and opens no MCP pipe, so a window of the same user-data folder keeps both; it starts none of a window's startup work (updates, settings sync, recovery, the start window, the setup). Its standard input and output are the agent's alone: on Unix they are duplicated for the server and the process's own become `/dev/null` and standard error (on Windows, the standard handles are swapped), so nothing a host or a program writes reaches the agent's pipe.
- Still deferred (`TODO(issue)`): pictures of a window's popups (menus, combo box drop-downs: separate top levels the agent doesn't list), drag and pointer gestures other than a click, an off-screen run on Windows and macOS, and a headless mode for a remote folder.
