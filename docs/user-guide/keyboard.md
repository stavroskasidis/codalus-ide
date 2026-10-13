# Keyboard shortcuts on each operating system

Daedalus Studio uses a classic IDE's shortcuts by default (Tools → Options → Keyboard → Keyboard Scheme offers VS Code's, and ReSharper's and Emacs's over the default; see [Settings](settings.md#keyboard-shortcuts)). `primary` in the key bindings is ⌘ on macOS and Ctrl elsewhere; `ctrl` is always the physical Control key. Your own bindings (Tools → Open Keyboard Shortcuts) override any default.

Some shortcuts belong to the operating system or the desktop, which takes them before any application sees them. The defaults avoid those, and where a classic shortcut collides, the OS gets a different default (M34a, 2026-10-12). `UxPassTests.No_default_binding_takes_a_shortcut_the_OS_keeps` keeps the list below checked.

## Completion

| Command | Windows | Linux | macOS |
|---|---|---|---|
| Trigger Suggest (List Members) | Ctrl+Space, Ctrl+J | Ctrl+Space, Ctrl+J | ⌥⎋ (the one the menus show), ⌃Space (the Control key), ⌃J |
| Parameter Info | Ctrl+Shift+Space | Ctrl+Shift+Space | ⌃⇧Space |
| Complete Word | Ctrl+Alt+Space | Ctrl+Alt+Space | ⌃. |

- **macOS:** ⌘Space is Spotlight. ⌃Space (the Control key, as in Xcode and VS Code) works only with one input source: with two or more, macOS's "Select the previous input source" takes it first (System Settings → Keyboard → Keyboard Shortcuts → Input Sources; on by default), and the app never sees it (seen on the maintainer's Mac: U.S. → Greek). So the menus show ⌥⎋, which always reaches the app; ⌃Space and ⌃J work too where macOS leaves them alone, or turn the input-source shortcut off. ⌘⌥Space is Finder search, so Complete Word is ⌃. there.
- **Linux:** IBus and Fcitx switch input methods with Ctrl+Space (older setups) or Super+Space. If Ctrl+Space doesn't reach the editor, use Ctrl+J, or change the input method's trigger (`ibus-setup` → General → Next input method).
- **Windows:** the Chinese, Japanese and Korean IMEs can toggle with Ctrl+Space; Ctrl+J works there.

## Edit's clipboard keys

Undo, Redo, Cut, Copy, Paste and Select All belong to the text controls (Ctrl+Z, Ctrl+Y, Ctrl+X, Ctrl+C, Ctrl+V, Ctrl+A; on macOS ⌘Z, ⇧⌘Z, ⌘X, ⌘C, ⌘V, ⌘A). The Edit menu shows them, and on macOS the global menu carries them as key equivalents, as Mac apps do, with Edit starting with them. They act on the focused text box, editor or terminal; elsewhere (the Solution Explorer) they run what the same keys do there.

## Other shortcuts that differ by OS

| Command | Windows | Linux | macOS | Why |
|---|---|---|---|---|
| Add Caret Above / Below | Ctrl+Alt+↑ / ↓ | Shift+Alt+↑ / ↓ | ⌃⌥↑ / ↓ | GNOME and KDE switch workspaces with Ctrl+Alt+arrows |
| Performance Profiler | Alt+F2 | Shift+Alt+F2 | ⌥F2 | Alt+F2 is the GNOME and KDE run dialog |
| Diagnostic Tools | Ctrl+Alt+F2 | Ctrl+Shift+F2 | ⌃⌥F2 | Ctrl+Alt+F1…F12 switch Linux virtual terminals |
| Apply Code Changes (Hot Reload) | Alt+F10 | Shift+Alt+F10 | ⌥F10 | Alt+F10 maximizes windows in GNOME |
| Task Runner Explorer | Ctrl+Alt+Backspace | Ctrl+Shift+Alt+R | ⌃⌥⌫ | Ctrl+Alt+Backspace stops the X server where "zap" is enabled |
| Toggle Outlining Expansion | Ctrl+M, Ctrl+M | Ctrl+M, Ctrl+M | ⌘⌥[ | ⌘M minimizes the window on macOS |
| Collapse to Definitions | Ctrl+M, Ctrl+O | Ctrl+M, Ctrl+O | ⌘K ⌘0 | the same |
| Expand All Outlining | Ctrl+M, Ctrl+L | Ctrl+M, Ctrl+L | ⌘K ⌘J | the same |
| Replace | Ctrl+H | Ctrl+H | ⌘⌥F | ⌘H hides the app on macOS |

## Shortcuts left as they are, with a note

- **macOS function keys:** F11 (Step Into) shows the desktop and F3/F4 run Mission Control and Launchpad unless "Use F1, F2, etc. keys as standard function keys" is on; hold fn otherwise.
- **Ctrl+Alt on European keyboards (Windows):** Ctrl+Alt is AltGr, so a few classic Ctrl+Alt shortcuts (Ctrl+Alt+L, Solution Explorer) can type a character on layouts such as Polish. Rebind them if you need those characters.
- **KDE:** Ctrl+Alt+L locks the screen; Solution Explorer is also on View → Solution Explorer.

## Reserved and never bound by default

- **Windows:** Win+anything, Win+Space (input sources), Alt+Space (the window menu), Alt+Tab, Ctrl+Esc, Ctrl+Shift+Esc (Task Manager), Ctrl+Alt+Del.
- **Linux:** Super+anything, Alt+F2, Alt+F10, Ctrl+Alt+F1…F12, Ctrl+Alt+arrows, Ctrl+Alt+T, Ctrl+Alt+Backspace.
- **macOS:** ⌘Space, ⌘⌥Space, ⌘Tab, ⌘H, ⌘M, ⌘⌥⎋ (Force Quit), ⌘⇧3/4/5 (screenshots), ⌃↑/⌃↓ (Mission Control), ⌃←/⌃→ (Spaces), ⌃⌥Space (next input source).
