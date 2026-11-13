# 0097. The IDE's AI features through an external agent

- **Status:** accepted (0.13, 2026-11-13)
- **Date:** 2026-11-13
- **Deciders:** maintainer (the grilling of 2026-11-12, below)
- **Amends:** [ADR 0079](0079-external-agents-over-the-agent-client-protocol.md) (the agent AI Chat has picked also answers the IDE's AI features, in sessions of their own on its connection), [ADR 0062](0062-ai-provider-api-and-data-boundaries.md) (inline completions, next edit and the AI actions no longer need a provider when an agent is picked), [ADR 0096](0096-the-first-run-setup-asks-about-ai.md) (the setup's agent card no longer says an agent brings no suggestions as you type)

## Context

Since M48 an external agent (ADR 0079) answers in AI Chat, and since 0.8.0 AI Chat remembers it (`ai.chat.agent`). Everything else that uses AI went only to the provider of ADR 0062, through `AiService.ChatAsync`: inline completions and next-edit suggestions (M28b), the commit message, the pull request's title and description, and Fix with AI. The actions that open in AI Chat (Explain, Ask AI, Ask AI Why It Fails, Generate Tests, Summarize with AI) went to the built-in assistant even with an agent picked. A user who connected only an agent (Connect's main path, ADR 0083) had AI Chat and nothing else: the editor's AI menu asked for a provider. The maintainer asked for all of the AI features to work under the external agents, suggestions while typing included.

ACP has no completion endpoint: an agent answers prompts in sessions, with its system prompt, its own tools and its model. A turn takes seconds, not the few hundred milliseconds of a provider's completion, and each one is spent from the user's plan or key.

## The maintainer's decisions (the grilling, 2026-11-12)

| Question | Options | Verdict |
|---|---|---|
| Who answers the features | AI Chat's pick; the agent only as a fallback when there is no provider; a separate setting | **AI Chat's pick** (`ai.chat.agent`): an agent answers everything, Built-in returns all of it to the provider. One choice, no new setting. |
| Suggestions while typing | Their own switches and a fast model; the same as with a provider; only on demand; left out | **Their own switches and a fast model**: off by default as before, a longer pause, a small model. |
| What the features' session may do | Nothing (text in, text out); reads only; the same as AI Chat | **Nothing**: no `codalus mcp`, and every file, terminal and permission request refused without asking the user. |
| The fast model | A picker with a guessed default; a picker with the chat's model by default; always the chat's | **A picker with a guessed default**, kept in the agent's entry. |
| Where the session runs | A second session in the chat's process; a process of its own | **A second session in the chat's process.** |
| Plugins' requests to a model (the proposed `LanguageModels` API) | Provider only; through the agent too | **Provider only**: the exception of ADR 0079 is granted to the IDE's own features, not to third-party code. |

## Decision

### Routing

