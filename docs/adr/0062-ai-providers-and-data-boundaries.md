# 0062. AI providers and data boundaries

- **Status:** accepted (M28a)
- **Date:** 2026-10-13
- **Deciders:** maintainer (D7: AI assistance as opt-in, provider-agnostic plugins, with no telemetry)

## Context

D7 decided on AI assistance, on these terms:
- off by default;
- with any provider, local models included;
- nothing sent anywhere until the user picks a provider;
- a visible indicator and a per-workspace switch;
- secrets and ignored files never sent.

Visual Studio's integration (GitHub Copilot, IntelliCode) is proprietary. M28 builds an open one. M28a sets the foundation (providers, data boundaries, chat). Inline completions (M28b), AI actions on existing surfaces (M28c), and agent mode with MCP (M28d) build on it.

## Decision

**Providers are plugins, through a proposed API.**
- `IProposedApi.Ai.RegisterModelProvider(id, label, provider)` offers an `IAiModelProvider`. It lists its models, streams a chat answer (`IAsyncEnumerable<string>` of text pieces), and configures itself (`ConfigureAsync`: asks for a key or a server address).
- Over the protocol, the shell's `ai/chat` request names a stream. The host sends the pieces as numbered `ai/chunk` notifications and answers with their count when the stream ends. The shell handles notifications concurrently, so it orders the pieces by number and waits for the count. Cancelling the request stops the provider.
- **The built-in providers are a thin plugin with no privilege** (`Codalus.Plugins.Ai`, `codalus.ai`, declared with `contributes.aiProviders`). `openai` talks to any OpenAI-compatible endpoint: OpenAI, or a local server such as Ollama, LM Studio, llama.cpp or vLLM, through its base URL. `anthropic` talks to Anthropic's Messages API.
- Keys go into the plugin's `Secrets`, the OS credential store. They are asked for with an input box that hides what's typed, so `InputBoxOptions.Password` is added (plugin API 1.1.0, additive).

**Off until the user turns it on.**
- `ai.enabled` is false by default. A workspace can set it to false for itself; it can't turn AI on for a user who hasn't.
- `ai.provider` and `ai.model` pick the model. Until both name something, no request is made.
- While AI is on, the status bar shows the provider and model; while a request is in flight it says so. Clicking it opens the chat.
- No telemetry: the IDE sends nothing but the requests the user makes, to the provider the user chose.

**The data boundary is in the shell, not the provider.** `AiContext` builds what a request carries. Only what the user attaches is sent: the selection, the active file, open files, the diagnostics of the active file, the last build's errors. Some files are never sent, whatever the user attaches:
- files `.gitignore` excludes (`git check-ignore`) or a `.codalusignore` lists (gitignore syntax);
- secrets: `secrets.json` (user secrets), `.env*`, `*.pfx`, `*.p12`, `*.key`, `*.pem`, `*.snk`, and appsettings files of the `Production` environment.

The chat lists what each message carries, so the user sees it before and after sending.

**The chat window** is a declarative view (D2):
- the conversation, with the attachments of each message;
- answers split into text and code blocks;
- **Apply** on a code block, which replaces the selection it was asked about, or the active file, through the same preview as a refactoring (`PreviewAsync`, `ApplyWorkspaceEditAsync`);
- Stop, New Chat, and the context switches.

## Consequences

- The API is proposed, so it can change while M28b–d prove it; third-party providers need `--enable-proposed-api` until it's promoted.
- Answers show as plain text and code blocks, not rendered Markdown, within the declarative view's elements. Richer rendering waits for webviews or a Markdown element.
- `git check-ignore` runs once per attached file. Folders outside a git repository rely on `.codalusignore` alone.
- Tests never reach a real provider: mock OpenAI-compatible and Anthropic servers stream their answers (server-sent events).
