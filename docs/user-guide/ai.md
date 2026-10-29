# AI assistance

Codalus IDE can send questions about your code to a language model you choose, suggest code as you type, help where it already shows you something (a commit, an exception, a failing test), and work in steps in agent mode, always asking before it changes a file or runs anything: a hosted one with your own key, or one running on your machine. It's off until you turn it on, and nothing is sent anywhere until you also choose a provider. There's no telemetry: nothing is counted or sent to the Codalus project.

## Turning it on

1. **Tools > AI > Turn On AI Assistance**, or **Turn On AI Assistance** in the AI Chat window (**View > AI Chat**, `Ctrl+Alt+I`).
2. Choose a provider:
   - **OpenAI-compatible**: OpenAI, or any server that speaks OpenAI's chat completions API, such as LM Studio (`http://localhost:1234/v1`), llama.cpp's server, vLLM or a company gateway. Set the address in **Tools > Options > AI: OpenAI-compatible** (`ai.openai.endpoint`). A key is optional for a local server.
   - **Ollama**: the models of Ollama on this machine (`ai.ollama.endpoint`, `http://localhost:11434/v1` by default). Nothing leaves your computer, and no key is needed.
   - **Anthropic**: Claude models with your Anthropic key (`ai.anthropic.endpoint` for a gateway).
   - Plugins can add other providers.
3. Enter your key when asked. It goes to the operating system's credential store (Credential Manager on Windows, the Keychain on macOS, the Secret Service on Linux), never to a file, and is sent only to the provider's address. **Tools > AI > Configure AI Provider…** replaces it, or removes it when you leave the box empty.
4. Choose a model. **Tools > AI > Choose AI Provider…** changes the provider or the model later (`ai.provider`, `ai.model`).

Or connect a coding agent that brings its own provider (Claude, Codex, Gemini CLI, GitHub Copilot and others) from AI Chat's **Connect** buttons: see [Connecting one](#connecting-one). Its dialog can turn AI assistance on for you, once its test passes.

While AI assistance is on, the status bar shows **AI:** and the provider, or **AI: working…** while a request runs. Click it to open AI Chat.

## AI Chat

Type a question in the box at the bottom and press `Enter` or click **Send** (`Shift+Enter` starts a new line). The answer streams in as the model writes it; while it does, **Send** becomes **Stop**, which ends it. You can type your next question meanwhile, but it isn't sent until the answer ends (in agent mode, until the whole turn ends, tool steps included).

**The conversation.** Your questions are on the right, each with what went with it under it: a chip for each part that was sent, **Not sent:** for a part that was left out (its tooltip says why), and how many secrets were redacted. The answers are on the left, beside the assistant's icon, with the model and the time: formatted text (headings, lists, `code` and links) and code blocks. You can select and copy any of it. A long conversation stays fast: only the part on screen is drawn. Before the first question, the window says what to do next: turn AI on, choose a provider, or ask.

