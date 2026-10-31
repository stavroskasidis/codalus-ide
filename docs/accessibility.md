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
- **Virtualized trees** (M57): the Solution Explorer, Git Changes, the Document Outline, the Git Repository tree, a commit's changes and Compare show a tree as a list of its visible rows (`TreeRowsList`), and expose it as a tree: the list is a `Tree`, each row a `TreeItem` whose folder has the ExpandCollapse pattern (Expand and Collapse run the view's own command, as `Right` and `Left`), and the row's name is its name alone (M56 added ", expanded" or ", collapsed" to it). The peer also knows each row's level and its place among its siblings; what reaches each platform through Avalonia 12.1:

  | | Role | Expanded / collapsed, Expand and Collapse | Level | Position and size of set |
  |---|---|---|---|---|
  | Windows (UI Automation) | TreeItem in a Tree | ExpandCollapse pattern | not passed (Avalonia doesn't answer `Level`) | not passed (nor `PositionInSet`, `SizeOfSet`) |
  | macOS (NSAccessibility) | tree item | expanded state and the expand and collapse actions | not passed (Avalonia's native peer has no disclosure level) | not passed |
  | Linux (AT-SPI) | tree item in a tree | `expandable`, `expanded` and `collapsed` states, the actions | not passed (Avalonia's `level` attribute is a heading's: it would make the row a heading) | `posinset` and `setsize` attributes |
- **Contrast**:
  - Blue, Light, Dark and High Contrast themes; the High Contrast theme follows the OS preference when the theme setting is Follow System.
  - Keyboard focus keeps the theme's visible focus indicator.
- **Not color alone**:
  - Diagnostics carry an icon and severity text.
  - Diffs mark lines with `+` and `−` as well as color.
  - Breakpoints differ by shape (circle, hollow, diamond, inner dot).
  - The current statement has an arrow as well as a highlight.
- **Status changes**: build, debug and Git status are status bar items with automation names. Errors also raise notifications.

## Known gaps (`TODO(issue)`)

- **Blame annotations and gutter change markers** are drawn in editor margins and aren't exposed to screen readers. The same information is available through the Changes window and the diff document; file and line history (Git depth) show the same history as a list, but a command that reads the blame of the current line aloud would close the gap.
- **The breakpoint margin** is mouse-only. `F9`, `Ctrl+F9` and the Breakpoints window cover the same actions from the keyboard.
- **The minimap** is mouse-only by design. It is decorative for keyboard users, who scroll with the editor.
- **The virtualized trees' level and place** reach only AT-SPI's position and size (above): Avalonia 12.1's UI Automation and NSAccessibility peers have no level, position or size to report. Narrator, VoiceOver and Orca haven't been tried on these trees: how each reads the flat list's tree items (and what Narrator works out for the level by itself) is still to check by hand.
- **Screen-reader testing**: the pass checked automation names and keyboard paths in headless tests and by inspection. Testing with NVDA, VoiceOver and Orca by people who use them daily is still to come; issues are welcome.
- **High-contrast colors for debugger and Git adornments** are fixed colors chosen to read on all three themes. They don't come from the theme tokens yet.
