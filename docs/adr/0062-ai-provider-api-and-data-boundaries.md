# 0062. AI assistance: the provider API and data boundaries

- **Status:** accepted (M28a plan, 2026-10-14; D7 and the maintainer's standing go-ahead to complete every milestone)
- **Date:** 2026-10-14
- **Deciders:** maintainer (D7: opt-in, with any provider, no telemetry)

## Context

M28 asks for AI assistance with parity to what the classic IDE does with GitHub Copilot: chat, inline suggestions, AI actions on existing surfaces, agent mode and MCP. Copilot's integration and IntelliCode are proprietary (the roadmap's traps), so this is our own open design. D7 fixed the principles:
- off by default, with no telemetry;
- the user picks a provider, and nothing is sent anywhere until then;
- a visible indicator and a per-workspace switch;
- secrets and files that `.gitignore` or a `.codalusignore` excludes are never sent;
- providers are plugins, none of them privileged; local models work; keys go to the OS credential store.

The constraints are the usual ones: plugin APIs start as proposed APIs (ADR 0003, 0058), the shell stays Native AOT clean and never blocks the UI thread, and tests never reach a real service (mock servers only).

## Options

| Question | Options | Verdict |
|---|---|---|
| Where providers run | In the shell; in a new host; as plugins in the plugin host | **Plugins**, as D7 says. A provider is a thin HTTP client; the plugin host already isolates and supervises plugins, and its secrets API keeps keys out of the shell's memory except when stored. |
| What the API models | Vendor SDK types; OpenAI's chat shape; our own small chat shape | **Our own**: messages with roles, text, tool calls and tool results, streamed back as parts. It is the common subset of OpenAI's and Anthropic's APIs and of local servers (Ollama, LM Studio, llama.cpp, vLLM), so each provider maps it in a few lines. Tools are in it from the start, so agent mode (M28c) needs no API change. |
| How a reply streams across processes | One request per chunk (polling); a request with notifications tagged by request id | **A request with notifications.** The shell sends `languageModels/chat` with a request id; the host posts `languageModels/chatPart` for each part and then answers the request. One writer per connection keeps them in order. Cancelling the request cancels the provider's token (`$/cancelRequest`). |
| Who builds the prompt | Each feature; one workbench service | **One service, `AiService`**, is the only code that sends workspace content to a provider. Every feature (chat now; completions, actions and the agent later) asks it, so the data boundary is enforced in one place and can be tested in one place. |
| Which built-in providers | One per vendor; one OpenAI-compatible plugin; both | **Two plugins.** `codalus.openai` speaks OpenAI's chat completions API, which OpenAI and every common local server serve, so it is two providers: *OpenAI-compatible* (an endpoint and an optional key) and *Ollama* (a local endpoint, no key). `codalus.anthropic` speaks Anthropic's Messages API. Other vendors are third-party plugins on the same API. |

## Decision

### The provider API (proposed, `Codalus.Plugins.Proposed`)

