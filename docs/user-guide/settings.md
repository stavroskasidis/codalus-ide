# Settings, themes and keyboard shortcuts

## Settings

File → Settings (`Ctrl+,`) lists every setting by category, with a search box. Changes apply immediately.

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

File → Color Theme (`Ctrl+K Ctrl+T`): Light, Dark, High Contrast, or Follow System.

## Keyboard shortcuts

File → Keyboard Shortcuts (`Ctrl+K Ctrl+S`) lists every command with its shortcut.
- **Change a shortcut**: select a command and press the new key combination. Conflicts are shown.
- **Where changes go**: `keybindings.json` in the user configuration folder (also editable as JSON).
- **Chords**: shortcuts can be two-key chords, like `Ctrl+K Ctrl+D`.
- **Context**: many shortcuts apply only in a context, such as an editor having focus.

## Accessibility

- **Keyboard**: every function is reachable from it. `F6` and `Shift+F6` move between panes, and every command is in the command palette.
- **Screen readers**: lists, trees and buttons have accessible names.
- **Color**: the High Contrast theme raises contrast throughout. Diffs mark lines with `+` and `−`, not only with color.
