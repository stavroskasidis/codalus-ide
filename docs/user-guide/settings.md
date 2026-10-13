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

**Fonts and Colors** (Tools → Fonts and Colors): change any color over the theme's:
- **Syntax**: comments, strings, keywords, numbers, types, functions and variables, or one semantic token type.
- **The editor**: selection, current line, highlighted references, matching braces, find matches, and the error, warning, info and hint squiggles.
- **The window**: its background, text, borders, accents and the other main colors. The setting below takes any of the theme's `Shell.*` resource keys.

Type a color as `#RRGGBB`, or `#RRGGBBAA` with transparency. **For every theme** applies it to every theme instead of the current one, and Reset goes back to the theme's color. The colors are saved in VS Code's shape, so they can also be edited as JSON:
- `editor.tokenColorCustomizations`: `{ "comments": "#6A9955", "semanticTokenColors": { "property": "#9CDCFE" } }`.
- `workbench.colorCustomizations`: `{ "editor.selectionBackground": "#26496980" }`, alpha last.
- Put either under `"[Dark]"` (a theme's name) to apply to that theme only.

## Moving your settings

- **From VS Code** (Tools → Import Settings from VS Code): its user `settings.json` and `keybindings.json` (VS Code, Insiders or VSCodium; the first one found).
  - Settings that are also ours come over with their values, including the two color settings. Its built-in themes become ours.
  - Key bindings whose commands exist here come over too.
  - You confirm first. The Daedalus Studio log (Help → Show Logs) lists each skipped setting and why.
- **From a classic IDE** (Tools → Import a .vssettings File): a file exported with that IDE's Import and Export Settings.
  - The text editor's font and size, and its colors for syntax and editor elements. Colors are added to yours; "automatic" colors stay with the theme.
  - Tab size, tabs or spaces, word wrap and line numbers (All Languages, else C#).
  - Your own keyboard shortcuts, for commands with an equivalent here.
  - Everything else in the file, such as tool window layouts and other components' options, has no counterpart. The log lists what was skipped.
- **Between machines** (Tools → Export Settings, Import Settings): one JSON file with your settings, keyboard shortcuts and saved window layouts.
  - Importing asks which of the three to take. Settings this version doesn't know are skipped and logged, shortcuts replace yours, and layouts are added.

## Window layouts

- **Save and apply** (Window → Save Window Layout, Apply Window Layout, Delete Window Layout): name the current arrangement of tool windows and bring it back later. Default is the layout Daedalus Studio starts with. Layouts are kept in the state folder's `layouts.json`.
- **Full Screen** (Window → Full Screen, `Shift+Alt+Enter`, `⌃⌘F` on macOS).
- **Distraction-Free Mode** (Window): only the documents, full screen, without the toolbar or status bar. Run it again to get your tool windows and window back as they were.

## Keyboard shortcuts

**Keyboard scheme** (setting Environment → Keyboard → Keyboard Scheme):
- **Default**, the classic IDE's keys: `F6` builds, `Ctrl+Q` searches, `Ctrl+,` goes to anything, `F1` is help, `Ctrl+K Ctrl+S` surrounds with a snippet, `Ctrl+F4` closes a document, `Alt+F6` moves between panes, `Ctrl+Alt+L` shows the Solution Explorer and `Ctrl+Alt+O` the Output.
- **VS Code**: `F1` opens the command palette, `Ctrl+,` Settings, `Ctrl+K Ctrl+S` Keyboard Shortcuts, `Ctrl+W` closes a document, `F6` moves between panes.
- **ReSharper**: the default scheme with ReSharper's keys on top: `Ctrl+Shift+R` refactors (quick actions), `Alt+Enter` fixes, `Alt+F7` finds usages, `Ctrl+W` and `Ctrl+Shift+W` expand and shrink the selection, `Ctrl+Alt+Enter` reformats, `Ctrl+E Ctrl+C` cleans up code, `Ctrl+P` shows parameter info, `Alt+End` goes to derived types, `Alt+PageDown`/`PageUp` to the next and previous problem, `Ctrl+Alt+Shift+Up`/`Down` move lines, `Ctrl+Alt+J` surrounds, `Shift+Alt+L` locates the file in the Solution Explorer.
- **Emacs**: the default scheme with Emacs's keys in the editor: `Ctrl+F`/`B`/`N`/`P` move by character and line, `Alt+F`/`B` by word, `Ctrl+A`/`E` to the line's start and end, `Ctrl+V`/`Alt+V` by page, `Alt+<`/`>` to the file's start and end; `Ctrl+Space` sets the mark (moves then select), `Ctrl+G` cancels it; `Ctrl+K` kills to the line's end, `Ctrl+W`/`Alt+W` cut and copy, `Ctrl+Y` pastes, `Ctrl+D` deletes, `Ctrl+/` undoes; `Ctrl+S` searches, `Alt+%` replaces, `Alt+;` comments, `Alt+G G` goes to a line; `Alt+X` runs a command; and `Ctrl+X` chords: `Ctrl+S` saves, `S` saves all, `Ctrl+F` opens a file, `B` switches (Go To All), `K` closes, `H` selects all, `U` undoes, `2`/`3` split, `O` moves to the next pane, `Ctrl+C` quits. There is no kill ring beyond the clipboard ring (`Ctrl+Shift+V`).

Only the chosen scheme's own keys are bound, so menus and the shortcuts list show its keys.

Tools → Keyboard Shortcuts lists every command with its shortcut.
- **Change a shortcut**: select a command and press the new key combination. Conflicts are shown.
- **Where changes go**: `keybindings.json` in the user configuration folder (also editable as JSON).
- **Chords**: shortcuts can be two-key chords, like `Ctrl+K Ctrl+D`.
- **Context**: many shortcuts apply only in a context, such as an editor having focus.

## Accessibility

- **Keyboard**: every function is reachable from it. `Alt+F6` and `Alt+Shift+F6` (`F6` and `Shift+F6` in the VS Code scheme) move between panes, and every command is in the command palette and the search box.
- **Screen readers**: lists, trees and buttons have accessible names.
- **Color**: the High Contrast theme raises contrast throughout. Diffs mark lines with `+` and `−`, not only with color.

## Updates and network access

**Updates** (Environment → Updates; M32):
- **Updates** (`update.mode`): installed builds (the Windows Setup.exe, the macOS .pkg, the AppImage, winget) download new releases in the background and apply them when the IDE restarts: a status bar item offers **Restart to update**, which reopens your solution. **notify** only says that a release is available; **off** stops all update traffic. Help → Check for Updates checks right away in every mode.
- **Update Channel** (`update.channel`): stable releases or nightly builds. Empty follows the channel the build came from. Switching from nightly builds to stable releases installs the newest stable release, even when it is older, after you confirm.
- **Other installs**: the `.deb` and `.rpm` update with the system (`apt upgrade`, `dnf upgrade`); the zip, `.app` zip and tar.gz say that a release is out and link to it. Development builds never check.
- **What's New** (Help → What's New): the release notes of the versions you skipped, shown once after an update.

**Network access.** Daedalus Studio connects to these places, and each can be turned off:

| What | Where | When | Off |
|---|---|---|---|
| Update checks and downloads | `api.github.com`, GitHub's release downloads | after startup, then every 4 hours | Updates → off |
| NuGet | your NuGet feeds (nuget.org by default) | restore, the package manager | your `NuGet.Config` |
| Node.js and the web language servers | `nodejs.org`, the npm registry | the first web file, after asking | Web → Web Language Servers → off |
| The browser debugger (vscode-js-debug) | GitHub's release downloads | the first browser debug session | don't debug in the browser |
| JSON schemas | `www.schemastore.org` | JSON files without a bundled schema, when downloads are allowed | the bundled schemas only |
| Help for a keyword (F1) | `learn.microsoft.com` | when you press F1 | — |

Daedalus Studio itself has no telemetry. The tools it runs connect on their own terms: git talks to your remotes, and the .NET SDK has its own telemetry, which `DOTNET_CLI_TELEMETRY_OPTOUT=1` turns off.
