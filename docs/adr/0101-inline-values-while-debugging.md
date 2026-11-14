# 0101. Inline values while debugging

- **Status:** accepted (0.13, 2026-11-13)
- **Date:** 2026-11-13
- **Deciders:** maintainer (D50, D54)
- **Milestone:** [M62](../roadmap.md#m62--inline-values-while-debugging)

## Context

At a stop, a method's values are in Locals, Autos and data tips, one hover at a time. Drawing each line's values after it in the editor shows the method's state at a glance, as the classic IDE's and VS Code's inline values do. LSP 3.17 has the request for it, `textDocument/inlineValue`: the language server says what each line reads, as text, a variable to look up, or an expression to evaluate, and the client fills in the values from the debugger.

## Decisions (D54, settled at M62's plan as recommended)

| Question | Verdict |
|---|---|
| On or off by default (`debug.inlineValues`)? | **On**, with a switch for all languages and the language settings' override for one. |
| Which values are read? | **Locals, parameters and fields, and auto-properties** (their compiler-made getter only reads their field). Other properties, calls and other objects' members are left to the data tip, so drawing values never runs the user's code. |
| How many lines above the stop? | **The stopped method's lines from its start to the stop's line, at most 100.** |
| Do the values follow the frame selected in the Call Stack? | **Yes.** |

## Decision

- **The language host** answers `textDocument/inlineValue` (`InlineValuesFeature`) for C# and Visual Basic with one code path through Roslyn's operations: local and parameter references and declarations become variable lookups (case-insensitive for Visual Basic), field references on `this` or static ones and auto-properties become evaluatable expressions (an instance member by its name, a static one by its type), each once per line. Top-level statements, lambdas, local functions and accessors are methods of their own.
- **The protocol** has `InlineValue` as one record with LSP's three shapes (text, variable lookup, evaluatable expression); `InlineValueProvider` is advertised.
- **The editor** (`CodeEditorController`, `InlineValuesRenderer`) asks at every stop in its document, for the selected frame: lookups come from the frame's scopes as Locals reads them (an expensive scope is left out), expressions are evaluated in the frame, at most 40, each with a 1-second timeout. Values arrive after the stop is drawn, a newer stop cancels them, and they clear when the program goes on. Each line draws `name = value, …` after its end in the inline hints' gray italics, a value changed since the previous stop in orange; a value is cut at 60 characters and the line at the editor's width.
- **Other languages:** an LSP server reached through the M11 bridge that answers the request gets the same drawing; one that doesn't draws nothing.

## Consequences

- At a stop the editor makes one request to the language service and one Locals read per scope, plus one evaluation per field: the cost is the Locals window's, and it runs after the frame is drawn.
- Properties with bodies show no value inline; their data tip does.
- Hovering an inline value itself opens nothing: the data tip opens on the variable in the code. `TODO(issue)`: a data tip on the drawn value.
- M64's XAML debugging can use the same renderer for binding values after XAML lines.
