# 0016. Language platform: languages, grammars and the LSP bridge

- **Status:** accepted (M11 plan, 2026-10-10; the maintainer's standing go-ahead for M9–M16)
- **Date:** 2026-10-10
- **Deciders:** maintainer

## Context

M11 makes "a language" a pluggable unit, so that F#, VB and other languages can come as plugins. It also gives first-class editing to the non-C# files of .NET repositories. The parts:

- a language registry (ids, file patterns, first lines);
- TextMate grammars;
- language configurations (comments, brackets, auto-closing pairs, folding, indentation);
- a generic bridge to LSP 3.17 servers;
- built-in editing for MSBuild (including evaluated values on hover), JSON with schemas, XML, YAML, `.editorconfig` and Markdown with a preview;
- language-agnostic fallbacks (word completion, indentation folding).

Constraints:
- The shell is Native AOT.
- The editor is AvaloniaEdit.
- MSBuild runs only in the build host (ADR 0004).
- Every dependency must be OSI-licensed and compatible with MIT.
- Typing must stay within budget in every file type.

## Options

**Grammars**
1. Hand-written AvaloniaEdit highlighting definitions (XSHD). Few exist, and plugins would need a format nobody else uses.
2. **TextMate grammars through TextMateSharp** (MIT), the engine AvaloniaEdit already integrates (AvaloniaEdit.TextMate, MIT). Its bundled grammars are VS Code's (MIT), and plugins can reuse any VS Code grammar unchanged.

**LSP types**
1. Generate the full LSP 3.17 model from `metaModel.json`, as the roadmap suggested.
2. **Keep the narrowed, source-generated types the C# host uses, and normalize what servers send** into them. That covers options objects, unions of locations and location links, marked strings, completion arrays, numeric diagnostic codes, `changes` maps and bare commands.

**Where JSON, XML, YAML and MSBuild services run**
1. In the shell. That would mean JSON schema validation and YAML parsing under Native AOT, and on the UI process.
2. In the build host. MSBuild is there, but so is everything else, and the build host only exists for trusted solutions.
3. **A small supervised `daedalus-markuphost`** speaking LSP. It asks the shell for evaluated MSBuild values, which the shell gets from the build host.

**JSON Schema validation**
1. JsonSchema.Net. Since version 8 it is under the Open Source Maintenance Fee EULA, which isn't an OSI license.
2. NJsonSchema (MIT). It pulls in Newtonsoft.Json and more than editing needs.
3. **Our own subset**: types, properties, patternProperties, additionalProperties, required, enum and const, items, numeric and string limits, patterns, allOf/anyOf/oneOf, local `$ref`s and descriptions. It is enough for completion, hover and the common diagnostics. Unknown keywords are ignored, so the risk is fewer diagnostics rather than false ones.

## Decision

**Registry**
- `LanguageRegistry` (workbench) holds languages and grammars.
- Files match by exact file name, then file-name pattern, then the longest extension, then the first line.
- The shell registers about forty built-in languages.
- Plugins contribute `contributes.languages`, `grammars` and `languageServers` in VS Code's shapes. A plugin with only these (and settings) needs no code: `entry` is optional since API 0.2.4.

**Highlighting**
- TextMate grammars color every language except C#, which keeps Roslyn's semantic tokens.
- The bundled registry loads on the first non-C# file, not at startup.
- Tokenizing runs on AvaloniaEdit.TextMate's background thread.
- Themes map to Dark+, Light+ and High Contrast Dark.
- Semantic tokens from other servers are remapped by name from their legend to the shell's.

**Language configuration**
- Drives line and block comments (Toggle, Comment, Uncomment), auto-closing pairs and typing over them (setting `editor.autoClosingBrackets`).
- Drives folding when the server has none: XML elements, region markers and indentation.
- Drives word completion when no server completes.

**Bridge**
- `contributes.languageServers` declares a server over stdio. The command is a file in the plugin folder (a `.dll` runs with `dotnet`) or a program on `PATH`.
- The shell supervises it like its own hosts, and one server may serve several languages.
- The client:
  - declares real client capabilities;
  - answers `workspace/configuration` from a settings section and accepts `client/registerCapability` and progress requests;
  - sends whole documents to full-sync servers;
  - normalizes every result (`LspNormalizer`).
- A conformance server in `tests/` answers in VS Code's shapes, and the bridge tests pin the normalization.

**Markup host**
- `daedalus-markuphost` serves `json`, `jsonc`, `xml`, `msbuild`, `yaml` and `editorconfig`.
- **JSON:** syntax errors, schema validation, completion of property names and values, and hover. Schemas are found in this order:
  - the document's `$schema` (a file next to it, or a URL);
  - schemas bundled for `global.json`, `launchSettings.json`, `appsettings*.json`, `dotnet-tools.json`, `runtimeconfig.template.json` and `daedalus-plugin.json`;
  - SchemaStore's catalog, downloaded and cached for a week when `json.schemaDownload` is on.
- **YAML:** syntax errors, plus schema validation and hover through the same machinery (YAML becomes positioned JSON values).
- **MSBuild:** well-formedness, completion of elements, properties, items, metadata, attributes and values (choices, TFMs, runtime identifiers), and warnings for unknown values. Hover adds the **evaluated value and where it is set**: the host asks the shell (`daedalus/msbuild/property`), and the shell asks the build host (`projectProperties/get`, ADR 0013).
- **`.editorconfig`:** keys and values of EditorConfig and the common .NET options, hover and value warnings.

**Markdown**
- Open Preview to the Side (Ctrl+K V) renders the document with the shell's Markdown renderer, 300 ms after each edit.
- **The preview in the editor (2026-10-31, from the maintainer's request and decisions of that day).** A Markdown editor's tab shows the text and its live preview side by side (`MarkdownSplit`), half and half, from the first open. The divider resizes them; its chevron collapses the preview to a strip at the right edge, which shows it again when clicked, and Markdown: Toggle Preview (Ctrl+K Ctrl+V; Ctrl+Shift+V is the clipboard ring) does the same. Whether it is collapsed and its width are one state shared by every Markdown editor, kept in `state.json` rather than in settings, as the layout is: collapsing one collapses the open ones and the next ones. The two sides scroll each other by proportion (the rendered Markdown has no source positions to align by line); a sync ignores the scroll events it causes and a render's change of extent. A collapsed or hidden preview doesn't render: it renders once when it shows again. A large file (M56) opens collapsed. Open Preview to the Side stays, for a preview to move or float elsewhere. Code Search's preview has none.
- **The preview's Markdown (2026-11-01, from the maintainer's report that a table rendered as one paragraph).** `MarkdownRenderer` stays the shell's own (no Markdig in the AOT shell; AI Chat's code blocks keep their indices): it adds GitHub's tables, laid out as a browser's automatic table (`MarkdownTable`: each column as wide as its content when the columns fit, otherwise at least its longest word and a share of the rest, by how much more it wants), task lists, strikethrough, setext headings and closing `#`s, rules of any length, quotes of several lines rendered as Markdown of their own, hard line breaks and `<br>`, `<https://…>` and bare addresses, images as a link with their description, link titles, `__bold__`, and hidden HTML comments. A heading's runs now take its weight (they were set to normal). Every view that renders Markdown gets them: the preview, plugin Markdown views, AI Chat, READMEs, pull request descriptions and hovers.

## Consequences

- Editing works for any language with a VS Code grammar and an LSP server, without plugin code.
- Results from servers that rely on VS Code-only client extensions may be incomplete.
- `TODO(issue)`, not in M11:
  - **LSP features:** inlay hints, call and type hierarchy, pull diagnostics, selection ranges, linked editing, CodeLens and document symbols for non-C# languages.
  - **Bridge:** snippet tab stops (snippets are inserted as plain text), and several servers merged for one language.
  - **XML schemas:** XSD validation of `.xml`, `.runsettings` and `nuget.config`.
  - **The `.resx` editor:** a grid with strongly typed resource generation.
  - **YAML:** completion.
  - **MSBuild:** package id and version completion from M9.
  - **Markdown preview:** relative links and images.
- Plugin grammars whose license isn't MIT-compatible can't be bundled with the shell. Plugins carry their own.
