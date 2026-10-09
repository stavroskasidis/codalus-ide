# Accessibility

The M7 accessibility pass (2026-10-09) covered keyboard access, screen-reader names, contrast, and not relying on color alone. Avalonia exposes UI Automation (Windows), NSAccessibility (macOS) and AT-SPI (Linux) from the same automation properties.

## In place

- **Keyboard**:
  - Every command is in the command palette (`Ctrl+Shift+P`), and every shortcut can be rebound.
  - `F6` and `Shift+F6` cycle focus through the visible panes.
  - Tool windows, lists and trees are keyboard-operable: Enter opens, Delete removes, F2 renames or edits values.
  - Dialogs have a default button (Enter) and Escape.
- **Debugger**: everything has a keyboard path: `F9` toggles breakpoints, Quick Info (`Ctrl+K Ctrl+I`) shows the data tip at the caret, and the Call Stack, Locals, Watch and Breakpoints windows are lists and trees.
- **Git**: staging and diffs work without the mouse: the Changes lists, Enter for the diff, and Stage Hunk at the caret.
- **Names**: list items, tree items, toolbar buttons, status bar items and tool windows have automation names. Rows describe their state: an Error List row reads "error CS0103: … line 5"; Changes rows say "staged" or "untracked"; breakpoint rows say "disabled"; variable rows read "name = value (type)".
- **Contrast**:
  - Light, Dark and High Contrast themes; the High Contrast theme follows the OS preference when the theme setting is Follow System.
  - Keyboard focus keeps the theme's visible focus indicator.
- **Not color alone**:
  - Diagnostics carry an icon and severity text.
  - Diffs mark lines with `+` and `−` as well as color.
  - Breakpoints differ by shape (circle, hollow, diamond, inner dot).
  - The current statement has an arrow as well as a highlight.
- **Status changes**: build, debug and Git status are status bar items with automation names. Errors also raise notifications.

## Known gaps (`TODO(issue)`)

- **Blame annotations and gutter change markers** are drawn in editor margins and aren't exposed to screen readers. The same information is available through the Changes window and the diff document; a "line history" command that reads the blame of the current line would close the gap.
- **The breakpoint margin** is mouse-only. `F9`, `Ctrl+F9` and the Breakpoints window cover the same actions from the keyboard.
- **The minimap** is mouse-only by design. It is decorative for keyboard users, who scroll with the editor.
- **Screen-reader testing**: the pass checked automation names and keyboard paths in headless tests and by inspection. Testing with NVDA, VoiceOver and Orca by people who use them daily is still to come; issues are welcome.
- **High-contrast colors for debugger and Git adornments** are fixed colors chosen to read on all three themes. They don't come from the theme tokens yet.
