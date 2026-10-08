# Shell ↔ language service host protocol (M0)

The language service host (`daedalus-lshost`) runs out of process. The shell starts it and talks to it over **stdio** using **JSON-RPC 2.0 with LSP framing** (`Content-Length` headers). Contracts reuse LSP names and shapes where the semantics match. IDE-specific extensions use the `daedalus/` prefix. The C# contracts are in `src/Daedalus.LanguageServices.Protocol`. The AOT-safe client and supervisor are in `src/Daedalus.LanguageServices.Client`.

This document covers M0 only. M3 replaces it with the full language-service contract and a version policy.

## Process model

- The shell owns the host process and supervises it (`LanguageHostClient`). If the connection drops, in-flight requests fail with `JsonRpcConnectionLostException`, the shell is notified (`HostCrashed`), and the host is restarted with exponential backoff, at most 5 times per minute. The solution is then reopened and open documents are replayed (`HostRestarted`). The UI never goes down with the host.
- Parameters are a single object (LSP style), and names are camelCase.
- Requests are dispatched in arrival order, so a request always observes every edit sent before it (`NonConcurrentSynchronizationContext` on the host).
- Cancellation uses LSP's `$/cancelRequest`.

## Methods

| Method | Kind | Standard? | Notes |
|---|---|---|---|
| `initialize` | request | LSP name, simplified shape | Returns `{serverName, serverVersion, legend}`. |
| `shutdown`, `exit` | request / notification | LSP | |
| `daedalus/openSolution` | request | **extension** | `{path}` (`.sln`, `.slnx` or `.csproj`). Returns `{root: SolutionNode, diagnostics: string[]}`. LSP has no solution model. |
| `textDocument/didOpen` | notification | LSP name, simplified | `{path, text, version}`. Documents are identified by file path instead of URI. |
| `textDocument/didChange` | notification | LSP name, **extended edits** | `{path, version, changes: [{offset, length, text}]}`. |
| `textDocument/didClose` | notification | LSP name | `{path}` |
| `textDocument/semanticTokens/full` | request | LSP | `{path}` → `{version, data}` |
| `textDocument/semanticTokens/range` | request | LSP, **line range** | `{path, startLine, endLine}` (zero-based, end-exclusive) → `{version, data}` |

### Extension details

- **Offset-based edits.** `didChange` edits are UTF-16 offsets into the document *before* that edit. Edits apply in order. AvaloniaEdit and rope-based editors produce offsets natively, which avoids line/column round-trips. Non-.NET clients must convert to UTF-16 (C# string indices).
- **Versioned token responses.** Semantic-token responses carry the document `version` they were computed for, and clients discard stale responses.
- **Token legend.** Indices 0–21 are the LSP standard token types. Indices 22 and up are Roslyn-specific (`delegate`, `record`, `recordStruct`, `field`, `constant`, `local`, `label`, `excludedCode`, `preprocessor`, `punctuation`, `stringEscape`, `xmlDocComment`, `controlKeyword`, `extensionMethod`, `stringVerbatim`). The legend is append-only. Modifiers: `static`.
- **Single-line, non-overlapping tokens.** Roslyn spans that are multi-line or nested (an escape inside a string) are split, and the inner span wins.

### Why range requests matter

On the 100k-line fixture, full-document classification took 15 s cold and 1.6–5.6 s warm, returning 549k tokens. A 150-line range took 5–50 ms. Clients must request the viewport plus a margin. M3 will add result caching and delta responses.
