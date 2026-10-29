# Debugging fixture

A console app for the debugger tests (ADR 0007): a loop, a method call, a record, a second thread, a caught exception and an exit code (the loop's total, 15). Tests find lines by the `// [name]` markers.

- `--wait-for <file>` waits for a file before running (attach tests).
- `--depth` runs the M14a fixtures (exceptions, settings, JSON).
- `--read` reads a line from the console and echoes it (debugging in the terminal, M14c).
- `--crash` ends with an unhandled exception (Just-In-Time crash dumps, M14c).

`Debugging.slnx` has the Debuggee alone. `Sessions.slnx` (M14c, ADR 0039) adds:
- `Partner`, a second console program for multiple startup projects (it returns its arguments' length);
- `Generator`, a source generator with a `DebugRoslynComponent` launch profile, and `GeneratorTarget`, the project it runs on.

`HotReload` (M55, ADR 0086), in `HotReload.slnx`, prints `Greeter.Message(i)` every 50 ms for the Hot Reload while debugging tests, which edit `Message` while it runs or is stopped. With an argument it waits for that file first (attach tests). Its markers are `[call]` and `[message]`; `HotReloadVb`, beside it in the solution, is the same program in Visual Basic (`[vb-call]`, `[vb-message]`).

`MonoDebuggee` (M20b, ADR 0065) is a console app on the Mono runtime: .NET 8's desktop Mono runtime pack, self-contained for this machine, started with the soft debugger's agent in `MONO_ENV_OPTIONS`. It tests the Mono debugger adapter without a device. Its markers are `[mono-…]`: a loop, a method call, auto-properties, a list, and a caught exception.

`NetFxDebuggee` (M43b, ADR 0076) is a .NET Framework 4.8 console app for the desktop-CLR debugger adapter (`codalus-debughost --netfx-dap`). It builds on every OS (the reference assemblies package); the adapter runs it on Windows, and elsewhere the tests read its metadata and portable PDB under a fake ICorDebug. Its markers are `[netfx-…]`: a loop over a list, auto-properties and a getter, a `[Flags]` enum, an array, a method to call, an async method's hoisted variables, `Debug.WriteLine`, a caught exception and an exit code (3). With `wait` it ticks for a minute (attach tests).
