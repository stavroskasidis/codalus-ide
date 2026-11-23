# Compatibility: what 1.x keeps stable

Codalus IDE 1.x keeps what you write and keep working from one version to the next: your settings, the `codalus` command line, and your user data (profiles, window layouts, Settings Sync). The plugin API has the same promise for plugins ([plugins.md](../plugins.md)). The decision is [ADR 0105](../adr/0105-what-1x-keeps-stable.md).

## The promise

Anything that works with one 1.x version works with every later 1.x version, and means the same:

- **Additions only.** A new version may add settings, values, command-line options, commands and files. An earlier version that meets them ignores them.
- **Or a migration on first start.** When something you keep has to change its shape or name, the new version changes your files the first time it starts, and you don't have to do anything.
- **Removals wait for 2.0.** What goes away is deprecated first: it keeps working through 1.x, Options marks it deprecated and says what to use instead, and the release notes list it. It's removed only in 2.0.

The one exception is the netcoredbg debugger ([below](#netcoredbg)).

## Settings

- **Every setting's key**, the IDE's own and those of the plugins that come with it, in your `settings.json`, in a solution's `.codalus/settings.json` and in `"[languageId]"` blocks ([settings.md](settings.md#settings)).
- **Every value a setting accepts**, a list's names included (`debug.adapter`'s `codalus`, `workbench.colorTheme`'s themes), and the keys inside a setting the IDE defines as an object, such as the color names of `workbench.colorCustomizations`. A value that worked keeps doing what it did.
- **A renamed setting** is read under its old name too, so a solution's settings in its repository go on working for everyone, whatever 1.x version they run.

Not promised: a setting's default (a minor version may change it, and says so in its release notes; a value you set yourself is kept), and how Options shows it (title, description, group, order). Settings of third-party plugins are each plugin's own.

A setting a version doesn't know is ignored and left in the file, so `settings.json` can be shared by an older and a newer 1.x. Import Settings skips it and names it in the log.

## The command line

Every form and option `codalus --help` lists ([getting-started.md](getting-started.md#the-command-line)), with its arguments and what it does:

| Form or option | |
|---|---|
| `codalus [path[:line[:column]]]` | Open a solution, project, folder or file |
| `codalus diff <left> <right>`, `codalus merge <local> <remote> <base> <merged>`, `--wait <file>` | Git's difftool, mergetool and editor ([git.md](git.md)) |
| `codalus mcp`, with `--agent-session <token>` and `--folder <path>` | The MCP server for AI agents ([ai.md](ai.md)) |
| `--user-data-dir <dir>`, `--locale <name>`, `--open <file>`, `--new-window`, `--skip-setup`, `--remote <target>` | |
| `--plugin-dir <dir>`, `--enable-proposed-api`, `--debug-plugin-host <file>`, `--dev` | Plugin development ([plugins.md](../plugins.md)) |
| `--list-plugins`, `--show-versions`, `--install-plugin <id[@version]>` or `<file.nupkg>` | Plugins without a window |
| `--version` (`-v`), `--help` (`-h`, `-?`) | |

Exit code 0 means success and anything else a failure. The text printed for people (`--help`, `--version`, `--list-plugins`) may be worded differently. Options `--help` doesn't list are the project's own tools for tests and benchmarks, and the `CODALUS_*` environment variables are mostly for tests and diagnostics: neither is promised.

## User data

Everything below is kept by every later 1.x version, which reads what an earlier one wrote:

| What | Where |
|---|---|
| Settings, keyboard shortcuts, snippets | `settings.json`, `keybindings.json` and `snippets/` in the user configuration folder ([settings.md](settings.md#settings)) |
| Saved window layouts and the window's layout | `layouts.json` and `layout.json` in the state folder ([settings.md](settings.md#window-layouts)) |
| Profiles | `profiles/` in the user configuration folder, one folder for each profile that isn't active ([settings.md](settings.md#profiles)) |
| Settings Sync | the folder or Git repository you sync to, and its `codalus-settings.json` ([settings.md](settings.md#settings-sync)) |
| Exported settings | the files Tools → Export Settings and Export Profile write ([settings.md](settings.md#moving-your-settings)) |

- **Shortcuts keep working**: the command IDs and `when` conditions that `keybindings.json` and your [menu and toolbar changes](settings.md#menus-and-toolbars) name are kept for them.
- **Settings Sync across versions**: machines on different 1.x versions can sync to the same folder or repository. What a newer version adds is ignored by an older one.
- **Going back** to an earlier version isn't promised. An earlier 1.x ignores settings it doesn't know and opens with its default layout when the saved one is from a later version.
- **Not kept**: the cache and log folders, and what the IDE keeps for itself in the state folder (such as the recent solutions list and the language service's caches). A new version may start them afresh.

`--user-data-dir` (or `CODALUS_USER_DATA_DIR`) puts all of these folders under one root ([getting-started.md](getting-started.md#command-line)).

## netcoredbg

The **netcoredbg** debugger (`debug.adapter` set to `netcoredbg`) is the one exception. It's a supported fallback in 1.0 ([debugging.md](debugging.md#the-codalus-debugger)). A 1.x minor version may deprecate it, with a warning in Options and the release notes, and a later 1.x minor may remove it once the Codalus debugger passes the same tests on every operating system. From then on `netcoredbg` in your settings means the Codalus debugger, and the setting that only netcoredbg uses (**Offer the Codalus debugger for edits**) goes with it.
