# 0084. Opt-in OS integration

- **Status:** accepted (M51, 0.8.0, 2026-10-26)
- **Date:** 2026-10-26
- **Deciders:** maintainer (M51 item 4's decisions of 2026-10-24: offered in the first-run setup and in Options, off until chosen, removable; folders and file types as two choices; the user's classes on Windows, LaunchServices on macOS, the XDG folders on Linux)

## Context

Until 0.8, Codalus IDE added nothing to the operating system beyond what its installers do (ADR 0028): a Start menu entry and the `codalus` command on Windows, the app bundle on macOS, and on Linux the packaged `codalus.desktop`, which declares `text/x-csharp` and `application/x-csproj`. There was no "Open with Codalus IDE" on a folder, and double-clicking a solution never opened it. The classic IDE and VS Code both offer these, VS Code as installer check boxes. The maintainer decided that Codalus IDE offers them after installing instead, in the first-run setup and in Tools → Options, off until the user chooses them, and removable.

Three things shape the design:
- **Each OS has its own mechanism, and its own limits.** Windows reads verbs and ProgIDs from `HKCU\Software\Classes` without administrator rights, but forbids setting a file type's default programmatically (the user's choice is hashed). macOS needs the document types declared in the app bundle's `Info.plist` and lets an app make itself the default through LaunchServices, but can't unset a default. Linux has no single file manager: GNOME Files, Dolphin, Nemo and Thunar each read their own actions, and file types need a MIME type, a desktop entry and the default in `mimeapps.list`.
- **"Removable" means exactly what was written.** Turning a choice off must not remove another app's entries, a script of the user's in the same folder, or a default the user chose themselves.
- **Tests never touch the machine** (CLAUDE.md): every root must be injectable.

## Decision

**The registration is the source of truth; there is no setting.** A setting (`os.*`) would drift from the system as soon as the user removes an entry by hand, reinstalls, or runs another copy (a development build) that registers itself. Instead `OsIntegration.GetStatus` reads the registry or the files back each time, and the toggles in Options and the setup act on them. Options shows the two choices as items of its own kind (`OsIntegrationSettingViewModel`, not in `settings.json`, no key shown) in Environment → System Integration. Each reads as:
- **Registered**: every entry is there and starts this copy;
- **NotRegistered**: none is;
- **Partial**: some are (one was removed, a file manager was installed since);
- **OtherCopy**: all are, for another copy of Codalus IDE;
- **Unavailable**: it can't be offered here (macOS file types outside the app bundle).