- **Manifest:** `contributes.languageModelProviders: [{ "id": "openai", "label": "OpenAI-compatible", "description": "…", "local": false }]`. The activation event `onLanguageModelProvider:<id>` is implicit, so a provider's plugin loads only when the user picks it.
- **`context.Proposed().LanguageModels.RegisterProvider(LanguageModelProvider)`**. `LanguageModelProvider` is an abstract class (new members don't break implementers):
  - `GetModelsAsync`: the models it offers now (id, name, context window, whether it takes tools);
  - `ChatAsync(ChatRequest, IProgress<ChatResponsePart>, CancellationToken)`: streams text and tool calls, and returns how the reply ended (`stop`, `length`, `toolCalls`, `refused`) with the token usage when the service reports it;
  - `GetStatusAsync` and `ConfigureAsync`: whether it is ready (a key, a reachable endpoint) and asking the user for what it needs (its own input boxes; keys to `ISecrets`).
- **Wire** (docs/plugin-protocol.md): `languageModels/registerProvider` and `unregisterProvider` (host → shell); `languageModels/models`, `status`, `configure` and `chat` (shell → host requests), and `languageModels/chatPart` (host → shell) while a chat runs.

### The data boundary (`AiService`, workbench `Ai/`)

- **Off by default.** `ai.enabled` is false. With it off no provider plugin loads, no AI command runs and no AI UI shows except the switch that turns it on. Only the user's settings turn it on: a workspace's settings (a cloned repository's) can turn it off, never on.
- **A provider is chosen, never assumed.** `ai.provider` is empty by default; until the user picks one (Tools → Options → AI, or the chat window's Choose Provider), nothing is sent. `ai.model` picks the model; empty means the provider's first.
- **A per-workspace switch.** `ai.enabled` is also a workspace setting: `false` in a folder's `.codalus/settings.json` turns AI off there, whatever the user's setting. Tools → AI → Turn Off for This Workspace writes it. Restricted mode (an untrusted folder, ADR 0004) sends no file of that folder: chat works with what the user types.
- **What is never sent:**
  - files that `.gitignore` (the repository's, with its nested files and `.git/info/exclude`) or a `.codalusignore` (same syntax, at the workspace root or below) excludes, and the globs of `ai.exclude`;
  - files that hold secrets by name: `.env*`, `*.pfx`, `*.p12`, `*.key`, `*.pem`, `id_rsa*`, `secrets.json`, `*.snk`, `*.publishsettings`, `.npmrc`, `.netrc`, `.git-credentials` (`AiExclusions.SecretFiles`);
  - secrets inside text that is sent: private key blocks, well-known token shapes (GitHub, GitLab, OpenAI, Anthropic, AWS, Slack, Azure storage keys, JWTs) and the values of `password=`, `pwd=`, `secret`, `token` and `apikey` keys and connection-string parts, replaced with `[redacted]` (`SecretRedactor`).
- **Visible.** A status bar item shows the provider and model, and *working* while a request runs; clicking it opens the chat. The Output window's *AI* channel logs each request: when, the provider and model, the size, and which context items went with it (names, not content). That log stays on this machine.
- **No telemetry.** Nothing is counted or sent anywhere but to the provider the user chose.

### Chat (M28a)

- **The AI Chat window** (View → AI Chat, `Ctrl+Alt+I`): a conversation with streamed replies, rendered as Markdown with code blocks.
- **Context the user can see.** Chips above the input say what goes with the next message and can be turned off: the selection, the current file, the open files, the Error List's errors and warnings in those files, the last build's output, and a summary of the solution (its projects, frameworks and the current file's document symbols). Each chip lists the files it sends and which were excluded.
- **Applying code.** Each code block has Copy, Insert at Cursor and Apply. Apply replaces the selection the question was about, or the whole file when the block is a whole file (it names the file in its fence, ```` ```csharp path=src/A.cs ````, or starts like the file); otherwise it inserts at the caret. Every change shows in the same diff preview as code fixes (M3) before it is applied, and goes into undo as one step.
- **Conversations aren't saved.** A chat lives as long as the window, so nothing a model wrote lands on disk or in a repository unless the user applies or copies it.

### Later parts

M28b adds inline completions and next-edit suggestions (through `AiService`, asynchronous, never blocking typing: budget 2) and the AI actions on existing surfaces. M28c adds agent mode and MCP; its safety model is its own ADR.

## Consequences

- A third-party provider is a plugin with a manifest entry and one class. Vendors' SDKs are its business; the built-in ones use `HttpClient` only and add no dependency.
- Every AI feature goes through `AiService`, so a test of the boundary (`AiBoundaryTests`) covers every feature, including later ones.
- Redaction is pattern-based: a secret in an unusual shape can still be sent if the file isn't excluded. The user guide says so and points to `.codalusignore` and `ai.exclude`.
- The API stays proposed until M28c has proved tools and the agent; it is a candidate for API 1.1.
- Prompt caching, effort and other vendor features are each provider's business; the API carries only what every provider understands, plus `ChatRequest.Options` (a string map) for provider-specific settings.
