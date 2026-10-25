# 0019. A classic IDE's look and feel

- **Status:** accepted (M29 plan, 2026-10-10; the maintainer's request of the same day)
- **Date:** 2026-10-10
- **Deciders:** maintainer

## Context

Up to M12 the shell looked like VS Code: an activity-style left column, a colored status bar, a command palette as the way in, VS Code's keys. The maintainer wants it closer to the classic IDE, so that a classic IDE user feels at home. The product rule still applies: no classic IDE branding in the product, so nothing in the UI, the setting values or the theme names names the classic IDE. The decisions to make:
- where the menu, the search box and the solution's name go;
- what the toolbar holds and how it stays in step with the solution, the startup project and the debugger;
- how themes, the document well and the status bar's states are colored;
- how a different default keyboard layout coexists with VS Code's, and with user bindings;
- how the settings become an Options tree without changing the settings model.

## Decisions

**A title row with the menu, the search box and the solution's name**
- On Windows the client area extends into the title bar (`ExtendClientAreaToDecorationsHint`, 32 px). The row holds the app icon, the menu, the search box and the solution's name. Its empty part drags the window, a double click maximizes it, and 138 px stay free for the system's caption buttons.
- On Linux and macOS the system keeps its title bar, and the same row sits below it. macOS keeps the global menu bar, so the row there holds only the search box and the solution's name.
- **The search box** (Ctrl+Q, `workbench.action.featureSearch`) searches commands, settings, files and the solution's types and members in one list, through the existing quick pick. A command runs; a setting opens in Settings, filtered to it; a file or symbol opens. The terminal leaves Ctrl+Q to the IDE.

**The standard toolbar**
- Under the title row: back and forward, open, save and save all, undo and redo, the solution configuration and platform, the startup project, Start (with the project and launch profile) and Start Without Debugging.
- While debugging, the same toolbar shows Continue (when stopped), Break All, Stop, Restart and the three step commands.
- `ToolbarViewModel` holds no logic of its own. The shell pushes the solution, the startup project and the debug state into it, and choices go back through callbacks to the same services the commands use.
- The configuration, the startup project and its profile left the status bar for the toolbar. The status bar shows a running program only while it runs, as a stop button.

**Themes as tokens**
- Blue (new), Light and Dark follow the classic IDE's palettes. Blue is a theme variant that inherits Light, so the Fluent controls and the editor, grammar and terminal colors use Light's.
- New tokens: the title bar, toolbar, search box, tabs and active tool window caption. Dock's own brushes (active tab, tab strip, surfaces, active caption) are mapped onto them in each theme.
- Dock uses its foreground brushes in more places than their names say: the inactive-tab and selected-tool-tab foregrounds also color other tabs. Only the active-tab brushes are overridden; Blue's dark inactive tabs aren't reproduced.
- **The status bar's state:** purple without a solution, the accent color with one, orange while debugging. The message area says "Ready" when there is nothing else to say.

**Keyboard schemes as a context key**
- Setting `keyboard.scheme`: `default` (the classic IDE's keys) or `vscode`. It sets the `keyboardScheme` context key. The default bindings that differ carry `when` clauses on it: F6 builds or moves between panes, F1 is help or the palette, Ctrl+, is Go To All or Settings, Ctrl+K Ctrl+S is Surround With or Keyboard Shortcuts, and Ctrl+W, Ctrl+Q, Alt+F6, Ctrl+Alt+L and Ctrl+Alt+O.
- One binding table, no second keymap file. User bindings (`keybindings.json`) apply on top of either scheme, as before.

**Layout, Solution Explorer and Options**
- The Solution Explorer opens on the right by default. Saved layouts are kept; Reset Layout applies the new default.
- The Solution Explorer gains a toolbar (Home, Sync with Active Document, Collapse All, Properties) and a search box (Ctrl+;) that filters the tree to the matching items and their parents.
- The settings' categories become a tree of groups (Environment, Text Editor, Projects and Solutions, Debugging, Source Control, Testing, Terminal and Interactive, Other). A group shows all its categories' settings. Tools → Options… opens it, and the theme and keyboard commands moved from the File menu to Tools. Settings stay in `settings.json`.
- **Density:** 12 px (9 pt) UI text, the system UI font, Cascadia Mono in the editor as before.

## Consequences

- A classic IDE user finds the menu, toolbar, search, keys, layout and colors where they expect them. VS Code users can switch the keyboard scheme back.
- Tests and the smoke test (step `shell.chrome`, with a screenshot per theme) cover the toolbar's state, the status bar's states, the search box, the schemes, the Options tree and the Blue theme.
- `TODO(issue)`, not in M29:
  - **Tabs:** Blue's dark inactive tabs (needs Dock's tab template, not just its brushes); pinned tabs; tab colors by project; the preview tab on the right.
  - **Tool windows:** inactive caption colors per theme; auto-hidden windows as side tabs are Dock's as they were.
  - **Solution Explorer:** file-type icons from an image library with a compatible license; Show All Files.
  - **Customization:** customizable toolbars (M23).
  - **Accessibility:** a contrast audit of Blue against WCAG AA with a tool, beyond picking the classic IDE's colors.