The check box is on, off, or undetermined (partial, another copy's); clicking an undetermined one removes what's there, and clicking again registers it all for this copy (Avalonia's two-state toggle goes from undetermined to off).

**Ownership.** An entry is Codalus IDE's by its name (registry keys `Codalus.*`, the Thunar action's `unique-id`) or by the marker `codalus-os-integration` in a file it wrote. A file at the same path without the marker is the user's: it's never written over or removed (and the feature reads as partial). Folders and registry keys emptied by a removal go too, up to the root they were created in; nothing else is pruned.

**What's written** (`src/Codalus.Workbench/Setup/OsIntegration*.cs`, one class per OS behind `OsIntegration`):

| | Folders | File types (`.sln`, `.slnx`, `.slnf`, `.csproj`, `.vbproj`, `.fsproj`) |
|---|---|---|
| Windows (`WindowsOsIntegration`, root `HKCU\Software\Classes`) | `Directory\shell\Codalus.OpenFolder` (`"<codalus>" "%1"`) and `Directory\Background\shell\Codalus.OpenFolder` (`"%V"`), with the app's icon. | A ProgID per type (`Codalus.sln`: its name, `DefaultIcon`, `shell\open\command`), listed in the type's `OpenWithProgids` (a `REG_NONE` value). The type's own default value is never touched: Codalus IDE is a handler, Windows asks once, and the UI and guide say so. `SHChangeNotify(SHCNE_ASSOCCHANGED)` after each change. |
| macOS (`MacOsIntegration`) | A Quick Action, `~/Library/Services/Open with Codalus IDE.workflow` (an Automator Run Shell Script workflow for `public.folder` in the Finder) that runs `open -n -a <bundle> --args <folder>`; `pbs -update` after. | The bundle declares the types (`CFBundleDocumentTypes` with `LSHandlerRank` Alternate, and `UTImportedTypeDeclarations` `io.github.stavroskasidis.codalus.<ext>`, in `eng/packaging/macos/Info.plist`), so packaging alone makes Codalus IDE an option, never the default. The opt-in runs `lsregister -f <bundle>`, then `LSSetDefaultRoleHandlerForContentType` per type. The types it took, with the app that was the default before, are kept in `~/Library/Application Support/Codalus/os-integration.txt` (per user, not per profile); turning it off gives each back to that app, or to another app that opens it. A type counts as Codalus IDE's only when it's in that record, so a default the user chose in the Finder isn't taken away. |
| Linux (`LinuxOsIntegration`, roots `$XDG_DATA_HOME`, `$XDG_CONFIG_HOME`) | A Nautilus script (`nautilus/scripts/Open with Codalus IDE`), a Dolphin service menu (`kio/servicemenus/codalus-open-folder.desktop`, executable for KDE 6), two Nemo actions (a folder; a folder's empty space), and a Thunar action in `Thunar/uca.xml` only when that file exists (Thunar writes its default actions into it the first time it runs; creating it would lose them). | `mime/packages/codalus-dotnet.xml` (`application/x-sln`, `-slnx`, `-slnf`, `-csproj`, `-vbproj`, `-fsproj`, the names MonoDevelop used and `codalus.desktop` already uses), `applications/codalus-solutions.desktop` (`NoDisplay`, `MimeType=`), `update-mime-database` and `update-desktop-database` when installed, then `xdg-mime default` (or `mimeapps.list` directly without it). Removal takes Codalus IDE's entry off those types' lines in `mimeapps.list` (and the legacy `applications/mimeapps.list`), keeping the user's other choices; a default chosen later stays. |

The tools (`xdg-mime`, `update-*`, `lsregister`, `pbs`) are the user's, run one process per operation through `ProcessRunner`, and their failure doesn't fail the registration: they rebuild caches the desktop also rebuilds by itself.

**The command is this copy's** (`OsLauncher`): the installed app on Windows is Velopack's launcher next to `current\` (which an update replaces); an AppImage is its file (`$APPIMAGE`), not the folder it mounts; `dotnet codalus.dll` keeps the dll; a development build registers the program that runs; `--user-data-dir` (or `CODALUS_USER_DATA_DIR`) goes along, so a portable profile opens itself. Quoting follows each format: `CommandLineToArgvW` on Windows, POSIX single quotes in scripts, the freedesktop `Exec` rules in desktop entries and Nemo actions.

**Opening what the OS hands over.** `codalus <folder or solution>` now asks the running window of its user-data folder first (a waiting `openPath` request over `SingleInstance`): a window with nothing open takes it and comes to the front, one with that same solution comes to the front, and otherwise the new process opens a window of its own, as before. `--new-window` skips the question. On macOS the Finder sends documents to the running app as an Apple Event, which arrives through Avalonia's `IActivatableLifetime` (`FileActivatedEventArgs`) and goes the same way (a new process with `--new-window` when a solution is open). Explorer passes a drive's root as `"C:\"`, which Windows' quoting reads back as `C:"`; the parser turns that back into `C:\`.

**Packaging.** The packaged Linux `codalus.desktop` keeps its two types and gains none: a desktop entry that declares a type nothing else opens becomes its default implicitly, which is what the opt-in is for. The opt-in's own desktop entry (`codalus-solutions.desktop`) is separate, so removing it never touches the package's.

**Tests** run every backend on every OS through its injectable roots: a temporary `HKCU\Software\Codalus.Tests\<guid>` key (Windows only, deleted after), a temporary Services folder and bundle with a stand-in LaunchServices, and temporary XDG folders with stand-in tools. The shell's fixture gives every headless test a Linux layout under its user data folder with no tools, so no shell test can register anything on the machine.

## Consequences

- Nothing changes for anyone who doesn't turn these on; both are off at a first start, after an upgrade, and in automation.
- Windows may ask once which app opens a solution: that is Windows' rule, and the description says so.
- Turning macOS file types off can't make "no app" the default again; a type no other app opens stays Codalus IDE's (as it would be for any app that declares it).
- Another copy registering itself replaces the first copy's entries; Options says when they belong to another copy.
- `TODO(issue)`: the real Finder Quick Action, LaunchServices defaults and Apple Event opening need validating on a Mac (the workflow is written by hand to Automator's format and unit-tested only through its files); Dolphin, Nemo and Thunar on real desktops; Windows 11's top-level context menu (it needs a packaged app's `IExplorerCommand`; the verb shows under Show more options); Flatpak, whose sandbox can't write these.
