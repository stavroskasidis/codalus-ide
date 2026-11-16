# 0105. What 1.x keeps stable: settings, the command line and user data

- **Status:** accepted (2026-11-15, D57 in the [roadmap](../roadmap.md#decision-register)); written down in [M65](../roadmap.md#m65--10-hardening-and-the-100-release), its scope items 4 and 5
- **Date:** 2026-11-16
- **Deciders:** maintainer (the questions of 2026-11-15: every answer below is theirs)
- **Amends:** [ADR 0094](0094-the-codalus-debugger-by-default.md) (netcoredbg stays a supported fallback through 1.0, not "one release")

## Context

Before 1.0 a minor version could change anything. The plugin API has been 1.0 since M22a, and [ADR 0058](0058-plugin-api-1-0.md) says what that means for it: minor versions only add, and a member is removed only in a major version, after `[Obsolete]` (`CODALUS0001`) for at least two minor releases. Nothing said the same of what users themselves write and keep: the settings in `settings.json`, the `codalus` command in their scripts and in git's configuration, and the profiles, layouts and sync targets they built up.

D57 (2026-11-15) made Codalus IDE's 1.0 a promise about those too, with one exception the maintainer named: netcoredbg, which ADR 0094 kept "for one release" as the fallback debugger.

What there is to promise, as of 0.14.0:

- **Settings.** About 150 settings in the shell and more in the built-in plugins, each a `SettingDescriptor` (a flat, dotted key such as `workbench.colorTheme`, a type, a default and, for some, an enum of values) registered by the shell's `BuiltIns*.cs` and the built-in plugins' `codalus-plugin.json` (`contributes.settings`). They are read from the user's `settings.json` and a solution's `.codalus/settings.json`, and from `"[languageId]"` blocks in either. `SettingsService` ignores a key it doesn't know and a value its schema refuses (and reports them in its diagnostics), and writes through `JsoncEditor`, which keeps the rest of the file, so a key a later version added survives in a file an earlier version writes.
- **The command line**, parsed by `ShellOptions` (`src/Codalus.Shell/ShellOptions.cs`) and listed by `codalus --help`: `codalus [path[:line[:column]]]`, `codalus diff`, `codalus merge`, `--wait`, `codalus mcp` (with `--agent-session` and `--folder`), `--user-data-dir`, `--locale`, `--plugin-dir`, `--enable-proposed-api`, `--debug-plugin-host`, `--dev`, `--open`, `--new-window`, `--skip-setup`, `--list-plugins` (with `--show-versions`), `--install-plugin`, `--remote`, `--version` (`-v`) and `--help` (`-h`, `-?`). Five more are parsed but not listed: `--bench`, `--exit-on`, `--screenshot` and `--smoke-test` (the benchmarks and the smoke test) and `--update-now` (`eng/test-update.sh`).
- **User data**, in the folders `AppPaths` resolves (each OS's own, or one root under `--user-data-dir` or `CODALUS_USER_DATA_DIR`):
  - the configuration folder: `settings.json`, `keybindings.json` (shortcuts naming command IDs, with `when` clauses) and `snippets/`;
  - the state folder: `layouts.json` (saved window layouts) and `layout.json` (the window's current layout, a `LayoutSnapshot` with a `Version`);
  - profiles (`ProfileStore`): `profiles/active` and, for each profile not active, `profiles/<name>/` with the same files and `window-layout.json`;
  - Settings Sync's targets (`SettingsSync`): a folder or a Git repository holding one `codalus-settings.json`, `{ "codalusSettings": 1, "settings", "keybindings", "layouts", "snippets" }`; Tools → Export Settings writes the same shape (with `profile` and `windowLayout`, without `snippets`).
- **One migration already exists.** `LayoutSnapshot.Upgraded` moves a version 1 `layout.json` to version 2 when the IDE starts: everything the user arranged stays, but the windows whose default place changed. A layout of a version it doesn't know isn't restored, and the default layout shows instead.

## Decision

**1.x keeps settings, the command line and user data working, alongside the plugin API (ADR 0058).** A file or a command line that works with one 1.x version works with every later 1.x version, and means the same.

| | What 1.x keeps |
|---|---|
| **Settings** | Every key of a built-in setting (the shell's and the built-in plugins'), in user and solution settings and in language blocks; every value it accepts, an enum's names included; the keys inside an object setting that the IDE defines, such as `workbench.colorCustomizations`' color names; and what each value does. A value that worked keeps working |
| **The command line** | Every form and option `codalus --help` lists, with its arguments and what it does; exit code 0 for success and another for failure |
| **User data** | The configuration folder's `settings.json`, `keybindings.json` and `snippets/`; the state folder's `layouts.json` and `layout.json`; profiles and their folders; Settings Sync's folders and repositories, and files written by Export Settings. A later 1.x reads what an earlier 1.x wrote. The command IDs and `when` context keys that `keybindings.json` and `workbench.commandBarCustomizations` name are kept for it |

**Within 1.x a change is additive or migrated on first start.**
- **Additive:** a new setting, value, option, command or file, or a new property in a file. An earlier 1.x that meets it ignores it: settings it doesn't know stay in the file unread, and an import skips them and logs them, as today.
- **Migrated:** a change of shape or of name is done to the user's own files the first time the new version starts, as `LayoutSnapshot.Upgraded` does to `layout.json`; a versioned file (`layout.json`, `codalus-settings.json`'s `codalusSettings`) gets the next version. A renamed setting key goes on being read under its old name too, from solution settings, which live in other people's repositories and aren't rewritten, and from synced documents written by an earlier 1.x. A Settings Sync target is shared by machines on different 1.x versions, so its document only gains properties within 1.x; a change that an earlier 1.x couldn't read waits for 2.0.

**A removal is deprecated first, and made only in 2.0.**
- A deprecated setting or value keeps working through 1.x. Options marks it deprecated and says what to use instead, and the release notes of the minor that deprecates it list it. A deprecated command-line option keeps working, and `--help` and the release notes say what to use instead.
- No setting is deprecated yet, so the setting schema has no marker for it; the first deprecation adds one to `SettingDescriptor`, which Options shows.

**The one exception: `debug.adapter`'s `netcoredbg` value.** It stays a supported fallback in 1.0, superseding ADR 0094's "one release". A 1.x minor may deprecate it, with a warning in Options and in the release notes, and a later 1.x minor may remove it once the parity suites pass under the Codalus debugger on every OS (D48), with `eng/get-netcoredbg.sh`, its copy in `RemoteDebuggers`, its workarounds in `DebugAdapter` (ADR 0094's list) and the settings that exist only for it (`debug.codalusOffer`). After that a `settings.json` or a `CODALUS_DEBUG_ADAPTER` that says `netcoredbg` gets the Codalus debugger: a value neither debugger knows is the default (ADR 0094, M58b).

**Not in the promise:**
- the options `--help` doesn't list (`--bench`, `--exit-on`, `--screenshot`, `--smoke-test`, `--update-now`), which are the project's own tools;
- the `CODALUS_*` environment variables (`docs/environment-variables.md`): most are for tests and diagnostics;
- defaults: a minor may change a setting's default, and says so in its release notes; a value you set yourself is kept;
- what Options shows of a setting (its title, description, group and order), and the text the command line prints for people (`--help`, `--version`, `--list-plugins`);
- the cache and log folders, and the state the IDE keeps for itself beside the layouts (such as `recent-solutions.json`, and the language host's caches): a later version may start them afresh;
- settings of third-party plugins, which are each plugin's own;
- going back: an earlier 1.x ignores what a later one added, and doesn't restore a layout of a later version, but nothing is promised for running an earlier version on later data.

## Consequences

- A change to a settings key or enum, to `ShellOptions`, or to the files above is reviewed against this ADR, as `PublicAPI.txt` is for the plugin API. No test enforces it yet: settings and options are registered in code, not in a frozen list.
- The user guide's [compatibility page](../user-guide/compatibility.md) states the promise for users, and 1.0's release text (M65's item 9) names it.
- netcoredbg's removal, when it comes, is a 1.x change that two release notes announce, not a 2.0 one; the `debug-netcoredbg` test shard runs for as long as the value is supported.
- 2.0 is where the deprecated settings, values and options go, each listed in its release notes.
