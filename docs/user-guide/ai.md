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

## The chat

Type a question and press Enter. The answer streams in, and **Stop** ends it. **New Chat** starts over; the conversation so far goes with each message, so a new chat sends less.

Code in an answer has **Apply…**. It replaces the selection you asked about, or the whole file when you sent the file, and shows the change first, as a refactoring does. Nothing changes until you choose Apply in that preview.
