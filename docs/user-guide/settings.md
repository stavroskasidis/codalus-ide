# Settings, themes and keyboard shortcuts

## The window

- **Title bar**: the menu, the search box and the open solution's name. On Windows they're in the window's title bar; drag its empty part to move the window, double-click it to maximize.
- **Search** (`Ctrl+Q`): Feature Search, in the Code Search window: commands, settings and Options pages. Its other tab, Code Search (`Ctrl+T`), finds files, types, members and text ([Code Search](editing.md#code-search)).
- **Toolbar**: back and forward, open, save and save all, undo and redo, the solution configuration and platform, the startup project, and Start (`F5`) and Start Without Debugging (`Ctrl+F5`). While debugging it shows Continue, Break All, Stop, Restart and the step commands.
- **Status bar**: purple without a solution, blue with one, orange while debugging. The right side shows the caret's line and column, indentation, line endings, encoding, language, Git branch and notifications.
- **Layout**: the Solution Explorer is on the right, the Error List and Output at the bottom. Window → Reset Layout brings this layout back.
  - Drag a tab to move a tool window to another group, a window's edge, a floating window or among the documents, where it gets a document tab. A tool window you open goes back where you last closed it, or opens as a tab in the group for its usual place (most at the bottom, with the Output), wherever you moved that group.

## Settings

Tools → Options… lists every setting in a tree of groups (Environment, Text Editor, Projects and Solutions, Debugging, Source Control, Testing, Terminal and Interactive), each with its icon and how many settings it has, and a search box (`@modified` shows only the changed ones). Choosing a group shows all its settings under a header with its name and how many of them are changed. Changes apply immediately: a changed setting is marked at its left edge and with a **Modified** chip, and **Reset** puts it back. An on/off setting is one check box labelled with its name.

- **User settings** live in `settings.json` in the user configuration folder:
  - Windows: `%APPDATA%\Codalus`
  - macOS: `~/Library/Application Support/Codalus`
  - Linux: `~/.config/codalus`
- **Solution settings** live in `.codalus/settings.json` next to the solution and override the user settings for that solution. While a solution is open, **User | Workspace** next to the search box chooses where your changes go; the line under it names the file. Settings that only make sense per user can't be changed on the Workspace side.
- **Editing the JSON**: open it with "Open User Settings (JSON)" in the command palette.
- **Settings for one language**: as in VS Code, a `"[languageId]"` block in either file sets values for that language's documents only, and wins over the general value in any file:

  ```jsonc
  {
    "editor.tabSize": 4,
    "[markdown]": { "editor.wordWrap": true, "editor.tabSize": 2 },
    "[csharp][vb]": { "editor.formatOnSave": true }
  }
  ```

  "Configure Language-Specific Settings…" in the command palette picks a language (the active document's first), adds its block to your settings and opens the file there. The `editor.*` settings, Format on Save and Code Cleanup on Save follow the block; an `.editorconfig` file still overrides the tab size and indentation.

Settings you may want first:

| Setting | What it does |
|---|---|
| Text Editor → Font Family, Font Size, Word Wrap, Minimap, Rulers, Render Whitespace | The editor's look. |
| Text Editor → Format on Type | Formats statements and blocks as you type `;` and `}`. |
| Language Services → Analysis Scope | `openDocuments` (default), `currentProject` or `solution` (also runs analyzers on closed files). Also in the Error List's toolbar. |
| Projects → Restore on Open | Restore packages when a trusted solution opens. |
| Build and Run → Save All Before Build, Run Console Apps in an External Console | |
| Debugging → Just My Code | Step and break only in your own code. |
| Privacy → Save Crash Reports | Off by default. See [Troubleshooting](troubleshooting.md). |

## Themes

Codalus IDE has five **theme families**: Classic, the default, in Blue, Light, Dark and High Contrast, and Harbor, Graphite, Daylight and Ember, each in Light, Dark and High contrast. The [first-run setup](getting-started.md#the-first-run-setup) offers them at the first start, Classic first (Help → Set Up Codalus IDE shows it again):

| Family | What it looks like |
|---|---|
| **Classic** (the default) | The classic IDE's familiar look, the themes from before 0.3: Blue, Light, Dark and High Contrast. |
| **Harbor** | The closest to a classic IDE: a deep navy title bar in Light, tool windows docked with gaps between them, the focused tool window's caption and the status bar in cobalt, a bar across the top of the active tab. |
| **Graphite** | Compact and dense: the title bar and toolbar in the window's color, hairlines between tool windows, a bold title with a teal dot on the focused one, an underline on the active tab, a dark teal status bar. |
| **Daylight** | Flat and airy: hairlines between tool windows, rounded tabs with the active one tinted violet, the Start button filled with the accent. |
| **Ember** | Warm, with floating panels: tool windows and documents as rounded cards with gaps on the window's background, rounded tabs, and a status bar in the window's color with a dot for the state, orange only while debugging. |

Choose one in either place; the IDE switches at once:
- **Tools → Options → Environment → Appearance → Color Theme**: the families as cards, Classic first, each with a picture of it, and a **mode**: Follow System, Light, Dark or High Contrast (Classic also has Blue). Follow System follows the operating system's light or dark setting and its high-contrast setting as they change, within the family.
- **Tools → Color Theme** (`Ctrl+K Ctrl+T`): every family following the system and in each mode, Classic first, then the plugins' themes. Moving through the list shows each theme; Enter keeps it, Escape goes back.

In `settings.json`, `workbench.colorTheme` is a family (`"classic"`, following the system) or a family and its mode (`"ember.dark"`, `"graphite.highContrast"`, `"classic.blue"`). Without the setting, Codalus IDE uses Classic following the system: Classic Light or Dark as your system is light or dark, and Classic High Contrast when its high-contrast setting is on. In 0.3.0 that default was Harbor, so if you never chose a theme, 0.4 shows Classic: pick Harbor once to keep it. A theme you chose stays as it is, and a setting saved by an earlier version (`"dark"`, `"blue"`, `"followSystem"`…) keeps its look: it reads as Classic.

Plugins can add color themes, which are listed with these; a plugin's theme goes over Classic's Light, Dark or High Contrast.

**File icon themes**: Options → Environment → Appearance → File Icon Theme (`workbench.iconTheme`) chooses the icons of files and folders in the Solution Explorer, tabs and pickers: the built-in classic icons (outline pages with a small colored badge, outline folders and project boxes, the default), Material Icon Theme's colored icons (`material`, also built in), or a plugin's. Icon themes use VS Code's format. Their SVG icons are converted the first time a theme is chosen (a moment, once per theme); an SVG icon that uses more than plain shapes with fills shows the built-in icon instead.

**Fonts and Colors** (Tools → Fonts and Colors): change any color over the theme's:
- **Syntax**: comments, strings, keywords, numbers, types, functions and variables, or one semantic token type.
- **The editor**: selection, current line, highlighted references, matching braces, find matches, and the error, warning, info and hint squiggles.
- **The window**: its background, text, borders, accents and the other main colors. The setting below takes any of the theme's `Shell.*` resource keys.

Type a color as `#RRGGBB`, or `#RRGGBBAA` with transparency. **For every theme** applies it to every theme instead of the current one, and Reset goes back to the theme's color. The colors are saved in VS Code's shape, so they can also be edited as JSON:
- `editor.tokenColorCustomizations`: `{ "comments": "#6A9955", "semanticTokenColors": { "property": "#9CDCFE" } }`.
- `workbench.colorCustomizations`: `{ "editor.selectionBackground": "#26496980" }`, alpha last.
- Put either under `"[Dark]"` (a theme's name: `"[Harbor Dark]"`, `"[High Contrast]"`) to apply to that theme only, or under a family's name (`"[Harbor]"`) to apply to its three modes.

## Moving your settings

- **From VS Code** (Tools → Import Settings from VS Code): its user `settings.json` and `keybindings.json` (VS Code, Insiders or VSCodium; the first one found).
  - Settings that are also ours come over with their values, including the two color settings and the ones in language blocks (`"[markdown]": { … }`). Its built-in themes become ours.
  - Key bindings whose commands exist here come over too.
  - You confirm first. The Codalus IDE log (Help → Show Logs) lists each skipped setting and why.
- **From a classic IDE** (Tools → Import a .vssettings File): a file exported with that IDE's Import and Export Settings.
  - The text editor's font and size, and its colors for syntax and editor elements. Colors are added to yours; "automatic" colors stay with the theme.
  - Tab size, tabs or spaces, word wrap and line numbers (All Languages, else C#).
  - Your own keyboard shortcuts, for commands with an equivalent here.
  - Everything else in the file, such as tool window layouts and other components' options, has no counterpart. The log lists what was skipped.
- **Between machines** (Tools → Export Settings, Import Settings): one JSON file with your settings, keyboard shortcuts and saved window layouts.
  - Importing asks which of the three to take. Settings this version doesn't know are skipped and logged, shortcuts replace yours, and layouts are added.

## Window layouts

- **Save and apply** (Window → Save Window Layout, Apply Window Layout, Delete Window Layout): name the current arrangement of tool windows and bring it back later. Default is the layout Codalus IDE starts with. Layouts are kept in the state folder's `layouts.json`.
- **Full Screen** (Window → Full Screen, `Shift+Alt+Enter`, `⌃⌘F` on macOS).
- **Distraction-Free Mode** (Window): only the documents, full screen, without the toolbar or status bar. Run it again to get your tool windows and window back as they were.

## Menus and toolbars

**Tools → Customize** changes the menus, context menus and toolbars:
- **What you can change**: choose a toolbar, a menu or one of its submenus, or the editor's or Solution Explorer's context menu.
- **Add Command…**: put any command there (the same list as the command palette).
- **Remove**: take an item out. A removed button of the standard toolbar shows as removed, and **Restore** brings it back.
- **Move Up, Move Down**: change the order. In menus the items then form one group, without the separators between groups. On the standard toolbar the built-in buttons keep their places and your commands move among themselves.
- **Reset**: the menu as it was.
- **New Toolbar…, Delete Toolbar**: your own toolbars, shown after the standard toolbar's buttons.

Changes apply at once and are the setting `workbench.commandBarCustomizations`, so they belong to the profile and sync with it.

## Profiles

A **profile** is a named set of your settings, keyboard shortcuts, snippets, saved window layouts and the window's current layout. Settings include the keyboard scheme, the theme and the plugins you turned off. File → Profiles:
- **Switch Profile** (or click the profile's name in the status bar): settings, shortcuts, snippets and layouts change at once. If the profiles turn different plugins off, a restart applies that, and the IDE offers it.
- **New Profile**: start from a copy of the current profile or from the defaults, then switch to it.
- **Delete Profile**: any but Default and the active one.
- **Export Profile, Import Profile**: one file, the same as Tools → Export Settings. Importing creates a new profile and switches to it.

The status bar names the active profile unless it's Default. Each profile's files are kept in `profiles/<name>/` in the user configuration folder while another one is active.

## Settings Sync

Settings Sync keeps your settings, keyboard shortcuts, snippets and saved window layouts the same on several machines. There's no Codalus IDE service: you choose where they're kept, and nothing syncs until you turn it on.
- **Turn on** (Settings Sync: Turn On in the command palette): pick a folder that another tool syncs (OneDrive, Dropbox, iCloud Drive, Syncthing…), or give a Git repository's URL. A private repository works, and so does a secret gist's clone URL. Git uses your own credentials.
- **When it syncs**: when Codalus IDE starts, a few seconds after you change something, and with Tools → Settings Sync: Sync Now. A change on one side goes to the other. If both changed since the last sync, you choose **Use Synced** or **Keep Mine**.
- **What stays on each machine**: the sync settings themselves, and the settings you list in Settings Sync → Settings Not Synced (`settingsSync.ignoredSettings`), such as paths that differ between machines. The window's current layout isn't synced either.
- **Profiles**: each profile syncs on its own, to the target set in it.
- **Turn off** (Settings Sync: Turn Off): what was synced stays where it is.

## Display language

Environment → Display Language (`workbench.locale`) chooses the language of menus, commands, settings and windows. Empty uses the operating system's language when Codalus IDE has it. It applies at the next start (File → Restart). `--locale <name>` overrides it for one run.

Translations come from the community; [Translating](../translating.md) explains how to help. Until a language is complete, its missing strings show in English. Two pseudo-languages are for checking translations:
- `qps-ploc` shows every translatable string accented and longer, so untranslated or clipped text stands out.
- `qps-plocm` does the same and lays the window out right to left, as Arabic or Hebrew would.

## Plugins

The Plugins window lists the installed plugins. **Turn Off** keeps a plugin from loading, and **Turn On** brings it back. It takes effect at the next start, and the window says so until then. The list is the setting `plugins.disabled`, so profiles can differ, and a workspace can turn more plugins off for itself in its own settings (the lists add up). File → Restart restarts Codalus IDE on the same solution.

**The Plugin Gallery** (Tools → Browse Plugins…) finds plugins on NuGet: nuget.org, and any other feed in **Plugin Sources** (`plugins.sources`).
- **Installing.** Search, pick a plugin and a version, and choose **Install**. Before it installs you see who publishes it, whether it's signed, and the permissions it declares: the network, starting programs, files outside the workspace, stored secrets. They tell you what it does. They can't be enforced, because a .NET plugin runs with your rights, so install plugins you trust. A new plugin works at once.
- **Signatures.** Packages from nuget.org must be signed (**Require Signed Plugins**, `plugins.requireSignature`: nuget.org's, every source's, or none), checked with `dotnet nuget verify`.
- **Updates.** Once a day Codalus IDE checks the plugins you installed from the gallery. With **Update Plugins Automatically** (`plugins.autoUpdate`, on by default) it downloads the newer versions, which apply at the next start; **Check for Plugin Updates** checks now.
- **Uninstalling.** It also applies at the next start. Until then, the gallery lists the change, with **Cancel This Change** and **Restart Now**.

**Performance.** Each plugin's row in the Plugins window says how long it took to start, how many of its commands ran and the slowest, and how much memory its host uses (the host's, shared by the plugins in it). When a plugin keeps slowing the start (more than two seconds, in three sessions), Codalus IDE says so and offers to turn it off.

**Bisect Plugins** finds the plugin behind a problem. Codalus IDE restarts a few times, each time with half of the remaining plugins turned off (for the workspace, or for you when no folder is open), and asks whether the problem is still there. It ends by naming the plugin, and puts your plugin settings back. Built-in plugins stay on.

## Components

Some tools are downloaded the first time a feature needs them: Node.js, the web language servers, the F# language service (FsAutoComplete and Fantomas), the browser debugger, netcoredbg for remote machines, and converted icon themes. **Tools → Components…** lists them with their version, size and licence. **Download Now** gets one ahead of time, and **Remove** frees its space; it comes back when a feature needs it.

**Offline layout**: on a machine with internet access, **Export Offline Layout…** copies the downloaded components to a folder. On a machine without it, **Import Offline Layout…** copies them in, so no feature tries to download anything. The .NET SDK has its own offline installer, and the layout doesn't include it.

## Keyboard shortcuts

**Keyboard scheme** (setting Environment → Keyboard → Keyboard Scheme):
- **Default**, the classic IDE's keys: `F6` builds, `Ctrl+Q` searches, `Ctrl+,` goes to anything, `F1` is help, `Ctrl+K Ctrl+S` surrounds with a snippet, `Ctrl+F4` closes a document, `Alt+F6` moves between panes, `Ctrl+Alt+L` shows the Solution Explorer and `Ctrl+Alt+O` the Output.
- **VS Code**: `F1` opens the command palette, `Ctrl+,` Settings, `Ctrl+K Ctrl+S` Keyboard Shortcuts, `Ctrl+W` closes a document, `F6` moves between panes.
- **ReSharper**: the default scheme with ReSharper's keys on top: `Ctrl+Shift+R` refactors (quick actions), `Alt+Enter` fixes, `Alt+F7` finds usages, `Ctrl+W` and `Ctrl+Shift+W` expand and shrink the selection, `Ctrl+Alt+Enter` reformats, `Ctrl+E Ctrl+C` cleans up code, `Ctrl+P` shows parameter info, `Alt+End` goes to derived types, `Alt+PageDown`/`PageUp` to the next and previous problem, `Ctrl+Alt+Shift+Up`/`Down` move lines, `Ctrl+Alt+J` surrounds, `Shift+Alt+L` locates the file in the Solution Explorer.
- **Emacs**: the default scheme with Emacs's keys in the editor: `Ctrl+F`/`B`/`N`/`P` move by character and line, `Alt+F`/`B` by word, `Ctrl+A`/`E` to the line's start and end, `Ctrl+V`/`Alt+V` by page, `Alt+<`/`>` to the file's start and end; `Ctrl+Space` sets the mark (moves then select), `Ctrl+G` cancels it; `Ctrl+K` kills to the line's end, `Ctrl+W`/`Alt+W` cut and copy, `Ctrl+Y` pastes, `Ctrl+D` deletes, `Ctrl+/` undoes; `Ctrl+S` searches, `Alt+%` replaces, `Alt+;` comments, `Alt+G G` goes to a line; `Alt+X` runs a command; and `Ctrl+X` chords: `Ctrl+S` saves, `S` saves all, `Ctrl+F` opens a file, `B` switches (Go To All), `K` closes, `H` selects all, `U` undoes, `2`/`3` split, `O` moves to the next pane, `Ctrl+C` quits. There is no kill ring beyond the clipboard ring (`Ctrl+Shift+V`).

Only the chosen scheme's own keys are bound, so menus and the shortcuts list show its keys.

Tools → Open Keyboard Shortcuts lists every command with its shortcut.
- **Change a shortcut**: select a command and press the new key combination. Conflicts are shown.
- **Where changes go**: `keybindings.json` in the user configuration folder (also editable as JSON).
- **Chords**: shortcuts can be two-key chords, like `Ctrl+K Ctrl+D`.
- **Context**: many shortcuts apply only in a context, such as an editor having focus.

## Accessibility

- **Keyboard**: every function is reachable from it. `Alt+F6` and `Alt+Shift+F6` (`F6` and `Shift+F6` in the VS Code scheme) move between panes, and every command is in the command palette and the search box.
- **Screen readers**: lists, trees and buttons have accessible names.
- **Color**: every theme family has a High contrast mode, and Classic has High Contrast; Follow System switches to it when the operating system's high contrast is on. Icons are monochrome there. A family's High contrast mode has black surfaces, white text with at least 7:1 contrast, and white borders around every tool window and field. Diffs mark lines with `+` and `−`, not only with color.

## Updates and network access

**Updates** (Environment → Updates):
- **Updates** (`update.mode`): installed builds (the Windows Setup.exe, the macOS .pkg, the AppImage, winget) download new releases in the background and apply them when the IDE restarts: a status bar item offers **Restart to update**, which reopens your solution (on macOS, an app that an older .pkg installed in `/Applications` asks once for an administrator's password, because that installer left it owned by the system). **notify** only says that a release is available; **off** stops all update traffic. Help → Check for Updates checks right away in every mode.
- **Update Channel** (`update.channel`): stable releases or nightly builds. Empty follows the channel the build came from. Switching from nightly builds to stable releases installs the newest stable release, even when it is older, after you confirm.
- **Other installs**: the `.deb` and `.rpm` update with the system (`apt upgrade`, `dnf upgrade`); the zip, `.app` zip and tar.gz say that a release is out and link to it. Development builds never check.
- **What's New** (Help → What's New): the release notes of the versions you skipped, shown once after an update.

**Network access.** Codalus IDE connects to these places, and each can be turned off:

| What | Where | When | Off |
|---|---|---|---|
| Update checks and downloads | `api.github.com`, GitHub's release downloads | after startup, then every 4 hours | Updates → off |
| NuGet | your NuGet feeds (nuget.org by default) | restore, the package manager | your `NuGet.Config` |
| Node.js and the web language servers | `nodejs.org`, the npm registry | the first web file, after asking | Web → Web Language Servers → off |
| The browser debugger (vscode-js-debug) | GitHub's release downloads | the first browser debug session | don't debug in the browser |
| JSON schemas | `www.schemastore.org` | JSON files without a bundled schema, when downloads are allowed | the bundled schemas only |
| Help for a keyword (F1) | `learn.microsoft.com` | when you press F1 | — |

Codalus IDE itself has no telemetry. The tools it runs connect on their own terms: git talks to your remotes, and the .NET SDK has its own telemetry, which `DOTNET_CLI_TELEMETRY_OPTOUT=1` turns off.
