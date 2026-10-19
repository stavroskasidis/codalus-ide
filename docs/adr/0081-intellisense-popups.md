# 0081. IntelliSense popups: our own, with classified text from the C# service

- **Status:** proposed (0.5.0, 2026-10-19), implemented on `claude/intellisense-popups`; the maintainer accepts it by merging the pull request
- **Date:** 2026-10-19
- **Deciders:** maintainer
- **Amends:** [ADR 0005](0005-editor-and-language-services.md) (signature help's members are the call site's, and hover, signature help and completion resolve carry classified text), [ADR 0019](0019-classic-ide-look-and-feel.md) (IntelliSense looks like a classic IDE's)

## Context

The maintainer compared the editor's IntelliSense with Visual Studio 2022's, from screenshots of `builder.AddTab(`:
- **Signature help** was a flat grey box with tiny filled arrows, the signature in plain text with the active parameter bold, and generic types unsubstituted (`wsLayoutBuilderBase<TModel, TLayoutBuilder>.AddTab(…)`). Visual Studio colors the signature like code, substitutes the call site's type arguments, and shows "˄ 1 of 2 ˅" chevrons.
- **The completion list** opened on top of signature help. Its items had generic glyphs, and it had no filters and no description beside the selection.
- **A third tooltip,** Quick Info with a diagnostic, opened over both.
- **Quick Info** showed the signature in a code block that scrolled and cut it off. It printed "Returns: Returns:", and its diagnostics were plain lines with no actions.

What existed:
- The completion list and signature help were AvaloniaEdit's `CompletionWindow` and `OverloadInsightWindow`. Both are anchored at the caret's line bottom, so they overlap.
- Each popup pushes a stacked input handler onto the text area. Closing signature help pops the completion list's handler with it, which closes the list.
- Their templates have no room for a filter bar.
- The C# service sent signature help as a label string built from `OriginalDefinition` (which loses the type arguments), and hover as Markdown built from the sections' plain text. Roslyn's own tags (`TaggedText`, `SymbolDisplayPart`) were dropped.

## Options

**The popups.**
1. Restyle AvaloniaEdit's windows. The overlap and the handler stack are in their base class, and the list's template can't take a filter bar.
2. **Our own popups** on Avalonia's `Popup`, placed by the editor, with the keys passed from the editor's own key handler.

**Classified text.**
1. Classify the label in the shell (TextMate or a second semantic-tokens request). This is a guess for generics, and another request.
2. **Extend the LSP results** with Roslyn's classified parts as runs whose kinds are the semantic token legend's names. LSP allows extra properties, so other clients ignore them.

## Decision

**Our own popups** (`Editor/IntelliSense/`):
- **The completion list** has a row per item: the kind's icon, the name with the typed text's matched characters bold, and the detail. The filter bar at the bottom has a toggle per kind present. The description of the selected item shows in a tooltip to its right, resolved lazily.
- **Signature help** has chevrons with "n of m", the colorized signature with the active parameter bold, the summary, and the active parameter's documentation.
- **Quick Info** has the kind's icon, the wrapped signature with its overload count, and the sections. Then its diagnostics, each code a link to its help, and Fix, Other Fixes and Add to Chat.
- **Placement:** the editor places them against the caret's line, the list below and signature help above. At the screen's bottom edge the list goes above and signature help below; they flip only when alone. They are placed again when the editor scrolls or the window moves, and close when the line leaves the view, the window is deactivated or Escape is pressed. Quick Info doesn't open while either is open.
- **Keys:** the editor's key handler gives the list its keys first, then signature help its Up and Down. The filters' shortcuts are key bindings (Alt and Visual Studio's letters) on the new `suggestWidgetVisible` context key, so they show in Keyboard Shortcuts and can be rebound. Emacs keeps its own Alt+F, Alt+V and Alt+X.
- **Icons:** the kind icons are our own pixel drawings (`eng/icons/symbol-icons.py`), compiled at build time like every icon, each in its kind's palette color.
- **The C# Interactive window** uses the same popups.

**Classified text** (`docs/protocol.md`, "Classified text"):
- `codalusParts` on signature help.
- `codalusDescription` on hover and resolved completion items. Section headings are left to the client, so "Returns:" shows once.
- `codalusGlyph` on completion items.
- Signature help keeps the member group's constructed methods, so a generic base class's members show the call site's type arguments.

## Consequences

- The popups look and behave like Visual Studio's, in every theme: code in the editor's own colors, and the chrome in theme tokens.
- Other language servers get the same popups. Their signatures are plain text with the active parameter bold, and their LSP kinds map onto the icons.
- Hover's Markdown and signature help's labels change for every client: type arguments are substituted, and headings appear once. The Visual Basic signature test now expects `List(Of Product)`.
- AvaloniaEdit's completion window is still used by snippets' choices.
- `TODO(issue)`: commit characters (Visual Studio commits on `(`, `.`, `;`); Visual Studio's "target-typed" filter and the expander that adds unimported types.
