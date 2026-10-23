# 0.6 checkpoint: Connect to *agent* (#79)

Date: 2026-10-22. Task #79 of 0.6, on top of M48 ([m48](m48.md), [ADR 0079](../adr/0079-external-agents-over-the-agent-client-protocol.md)) and the combined 0.6 branch. The design: D43 (`docs/design/controls.md`'s D43 section, `AgentConnect.dc.html`, `AiChat.dc.html`), approved 2026-10-21 with the plan's decisions (D-terminal-auth, D-probe, D-save, D-gate, D-ai-on, D-keys, D-plugin-secrets, D-node, D-remote, D-scope). Decisions: [ADR 0083](../adr/0083-connecting-external-agents-from-templates.md), which amends ADR 0079.

## Done

**Templates** (`AcpAgentTemplates`, Workbench): seven agents, pinned to the ACP registry on 2026-10-22. Claude (`@agentclientprotocol/claude-agent-acp` 0.94.0), Codex 2.2.2, Gemini CLI 0.65.0 and GitHub Copilot 1.0.103 get one click. pi-acp 0.0.34, OpenCode 1.19.4 and goose 1.55.0 are under More agents…. Each template has its vendor, its tier, the Node.js major it needs (from npm's `engines`), its own CLI and that CLI's minimum, its sign-in options, how sign-in is checked (`AcpSignInCheck`), how its CLI installs, and whether it can sign out. `AcpAgentCatalog` is re-pinned to the same versions, and Add agent… is replaced by Connect and More agents….

**The Connect dialog** (`ConnectAgentViewModel`, `ConnectAgentView`, `ShellHost.AcpConnect.cs`): an in-window dialog of 720 px, drawn as `AgentConnect.dc.html`:
- **Requirements**: Node.js and npx (or the agent's CLI) found on the PATH, each with its version, path and the minimum it needs, and a refresh button.
- **Installs**: when something is missing, the user's own package managers are offered first:
  - winget, after which the PATH is read again from the registry;
  - Homebrew;
  - snap on Ubuntu (`node`, `24/stable`);
  - apt or dnf, but only when the candidate they'd install is new enough;
  - nodejs.org's page, everywhere.

  Codalus's own Node.js is offered as an opt-in only where none of those can install a new enough one (D-node). The entry then gets `"node": "builtIn"`, and the agent runs with that folder first on the PATH.
- **Sign in**: the template's options, such as an agent method, a terminal method, an API key, the CLI's own login, or "I'm signed in already".
- **What runs**: the exact command line, the sign-in command line and the variables it starts without.
- **Consent**: a required "Turn on AI assistance" box when AI is off (D-ai-on), and the external agent label.
- **Buttons**: in the OS's order. Connect stays disabled until the consent is given.

**Claude's sign-in** (D43): it goes through Claude Code's own login in the official adapter, with no `--hide-claude-auth`. Codalus never asks for, reads or stores Claude's tokens. A subscription sign-in starts the adapter without `ANTHROPIC_API_KEY`, `ANTHROPIC_AUTH_TOKEN` and `ANTHROPIC_BASE_URL` (the entry's `unsetEnv`). The agent is named "Claude" and nothing is added to its prompt. In an SSH session the adapter's own remote sign-in shows, with the hint to run `claude` and type `/login` (`msg.acp.claudeLoginHint`). The adapter is fetched with `npx -y` at run time.

**Terminal sign-in** (D-terminal-auth): `clientCapabilities.auth.terminal` is advertised now. A terminal method runs the configured command with that method's arguments and variables, in Codalus's terminal, after a confirmation that shows the command line and the variables' names. The agent then starts again and `authenticate` is never sent for it. Variables that `AcpTerminals.RefusedVariable` refuses are refused here too. A terminal method whose arguments differ from the ones the dialog showed is refused. Terminal sign-in is refused in a remote window.

**The connection test** (`AcpAgentConnector.TestAsync`, D-probe, D-gate):
- It runs on the dialog's unsaved consent: the box is checked, or AI is on in the user's settings.
- Download step: `npx --yes --package=<pkg>@<ver> --loglevel=http -- node -e 0`, with npm's E404, network, proxy, certificate and EBADENGINE errors given in plain words.
- The agent starts in `<state>/external-agents/connect-test`, with no workspace and a client handler that refuses every file, terminal and permission request (`RefusingClientHandler`).
- Then the chosen sign-in, `session/new` and the template's check:
  - Codex and Gemini CLI: `session/new` itself.
  - Claude: its `_auth/status_update`, waited for up to 8 s. `kind: "none"` means signed out.
  - The others: "not confirmed".

  No prompt is ever sent, and silence is never counted as "connected".
- Each step is a line in the dialog and in the AI Output channel. Lines give names, never values.
- If AI is turned off meanwhile and the consent came from the setting, the test stops.

**Saving** (D-save): settings are written only after a passing test. Save Anyway is offered only when sign-in isn't confirmed. A failed test offers fixes and Try Again. The entry goes to the user's settings as one member (`JsoncEditor.SetMember`, `SettingsService.SetMemberAsync`), so comments and the other entries stay. Its shape is `command`, `args`, `secretEnv`, `unsetEnv`, `template` and `node`. `ai.enabled` is written only when the box turned AI on.

**Keys** (D-keys, `AcpAgentSecrets`):
- API keys go to SecretStorage under the shell's own id, `codalus.shell.agents`, with the key `<agent>/<NAME>`. The entry's `secretEnv` names them.
- Keys are read once per agent and then cached. A changed key restarts the session.
- Their values are redacted by exact value in every output, beside `SecretRedactor`'s patterns.
- Codex is the exception: its key goes once in `authenticate`'s `_meta["api-key"]` and stays in Codex's own store.
- With no persistent credential store, the key is kept for the session, and the dialog says so.

**Plugins and agents' keys** (D-plugin-secrets): a plugin host's `secrets/*` request must name one of that host's own plugins, and never an id under `codalus.shell.` (`PluginManager.SecretOwnerRefusal`, `docs/plugin-protocol.md`). `ISecrets` and `PublicAPI.txt` are unchanged.

**Manage, Check and Disconnect**:
- Manage Agents… lists the configured agents.
- Check runs the test on a saved entry.
- Disconnect stops the agent's chat. With "Also sign out" (unchecked by default, offered when the template can sign out), it starts the agent once and sends `logout`. Then it removes the entry and the keys. `ai.enabled` is never changed.

**Remote windows** (D-remote): Connect, Check and terminal sign-in say why they can't run, and AI Chat disables its buttons.

**AI Chat**:
- The off and no-provider states offer the four one-click agents, More agents… and a note.
- An external agent that is configured but not running gets its own ready state.
- The agent menu adds Connect, More agents… and Manage Agents….
- An `auth_required` on a prompt offers sign-in and then sends the prompt once more. After a terminal sign-in this is a new session, with a notice that the context was lost.

**Commands**: `ai.agents.connect`, `ai.agents.connect.<id>` (one per template), `ai.agents.manage`, `ai.agents.check` and `ai.agents.disconnect`. The last three are enabled by the `externalAgentsConfigured` context key. They are in the palette and under Tools > AI. New icon kinds: `bot` (purple) and `plug-disconnected` (red).

**Processes**: `ProcessLaunchInfo.UnsetEnvironment`, for local processes only.

**Smoke test**: the step `ai.connect` (D43, after `ai.chat`) puts stand-in `node` and `npx` on the agents' PATH and makes the window local. It then checks:
- AI Chat's off state offers the one-click agents (`ai-chat-connect.png`);
- Connect to Claude shows its requirements, sign-in and exact commands (`agent-connect.png`, also the website's tour image);
- Cancel leaves the user's settings unchanged.

The step never reaches the network.

**Command sweep**: skip reasons for `ai.agents.*`: they start npm or the agent, or sign in and out with the vendor.

**Docs and site**:
- ADR 0083, and ADR 0079's amendment;
- the user guide's [Connecting one](../user-guide/ai.md#connecting-one) section. Its coverage table is checked by `AcpAgentCatalogTests`;
- `docs/plugin-protocol.md`, and the "Built in 0.6" note in `docs/design/controls.md`;
- `CHANGELOG.md` (0.6.0) and the roadmap;
- the website's Features page: the AI bullets, and a "Connect an agent" tour image.

## Tests

All tests use stand-ins. They reach no network and install nothing:
- `StandInAcpAgent` gains these modes: an init file, required variables (matched by `sha256:`), auth on prompt, an auth status, terminal auth, the CLI's login, login failures, client probes, stderr, a start delay, an environment report, and logout.
- `StandInNode` is a `node` and `npx` stand-in. It can be made to download fine, give E404, go offline, fail its engine check or hang.
- `FakePersistentCredentialStore` is a fake credential store.
- `AcpRecordings` holds `initialize` answers for Claude 0.94.0 (local and SSH), Codex 2.2.2, Gemini CLI 0.65.0 and pi-acp 0.0.34. They are labeled as read from the packages' code: they weren't recorded from the real agents. `eng/record-acp-initialize.sh` refreshes them by hand.

The test classes:
- **`AcpAgentConnectorTests`** (13, new):
  - requirements, installs and Node options per OS;
  - the test's steps and its progress, the download step's errors, and the refusing handler;
  - the sign-in checks (Claude's status, `session/new`, not confirmed);
  - AI turned off during a test;
  - saving only after a pass, Save Anyway, and Disconnect with sign-out.
- **`AcpAgentSecretsTests`** (7, new): the probe, storing and caching, deletion, the session-only fallback, redaction, and the plugin owner check.
- **`AcpAgentTests`, `AcpAgentCatalogTests`, `SettingsTests`** (17 new):
  - terminal sign-in and its refused variables;
  - `auth_required` on a prompt and the prompt sent again;
  - `secretEnv` and `unsetEnv` parsing and launching;
  - templates, Node options, the Windows PATH merge, npm errors and the user guide's coverage table;
  - `SetMember` keeping comments.
- **`AcpConnectShellTests`** (Shell, headless, 8, new):
  - with AI off, the chat offers the one-click agents, and the dialog shows what runs before anything runs;
  - in an SSH session, Claude's own remote sign-in shows with its hint;
  - a passing test writes the entry and turns AI on, and Start Chat picks the agent;
  - a terminal sign-in runs in the terminal, and the test goes on when it ends;
  - an API key goes only to the credential store, and the dialog says it lasts until Codalus closes;
  - a signed-out Claude fails with fixes, and nothing is written;
  - a remote window disables the buttons with the reason, and the dialog refuses;
  - the commands are offered while AI is off, and under Tools > AI.
- **Updated:**
  - `AcpShellTests`: More agents… replaces Add agent…, and the picker has 10 items;
  - `AiChatViewTests`: the host;
  - `CommandSweepTests`;
  - localization: `en.json` regenerated with `CODALUS_UPDATE_LOCALIZATION=1`; `localization-literals.txt` unchanged.

## Results

**Linux** (this container, the .NET 11 RC2 SDK, shared with other agents' builds):
- **Build**: Workbench, Shell and the test projects built with warnings as errors, 0 warnings.
- **Workbench**: the `*Acp*`, `*.Ai*`, `*Settings*`, `*Jsonc*`, `*Mcp*` and `*Secret*` classes. 102 tests: 101 passed, 1 skipped (it was already skipped before), none failed. Repeated with no failures. `AcpAgentConnectorTests` passed three runs in a row.
- **Shell**: `AcpConnectShellTests`, `AcpShellTests`, `AiChatViewTests`, `AiChatTests`, `LocalizationCatalogTests`, `IconColorTests`, `CommandSweepTests`, `MenuEntryTests` and `PathBoxTests` all passed, on `DISPLAY=:99`.
- **Plugins**: `HostingPluginHostTests`, `LanguageModelPluginHostTests` and `PluginHostIntegrationTests` (the plugin owner check, with real plugin hosts): 22 of 22 passed.
- **Smoke**: the step `ai.connect` in the real Debug app (`CODALUS_SMOKE_ONLY=ai.connect`) passed in 1.5 s.
- **Website**: `Codalus.Website.Tests` 12 of 12, and the export passed.

**Windows and macOS: not validated.** This includes the `.cmd` stand-ins, winget and the PATH read again from the registry, Homebrew, and the dialog's button order on macOS.

**Also not run**: the whole shell suite.

## Gaps

- `TODO(issue)`: the Options → AI → External Agents editor, and "Update available" from the entry's `template` field (D-scope, phase 2).
- `TODO(issue)`: templates as a plugin contribution point.
- `TODO(issue)`: elicitation, such as Codex's device code.
- `TODO(issue)`: agents and their sign-in in remote windows.
- `TODO(issue)`: Claude's identity (the account its status reports) in the chat's header.
- `TODO(issue)`: recordings from the real agents. The maintainer should connect each real agent once, and confirm that the download step and `npx -y` share npm's cache, the snap channel and the winget id.
- `TODO(issue)`: the Node.js minimums for Copilot and Codex. They are read from `engines`, and Copilot may need 22.
- `TODO(issue)`: `AcpVersion.Schema` wasn't re-read against the registry's schema, because no copy was available.
- Validation on Windows and macOS (above).
