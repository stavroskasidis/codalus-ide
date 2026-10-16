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

Type a question and press `Enter` (`Shift+Enter` starts a new line). The answer streams in as the model writes it; **Stop** ends it.

**What goes with your question.** The buttons above the box say what the IDE attaches, and the line under them lists it before you send:
- **Selection**: the text selected in the editor.
- **Current file**: the active editor's whole file.
- **Open files**: the other open editors.
- **Errors**: the Error List's errors and warnings in the open files (up to 50).
- **Build output**: the end of the last build's output.
- **Solution**: the solution's projects, their target frameworks and output types.

Each answer shows what was sent with its question, what was left out and why, and how many secrets were redacted.

**Code in answers.** Under each code block:
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

Turn on **Agent** in AI Chat's toolbar to let the model work in steps: it reads and searches the workspace, looks at the Error List and the build output, builds, proposes edits and asks to run commands. Each step shows under your question as it happens (`⚙ read_file src/App/Program.cs → …`).

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
| `ai.mcpServer.enabled` | `false` | Let `codalus mcp` reach the IDE |
| `ai.openai.endpoint` | `https://api.openai.com/v1` | The OpenAI-compatible server |
| `ai.ollama.endpoint` | `http://localhost:11434/v1` | Ollama's address |
| `ai.anthropic.endpoint` | `https://api.anthropic.com` | Anthropic's API, or a gateway |
| `ai.anthropic.maxOutputTokens` | `16000` | The longest answer from Anthropic |
