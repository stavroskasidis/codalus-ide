# Debugging fixture

A console app for the debugger tests (ADR 0007): a loop, a method call, a record, a second thread, a caught exception and an exit code (the loop's total, 15). Tests find lines by the `// [name]` markers.

- `--wait-for <file>` waits for a file before running (attach tests).
- `--depth` runs the M14a fixtures (exceptions, settings, JSON).
- `--read` reads a line from the console and echoes it (debugging in the terminal, M14c).
- `--crash` ends with an unhandled exception (Just-In-Time crash dumps, M14c).

`Debugging.slnx` has the Debuggee alone. `Sessions.slnx` (M14c, ADR 0039) adds:
- `Partner`, a second console program for multiple startup projects (it returns its arguments' length);
- `Generator`, a source generator with a `DebugRoslynComponent` launch profile, and `GeneratorTarget`, the project it runs on.

`HotReload` (M55, ADR 0086), in `HotReload.slnx`, prints `Greeter.Message(i)` every 50 ms for the Hot Reload while debugging tests, which edit `Message` while it runs or is stopped. With an argument it waits for that file first (attach tests). Its markers are `[call]` and `[message]`; `HotReloadVb`, beside it in the solution, is the same program in Visual Basic (`[vb-call]`, `[vb-message]`). `HotReloadLib` is a library HotReload loads late: it calls it only once a file named `later` is next to it.

`MonoDebuggee` (M20b, ADR 0065) is a console app on the Mono runtime: .NET 8's desktop Mono runtime pack, self-contained for this machine, started with the soft debugger's agent in `MONO_ENV_OPTIONS`. It tests the Mono debugger adapter without a device. Its markers are `[mono-…]`: a loop, a method call, auto-properties, a list, and a caught exception.

`NetFxDebuggee` (M43b, ADR 0076) is a .NET Framework 4.8 console app for the desktop-CLR debugger adapter (`codalus-debughost --netfx-dap`). It builds on every OS (the reference assemblies package); the adapter runs it on Windows, and elsewhere the tests read its metadata and portable PDB under a fake ICorDebug. Its markers are `[netfx-…]`: a loop over a list, auto-properties and a getter, a `[Flags]` enum, an array, a method to call, an async method's hoisted variables, `Debug.WriteLine`, a caught exception and an exit code (3). With `wait` it ticks for a minute (attach tests).

`Async.slnx` (M61, ADR 0100) is async code built two ways, for the debugger on runtime async (.NET 11): `AsyncDebuggee` compiles it as state machines, and `AsyncDebuggee.RuntimeAsync` compiles the same files (`AsyncDebuggee/*.cs`) with `runtime-async=on`. `AsyncLib` and `AsyncLib.RuntimeAsync` are one library built the same two ways; each program references the one built the other way, so the two kinds await each other. An argument picks a scenario (`steps`, `chain`, `throw`, `mixed`, `race`, `held`; none runs them all but `held`): locals around `Task.Delay` and `Task.Yield`, an awaited call returning a value, a chain of awaits (also the async call stack's, M61b), an exception thrown after an await, the library awaiting the program's callback, an await that resumes on another thread before the awaiting thread has left the method, and a caller held inside the method's await while the method resumes elsewhere (`Elsewhere.cs`). Its markers are `[run-…]`, `[double-…]`, `[chain-…]`, `[throw]`, `[mixed-…]`, `[race-…]`, `[held-…]`, `[lib-…]` and `[main-…]`. Only `RuntimeAsyncDebuggingTests` and `AsyncCallStackTests` build it.

`RuntimesDebuggee` (M58b, ADR 0094) is one small console app built for several .NET versions (`netcoreapp3.1` to the current one, C# 7.3), for the debugger on runtimes other than the one the tests run on: the x64 runtimes of an Apple silicon Mac, under Rosetta 2 (`DebuggerBundleTests`, with `eng/get-dotnet-x64.sh`). It has Debuggee's `[loop]` and `[print]` lines and values, prints `runtime=<major>.<minor>`, and exits with 15. It is in no solution: only those tests build it.
