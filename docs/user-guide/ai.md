# AI assistance

Codalus IDE can send questions about your code to a language model you choose: a hosted one with your own key, or one running on your machine. It's off until you turn it on, and nothing is sent anywhere until you also choose a provider. There's no telemetry: nothing is counted or sent to the Codalus project.

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
| `ai.openai.endpoint` | `https://api.openai.com/v1` | The OpenAI-compatible server |
| `ai.ollama.endpoint` | `http://localhost:11434/v1` | Ollama's address |
| `ai.anthropic.endpoint` | `https://api.anthropic.com` | Anthropic's API, or a gateway |
| `ai.anthropic.maxOutputTokens` | `16000` | The longest answer from Anthropic |
