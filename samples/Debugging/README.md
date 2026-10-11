# Debugging fixture

A console app for the debugger tests (ADR 0007): a loop, a method call, a record, a second thread, a caught exception and an exit code (the loop's total, 15). Tests find lines by the `// [name]` markers.

- `--wait-for <file>` waits for a file before running (attach tests).
- `--depth` runs the M14a fixtures (exceptions, settings, JSON).
- `--read` reads a line from the console and echoes it (debugging in the terminal, M14c).
- `--crash` ends with an unhandled exception (Just-In-Time crash dumps, M14c).

`Debugging.slnx` has the Debuggee alone. `Sessions.slnx` (M14c, ADR 0039) adds:
- `Partner`, a second console program for multiple startup projects (it returns its arguments' length);
- `Generator`, a source generator with a `DebugRoslynComponent` launch profile, and `GeneratorTarget`, the project it runs on.
