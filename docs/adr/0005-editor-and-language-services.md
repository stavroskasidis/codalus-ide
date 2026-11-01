# 0005. Editor and language services

- **Status:** accepted (M3 plan approval, 2026-10-09), amended 2026-10-31 ([document sync off the UI thread](#amendment-2026-10-31-document-sync-off-the-ui-thread-m57))
- **Date:** 2026-10-09
- **Deciders:** maintainer

## Context

The project brief's M3 asks for a real editor (tabs, split views, preview tabs, multi-caret editing, folding, minimap, find/replace) and C# language features on Roslyn, "built on a language-service abstraction that F# and VB can plug into later": semantic highlighting, completion, signature help, Quick Info, live diagnostics and the Error List, navigation, Find All References, Rename with preview, code fixes and refactorings with diff preview, `.editorconfig` formatting, Go To All and Find in Files. Analyzers and source generators must work. The maintainer split it into M3a (editor, project data, highlighting, diagnostics, completion, signature help, Quick Info, Go to Definition/Implementation, editor budgets) and M3b (references, rename, lightbulb, formatting, Go To All, Find in Files, metadata navigation).

What exists:
- The M0 language host (`daedalus-lshost`) loads solutions with Roslyn's `MSBuildWorkspace` and serves semantic tokens over a protocol that describes edits as offsets. ADR 0004 moved MSBuild into the build host and left a `TODO(issue)`: feed Roslyn from the build host instead.
- The shell edits plain text with AvaloniaEdit (ADR 0001). AvaloniaEdit 12 has folding, a search/replace panel, completion and overload windows and box selection, but no multiple carets and no minimap.
- Roslyn's own LSP server (`Microsoft.CodeAnalysis.LanguageServer`) is not published on nuget.org, loads projects with its own MSBuild host, and has no stable API. `Microsoft.CodeAnalysis.Features` and `Microsoft.CodeAnalysis.CSharp.Features` (MIT) are on nuget.org and carry completion, Quick Info, code fixes, refactorings and more behind public APIs; signature help and metadata-as-source are internal.

## Options

**Protocol.**
1. Keep the M0 protocol (offsets, Daedalus-specific messages).
2. **Standard LSP 3.17**, with `daedalus/` extensions only where LSP has no message.

**Where Roslyn gets project data.**
1. `MSBuildWorkspace` in the language host (M0). A second MSBuild in a second process, with its own evaluation and a full compile before anything shows; contradicts ADR 0004.
2. **Design-time builds in the build host**, sent to the language host as compiler command lines.
3. Re-derive compiler inputs from the evaluation alone. Misses everything targets compute (resolved references, analyzers, generator inputs, `DefineConstants` from targets).

**Language host.**
1. Roslyn's LSP server. Not on nuget.org; loads projects itself.
2. **Our host on Roslyn's public Workspaces and Features APIs** (the M0 host, grown).

## Decision

**Protocol: standard LSP** (UTF-16 line/character positions, incremental `didChange` with ranges). The shell's language client is generic, so a later F# service (FsAutoComplete speaks LSP) or VB service plugs in without editor changes. Extensions, documented in `docs/protocol.md`: `daedalus/projects/update` (project data), `daedalus/documentOptions` (indentation from `.editorconfig`) and project-context selection for multi-targeted files. The M0 offset-based messages and `daedalus/openSolution` are retired with the spike.

**Project data: design-time builds in the build host.** After evaluation (and restore, when trusted), the build host builds each project's `CompileDesignTime` targets with `DesignTimeBuild=true`, `SkipCompilerExecution=true` and `ProvideCommandLineArgs=true`, the same targets Roslyn's and the C# Dev Kit's project systems run, and collects the `csc` command line. Builds run in parallel through one `BuildManager`, for the active target framework of each project (multi-targeted projects get a project-context picker in the editor). They re-run, debounced, after project-file changes, restores, configuration or framework switches, for the affected projects and their dependents. The shell forwards the results to the language host, which parses them with `CSharpCommandLineParser` into Roslyn `ProjectInfo`: sources, metadata references, analyzer and generator references, compilation and parse options, `.editorconfig` files, additional files. The language host no longer references MSBuild.

**Language host: ours, on Roslyn's public APIs.** A `Workspace` subclass with MEF host services that include the Features assemblies holds the solution; open documents are kept in memory and are authoritative. Features use `CompletionService`, `QuickInfoService`, `SymbolFinder`, `Classifier`, block structure from the syntax tree and `CompilationWithAnalyzers`; signature help is our own provider on `SemanticModel`; metadata navigation (M3b) decompiles with ICSharpCode.Decompiler after a license check. Source generators run inside Roslyn; generated documents are navigable, read-only. Files outside any loaded project, and everything in untrusted folders, go to a "Miscellaneous Files" project with framework references: syntax and in-file semantics only.

**Diagnostics.** Open documents get compiler and analyzer diagnostics, debounced while typing. The whole solution gets compiler diagnostics in the background at low priority, so the Error List shows build-breaking errors in closed files; `languageServices.analysisScope = solution` extends analyzers to closed files. Diagnostics are pushed (`textDocument/publishDiagnostics`).

**The abstraction.** The workbench keeps a `LanguageServiceRegistry`: language id, file extensions, how to start the host, capabilities. C# is registered as built-in. The editor talks to whatever service owns a document's language through the generic client. `TODO(issue)`: a `languageServers` contribution point in the plugin manifest, so F#/VB services can be plugins (post-MVP).

**The editor.** AvaloniaEdit stays (ADR 0001). We add multiple carets (a selection-set model applied as one undo group, extra carets drawn on a layer of their own) and a minimap (a cached, incrementally updated rendering of the document). Split views use Dock's document groups; a second view of the same document shares its `TextDocument`. Preview tabs and per-solution session state (open documents, carets, splits) live in the layout manager.

**Lifetime.** The language host starts when the first file of a registered language opens or a trusted solution finishes loading, never at startup (cold-start budget). It is supervised like the other hosts: on a crash it restarts, and the client re-sends the project data and the open documents.

## Consequences

- MSBuild runs in exactly one process, and Roslyn sees what the real build sees, including analyzers, generators and target-computed options.
- Language features arrive after evaluation, restore and the design-time build. ADR 0002's budget 4 says "with language services ready in the background"; M3a records `language_ready_ms` on the 50-project fixture as an informational metric before a budget is set.
- Positions are converted between offsets and UTF-16 line/character in the shell; AvaloniaEdit's line tree makes it cheap, and it is unit-tested.
- Some of the classic IDE's features rely on internal Roslyn services (inline rename's conflict UI, metadata-as-source, signature help). We implement our own where needed; each one is a maintenance cost when Roslyn changes.
- A third helper process runs while C# is being edited. Memory with a solution open is measured in M3a.

## Amendment (2026-10-31): document sync off the UI thread (M57)

The decision's incremental `didChange` and its open documents went out from the UI thread: each `didOpen` made the whole text a string and encoded it there (0.7–0.8 s for a 19 MB XML file just under the large-file limits, every `<` six bytes of JSON), and so did each key's whole text for a server that syncs whole documents (`TextDocumentSyncKind.Full`; about 0.3 s a key at 15 MB). Measured in the stall tests, in a 4-core container; they decide nothing.
- **One writer per connection.** Every outgoing message of a `JsonRpcConnection` (requests, notifications, responses, cancellations), in every host, is queued in call order and serialized, written and flushed by one loop. Queueing is synchronous in the caller, so the ordering contract holds: a request always follows the edits queued before it, from any thread. A remote window's path rewrite (`MappedJsonStream`) runs on that loop too. Frames are never cut, the stuck-write reports stay, and disposal fails what is still queued. A stream that fails, whatever the exception, ends the writes: the message being written, those queued and later ones fail with a lost connection rather than wait.
- **A snapshot, not the text.** The editor keeps each document's latest AvaloniaEdit snapshot with its version, taken together under a lock; `didOpen` takes the snapshot when it is queued and makes its text on the loop, so a request queued behind it never reads a later edit's text; it reads the latest snapshot instead only when the loop already skipped a newer change because the document wasn't open there (queued from the editor's thread just before the `didOpen`), so no edit is lost. A restarted server's replay opens each document the same way, read when its own `didOpen` is queued and written rather than when the replay begins, and skips one closed meanwhile. A whole-text `didChange` carries the snapshot taken for that change and makes its text on the loop, never from a later snapshot: a later text would pass a request queued before its change (a completion's position read against the next key's text). The UI thread's cost is O(1), and the wire format is unchanged.
- **Versions.** The client tracks, per connection, the version each server has: a `didOpen` sets it (it may carry edits queued after it, read later), a change at or below it is skipped, a change for a document not open there is skipped, and `didClose` forgets it. A whole text still queued is replaced by a newer one (LSP allows the gaps); incremental changes go one by one.
- **Cancellation.** A request cancelled while queued is never written and sends no `$/cancelRequest`; one written sends it, as before. A queued notification is never dropped: its token stops only the caller's wait.
- **Razor's HTML** (ADR 0027) is projected off the UI thread from a snapshot at the Razor file's version, sent with that version, and dropped when a newer projection went first or the file closed meanwhile.
- `TODO(issue)`: Code Search still makes every open snapshot a string on the UI thread for its text results; streaming the snapshot's chunks to the stream without one big string.
