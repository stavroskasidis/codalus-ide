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

## Amendment (2026-11-12): on macOS the program starts through `/bin/sh`

The decision says the pty opened by name on fds 0–2 "makes it the controlling terminal". That holds on Linux, where glibc and musl start the new session (`POSIX_SPAWN_SETSID`) before they run the file actions. On macOS it doesn't: XNU's `posix_spawn` runs the file actions first and starts the session after the image is activated (`bsd/kern/kern_exec.c`), so the open is by a process that leads no session, the kernel's `TIOCSCTTY` refuses it (`EPERM`, ignored), and the program ends up leading a session with no controlling terminal.

Without one the terminal has no foreground process group, and what the kernel sends to it goes to nobody: `SIGWINCH` on a resize, and `SIGINT` on Ctrl+C in a program that leaves Ctrl+C to the terminal. `/dev/tty` can't be opened either. bash hid this: it opens its terminal by name when it starts (`check_dev_tty`), which takes it, so the tests, whose programs are `sh`, passed on macOS. A program started by itself doesn't: the report was AI Console (ADR 0095), where an agent's CLI kept drawing at the size it started with after the window was resized; a console app from Run Without Debugging in the integrated terminal was in the same state. So, by its source, was zsh, macOS's default shell, in the Terminal: it opens its terminal with `O_NOCTTY` everywhere and turns job control off without a word when it has no controlling terminal (not tried on a Mac when this was written).

**Decision.** On macOS the program is started through `/bin/sh -c 'true <>"$0"; exec "$@"' <pty> <program> <arguments…>` (`UnixPseudoTerminal.StartThroughShell`). The shell is what `posix_spawn` starts, so it leads the new session; it opens the pty by name, and the first terminal a session leader opens without `O_NOCTTY` becomes its session's; then it `exec`s the program, which is the same process, so the process id, the exit code and the session are the program's. The program and its arguments are the shell's own arguments, never shell text. Linux keeps the direct start.

Other options: a native helper of our own that does the same two calls (what node-pty ships as `spawn-helper`), which is a binary to build, sign and notarize for each macOS architecture for two lines of work `/bin/sh` already does; sending `SIGWINCH` to the program's process group ourselves after a resize, which fixes the resize and leaves Ctrl+C and `/dev/tty` broken; and `forkpty`, ruled out above.

**Consequences.**

- One more `exec` per terminal on macOS (a few milliseconds).
- A program that can't be executed is the shell's message in the terminal and exit code 126, not a failed start; the program is looked up before the start, so this is a file that exists and can't run.
- zsh opens its redirections with `O_NOCTTY`, so with zsh chosen as `/bin/sh` (`/var/select/sh`; bash is macOS's default, and dash works too) the program starts as it did before this amendment.
- `PseudoTerminalTests` starts `sleep` and `cat /dev/tty` by themselves (Ctrl+C ends the first, the second reads the terminal): programs that, unlike a shell, don't take the terminal themselves. With the open made `O_NOCTTY` on Linux, which is macOS's state, they and the resize test fail without the shell in between and pass with it.
