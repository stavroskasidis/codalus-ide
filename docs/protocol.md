# Shell ↔ language service protocol

Language services run out of process and speak **LSP 3.17** over **stdio** (JSON-RPC 2.0, `Content-Length` framing), with a few `daedalus/` extensions where LSP has no message ([ADR 0005](adr/0005-editor-and-language-services.md)). The shell's client is generic (`LanguageHostClient` in `src/Daedalus.LanguageServices.Client`, AOT-safe), so any LSP server can serve a language; the C# service is `daedalus-lshost` (`src/Daedalus.LanguageServices.Host`, Roslyn). Contracts are in `src/Daedalus.LanguageServices.Protocol`.

## Process model

- The workbench's `LanguageService` starts a language's host when the first file of that language opens, or when the first design-time build of one of its projects arrives. It never starts at shell startup.
- The host is supervised (`SupervisedProcess`): on a crash it restarts with backoff (at most 5 times a minute), and the client sends `initialize`, the projects (`daedalus/projects/update`) and every open document (`didOpen` with the editor's current text) again. Diagnostics of a host that gives up are cleared.
- Notifications are handled in arrival order, and a request reads the server's state when it arrives, so it observes every earlier edit. Cancelled requests send `$/cancelRequest`.

## LSP subset

| Method | Notes |
|---|---|
| `initialize` / `initialized` / `shutdown` / `exit` | `initializationOptions` take the same settings as `workspace/didChangeConfiguration`. |
| `textDocument/didOpen`, `didChange`, `didClose`, `didSave` | Incremental sync (`textDocumentSync: 2`). URIs are `file://` URIs. |
| `workspace/didChangeConfiguration` | `{settings: {analysisScope: "openDocuments" \| "solution"}}`. |
| `textDocument/completion`, `completionItem/resolve` | The list sets `itemDefaults.editRange` (the span being completed). Items carry no edit; `resolve` fills `textEdit`, `additionalTextEdits` (e.g. an added `using`) and `documentation`. Items in a list in suggestion mode have empty `commitCharacters`. |
| `textDocument/hover` | Markdown: the signature in a `csharp` code block, then documentation. |
| `textDocument/signatureHelp` | Parameter labels are substrings of the signature label. |
| `textDocument/definition`, `textDocument/implementation` | `Location[]`. Definitions in source-generated files use `daedalus-generated:` URIs; symbols defined in referenced assemblies point into a decompiled document (`daedalus-metadata:` URI). |
| `textDocument/documentHighlight` | Definitions are `Write`, references `Read`. |
| `textDocument/foldingRange` | `startCharacter` is the end of the first line (the fold hides the rest); `kind` is `region`, `imports`, `comment` or absent; `collapsedText` labels regions and comments. |
| `textDocument/semanticTokens/full`, `/range` | See the token legend below. Clients should ask for the visible range. |
| `textDocument/references` | `Location[]`, ordered by file and position. |
| `textDocument/prepareRename`, `textDocument/rename` | `prepareRename` returns the identifier's range and name, or null where renaming isn't possible (keywords, symbols outside the solution). `rename` returns a `WorkspaceEdit`. |
| `textDocument/codeAction`, `codeAction/resolve` | Code fixes (`quickfix`) for the diagnostics on the range's line and refactorings (`refactor`) at the range: Roslyn's built-in providers and the projects' analyzer packages' fixes. Actions are listed without edits; `resolve` returns the `WorkspaceEdit`. Nested actions are flattened ("Parent: Child"). |
| `textDocument/formatting`, `/rangeFormatting`, `/onTypeFormatting` | Roslyn's formatter with the project's `.editorconfig`. On type: after `}` (the block) and `;` (the statement or member). |
| `workspace/symbol` | Source declarations (types and members) matching the query (camel case or substring), at most 200. |
| `textDocument/publishDiagnostics` (server → client) | Open documents: compiler and analyzer diagnostics shortly after each edit, with the document `version`. Closed files: compiler diagnostics of the whole solution from a background pass (analyzers too with `analysisScope: solution`). `code` is the diagnostic id; `codeDescription.href` its help link; `tags` mark unnecessary (faded) and deprecated code. Hidden diagnostics are sent only when they mark unnecessary code, as hints. |
| `window/logMessage` (server → client) | Analyzer load failures, analyzer exceptions, project problems. |

Shapes narrowed from LSP's unions (documented on the C# types): `documentation` is always `MarkupContent`, parameter labels are strings, diagnostic codes are strings.

## Extensions

| Method | Kind | Params → result | Why |
|---|---|---|---|
| `daedalus/projects/update` | notification | `{projects: ProjectData[]}` | LSP has no project model. Adds or replaces projects by path. |
| `daedalus/projects/remove` | notification | `{paths}` | |
| `daedalus/documentOptions` | request | `{textDocument}` → `{insertSpaces, tabSize, indentSize, newLine?}` | Editor indentation and line endings from `.editorconfig`. |
| `daedalus/sourceGeneratedText` | request | `{textDocument}` → `{text, hintName, generatorName}` | The content of a `daedalus-generated:` document (opened read-only). |
| `daedalus/metadataText` | request | `{textDocument}` → `{text, assemblyName, typeName}` | The decompiled C# of a `daedalus-metadata:` document (ICSharpCode.Decompiler; reference assemblies decompile to stubs). |
| `daedalus/workspaceStatus` | notification (server → client) | `{state: loading \| ready, projects, documents}` | `ready` after a background pass has compiled every project; status bar and the `language_ready_ms` benchmark. |

**`ProjectData`** `{path, name, language, targetFramework, arguments, outputPath?, outputRefPath?, projectReferences?}` is a design-time build's result (the build host's `project/compilerInputs`, [project-protocol.md](project-protocol.md)): the compiler command line, with sources relative to the project directory. The server parses it like the compiler does. A reference to another project's output (or reference assembly), or to the assembly of a referenced project built for another target framework, becomes a project reference. Only one target framework per project is loaded: the active one, chosen in the editor's project-context picker.

**Projects and files.** Files of loaded projects get full semantics. Files outside any loaded project, and every file of an untrusted folder, go to a "Miscellaneous Files" project with framework references only. Open documents move between the two as projects arrive. Closed files are reloaded when they change on disk.

## Semantic token legend

Indices 0–21 are the LSP standard token types. Indices 22 and up are Roslyn classifications LSP has no name for: `delegate`, `record`, `recordStruct`, `field`, `constant`, `local`, `label`, `excludedCode`, `preprocessor`, `punctuation`, `stringEscape`, `xmlDocComment`, `controlKeyword`, `extensionMethod`, `stringVerbatim`. The legend is append-only. Modifiers: `static`. Tokens are single-line and don't overlap: multi-line Roslyn spans are split, and nested spans (an escape inside a string) win over their container.

Range requests matter: on the 100k-line fixture, classifying the whole document took 1.6–15 s and returned 549k tokens; a 150-line range takes 5–50 ms (ADR 0002).
