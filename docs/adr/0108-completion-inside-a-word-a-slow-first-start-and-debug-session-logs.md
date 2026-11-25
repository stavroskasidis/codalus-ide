# 0108. Completion inside a word, a slow first start, and the debug sessions' logs

- **Status:** accepted (2026-11-24; the maintainer's answers of 2026-11-24)
- **Date:** 2026-11-24
- **Deciders:** maintainer
- **Builds on:** M12 (the completion list), M56 (commits that don't wait for the resolve), [ADR 0086](0086-a-managed-coreclr-debugger.md) and [ADR 0094](0094-the-codalus-debugger-by-default.md) (the Codalus debugger), M51 (Report a Problem)

## Context

A published review of 1.0, on macOS in a virtual machine (a new Avalonia app from its template, a little code, a breakpoint), reported two things.

1. **"Text getting deleted as if some kind of overwrite mode were on."** The editor has no overwrite mode. A letter typed right before a word asked for completion, and Roslyn's span for a list asked inside a word covers the word's rest (it "overwrites", in its own words). The list was filtered by the typed text alone, so `v` before `InitializeComponent` hard-selected `var`, and Space committed it over the span. The span's end was also read in the document after the typing that followed the request, not the one it was measured in. Typing `var ` before `title.Trim();` gave `var le.Trim();`; the C# service test reproduces it on 1.0.
2. **"The first time I tried this I got a crash"**, and the second start worked. Nothing says what failed. The likeliest cause in the code is the Codalus debugger's fixed limits for a launch (30 seconds for its launcher to report the program, 30 for the program's runtime to start): the first launch of an executable just built is scanned by macOS, slower still in a VM, and past a limit the debugger ended the program and the session failed. netcoredbg's start timeouts already start again (three attempts); the Codalus debugger's didn't. And there was nothing to send: the IDE's log is in memory only, and the debugger's log only with `CODALUS_CLR_DEBUGGER_LOG`.

## Decision

### Completion inside a word, as the classic IDE

The maintainer's choice, over insert-only completion (VS Code's default) and a setting between the two.

- **A letter starts a list only at a word's start**: not when a word character follows the caret (Roslyn's `IsStartingNewWord`). `CodeEditorController.OnTextEntered`.
- **A list asked inside a word is filtered by the whole word.** When the list's range (`itemDefaults.editRange`) ends after the request's position, that much of the word after the caret (`CompletionPopup.TailLength`, at most the line's rest) joins the typed text in the filter, and nothing matching closes the list. It follows edits: text typed at the caret goes before it, and deleting in it shortens it.
- **A commit replaces the whole word**: the segment it gets (`CompletionSpan`) covers the typed text and the word's rest.
- **An edit's end after the request's position is measured from the typing since**: the list's ranges are in the document as it was at the request, and what was typed since went in at the caret, so such an end lies that far past the typed text (`CompletionSpan.OffsetOf`). This also corrects a server's edit that replaces past the caret (a JSON value's closing quote), for every language.

### A slow first start starts again

The maintainer's choice, over waiting while the program runs (a hung program would wait for Stop) and a longer wait then one more try.

- The Codalus debugger fails a launch that ran past one of its limits with `ClrStartTimeoutException`, which the DAP server sends as an error with a code: `message` is `codalus.startTimeout` (DAP's "raw error in short form") and `body.error.format` the text shown. `DapConnection` keeps the code (`DapException.Code`); `DebugAdapter.IsClrStartTimeout` tells it.
- `DebugService` starts such a session again as it does after netcoredbg's timeout (`IsStartupTimeout`, `StartupAttempts` = 3), the Debug Console saying which limit was passed and "starting again (2 of 3)". The debugger has ended the program by then, so nothing is left behind.

### Each debug session's own log on disk

The maintainer's choice, over the full DAP traffic on disk (variable values, program output) and nothing by default.

- `DebugSessionLog` (Workbench) writes each session's lines that are the debugger's own (`DebugOutput.IsDiagnostic`: the adapter's log, the starts and their limits, failures, why it ended; not the program's output, a logpoint's message or what the program writes to the debugger) to `<logs>/debugger/session-<time>.log`, each with the time since the session began, secrets redacted. The last 10 are kept, at most 1 MB each. A session that starts again stays one log; `DebugService.StartFailed` records why a start failed.
- Report a Problem attaches the last 100 lines of the latest one beside the IDE log's 200, under the same checkbox, redacted the same way.
- `CODALUS_CLR_DEBUGGER_LOG` keeps writing the Codalus debugger's full traffic, for a deeper look.

## Consequences

- Typing in front of a word, or inside one, leaves it as typed; a list asked inside a word with `Ctrl+Space` replaces the word, as in the classic IDE.
- A first start that's slower than the debugger's limits costs one more start, not the session. A program that never starts fails after three attempts, about 90 seconds, with the reason.
- A report of a debugging problem carries the session's own account, with its timing.
- Not done: macOS's crash reports (`~/Library/Logs/DiagnosticReports`) aren't collected; the troubleshooting page says where they are and how to tell which process crashed. The IDE's own log stays in memory.
- Tests: `CompletionRangeTests`, `IntelliSensePopupTests.With_the_csharp_service_typing_before_a_word_keeps_it_and_completing_inside_one_replaces_it`, `ClrStartTimeoutTests`, `DebugStartAgainTests` (the scripted adapter's `clrStartTimesOut`) and `DebugSessionLogTests`.
