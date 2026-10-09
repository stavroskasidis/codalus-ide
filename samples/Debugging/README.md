# Debugging fixture

A console app for the debugger tests (ADR 0007): a loop, a method call, a record, a second thread, a caught exception and an exit code (the loop's total, 15). `--wait-for <file>` makes it wait for a file before running (attach tests). Tests find lines by the `// [name]` markers.
