# 0107. The user's environment read again, and the terminal card

- **Status:** accepted (2026-11-23; the maintainer's answers of 2026-11-23)
- **Date:** 2026-11-23
- **Deciders:** maintainer
- **Builds on:** M37 (the environment check and its installs), M46 (the first-run setup), M49 and [ADR 0080](0080-the-hosts-dotnet-runtime.md) (the hosts' runtime and the .NET root), M56 (the login shell's environment at startup), [ADR 0083](0083-connecting-external-agents-from-templates.md) (Connect and its requirements), [ADR 0014](0014-terminal-emulator.md) (the terminal)

## Context

A first run on Linux showed two problems.

1. **What the user installs by hand isn't found until a restart.** The shell reads the login shell's environment once, at startup, and only when it wasn't started from a terminal (`LoginShellPath.Import`); it chooses the .NET root once (`UseUserDotnet`), and again only after an install it ran itself. Check Again, the setup's Tools step and Connect's Check again probed with that environment. So an SDK installed with Microsoft's script into `~/.dotnet` (found as an SDK, but the hosts' root, `DOTNET_ROOT`, still pointed elsewhere: ".NET runtime for Codalus IDE" stayed missing), and Node.js installed from its download page or with nvm (on the PATH only of shells started after the change to `~/.profile` or `~/.bashrc`), stayed missing until the IDE restarted. On Windows the registry's PATH had the same gap, read again only after a winget install in Connect.
2. **A command in the terminal ran under the window the user was in.** Installs under sudo (the distribution's .NET or git package, Microsoft's `.pkg`) and agents' sign-ins run in the main window's Terminal tool window. The setup and the start window are owned by the main window and stay above it, and Connect, the dialog frame and Report a Problem are in-window dialogs over it: the password prompt or the sign-in's code was out of sight, and the user didn't know anything waited for them.

## Decision

### Every check the user asks for reads the environment again

- `ShellHost.RefreshEnvironmentAsync` reads the user's PATH again, off the UI thread (`UserPath.Refresh`: the login shell's on macOS and Linux, `LoginShellPath.Refresh`; the registry's on Windows, `WindowsPath.Refresh`), then chooses the .NET root again (`UseUserDotnet`). Calls while one runs share it.
- It runs before Check Again in Check Environment, the setup's Tools step (both through `RecheckEnvironmentAsync`), Connect's requirements check (its opening, Check again and the check after an install), and after any install. Not at startup, which has just read it.
- **On macOS and Linux, also for an IDE started from a terminal** (the maintainer's choice): that terminal's PATH is from before the install. Only the folders the IDE's PATH lacks are added, first, in the login shell's order (nvm's Node.js before a distribution's older one); nothing is removed or reordered, so a terminal's own additions and the .NET root the IDE put first stay. The network variables are taken again, as at startup. `CODALUS_NO_SHELL_PATH` turns it off as it turns off the import. On Windows the registry's new folders go at the end, as `WindowsPath` already did.
- A read costs a login shell (well under a second for most profiles, at most 5 s, then given up). The maintainer chose this over reading on window focus (more processes for nothing) and over keeping startup's rule (a terminal launch would still need a restart).
- When Check Again finds the IDE's runtime that was missing, the hosts that failed for it start again (`RestartHostsAsync`), as after an install the IDE ran.
- An install the IDE runs in the terminal that isn't .NET's (git from the distribution, a workload) is now awaited as .NET's is, and checked again when it ends, instead of asking the user to check again.

### The terminal card

- When the IDE starts a command in the terminal that the user may have to answer (`ShowCommandTerminal`: the installs of M37, Connect's installs and sign-ins, a chat's sign-in) while the Terminal tool window is covered (`TerminalIsCovered`: a companion window is open, or Connect, the dialog frame or Report a Problem), the window in front draws a **terminal card** over everything: the dialog frame with the command's terminal for a body, the focus in it (the maintainer's choice, over minimizing the setup while it runs, which loses the user's place, and a separate always-on-top window, one more window to manage across X11, Wayland, macOS and Windows).
- It is drawn where the other overlays are (`CompanionOverlays` in the setup and the start window, a layer of the main window's `ModalLayers` otherwise), above the dialogs, and counts as modal there: the setup's Escape, Alt+Left and Alt+Right wait while it shows.
- It closes when the command ends. **Hide** closes it sooner; the command goes on in the Terminal tool window, where it showed all along, and Connect's Show Terminal brings the card back while the tool window is still covered.
- The session is shown in two views at once (the tool window behind, the card in front). The card holds its size (`TerminalSession.SizeOwner`): the tool window draws it as it is instead of resizing the pty to its own bounds, and sizes it again when the card lets go.
- While the tool window is covered, it doesn't take the focus (`TerminalViewModel.MayTakeFocus`, `ShowToolWindow(focus: false)`): its terminal took it from the card.

## Consequences

- An SDK, a runtime, Node.js, git or an agent's CLI installed by hand is found by Check Again, without a restart. Hosts already running keep the environment they started with, as before.
- Each Check Again starts the user's login shell once. A profile that prints or prompts is handled as at startup (the markers around `env`, standard input closed, the time limit).
- What a sudo install or a sign-in asks shows where the user is looking, on every OS.
- Tests: `ShellFixture` replaces the refresh with a counter (`EnvironmentRefreshStandIn`), so no shell test starts a login shell or points the test process's `DOTNET_ROOT` at a described machine; `LoginShellPath.Refresh` takes the process's variables as functions, so its unit test doesn't change the PATH the parallel tests share.
