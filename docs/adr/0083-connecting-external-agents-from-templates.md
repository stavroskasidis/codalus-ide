# 0083. Connecting external agents from templates

- **Status:** accepted (0.6.0, task #79, 2026-10-22; D43)
- **Date:** 2026-10-22
- **Deciders:** maintainer (D43: the mockups and the plan's decisions, approved 2026-10-21)
- **Amends:** [ADR 0079](0079-external-agents-over-the-agent-client-protocol.md) (terminal sign-in is advertised; Connect replaces Add agent…; `secretEnv`, `unsetEnv`; agents' keys), [ADR 0031](0031-git-hosting-and-repository-features.md) (a plugin host's `secrets/*` requests are checked against its own plugins)

## Context

M48 (ADR 0079) let AI Chat run external agents over the Agent Client Protocol, and its Add agent… wrote a pinned `ai.agents` entry. Everything after that was the user's: installing Node.js or the agent's CLI, signing the agent in (ADR 0079 deferred terminal sign-in, which Claude's and pi's adapters need), finding out whether it works, and putting a key somewhere. The maintainer asked for agents to be set up from the IDE, with no manual steps where the user's own package managers can do them, and approved the drawn dialog and the plan's decisions as D43.

The pinned packages decide much of the design (read from their code, recorded in `tests/Shared/AcpRecordings`):

- Claude's adapter offers only terminal methods (`claude-ai-login`, `console-login`; in an SSH session or with `NO_BROWSER`, only `claude-login`, its own TUI). It creates a session while signed out and asks for sign-in only on `session/prompt`. It pushes who is signed in on a vendor notification, `_auth/status_update`, marked by `agentCapabilities._meta.authStatus`: `kind: "none"` is signed out, and silence means it couldn't tell. It advertises `logout`.
- Codex and Gemini CLI refuse `session/new` with `auth_required` while signed out. Codex's `api-key` method takes the key in `authenticate`'s `_meta["api-key"].apiKey` and keeps it in its own store (`~/.codex`); it advertises `logout`. Gemini CLI's methods are its auth types (`oauth-personal`, `gemini-api-key`); authenticating a different way resets its saved Google sign-in and rewrites its own settings file.
- pi's adapter offers one terminal method (`pi_terminal_login`) and needs pi's own CLI; OpenCode and goose run their own CLIs, which the registry ships only as archives.
- Copilot, pi, OpenCode and goose aren't known to refuse anything while signed out.

## Options

| Question | Options | Verdict |
|---|---|---|
| Where the agents come from | Fetch the ACP registry at run time; committed templates | **Committed templates** (`AcpAgentTemplates`), re-pinned by hand to the registry (2026-10-22): Claude 0.94.0, Codex 2.2.2, Gemini CLI 0.65.0, GitHub Copilot 1.0.103, pi-acp 0.0.34, OpenCode 1.19.4, goose 1.55.0. One click for the first four, More agents… for the rest. |
| Terminal sign-in | Keep it deferred; run it in a forced local terminal; advertise `auth.terminal` and run it in Codalus's terminal | **Advertise it** (D-terminal-auth): the configured command with the method's arguments and variables, in the agent's own folder, after showing the exact command line and the variables' names; then the agent starts again (the protocol's "reconnect and reinitialize"), and `authenticate` is never sent for it. Refused in a remote window, whose terminal runs on the remote host. |
| How the test knows sign-in worked | `session/new` for every agent; one tiny prompt (D-probe); per template | **Per template** (`AcpSignInCheck`): `session/new` for Codex and Gemini CLI, Claude's `_auth/status_update` (waited for up to 8 s), and "not confirmed" for the others. No prompt is ever sent. Silence is never "connected". |
| When settings are written | When the dialog's consent box is checked; after the test | **Only after a passing test** (D-save), or with Save Anyway when sign-in isn't confirmed; a failed test offers fixes and Try Again. The test runs on the dialog's unsaved consent (D-gate): the box is checked, or AI is on in the user's settings. `ai.enabled` is written with the entry, only when the box turned it on (D-ai-on). |
| Where keys go | The settings' `env`; the agent's own store; SecretStorage | **SecretStorage**, under the shell's own id (`codalus.shell.agents`, key `<agent>/<NAME>`), with the names in the entry's `secretEnv`; except Codex, whose key goes once in `authenticate` and stays with Codex. Without a working credential store the key is kept for the session, and the dialog says so (D-keys). |
| Plugins and agents' keys | A naming convention; check the plugin id | **Checked** (D-plugin-secrets): a plugin host's `secrets/*` request must name one of that host's own plugins, and never an id under `codalus.shell.`. |
| Node.js | Only the user's; Codalus's pinned Node.js (ADR 0024, 0027) | **The user's package managers first**: winget (then the PATH is read again from the registry), Homebrew, snap on Ubuntu, apt or dnf only when the version they'd install is new enough; nodejs.org's page everywhere. **Codalus's Node.js 24.21.0 only as an opt-in where none of those can** (D-node), following ADR 0027's `Npm.BuiltIn`: the entry gets `"node": "builtIn"`, and the agent runs with its folder first on the PATH. |
| Remote windows | Refuse; force a local terminal | **Refuse** (D-remote), as ADR 0079 does for agents: Connect, Check and terminal sign-in say why, and AI Chat disables its buttons. `TODO(issue)`. |
| Comments in `ai.agents` | Document that writes drop them; edit one member | **Edit one member** (`JsoncEditor.SetMember`, `SettingsService.SetMemberAsync`): Connect, Disconnect and Add agent's successor leave the other entries and the comments inside the object as they are. |

## Decision

### Templates and the entry

A template (`AcpAgentTemplate`) is the run part (`AcpKnownAgent`: the pinned command), its vendor, its tier, what it needs (a Node.js major from npm's `engines`, its own CLI and that CLI's minimum), its sign-in options (`AcpSignInOption`: an agent method, a terminal method with the arguments the dialog shows, an API key given as a variable or once in `_meta`, the agent CLI's own login command, or "I'm signed in already"), how its sign-in is checked, how its own CLI installs (npm with an exact version, or Homebrew), and whether it can sign out. The entry Connect writes is `{ "command", "args", "secretEnv"?, "unsetEnv"?, "template": "claude@0.94.0", "node"? }` in the user's settings only. The test refuses to run a terminal method whose arguments differ from the ones the dialog showed.

### Claude's sign-in (D43)

Claude's subscription goes through Claude Code's own login in the official adapter, with no `--hide-claude-auth`. Codalus never asks for, reads or stores Claude's tokens. A subscription sign-in starts the adapter without `ANTHROPIC_API_KEY`, `ANTHROPIC_AUTH_TOKEN` and `ANTHROPIC_BASE_URL` (the entry's `unsetEnv`; they would outrank the login), names the agent Claude (never Claude Code), adds nothing to its prompt, shows the adapter's remote sign-in in an SSH session with the hint to run `claude` and type `/login`, and fetches the adapter with npx at run time.

### The connection test

`AcpAgentConnector.TestAsync`: the requirements; the download step, `npx --yes --package=<pkg>@<ver> --loglevel=http -- node -e 0`, whose `http fetch` lines are the progress and whose E404, network, proxy, certificate and EBADENGINE errors are said in plain words (so the agent's own start keeps its 60 s); the agent started in `<state>/external-agents/connect-test` with no workspace, `mcpServers: []`, and a client handler that refuses every file, terminal and permission request; the chosen sign-in; `session/new`; then the template's check. The agent is stopped at the end. If AI is turned off meanwhile and the consent was the setting's, the test stops. Every step is a line of the AI Output channel, with names, never values (Claude's report only by its kind). The test's `session/new` may leave an empty session in the agent's own history.

### Agents' keys

`secretEnv` names (a variable's name, not one `AcpTerminals.RefusedVariable` refuses, not also in `env`; a bad one skips the entry with why) are read from SecretStorage once per agent and kept until a key is stored or deleted, so a turn doesn't ask the OS store; a changed key restarts the session. The values join the agent's environment and the session's exact-value redaction (command output, stderr tails, messages) beside `SecretRedactor`'s patterns. SecretStorage is probed once before the dialog says where a key goes, since it is "persistent" until a call fails. A key in a variable is visible to commands the agent runs by itself; Codalus's own `terminal/*` launches don't inherit it.

### Disconnect

Disconnect stops the agent's chat; with "Also sign out" (unchecked; offered when the template can sign out) it starts the agent once in the test folder and sends `logout`; then it removes the entry and the keys. It never changes `ai.enabled` and never touches npm's cache.

## Consequences

- AI Chat's off and no-provider states offer the one-click agents, its agent menu adds Connect, More agents… and Manage Agents…, and the palette and Tools > AI have the commands. The Options editor for External Agents is a later PR (D-scope, `TODO(issue)`); the setting's description mentions Connect and `secretEnv`.
- The internal plugin protocol changes: `secrets/*` requests are checked against the host's plugins (`docs/plugin-protocol.md`); `ISecrets` and `PublicAPI.txt` are unchanged. Plugins that share a host group could still read each other's secrets; agents' keys are under the shell's id, which no plugin can name.
- `ProcessLaunchInfo` gains `UnsetEnvironment`, for local processes only.
- Pairs that stay manual (a page) without Codalus's Node.js: Claude (and Copilot, if it needs 22) on Debian or Ubuntu without snap and on macOS without Homebrew; goose on Windows and most Linux distributions. The user guide has the table, checked by `AcpAgentCatalogTests`.
- Risks: Claude's check relies on a vendor `_meta` extension that may change; Codex continues session creation when its account read is unavailable; Copilot, pi, OpenCode and goose may create sessions signed out. Each of these is caught by the first prompt's `auth_required`, which now offers sign-in and sends the prompt again once (a new session after a terminal sign-in: no `session/load`).
- Manual checks for the maintainer, not tests: connect the real agents once; refresh the recordings with `eng/record-acp-initialize.sh` (the committed ones are read from the packages' code); confirm the download step and `npx -y` share npm's cache; the snap channel and the winget id.
- Deferred (`TODO(issue)`): the Options → AI → External Agents editor and "Update available" from the `template` field; templates as a plugin contribution point; elicitation (Codex's device code); agents and sign-in in remote windows; Claude's identity in the chat's header.