- `AiService` gains `ExternalAnswerer` (`IAiExternalAnswerer`, set by `AcpAgents`) and `ExternalAgent`: AI Chat's pick when it is an entry of `ai.agents` and AI is on, else null. `CanAnswer` is a provider or an external agent; inline completions, next edit and the shell's AI actions check it instead of `AiState.Ready`.
- `AiService.ChatAsync` takes an `AiRoute`: `Provider` (the default: chat, the built-in agent, plugins), `Suggestion` (completions, next edit) or `Action` (`AskAsync`: the commit message, the pull request, Fix). A request with a route other than `Provider`, without tools, while `ExternalAgent` is set, goes to the agent. The feature prepares its context exactly as for a provider (`PrepareAsync`: exclusions, redaction, the budget; a completion's own redaction and excluded-file check), and the request is a line of the *AI* Output channel with what was sent, as a provider's.
- The prompt is one text block: a preamble (`AiService.ExternalAgentPreamble`: answer with the reply only, use no tools, everything needed is here), the feature's instructions, then its user message. The agent's message chunks are the reply (thoughts and tool calls are left out); the features' own parsing (`AiCompletions.Clean`, `AiNextEdit.Parse`, the fix's code block) is unchanged.
- The actions that open in AI Chat go to the agent's chat (`SendExternalAsync`) when an agent is picked, as a typed question does, with its steps, preview and approvals.
- The status bar reads **AI: *agent***; `EnsureAiAsync` no longer asks for a provider when an agent is picked (the gate's refusal says why instead).

### Text sessions (`AcpTextSession`)

- **On the chat's connection.** `AcpAgentSession.GetTextSessionAsync` sends another `session/new` on its client (the workspace as `cwd`, no MCP servers), once per purpose: `Suggestions` and `Actions`. They live as long as the chat's process and are created again after a terminal sign-in's restart.
- **Routing on the connection.** `AcpClient` now hands every request and update to the handler of its `sessionId` (`Route`); the chat's own session is `PrimarySession`. A message of a session with no handler of its own, once the primary is set, is refused (an update ignored): the chat never shows a text session's chunks, and a text session's requests never reach the chat's handler, even between `session/new`'s answer and the route's registration.
- **Text in, text out.** A text session advertises nothing new (the capabilities are the connection's), but refuses `fs/*` and `terminal/*` with a JSON-RPC error and answers `session/request_permission` with `cancelled`, each a line of the *AI* Output channel, and never asks the user. One prompt runs at a time; a cancelled one sends `session/cancel` for its session only and waits up to 3 seconds for the agent's answer before the next may go.
- **The model.** `Actions` use the chat's current model. `Suggestions` use, in order: the user's pick (`ai.agents.<agent>.suggestions.model`, when the agent still offers it), a small model guessed by its name (`AcpTextSession.GuessFastModel`: the words *haiku*, *mini*, *flash*, *nano*, *lite* in that order of preference; of several with the word, the one with the fewest such words, then the last listed), the chat's model, or the agent's default. The *AI* Output channel says which and why. The pick is not part of the entry's identity (`SameAs`): it never restarts the agent, only drops the text sessions.
- **AI Chat's picker.** While `ai.completions.enabled` or `ai.nextEdit.enabled` is on and the agent offers a model choice, a **Suggestions:** picker follows the agent's own: **Automatic (*guess*)**, then the agent's models. Options has no editor for it (the External Agents page is deferred, ADR 0083).

### The gate, and starting the agent

- A feature's request checks ADR 0079's gate (`AcpAgents.WhyNot`: AI on, a trusted folder, not a remote window) before and after the session starts; a refusal is `AiUnavailableException` with the reason (suggestions show nothing; an action shows the message).
- When AI Chat hasn't started the agent, the feature starts it through `AcpAgents.Host` (the shell's `IAcpHost`, so the chat's session is the usual one, with its `codalus mcp` pipe). A suggestion never signs in; an action the user chose may, as a question in AI Chat does.
- Suggestions under an agent wait 1,000 ms after typing pauses (`AiCompletions.DefaultAgentDelay`) unless the user set `ai.completions.delayMilliseconds`; `ai.completions.model` applies to providers only.

## Consequences

- With only an agent connected, every AI feature works; the Connect dialog's consent line says inline suggestions come back answered by the agent picked in AI Chat, and the first-run setup's agent card (ADR 0096) says the agent answers the AI actions and, once turned on, suggestions as you type. The setup still offers the suggestion boxes only on the built-in assistant's path (ADR 0096's "one path"): with an agent, they are turned on in Options. *Amended 2026-11-13:* the agent card's "What leaves this machine" says what the AI features send it too ("What it reads, and what the AI features send it, to its own provider."), and a note under the agent's two windows (`AgentFeaturesNote`) says it answers the AI actions and, once turned on in Tools → Options → AI, suggestions as you type. The three reasons to run an agent from the IDE stay three (ADR 0096).
- The boundary is as ADR 0079's: what the IDE gives a text session has passed `AiService`'s checks, and the agent may still read the disk by itself and send what it has to its provider. A text session gets less from the IDE than the chat's (no tools, no reads).
- Suggestions through an agent are slower and cost more than a provider's completion; they keep their own switches, off by default, and the user guide says so.
- Tests use the stand-in agent's text sessions (`sess_text_N`: `ACP_FAKE_TEXT_REPLY`, `ACP_FAKE_TEXT_PROBE`, `ACP_FAKE_TEXT_SLOW`, `ACP_FAKE_FAST_MODEL`).
- Deferred (`TODO(issue)`): plugins' model requests through the agent (decided against for now, above); the model for suggestions in the Options page's External Agents editor (with that page); closing a text session on the agent's side when ACP's `session/close` is stable (today it is forgotten, and ends with the process).
