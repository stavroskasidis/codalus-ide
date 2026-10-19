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

While AI assistance is on, the status bar shows **AI:** and the provider, or **AI: working…** while a request runs. Click it to open AI Chat.

## AI Chat

Type a question in the box at the bottom and press `Enter` or click **Send** (`Shift+Enter` starts a new line). The answer streams in as the model writes it; while it does, **Send** becomes **Stop**, which ends it.

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
- **The editor: AI > Fix with AI** fixes the error or warning at the caret: the lines around it go to the model, and the fix shows in a preview before it is applied.
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

### Setting one up

1. Install the agent yourself, and sign in the way it says (Codalus doesn't install or ship any agent).
2. Add it to your user settings (**Tools > Options > AI: External Agents**, or `settings.json`). A workspace's settings can't add one. Each entry is the command that starts the agent in ACP mode, with its arguments and environment:

   ```json
   "ai.agents": {
     "pi": { "command": "npx", "args": ["-y", "pi-acp@0.0.34"] },
     "claude": { "command": "npx", "args": ["-y", "@agentclientprotocol/claude-agent-acp@0.93.0"] },
     "codex": { "command": "npx", "args": ["-y", "@agentclientprotocol/codex-acp@2.2.2"] },
     "gemini": { "command": "npx", "args": ["-y", "@google/gemini-cli@0.64.0", "--acp"] }
   }
   ```

   These are examples pinned to the versions in the [ACP registry](https://agentclientprotocol.com/get-started/registry) on 2026-10-18; pin the version you've checked. `env` passes variables (`"env": { "ANTHROPIC_API_KEY": "…" }`), but a key there is in a file: prefer the agent's own sign-in, which keeps it in the agent's store. On Windows, `npx` is `npx.cmd`, which Codalus runs through `cmd.exe` for you.
3. Turn on AI assistance and open a trusted solution or folder.
4. In AI Chat, choose the agent in the agent picker, after the provider in the toolbar (**Built-in** is the built-in assistant). Choosing another agent starts a new chat.

While an external agent is chosen, the label shows above the question box. If AI assistance is off, no folder is open, or the folder isn't trusted, the chat says why under the label, and the agent doesn't start.

### What you see

- **The answer** is under the agent's name, with an **External agent** chip, and streams in as Markdown; the agent's **thinking** is folded under **Thinking**; its **plan** is a checklist above the steps.
- **Steps** are the agent's tool calls, shown like the built-in agent's: an icon for how it stands and one for its kind, its title, and the files it works on as links that open the file at the line (only files in the workspace open). A step's details (the changes it reports, read-only, and a command's output, open while it runs) are under its chevron.
- **Modes**: when the agent has modes (for example Ask and Code), a second picker in the toolbar switches them.
- **Commands**: typing `/` offers the agent's slash commands; `Tab` or a click puts one in the box.
- **Stop** asks the agent to stop (`session/cancel`), answers its open questions as cancelled, closes an open approval and stops its commands. **New Chat** stops the agent's process; each chat runs one process, which also stops when the folder closes and when the IDE exits.
- If the agent crashes, the turn shows its last lines of output, and your next question starts it again.
- If the agent needs you to sign in, Codalus shows the ways it offers and runs the one you choose. The agent manages its own sign-in: Codalus doesn't see or keep its credentials.

### The safety model, and its limits

What the agent asks of the IDE goes through the IDE's checks:
- **Reading** (`fs/read_text_file`): the editor's text when the file is open (unsaved changes included), else the disk's. Only inside the folder: paths that leave it, links included, are refused, and so are files that are never sent (below). What it gets is redacted.
- **Writing** (`fs/write_text_file`): every change shows in the preview first. **Apply** puts it in the editor (unsaved, one step in undo; the file opens if it isn't open), and **Reject** tells the agent you rejected it. The same paths are refused.
- **Permissions**: when the agent asks before acting, the approval dialog shows the action, its kind, its input and its changes, with **Allow Once**, **Reject** and **Always Reject**. **Reject** is the default (Enter and Esc reject). There's no "always allow".
- **Commands**: each one asks first with the exact command line and its folder, as agent mode's do, and runs for at most `ai.agent.commandTimeoutSeconds`. Its output is redacted before the agent reads it, and Stop kills it.
- **The IDE's tools**: every agent gets `codalus mcp` (the solution, the open files, the Error List, the build output, and a build that asks first) through a pipe of its own that lives as long as the chat, for your user only, even when **MCP Server** is off.
- **The log**: each prompt, request and tool call is a line of the *AI* Output channel (the agent, the method, the path or command, never the content).

What Codalus can't check:
- The agent can read and write files by itself, without asking the IDE. **Only changes it makes through `fs/write_text_file` are reviewed before they land**, and whether it uses it depends on the agent. A change it made itself shows read-only in its step, and an open editor reloads the file (asking first if you have unsaved changes).
- Whatever the agent has, it can send to its provider. Keep files out of reach with `.codalusignore` and `ai.exclude` for what goes through the IDE, and with the agent's own settings for what it reads by itself.
- Its commands run with your permissions once you allow them, like agent mode's.

Not supported yet: MCP servers over HTTP for agents, the agent's forms (elicitation), signing in from a terminal, resuming an earlier session, and external agents in remote windows.

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
| `ai.agents` | `{}` | External ACP agents AI Chat can run: `{ "name": { "command", "args", "env" } }` (user settings only) |
| `ai.openai.endpoint` | `https://api.openai.com/v1` | The OpenAI-compatible server |
| `ai.ollama.endpoint` | `http://localhost:11434/v1` | Ollama's address |
| `ai.anthropic.endpoint` | `https://api.anthropic.com` | Anthropic's API, or a gateway |
| `ai.anthropic.maxOutputTokens` | `16000` | The longest answer from Anthropic |
