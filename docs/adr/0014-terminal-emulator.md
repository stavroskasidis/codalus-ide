# 0014. Terminal emulator: engine, renderer and PTY placement

- **Status:** accepted (M10 plan, 2026-10-09; the maintainer's standing go-ahead for M9–M16)
- **Date:** 2026-10-09
- **Deciders:** maintainer

## Context

M10 asks for an integrated terminal: several instances and splits; profiles (pwsh, bash, zsh, fish, cmd and a "dotnet developer" profile); shell integration (cwd tracking with OSC 7, command marks with OSC 133, clickable `file:line:col` paths and URLs); xterm-compatible emulation (true colour, wide characters and emoji, combining marks, the alternate screen, mouse reporting, bracketed paste, IME, resizing, scrollback, find); IDE theme colours and fonts; configurable key passthrough; a screen-reader mode; and running console programs in it. Budgets: keystroke echo within the editor's typing budget, no frame over 50 ms while `cat`-ing a million lines, ≤ 30 MB per terminal with 10k lines of scrollback.

Constraints: the shell is Native AOT (no reflection, P/Invoke through source-generated stubs); latency-critical paths stay in the shell; Windows, Linux and macOS.

## Options

**VT engine**
1. **XtermSharp** (MIT). Unmaintained since 2021, written around a mutable, reflection-free but allocation-heavy buffer and Xamarin-era APIs; its renderer is not reusable for Avalonia.
2. **Our own engine** (a DEC/ANSI parser after Paul Williams' state machine, and a screen model of cell rows). About two thousand lines, fully testable with recorded streams, shaped for our renderer (per-row dirty flags, compact cells).

**PTY**
1. `vs-pty.net` (MIT). Unix support through `forkpty` from managed code; Windows through ConPTY. Not packaged for AOT.
2. **Our own P/Invoke in `DaedalusStudio.Platform`**: ConPTY (`CreatePseudoConsole`, Windows 10 1809+) on Windows; on Linux and macOS either `forkpty` (the child runs managed code until `execve`) or `openpty` plus `posix_spawn` (libc sets the child up; no managed code runs in it).
3. A separate `daedalus-ptyhost`. Crash isolation for code that is small and stable, at the cost of an extra hop on every keystroke.

**Renderer**
1. AvaloniaEdit (the editor's control) in read-only mode. Built for documents, not for a cell grid redrawn at terminal rates, and it can't show per-cell colours cheaply.
2. **A custom control** that draws visible rows as runs of same-styled cells, with a glyph cache and dirty-row invalidation.

## Decision

- **Engine:** our own (option 2), in a new UI-agnostic library `DaedalusStudio.Terminal`: the parser, the screen model (primary and alternate screens, scrollback ring, scroll regions, tab stops, charsets, modes), input encoding (keys, mouse, paste) and the link and mark scanners. Width comes from a compact Unicode table (East Asian Wide/Fullwidth and emoji presentation are two cells; combining marks join the previous cell).
- **PTY:** our own P/Invoke (option 2) in `DaedalusStudio.Platform`, in the shell process: ConPTY on Windows; `openpty` and `posix_spawn` on Linux and macOS, with `POSIX_SPAWN_SETSID`, the pty opened by name on fds 0–2 (which makes it the controlling terminal), `addchdir_np`, default signal dispositions and an empty signal mask. Terminal I/O is light and latency critical; child processes are already isolated from the shell. Supported Windows floor: Windows 10 1809 (ConPTY). If PTY handling ever crashes the shell in practice, it moves to a `daedalus-ptyhost` with the same interface.
- **Renderer:** a custom Avalonia control (option 2) in the shell; the screen model reports dirty rows, the control redraws at most once per frame, and output is parsed off the UI thread in batches.
- **Shell integration:** for bash, zsh and pwsh the profile starts the shell with a small script (shipped in `terminal/` next to the shell) that sources the user's own startup files, then emits OSC 7 (cwd) and OSC 133 (prompt, command, output, exit code). Other shells work without marks.
- **Accessibility:** an automation peer exposes the buffer as text (the visible screen and the cursor line); a screen-reader mode announces new output in batches.

## Consequences

- No third-party dependency for the terminal; we own its correctness, so recorded VT streams (`ls --color`, a full-screen app, a progress bar, `dotnet build`) are replayed into the engine and compared with screen snapshots, on every OS.
- `forkpty` from managed code was the first implementation and hung on Linux: the child JIT-compiles and binds the P/Invoke stubs it calls, in a copy of a multi-threaded runtime whose locks other threads may hold. `posix_spawn` needs glibc 2.29+ (`addchdir_np`) and macOS 10.15+.
- `ioctl` is variadic: on Apple silicon its third argument must be on the stack, so the resize call pads the register arguments (see `UnixPseudoTerminal.Resize`).
- A process inherits "ignore Ctrl+C" on Windows from the one that starts it (test runners set it), so the shell clears it before creating a pseudo-console; otherwise Ctrl+C in the terminal does nothing.
- `TODO(issue)`: sixel/kitty graphics, Unicode 15 grapheme clustering beyond combining marks, and right-to-left text are not supported.
