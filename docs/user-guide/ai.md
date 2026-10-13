# AI assistance

Codalus IDE can ask an AI model about your code. It's **off until you turn it on**, it uses **the provider you choose** (a cloud service with your own key, or a model running on your own machine), and it sends **only what you attach to a message**. Codalus IDE has no telemetry, and makes no AI request of its own.

## Turning it on

1. Open **View → AI Chat** and choose **Turn On AI** (the setting `ai.enabled`).
2. Pick a provider:
   - **OpenAI-compatible**: OpenAI, or a server on your own machine (Ollama, LM Studio, llama.cpp, vLLM). Set its address in **AI Providers → `ai.openai.baseUrl`**, for example `http://localhost:11434/v1` for Ollama or `http://localhost:1234/v1` for LM Studio. A local server needs no key.
   - **Anthropic**: Claude models, with your API key from console.anthropic.com.
   - Plugins can add more providers.
3. Choose **Configure…** to enter the key. It goes to your system's credential store (Windows Credential Manager, the macOS Keychain, or the Secret Service on Linux), never to a file.
4. Pick a model; **Refresh Models** lists the provider's.

While AI is on, the status bar shows the provider and model, and says when a request is being sent. A workspace can turn AI off for itself: set `ai.enabled` to `false` in its `.codalus/settings.json`. It can't turn AI on for someone who hasn't.

## What is sent

A message carries your question and what you tick under it:
- **Selection**: the selected text, or with nothing selected, **Active file**: the whole file;
- **Open files**: the other open files (up to ten);
- **Problems**: the active file's errors and warnings;
- **Build errors**: the last build's errors.

Each message lists what it carried ("Sent with: …"). Some files are **never sent**, whatever you tick:
- files your `.gitignore` excludes, and files listed in a `.codalusignore` at the workspace's root (same syntax as `.gitignore`);
- secrets by name: user secrets (`secrets.json`), `.env` files, keys and certificates (`.pfx`, `.p12`, `.key`, `.pem`, `.snk`), and `appsettings.Production.json`.

When something is left out, the chat says why ("Not sent: …").

## Inline suggestions

With **AI Inline Suggestions** on (`ai.inlineSuggestions`, off by default, and only while AI is on), the model proposes code at the caret when you pause typing: it shows in gray italics after the caret, with the number of further lines when it has more than one. **Tab** inserts it; typing on, moving the caret or **Esc** dismisses it. Typing never waits for a suggestion.

Each suggestion sends the file around the caret (about 4,000 characters before it and 1,500 after) to your provider, under the same rules as the chat: files that are secrets or ignored are never sent, so they get no suggestions. A workspace can turn suggestions off for itself, as it can AI.

## AI actions

Where you are already working, a command asks for you, with the context that place has. Each runs only when you choose it, with the same rules about what is sent:
- **Ask AI About This Problem** (the editor's context menu): the error or warning at the caret, with the 15 lines around it.
- **Write Tests with AI** (the editor's context menu, with code selected): tests for the selection, in the solution's test framework when it has test projects.
- **Explain the Exception with AI** (Debug menu, stopped on an exception): its type, message, stack and inner exceptions, and the code where the program stopped.
- **Explain the Test Failure with AI** (Test menu): the failed test selected in the Test Explorer, with its message, expected and actual values, stack and source.
- **Draft Commit Message with AI** (Git menu): a message for the staged changes, written into Git Changes' message box for you to edit. The diffs of secret or ignored files aren't sent; their names are.

The answers to the first four come in the AI Chat, where you can ask more.

## The chat

Type a question and press Enter. The answer streams in, and **Stop** ends it. **New Chat** starts over; the conversation so far goes with each message, so a new chat sends less.

Code in an answer has **Apply…**. It replaces the selection you asked about, or the whole file when you sent the file, and shows the change first, as a refactoring does. Nothing changes until you choose Apply in that preview.

## Agent mode

Check **Agent mode** in the AI Chat to have the AI do multi-step work: it reads and searches the workspace's files, looks at problems, changes files, runs commands and builds, until it can answer. Each step shows in the conversation as it happens (▸ the tool, ↳ its result), and **Stop** ends it. It takes at most 25 steps per question.

You stay in charge:
- **Every change is shown first**, in the same preview as a refactoring, and made only when you choose Apply. If you cancel, the AI is told the change was rejected.
- **Every command asks first**, showing the command; **Don't Run** is the default.
- Changing files, commands and builds are offered only in a **trusted** workspace. Elsewhere the agent can only read, search and look at problems.
- The same data boundary applies: the agent can't read secrets or files your `.gitignore` or `.codalusignore` exclude, and never leaves the workspace's folder.

Approving a command runs it with your rights. The approvals are prompts, not a sandbox.

## MCP servers

The agent can also use the tools of your [MCP](https://modelcontextprotocol.io) servers. List them in the setting `ai.mcpServers`, in the same shape as other MCP clients:

```json
"ai.mcpServers": {
  "files": { "command": "npx", "args": ["-y", "@modelcontextprotocol/server-filesystem", "C:/notes"] }
}
```

A server starts when agent mode first needs it, and stops when the window closes. Its tools join the agent's as `server__tool`. **Each call asks first**: Allow Once, Allow for This Session (for that server), or Don't Allow. Servers that run over stdio are supported; HTTP servers aren't yet.

## Codalus IDE as an MCP server

`codalus mcp` is an MCP server for other agents (Claude Code, other editors' assistants). It answers from the Codalus IDE you have running, with the same user-data folder:
- `get_solution`: the open solution, its projects and their target frameworks;
- `get_problems`: the errors and warnings of a file, or of every file;
- `get_test_results`: the Test Explorer's last results, with the failures' messages;
- `build`: builds the solution (a trusted one) and returns its errors.

It only reads what the IDE has, apart from building; it changes nothing. For example, with Claude Code:

```bash
claude mcp add codalus -- codalus mcp
```

Add `--user-data-dir <folder>` after `mcp` when the IDE runs with one.
