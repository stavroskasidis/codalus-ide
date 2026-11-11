# 0095. AI Console: an agent's own CLI in the terminal, and how far the external agents' exception goes

- **Status:** accepted (0.13, 2026-11-11)
- **Date:** 2026-11-11
- **Deciders:** maintainer (the grilling of 2026-11-11, below)
- **Amends:** [ADR 0079](0079-external-agents-over-the-agent-client-protocol.md) (the exception also covers a connected agent's own CLI, which the IDE starts but doesn't serve; `ai.agents` entries gain `console`), [ADR 0083](0083-connecting-external-agents-from-templates.md) (Start a Chat after Connect opens AI Console), [ADR 0062](0062-ai-provider-api-and-data-boundaries.md) (Ctrl/Cmd+Alt+I is AI Console's key when an agent with a CLI is connected)

## Context

AI Chat runs an external agent over the Agent Client Protocol (ADR 0079): the IDE draws the conversation, and the agent's file, terminal and permission requests go through the IDE's checks. The maintainer asked for an alternative: a console, like the agents' own command-line interfaces, opened in the bottom group beside the Output, the Debug Console and the Terminal, where AI Chat opens on the right.

The agents' CLIs are full-screen terminal programs with their own prompt, slash commands, history and approvals. They can't be drawn by the IDE, only run in its terminal emulator (ADR 0014). And a CLI in a terminal works on the disk by itself: nothing it does passes through the refactoring preview or the IDE's command approval. That is more than ADR 0079's exception allowed, where the agent could work by itself but was offered the IDE's requests.

## The maintainer's decisions (the grilling, 2026-11-11)

| Question | Options | Verdict |
|---|---|---|
| What the console is | A console-styled view of the chat's own session; the agent's real CLI in a terminal; both | **The agent's real CLI in a terminal.** |
| Which program | The connected agent's own CLI from the PATH; the pinned package through npx; any command from a setting | **The connected agent's own CLI**, found on the PATH, with its install offered when it's missing; an entry written by hand names its CLI with `console`. The built-in assistant has none. |
| The rules it runs under | The external agents' gate, labeled; the gate and a one-time acknowledgement; as a terminal, with no gate | **The external agents' gate, labeled**, as an amendment to ADR 0079. |
| The IDE's tools | A session pipe per console; that and an entry in the CLI's own config; none | **A session pipe per console**, given on the command line where the CLI takes one. Nothing is written to a CLI's own config; a CLI that takes none says so. |
| AI Chat and the console | A setting that picks the surface; two independent windows; the console for every external agent | **Two independent windows, the console the recommended one.** |
| What "recommended" changes | The entry points; docs and menu order only; the editor's AI actions too | **The entry points**: Connect's Start a Chat, Ctrl/Cmd+Alt+I when an agent with a CLI is connected, the menus' order and the documentation. The editor's AI actions stay AI Chat's. |
| Sessions | Tabs, started fresh; one session; tabs, resumed after a restart | **Tabs, resumed after a restart.** |
| Context from the editor | References, never content; the selected text pasted; nothing | **References, never content.** |
| Delivery | One change with a draft PR; one change; an ADR and a plan first | **One change with a draft PR.** |

## Decision

### The window

AI Console is a tool window (`aiConsole`) whose default place is the bottom group. Each console is a tab that holds one terminal session of the IDE's terminal service, so the emulator, the font, the colors, links and the keys kept from the shell (`terminal.commandsToSkipShell`, which now includes the AI key) are the Terminal's. Its toolbar has the agent a new console starts, New, Restart, Close and AI Chat. It has no split, find or clear: a CLI redraws its own screen.

The first console starts when the window is first shown in a workspace, for the agent chosen last (`ai.console.agent`, then AI Chat's `ai.chat.agent`, then the first). Closing the last console doesn't start another.

### Which program runs (`Codalus.Workbench/Ai/Cli/`)

- `AgentClis.Known` has one CLI per agent template, by the program the catalog names: `claude`, `codex`, `gemini`, `copilot`, `pi`, `opencode`, `goose`. An entry Connect wrote (`template`) has its template's CLI.
- An entry written by hand names its own: `"console": "mycli"` or `"console": { "command": "mycli", "args": ["chat"] }`. It runs as written, with no IDE tools and nothing resumed. `console` is read from the user's settings with the rest of `ai.agents`, and isn't part of the entry's identity for AI Chat.
- The program is found on the PATH (or is the rooted path `console` gives). **A program inside the workspace is refused**: a cloned repository's folder on the PATH (`node_modules/.bin`, a relative entry) can't supply what the console starts.
- It starts in the workspace's folder, where a CLI works. ADR 0079 starts the ACP adapter in an empty folder because `npx` resolves a package there; the console runs an installed program by its full path instead.
- Its environment is the Terminal's, plus the entry's `env` and `secretEnv` values (from the OS credential store, never a file or the log), without its `unsetEnv`, and with Codalus's Node.js first on the PATH for `"node": "builtIn"`.
- On Windows it starts as an agent's command does (`WindowsCommand`): npm's `.cmd` scripts through `cmd.exe`, only with arguments `cmd.exe` reads as written.
- A CLI that isn't installed: the window says so and offers the install its template knows (pi, OpenCode, goose), npm pinned to the catalog's version where the agent's package is its CLI (Gemini CLI, GitHub Copilot), or the CLI's own install page (Claude, Codex, whose CLIs are other packages than their adapters). The exact command shows before it runs.

### The exception, widened (amends ADR 0079)

A console starts only where an external agent may (`AcpAgents.WhyNot`): AI assistance on, the agent in the user's `ai.agents`, a trusted folder, and not a remote window. The gate is read again when AI's state, the workspace, its trust or the agents change; a console that may no longer run is ended, and the window says why.

What holds:
- off by default, user settings only, trusted folders only, never remote, as for every external agent;
- the IDE's tools the CLI gets pass the same dispatcher as every agent's (`ShellHost.IdeToolAsync`): paths inside the workspace, exclusions, redaction, `ai.agent.ideActions`, a line of the AI Output per call;
- each start is a line of the AI Output: the agent and the command line, never the environment;
- Codalus sends nothing of the workspace to the CLI. References name files; the CLI reads them.

What doesn't hold, and the label says:
- **the CLI changes files and runs commands by itself.** No edit passes the refactoring preview and no command the IDE's approval. What asks the user is the CLI's own permission prompts;
- what it reads goes to its provider under that provider's terms.

Every console shows, above its terminal: "External agent's own CLI: it uses its own provider and data policy, and changes files and runs commands itself, asking you with its own prompts. Codalus doesn't preview its edits." On a row of its own that wraps (up to three lines), with its whole text in its tooltip: the first smoke pictures, on both Linux and Windows, showed a one-line label beside the toolbar cut to "it uses its own provider and d…" at the bottom group's usual width, which left out the part that matters. Above it, whether the IDE's tools are connected.

The same program typed into the Terminal runs today with no gate at all. The gate is here because Codalus starts the program, gives it the user's keys and the IDE's tools, and presents it as a feature of the IDE.

### The IDE's tools

Each console whose CLI can take an MCP server for one run gets `codalus mcp --agent-session <token>` on a pipe of its own (ADR 0079's, opened through the same `StartMcpServer`), closed when its CLI ends. The caller's name in approvals and the log is "*agent* (AI Console)", so a grant for a chat isn't a console's.

| CLI | How it takes the server | Its last conversation |
|---|---|---|
| Claude | `--mcp-config <file>` | `--session-id <uuid>` at start, `--resume <uuid>` |
| Codex | `-c mcp_servers.codalus.command='…' -c mcp_servers.codalus.args=[…]` (TOML literal strings) | `resume --last` |
| GitHub Copilot | `--additional-mcp-config @<file>` | `--continue` |
| goose | `session --with-extension "<command line>"`, when no part of it has a space or a quote | `session --resume` |
| Gemini CLI | none: its servers come from its settings file | `--resume latest` |
| pi, OpenCode | none confirmed | none confirmed |

The files are in `<state>/ai-console/`, hold only the command and the pipe's token (no secret: the pipe's access check is the operating system's), and are deleted with the console. Nothing is written to a CLI's own configuration. A console without the tools says "The IDE's tools aren't connected: *cli* takes no MCP server for one run." The flags are from each CLI's documentation on 2026-11-11; the versions aren't pinned, since the CLI is the user's.

### Consoles that come back

The running consoles of a workspace are kept in the user's state (`<state>/ai-console.json`: the agent and, for Claude, the conversation's id). Never in the solution's `.codalus` folder, which a cloned repository could ship; an id that isn't a UUID is dropped when read, since it becomes an argument.

When the window is shown again for that workspace (after a restart, or after the gate closed them), each kept console starts again:
- by its own id where Codalus gave the CLI one (Claude);
- else the first console of an agent continues the CLI's last conversation in the folder, and the others start fresh;
- a resumed start that ends with an error within 20 seconds had nothing to resume (a console nothing was said in): it starts fresh in the same tab, once.

A CLI that ends by itself leaves its tab with what it printed and isn't kept; Restart starts a new conversation in it.

### References

**Add Selection to AI Console** and **Add File to AI Console** (the editor's AI menu, Solution Explorer's context menu, the palette), and a file dropped on the console, type a reference into the selected console's prompt without sending it: `@src/App/Program.cs#L10-24` for Claude, `@src/App/Program.cs (lines 10-24)` for Codex, Gemini CLI and Copilot, `src/App/Program.cs:10-24` for the others. The agent reads the file itself, from the disk (the status bar says so when the file has unsaved changes). A file the AI boundary excludes (ADR 0062) isn't named.

### Entry points

- `ai.console.show` has the AI key (Ctrl/Cmd+Alt+I). It opens AI Console when an agent with a CLI is connected, else AI Chat. `ai.chat.show` keeps its command and menu items, without a default key.
- Connect's Start a Chat opens a console for the agent when it has a CLI, else AI Chat. AI Chat answers with that agent too.
- View and Tools > AI list AI Console first.
- Explain, Fix with AI, Generate Tests and the other AI actions still use AI Chat: they need the IDE's context and preview.

## Consequences

- A user who prefers an agent's own interface has it in the IDE, with the IDE's tools where the CLI takes them, beside the build output and the terminal.
- AI Chat remains the only place where an external agent's edits are previewed and its commands approved by the IDE. The label and the user guide say which window does what.
- The CLIs change. A flag that changes breaks a console's start (its tab shows the CLI's own error) or drops its tools; the catalog is one file to update.
- **Tests** (Linux validated; Windows and macOS not yet):
  - Workbench (`AgentConsoleTests`): each CLI's command line, the environment, the program refused inside the workspace, references, and the kept consoles.
  - Shell (`AiConsoleTests`), with a stand-in CLI on a test PATH: the console at the bottom with its label, the pipe reaching this IDE, the gate, a console resumed by its id and one with nothing to resume, a missing CLI, references, and the entry points.
  - The smoke test's `ai.console` step runs it in the shipped app.
- Deferred (`TODO(issue)`):
  - the IDE's tools and resuming for OpenCode and pi, once their flags are confirmed;
  - a console's own conversation for CLIs that take no id (they continue the folder's last);
  - find in a console;
  - AI Console in remote windows;
  - pinned installs for Claude's and Codex's CLIs.