**The toolbar.** **New Chat**, the provider and model (click to change them), the **Agent** switch ([agent mode](#agent-mode)), the request log, and **Configure**.

**What goes with your question.** The chips above the box say what the IDE attaches (click one to turn it on or off), and the line under them lists it before you send:
- **Selection**: the text selected in the editor.
- **Current file**: the active editor's whole file.
- **Open files**: the other open editors.
- **Errors**: the Error List's errors and warnings in the open files (up to 50).
- **Build output**: the end of the last build's output.
- **Solution**: the solution's projects, their target frameworks and output types.

**Code in answers.** A code block is drawn in your editor's colors and font, and its header names its language (and the file, when the block names one) and has three buttons, ready once the answer is complete:
- **Copy** copies it.
- **Insert at Cursor** inserts it at the caret of the active editor.
- **Apply…** shows the change in a preview first, then replaces the selection you asked about (indented like it), or the whole file when the block names that file (```` ```csharp path=src/App/Program.cs ````) or starts like it. Otherwise it inserts at the caret. Applying is one step in the editor's undo.

**New Chat** starts over. Conversations aren't saved: when you close the IDE, they're gone, and nothing a model wrote is on disk unless you applied or copied it.

In the editor's context menu, **AI > Explain the Selection** asks right away, and **AI > Ask AI About the Selection** opens the chat with the selection attached.

## Inline suggestions

With **Inline Suggestions** on (`ai.completions.enabled`, in **Tools > Options > AI**), the IDE suggests code as you type. When typing pauses at the end of a line, the suggestion shows in gray italics at the caret, every line of it.
- `Tab` accepts it, as one step in undo.
- `Ctrl+Right` (`Alt+Right` on macOS) accepts its next word.
- `Esc` dismisses it, and so does moving the caret. Typing what it shows keeps the rest.

It sends the text around the caret (up to 6,000 characters before it and 2,000 after) every time you pause, so it has its own switch, off by default. Files that are never sent (below) get no suggestions. **Inline Suggestions Model** (`ai.completions.model`) picks a faster model for it than the chat's, and **Inline Suggestions Delay** (`ai.completions.delayMilliseconds`, 400) how long you pause first. Suggestions never wait on typing: a new keystroke cancels the request.

## Next edit suggestions

With **Next Edit Suggestions** on (`ai.nextEdit.enabled`), the IDE predicts your next edit after you make one: rename a parameter, and it suggests the same rename where the parameter is used. The predicted lines show highlighted, with the new text at their end (`⇥ Print(total);`). `Tab` applies it, and the applied edit can lead to the next suggestion; `Esc` or any other edit dismisses it. It sends your last few edits and the 40 lines above and below the caret, so it has its own switch, off by default.

## AI actions

Where AI can help with something the IDE already shows, there's an action for it. Each one runs only when you choose it, and shows only while AI assistance is on.
- **Git Changes: ✦** next to the message box writes the commit message for the staged changes, in the style of the repository's recent subjects. Staged files that are never sent are named, not shown.
- **Create Pull Request: Write with AI** writes the title and description from the branch's commits and diff.
- **The exception helper: Ask AI.** When the debugger stops on an exception, Ask AI asks AI Chat why it was thrown, with the exception, its stack, the call stack and the code around the current statement.
- **Test Explorer: Ask AI Why It Fails** (on a failed test's context menu, or **Tools > AI**) asks AI Chat with the test's message, expected and actual values, stack, output and test file.
- **The debugger's values: Ask AI About the Value** (the menu of a value in Locals, Autos, Watch, QuickWatch and data tips) asks AI Chat whether the value is what the code expects there, with the value, its type and expression, the code around the current statement and the call stack.
- **The editor: AI > Fix with AI** fixes the error or warning at the caret: the lines around it go to the model, and the fix shows in a preview before it is applied.
- **Quick actions (`Ctrl+.`)**: after the language's fixes and refactorings come **Fix with AI** and **Explain with AI** for the first error or warning on the line, and **Generate Tests with AI**, marked *AI*.
- **The Error List**: a row's menu has **Fix with AI** (it opens the file at the error and fixes it as above) and **Explain with AI** (AI Chat explains it, with the lines around it), beside **Go To** and **Copy**.
- **The editor: AI > Generate Tests with AI** asks AI Chat for tests of the selection (or the current file) with the solution's test framework (xUnit, NUnit, MSTest or TUnit); Apply on the answer's code block creates the test file.
- **A CPU profiling report: Summarize with AI** asks AI Chat where the time goes, with the report's summary and its top 40 functions.

## Agent mode

Turn on the **Agent** switch in AI Chat's toolbar to let the model work in steps: it reads and searches the workspace, looks at the Error List and the build output, builds, proposes edits and asks to run commands. Each step shows in the answer as it happens, on a line of its own: an icon for how it stands (a check when it's done, a clock while it runs, an error when it failed or you refused it, a square when you stopped it) and one for what it does, the tool (`read_file`), the file it read or changed (click it to open the file), what it searched for or ran, and how it ended. Answers given in agent mode carry an **Agent** chip.

What it can't do without you:
- **Every change shows in the preview first.** **Apply** changes the file in the editor (unsaved, in undo), **Cancel** rejects it, and the model is told it was rejected.
- **Every command asks first**, with the exact command line and its folder, and **Reject** is the default: pressing Enter refuses. An allowed command runs for at most 2 minutes (`ai.agent.commandTimeoutSeconds`), and its output (redacted) goes back to the model.
- **Only inside the workspace.** Paths that leave it, links included, are refused, and so are files that are never sent (below).
- **Only in trusted folders**, and only with a model that takes tools. A model that doesn't, which is common among small local ones, is named, so you can choose another.
- **At most 25 steps** per question (`ai.agent.maxSteps`), and **Stop** ends it at once.

The model is told that what it reads (files, command output, tool results) is data, not instructions. The approvals above hold whatever a file tells it. Each tool call is a line of the request log: the tool and its target, never the content.

## MCP servers

Agent mode can use the tools of MCP (Model Context Protocol) servers you configure in your user settings (`ai.mcp.servers`; a workspace's settings can't add one):

```json
"ai.mcp.servers": {
  "github": { "command": "npx", "args": ["-y", "@modelcontextprotocol/server-github"], "env": { "GITHUB_PERSONAL_ACCESS_TOKEN": "…" } }
}
```

Servers start the first time agent mode needs their tools and stop when the IDE closes. Their tools are offered to the model as `<server>__<tool>`. Each call asks first, showing the server, the tool and the arguments, and the results are redacted. Servers are started over stdio. Servers over HTTP, and MCP's resources and prompts, aren't supported yet.

## Codalus as an MCP server

AI agents that run outside the IDE (in a terminal, or another editor) can use what the IDE knows. Turn on **MCP Server** (`ai.mcpServer.enabled`) in **Tools > Options > AI**, then configure the agent with this stdio server:

```json
{ "command": "codalus", "args": ["mcp"] }
```

(add `"--user-data-dir", "<dir>"` when you run the IDE with one). The tools reach the running IDE of the same user:
- `get_solution`: the solution and its projects, frameworks and output types;
- `get_open_files`: the open files, the active one and its selection;
- `get_diagnostics`: the Error List;
- `get_build_output`;
- `build`, which asks you in the IDE first.

Excluded files are left out, and text is redacted. Each call is logged in the AI Output channel. With the setting off (the default), or the IDE closed, every call answers with what to do.

## External agents (ACP)

AI Chat can also run a coding agent that is a program of its own, such as pi, Claude, Codex, Gemini CLI, GitHub Copilot, OpenCode or Goose, next to the built-in assistant. Codalus talks to it over the [Agent Client Protocol](https://agentclientprotocol.com) (ACP, version 1): it starts the agent, shows its answers, plans and steps in the chat, and serves what the agent asks of the IDE (reading and writing files, running commands, asking your permission) with the same checks as agent mode.

> **External agent: uses its own provider and data policy. Codalus checks what it asks the IDE for.** An external agent calls its own model provider with its own account. What it reads (through the IDE or by itself) goes there, under that provider's terms, not through Codalus's AI boundary. The built-in assistant stays the default and keeps the full boundary. AI Chat shows this label whenever an external agent is chosen.

### Connecting one

**Where.** While AI assistance is off, or on without a provider, AI Chat offers a row under its next step: **Or connect a coding agent that brings its own provider**, with **Claude**, **Codex**, **Gemini CLI** and **GitHub Copilot** as buttons and **More agents…** for every agent Codalus knows. While AI is on, the agent picker (after the provider in the toolbar) lists Built-in and your agents, then **Connect to** each one-click agent you haven't connected, **More agents…** and **Manage Agents…**. The commands are in the Command Palette and in **Tools > AI** too: **AI: Connect to Claude…**, **AI: Connect to an Agent…** and the others.

**The Connect dialog** checks, signs in and tests before anything is saved:

1. **Requirements.** Each program the agent needs (Node.js and npx, or the agent's own CLI) is looked for on your PATH, with its version and the version the agent needs. **Check Again** looks again.
2. **Installing what's missing**, with your own package manager, each option with its exact command: **winget** on Windows (it runs in the dialog, and Codalus reads the new PATH when it ends), **Homebrew** on macOS, **snap** on Ubuntu (in the Terminal, where it asks for your password), **apt** or **dnf** only when the Node.js they would install is new enough (with npm, which is a package of its own on Debian and Ubuntu), and nodejs.org's page everywhere. A Terminal install keeps the dialog open: it checks again when the command ends. Where none of your package managers can install a new enough Node.js, **Codalus's Node.js** (the checksum-verified 24.22.2 it already uses for npm scripts) is offered as an opt-in; the agent's entry then says `"node": "builtIn"`, and the agent runs with that Node.js first on its PATH. An agent's own CLI installs from npm, pinned (`npm install -g @earendil-works/pi-coding-agent@1.4.0` for pi, `npm install -g opencode-ai@1.19.4` for OpenCode; with `sudo` in the Terminal when npm's global folder isn't yours), or goose's from Homebrew.
3. **Sign in**, in one of the ways the agent offers (below).
4. **What runs**: the exact command lines, the sign-in's command, and a variable's name, never its value.
5. **Turn on AI assistance**: a check box, shown only while AI is off. Its text says what else comes back on (a provider you chose earlier, inline suggestions). **Connect** stays disabled until it's checked. Nothing is written yet.
6. **Connect** runs the test: the requirements, the download (npm's own progress), the agent's start, the sign-in, and a check that it's signed in. The test runs in an empty folder of the IDE's (`<state>/external-agents/connect-test`), with no workspace, no MCP servers and no prompt, and Codalus refuses anything the agent asks the IDE for meanwhile. Its session may stay in the agent's own history. Cancel stops it and writes nothing.

**Only when the test passes** does Codalus write the agent's entry (`ai.agents.<agent>`) to your user settings, and `ai.enabled` when the check box turned AI on; **Start a Chat** picks the agent. When the agent didn't say whether you're signed in, **Save Anyway** writes it (your first question then asks you to sign in if you aren't), **Sign In…** goes back, and **Cancel** writes nothing. A failed test says why in plain words, with its fixes as buttons (**Sign In with Claude subscription…**, **Use an API Key…**, npm's proxy settings) and **Try Again**; nothing is written. Connect keeps the comments and the other entries in your `ai.agents`.

| Agent | What runs | Version | Sign-in | How the test checks it |
|---|---|---|---|---|
| Claude | `npx -y @agentclientprotocol/claude-agent-acp@0.94.0` | 0.94.0 | Claude subscription or Anthropic Console account (Claude Code's own login, in the Terminal), an API key (`ANTHROPIC_API_KEY`), or signed in already | Claude reports who is signed in |
| Codex | `npx -y @agentclientprotocol/codex-acp@2.2.2` | 2.2.2 | ChatGPT (your browser), an API key (kept by Codex), or signed in already | the session is refused while signed out |
| Gemini CLI | `npx -y @google/gemini-cli@0.65.0 --acp` | 0.65.0 | Google account (your browser), a Gemini API key (`GEMINI_API_KEY`), or signed in already | the session is refused while signed out |
| GitHub Copilot | `npx -y @github/copilot@1.0.103 --acp` | 1.0.103 | a GitHub token (`GH_TOKEN`), or signed in already | not confirmed |
| pi | `npx -y pi-acp@0.0.34` (and pi's own CLI, 0.81 or later) | 0.0.34 | pi's setup in the Terminal, or signed in already | not confirmed |
| OpenCode | `opencode acp` (install OpenCode first) | tested with 1.19.4 | `opencode auth login` in the Terminal, or signed in already | not confirmed |
| goose | `goose acp` (install goose first) | tested with 1.55.0 | `goose configure` in the Terminal, or signed in already | not confirmed |

The versions are the [ACP registry](https://agentclientprotocol.com/get-started/registry)'s, checked on 2026-10-22 and committed with Codalus: it doesn't fetch the list. The npx packages are pinned to those exact versions; `npx` downloads the package from npm the first time and runs it as you, with your account's access to your files, not only the folder's. An agent's process starts in an empty folder of the IDE's, not in your workspace, so a repository's `.npmrc` or `node_modules` can't change what `npx` runs; the workspace reaches the agent as its session's folder. On Windows, `npx` is `npx.cmd`: Codalus runs npm's own script with Node.js directly, so nothing in its arguments is read by `cmd.exe`.

Where Codalus can set an agent up without opening a page:

| Agent | Windows | macOS | Linux |
|---|---|---|---|
| Claude | winget | Homebrew, or Codalus's Node.js | snap (Ubuntu), apt or dnf with Node.js 22, or Codalus's Node.js |
| Codex | winget | Homebrew, or Codalus's Node.js | snap (Ubuntu), apt or dnf, or Codalus's Node.js |
| Gemini CLI | winget | Homebrew, or Codalus's Node.js | snap (Ubuntu), apt or dnf with Node.js 20, or Codalus's Node.js |
| GitHub Copilot | winget | Homebrew, or Codalus's Node.js | snap (Ubuntu), apt or dnf, or Codalus's Node.js |
| pi | winget, then npm | Homebrew or Codalus's Node.js, then npm | Node.js as for Claude, then npm |
| OpenCode | winget, then npm | Homebrew or Codalus's Node.js, then npm | Node.js as for Codex, then npm |
| goose | manual (its install page) | Homebrew | manual (its install page) |

Without Codalus's Node.js (you can always pick nodejs.org's page instead), Claude needs a page on Debian or Ubuntu without snap and on macOS without Homebrew: their own Node.js is older than the 22 it needs.

**Ways to sign in.**
- **In the Terminal**: the agent's own login (Claude's subscription and Console account, pi's setup, OpenCode's and goose's own commands) runs in a Terminal tab of its own, with its exact command shown first; the dialog continues when it ends, and the agent starts again to read the new sign-in. For Claude, Codalus doesn't take the command's exit code as the answer: Claude reports who is signed in, and the test waits for that report.
- **Claude subscription** uses Claude Code's own login in the official adapter, so the sign-in stays with Claude Code: Codalus never asks for, reads or keeps Claude's tokens. With it, Claude starts without `ANTHROPIC_API_KEY`, `ANTHROPIC_AUTH_TOKEN` and `ANTHROPIC_BASE_URL` from your environment, which would outrank the login (its entry lists them in `unsetEnv`). In an SSH session Claude offers its own **Log in with Claude** in the Terminal; if that doesn't work, run `claude` in a terminal, type `/login`, and choose **I'm signed in already**.
- **In your browser**: Codex's ChatGPT and Gemini CLI's Google account; the agent opens it itself.
- **An API key**: typed in the dialog's password box. Codalus keeps it in the OS credential store (Windows Credential Manager, macOS Keychain, Secret Service), never in a file, and the agent gets it as its variable (`ANTHROPIC_API_KEY`, `GEMINI_API_KEY`, `GH_TOKEN`) each time it starts. Commands the agent runs by itself inherit that variable; the commands Codalus runs for it don't. When the credential store can't be used, the dialog warns that the key lasts until Codalus closes. **Codex** is different: its key is sent once, in its sign-in request, and Codex keeps it in its own sign-in store (`~/.codex`); Disconnect can sign it out.
- **Gemini CLI** warns that signing in a different way resets its saved Google sign-in and changes its own settings file (`~/.gemini/settings.json`).
- **I'm signed in already**: nothing to do; the test checks it.

**Manage Agents…** lists your agents (the template, where the key is, which one is chatting; an entry you wrote by hand says so); choosing one offers **Check Connection** (the test again, in the empty folder, without a prompt), **Change API Key…**, **Sign In Again…**, **Edit in settings.json** and **Disconnect…**. **Disconnect** says what it removes (the entry, the key, the running chat) and what stays (AI assistance on, npm's cache), with an unchecked **Also sign out of** *agent* **on this computer** for agents that can sign out (Claude, Codex), which runs the agent's own sign-out.

Connecting isn't possible from a remote window (WSL, SSH, containers): the agent would run on this computer while the folder and its terminal are on the remote one. AI Chat's buttons are disabled there, with the reason.

**By hand.** Add an entry to your user settings (**Tools > Options > AI: External Agents**, or `settings.json`); a workspace's settings can't add one. Each entry is the command that starts the agent in ACP mode, with its arguments and environment:

```json
"ai.agents": {
  "claude": { "command": "npx", "args": ["-y", "@agentclientprotocol/claude-agent-acp@0.94.0"], "secretEnv": ["ANTHROPIC_API_KEY"], "template": "claude@0.94.0" },
  "pi": { "command": "npx", "args": ["-y", "pi-acp@0.0.34"] },
  "my-agent": { "command": "/opt/my-agent/bin/my-agent", "args": ["--acp"] }
}
```

Pin the version you've checked. `env` passes variables, but a value there is in a file: put a key's name in `secretEnv` instead (Connect stores the value in the credential store; a name that isn't a variable's, that changes what runs such as `PATH`, or that is also in `env` skips the entry, and AI Chat says why). `unsetEnv` lists variables the agent starts without. `template` records the template Connect used.

**Then**, open a trusted solution or folder, and choose the agent in the picker (**Built-in** is the built-in assistant). Choosing another agent starts a new chat.

While an external agent is chosen, the label shows above the question box, and the empty chat reads "Ask *agent* about your solution. It answers with its own provider; Codalus asks you before it changes a file or runs a command." If AI assistance is off, no folder is open, or the folder isn't trusted, the chat says why under the label, and the agent doesn't start.

### What you see

- **The answer** is under the agent's name, with an **External agent** chip, and streams in as Markdown; the agent's **thinking** is folded under **Thinking**; its **plan** is a checklist above the steps.
- **Steps** are the agent's tool calls, shown like the built-in agent's: an icon for how it stands and one for its kind, its title, and the files it works on as links that open the file at the line (only files in the workspace open). A step's details (the changes it reports, read-only, and a command's output, open while it runs) are under its chevron.
- **Model, mode and effort**: when the agent offers choices for its session over ACP (its model, its modes such as Ask and Code, its effort or thinking level, or others of its own), each one has a picker in the toolbar after the agent picker, showing its current value. They show as soon as AI Chat shows the agent in a trusted folder: Codalus starts the agent then, so you can pick before you ask anything (it doesn't sign in on its own: an agent that needs you to sign in first shows them after your first question, which asks). A pick goes to the agent at once and is kept in its entry (`ai.agents.<agent>.choices`), so its next sessions start with it; a value the agent refuses, or no longer offers, is left as the agent has it. Agents that offer none show no pickers.
- **Across a restart**: AI Chat opens with the agent you chose last (`ai.chat.agent`), not the built-in assistant.
- **Commands**: typing `/` offers the agent's slash commands; `Tab` or a click puts one in the box.
- **Stop** asks the agent to stop (`session/cancel`), answers its open questions as cancelled, closes an open approval and stops its commands. **New Chat** stops the agent's process; each chat runs one process, which also stops when the folder closes and when the IDE exits.
- If the agent crashes, the turn shows its last lines of output, and your next question starts it again.
- If the agent needs you to sign in (when the chat starts, or when a question is refused, as Claude does), Codalus shows the ways it offers and runs the one you choose; a sign-in in the Terminal shows its exact command first, and the agent starts again afterwards. The question is then sent again, once; when the agent had to start a new session, the turn says the earlier conversation is gone. The agent manages its own sign-in: Codalus doesn't see or keep its credentials (an API key you gave Connect is the exception, kept in the credential store).

### The safety model, and its limits

What the agent asks of the IDE goes through the IDE's checks:
- **Reading** (`fs/read_text_file`): the editor's text when the file is open (unsaved changes included), else the disk's. Only inside the folder: paths that leave it, links included, are refused, and so are files that are never sent (below). What it gets is redacted.
- **Writing** (`fs/write_text_file`): every change shows in the preview first. **Apply** puts it in the editor (unsaved, one step in undo; the file opens if it isn't open), and **Reject** tells the agent you rejected it. The same paths are refused.
- **Permissions**: when the agent asks before acting, the approval dialog shows the action, its kind, its input and its changes, with **Allow Once**, **Reject** and **Always Reject**. **Reject** is the default (Enter and Esc reject). There's no "always allow".
- **Commands**: each one asks first with the exact command line, its folder and every environment variable the agent sets for it (secrets redacted), as agent mode's do, and runs for at most `ai.agent.commandTimeoutSeconds`. Variables that change what a command runs (`PATH`, `LD_*` and `DYLD_*`, `NODE_OPTIONS`, `GIT_*`, `NPM_CONFIG_*`, `DOTNET_*` and similar) are refused before you're asked. Its output is redacted before the agent reads it, and Stop kills it.
- **The IDE's tools**: every agent gets `codalus mcp` (the solution, the open files, the Error List, the build output, and a build that asks first) through a pipe of its own that lives as long as the chat, for your user only, even when **MCP Server** is off.
- **Only while it may run**: if AI assistance is turned off (by you, or for the workspace) or the folder stops being trusted, a running agent stops, and any request it sends meanwhile is refused. The same holds for its `codalus mcp` pipe, which also only shows files under the workspace.
- **The log**: each prompt, request and tool call is a line of the *AI* Output channel (the agent, the method, the path or command, never the content).

What Codalus can't check:
- The agent can read and write files by itself, without asking the IDE. **Only changes it makes through `fs/write_text_file` are reviewed before they land**, and whether it uses it depends on the agent. A change it made itself shows read-only in its step, and an open editor reloads the file (asking first if you have unsaved changes).
- Whatever the agent has, it can send to its provider. Keep files out of reach with `.codalusignore` and `ai.exclude` for what goes through the IDE, and with the agent's own settings for what it reads by itself.
- Its commands run with your permissions once you allow them, like agent mode's.

Not supported yet: MCP servers over HTTP for agents, the agent's forms (elicitation), resuming an earlier session, and an Options page for External Agents (edit the JSON, or use Manage Agents…). External agents don't run in remote windows (WSL, SSH, containers): the agent and its commands would run on this machine, not where the folder is, so the chat says why and nothing starts.

## What is never sent

AI assistance has one gate that every request goes through:
- **Files your repository ignores**: whatever `.gitignore` (the repository's, nested ones, and `.git/info/exclude`) excludes.
- **Files a `.codalusignore` excludes**: same syntax as `.gitignore`, at the workspace's root or in any folder below. Use it for files that are in the repository but shouldn't reach a model.
- **Files named in the `ai.exclude` setting**: `.gitignore` patterns relative to the workspace, for example `secrets/` or `**/*.local.json`.
- **Files that hold secrets by their name**: `.env` and `.env.*`, `*.pfx`, `*.p12`, `*.key`, `*.pem`, SSH keys (`id_rsa`…), `secrets.json`, `*.snk`, `*.publishsettings`, `.npmrc`, `.netrc`, `.git-credentials`, `*.keystore`, `*.jks`.
- **Secrets inside what is sent**: private key blocks, token shapes (GitHub, GitLab, OpenAI, Anthropic, AWS, Slack, Google, JSON web tokens), and the values of password, secret, token and key settings and connection-string parts are replaced with `[redacted]`. This is pattern matching, so it can miss a secret in an unusual shape: keep such files out with `.codalusignore` or `ai.exclude`.
- **Files of a folder you haven't trusted** (restricted mode): chat works with what you type, the Error List and the build output only.

**Size.** Up to 120,000 characters of context go with a question (`ai.chat.maxContextCharacters`). A longer file is cut, and the chat says so.

**The request log.** **Tools > AI > Show AI Request Log** (or **Request Log** in AI Chat) opens the *AI* Output channel: each request's time, provider, model and size, which context went with it and which didn't, the redactions, and the tokens the provider reported. It lists names, never the content, and it stays on your machine.

## Turning it off

- **Everywhere:** turn off **Enable AI Assistance** (`ai.enabled`) in **Tools > Options > AI**.
- **For one workspace:** **Tools > AI > Turn AI Off or On for This Workspace** writes `"ai.enabled": false` to the workspace's `.codalus/settings.json`. Commit it to keep AI off for everyone who opens the repository. A workspace's settings can turn AI off, never on: a repository you clone can't turn it on for you.

## Settings

| Setting | Default | What it does |
|---|---|---|
| `ai.enabled` | `false` | AI assistance on or off (a workspace can only turn it off) |
| `ai.provider` | empty | The provider: `openai`, `ollama`, `anthropic`, or a plugin's |
| `ai.model` | empty | The model; empty is the provider's first |
| `ai.exclude` | `[]` | `.gitignore` patterns of files never sent |
| `ai.chat.maxContextCharacters` | `120000` | The most context sent with a question |
| `ai.completions.enabled` | `false` | Inline suggestions |
| `ai.completions.model` | empty | Their model; empty is the chat's |
| `ai.completions.delayMilliseconds` | `400` | The pause before a suggestion is asked for |
| `ai.nextEdit.enabled` | `false` | Next edit suggestions |
| `ai.agent.maxSteps` | `25` | The most tool calls agent mode makes for one question |
| `ai.agent.commandTimeoutSeconds` | `120` | How long an allowed command may run |
| `ai.mcp.servers` | `{}` | MCP servers whose tools agent mode may use (user settings only) |
| `ai.mcpServer.enabled` | `false` | Let `codalus mcp` reach the IDE (external agents' own pipes don't need it) |
| `ai.agents` | `{}` | External ACP agents AI Chat can run: `{ "name": { "command", "args", "env", "secretEnv", "unsetEnv", "choices" } }` (user settings only; **AI: Connect to an Agent…** writes it, AI Chat's pickers write `choices`) |
| `ai.chat.agent` | `""` | Who answers in AI Chat: empty for the built-in assistant, or an agent of `ai.agents` (the picker writes it) |
| `ai.openai.endpoint` | `https://api.openai.com/v1` | The OpenAI-compatible server |
| `ai.ollama.endpoint` | `http://localhost:11434/v1` | Ollama's address |
| `ai.anthropic.endpoint` | `https://api.anthropic.com` | Anthropic's API, or a gateway |
| `ai.anthropic.maxOutputTokens` | `16000` | The longest answer from Anthropic |
