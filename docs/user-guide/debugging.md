# Debugging

Daedalus Studio debugs .NET programs with **netcoredbg**, the open-source .NET debugger from Samsung, which is bundled with the packages. It debugs programs of every .NET version from .NET Core 2.0. On Apple silicon, the programs of .NET 5 and earlier run as x64 under Rosetta 2, and the macOS package bundles the x64 netcoredbg for them ([Older .NET versions](dotnet-versions.md)).

## Starting

| Command | Shortcut |
|---|---|
| Start Debugging (builds the startup project first, using its launch profile) | `F5` |
| Attach to Process (the .NET processes are listed first) | `Ctrl+Alt+P` |
| Reattach to Process (the last one again, by id while it runs, otherwise by name) | `Shift+Alt+P` |
| Stop (terminates a launched program, detaches from an attached one) | `Shift+F5` |
| Restart | `Ctrl+Shift+F5` |
| Break All | `Ctrl+Alt+Break` |

The program's output and the debugger's messages appear in the **Debug Console**.

## Breakpoints

- **Add or remove**: click in the margin left of the line numbers, or press `F9` on the line. `Ctrl+F9` enables or disables it.
- **How they look**: red circles. Disabled breakpoints, and those that haven't bound to code yet, are hollow. A dot marks a condition or hit count; a diamond marks a logpoint.
- **Conditions and actions**: Debug → Breakpoint Conditions and Actions sets them on the caret's line, or right-click the breakpoint in the margin:
  - **Condition**: a C# expression; the debugger breaks only when it is true.
  - **Hit count**: `5` (the 5th hit), `>= 5` (from the 5th hit on) or `% 5` (every 5th hit).
  - **Log message**: makes the breakpoint a *logpoint*, which writes the message and continues. `{expression}` inserts a value; `$FUNCTION` and `$TID` insert the function and thread.
- **Labels, dependencies and temporary breakpoints** (right-click the breakpoint → Labels, Dependency and Temporary):
  - **Labels** group breakpoints; the Breakpoints window searches them.
  - **Only enable after another breakpoint is hit**: the breakpoint stays inactive until the one it depends on is hit in the session.
  - **Temporary**: removed once it stops the program. Debug → Insert Temporary Breakpoint (`Shift+Alt+F9`, `T`) adds one on the caret's line.
- **Function breakpoints**: Debug → New Function Breakpoint breaks when a method is entered: `Method`, `Type.Method` or `Namespace.Type.Method`, with an optional condition.
- **The Breakpoints window** (`Ctrl+Alt+B`) lists source and function breakpoints with their labels and settings. Search by location, condition or label; enable or disable the ones shown; double-click to open; `Delete` to remove; right-click to edit labels. **Export** and **Import** save breakpoints to a JSON file and read them back.
- **Edits**: breakpoints follow your edits to the file, and are saved with the solution.

## Running to a line

- **Run to Cursor** (`Ctrl+F10`): continues to the caret's line. It stops earlier at a breakpoint on the way. With no debug session, it starts debugging and stops at the line.
- **Force Run to Cursor** (Debug menu): continues to the line, past every breakpoint on the way.
- **Run to Click**: while paused, `Ctrl`+click a line in the breakpoint margin.

## While stopped

- **Stepping**: Step Over (`F10`), Step Into (`F11`), Step Out (`Shift+F11`), Continue (`F5`).
- **The current statement** is highlighted, with a yellow arrow in the margin.
- **PerfTips**: the time since the program was resumed appears in gray at the end of the current line, e.g. `≤ 12 ms elapsed`. It includes the debugger's own overhead, so use it to compare steps, not as a profiler. Turn it off with the `debug.perfTips` setting.
- **Locals** and **Autos**: Autos shows the variables used on the current and previous lines. Expand objects to see their members.
- **Watch**: add expressions in the box at the top, or with Debug → Add Watch on the selection. They are evaluated at every stop and saved with the solution.
- **QuickWatch** (`Shift+F9`): evaluates the selection, or the expression under the caret, as a tree. Change the expression and press Enter to evaluate another; **Add Watch** copies it to Watch.
- **Changing values**: in Locals, Autos, Watch, QuickWatch and data tips, `F2` or double-click edits a value: a variable, a field or a property, or a watch expression that can be assigned.
- **The context menu** of a value copies it or its expression, adds a watch, or opens QuickWatch. Strings also have **View → Text, JSON, XML or HTML Visualizer**: the whole string in a read-only, colored document, with JSON and XML indented.
- **Call Stack** (`Ctrl+Alt+C`): the frames of the selected thread. Pick another thread at the top. Double-click a frame to see its source and its variables.
- **Threads** (`Ctrl+Alt+H`): every thread with its id, name and location. Double-click to switch to a thread; flag threads and show only the flagged ones.
- **Modules** (`Ctrl+Alt+U`): the loaded modules with their symbol status and path.
- **Data tips**: hover over a variable or member access to see its value as a tree: expand members, and edit values with `F2` or double-click.
- **Debug Console**: type an expression and press Enter to evaluate it in the selected frame. Up and Down recall earlier ones.

## Exceptions

- **The exception helper** opens at the statement when the program stops on an exception. It shows the type, the message and the chain of inner exceptions. From there:
  - **View Details** opens `$exception` in QuickWatch.
  - **Copy Details** copies the types, messages and stack traces.
  - **Break when this exception type is thrown** turns that type on or off in the exception settings.
- **Exception Settings** (`Ctrl+Alt+E`) lists the common runtime exceptions. A checked type breaks when it is thrown, even if it is caught. **All other exception types** covers the types not in the list. Add your own types by their full name. User-unhandled exceptions always break. The settings are saved with the solution.
- Types match exactly: checking `System.ArgumentException` doesn't break on `System.ArgumentNullException`.

## Known limitations

Blazor WebAssembly apps, and the pages of ASP.NET Core apps, are debugged in the browser (see [Web development](web.md#debugging-the-server-and-the-page-together)). There, function breakpoints, the Modules window and the .NET exception settings don't apply.

These come from netcoredbg:
- no Edit and Continue or hot reload
- no data breakpoints
- no Set Next Statement and no Step Into Specific
- no Memory, Disassembly or Registers windows
- no mixed-mode debugging, and no debugging of dumps
- `[DebuggerDisplay]` and debugger type proxies aren't applied yet: objects show as `{Type}`
- no console input for the program being debugged
- on macOS, the program's exit code is reported as 0
- no debugger for macOS on Intel

Hit counts, logpoints, dependent and temporary breakpoints, and Run to Cursor are implemented by the IDE, so each hit pauses the program briefly.
