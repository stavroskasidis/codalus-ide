# Settings, themes and keyboard shortcuts

## The window

- **Title bar**: the menu, the search box and the open solution's name. On Windows they're in the window's title bar; drag its empty part to move the window, double-click it to maximize.
- **Search** (`Ctrl+Q`): commands, settings, files, and the solution's types and members, in one list.
- **Toolbar**: back and forward, open, save and save all, undo and redo, the solution configuration and platform, the startup project, and Start (`F5`) and Start Without Debugging (`Ctrl+F5`). While debugging it shows Continue, Break All, Stop, Restart and the step commands.
- **Status bar**: purple without a solution, blue with one, orange while debugging. The right side shows the caret's line and column, indentation, line endings, encoding, language, Git branch and notifications.
- **Layout**: the Solution Explorer is on the right, the Error List and Output at the bottom. Window → Reset Layout brings this layout back.

## Settings

Tools → Options… lists every setting in a tree of groups (Environment, Text Editor, Projects and Solutions, Debugging, Source Control, Testing, Terminal and Interactive), with a search box. Choosing a group shows all its settings. Changes apply immediately.

- **User settings** live in `settings.json` in the user configuration folder:
  - Windows: `%APPDATA%\Daedalus Studio`
  - macOS: `~/Library/Application Support/Daedalus Studio`
  - Linux: `~/.config/daedalus-studio`
- **Solution settings** live in `.daedalus/settings.json` next to the solution and override the user settings for that solution.
- **Editing the JSON**: open it with "Open User Settings (JSON)" in the command palette.

Settings you may want first:

| Setting | What it does |
|---|---|
| Text Editor → Font Family, Font Size, Word Wrap, Minimap, Rulers, Render Whitespace | The editor's look. |
| Text Editor → Format on Type | Formats statements and blocks as you type `;` and `}`. |
| Language Services → Analysis Scope | `openDocuments` (default) or `solution` (also runs analyzers on closed files). |
| Projects → Restore on Open | Restore packages when a trusted solution opens. |
| Build and Run → Save All Before Build, Run Console Apps in an External Console | |
| Debugging → Just My Code | Step and break only in your own code. |
| Privacy → Save Crash Reports | Off by default. See [Troubleshooting](troubleshooting.md). |

## Themes

Tools → Color Theme (`Ctrl+K Ctrl+T`): Blue, Light, Dark, High Contrast, or Follow System (Light or Dark, as the operating system is set).

## Keyboard shortcuts

**Keyboard scheme** (setting Environment → Keyboard → Keyboard Scheme):
- **Default**, the classic IDE's keys: `F6` builds, `Ctrl+Q` searches, `Ctrl+,` goes to anything, `F1` is help, `Ctrl+K Ctrl+S` surrounds with a snippet, `Ctrl+F4` closes a document, `Alt+F6` moves between panes, `Ctrl+Alt+L` shows the Solution Explorer and `Ctrl+Alt+O` the Output.
- **VS Code**: `F1` opens the command palette, `Ctrl+,` Settings, `Ctrl+K Ctrl+S` Keyboard Shortcuts, `Ctrl+W` closes a document, `F6` moves between panes.

Tools → Keyboard Shortcuts lists every command with its shortcut.
- **Change a shortcut**: select a command and press the new key combination. Conflicts are shown.
- **Where changes go**: `keybindings.json` in the user configuration folder (also editable as JSON).
- **Chords**: shortcuts can be two-key chords, like `Ctrl+K Ctrl+D`.
- **Context**: many shortcuts apply only in a context, such as an editor having focus.

## Accessibility

- **Keyboard**: every function is reachable from it. `Alt+F6` and `Alt+Shift+F6` (`F6` and `Shift+F6` in the VS Code scheme) move between panes, and every command is in the command palette and the search box.
- **Screen readers**: lists, trees and buttons have accessible names.
- **Color**: the High Contrast theme raises contrast throughout. Diffs mark lines with `+` and `−`, not only with color.
