# Debugging

Daedalus Studio debugs .NET programs with **netcoredbg**, the open-source .NET debugger from Samsung, which is bundled with the packages.

## Starting

| Command | Shortcut |
|---|---|
| Start Debugging (builds the startup project first, using its launch profile) | `F5` |
| Attach to Process (the .NET processes are listed first) | `Ctrl+Alt+P` |
| Stop (terminates a launched program, detaches from an attached one) | `Shift+F5` |
| Restart | `Ctrl+Shift+F5` |
| Break All | `Ctrl+Alt+Break` |

The program's output and the debugger's messages appear in the **Debug Console**.

## Breakpoints

- **Add or remove**: click in the margin left of the line numbers, or press `F9` on the line. `Ctrl+F9` enables or disables it.
- **How they look**: red circles. Disabled breakpoints, and those that haven't bound to code yet, are hollow. A dot marks a condition or hit count; a diamond marks a logpoint.
- **Conditions and actions**: Debug → Breakpoint Conditions and Actions sets them on the caret's line:
  - **Condition**: a C# expression; the debugger breaks only when it is true.
  - **Hit count**: `5` (the 5th hit), `>= 5` (from the 5th hit on) or `% 5` (every 5th hit).
  - **Log message**: makes the breakpoint a *logpoint*, which writes the message and continues. `{expression}` inserts a value; `$FUNCTION` and `$TID` insert the function and thread.
- **The Breakpoints window** (Debug → Breakpoints) lists them all: enable or disable, double-click to open, `Delete` to remove.
- **Edits**: breakpoints follow your edits to the file, and are saved with the solution.

## While stopped

- **Stepping**: Step Over (`F10`), Step Into (`F11`), Step Out (`Shift+F11`), Continue (`F5`).
- **The current statement** is highlighted, with a yellow arrow in the margin.
- **Locals** and **Autos**: Autos shows the variables used on the current and previous lines. Expand objects to see their members; `F2` or double-click changes a value.
- **Watch**: add expressions in the box at the top, or with Debug → Add Watch on the selection. They are evaluated at every stop and saved with the solution.
- **Call Stack**: the frames of the selected thread. Pick another thread at the top. Double-click a frame to see its source and its variables.
- **Data tips**: hover over a variable or member access to see its value.
- **Debug Console**: type an expression and press Enter to evaluate it in the selected frame. Up and Down recall earlier ones.
- **Exceptions**: Debug → Exception Settings chooses between breaking on user-unhandled exceptions (the default) and breaking whenever an exception is thrown. An exception stop shows its type and message.

## Known limitations

These come from netcoredbg:
- no Edit and Continue or hot reload
- no data breakpoints
- no "set next statement"
- no mixed-mode debugging, and no debugging of dumps
- exceptions can't be configured per exception type
- no console input for the program being debugged
- on macOS, the program's exit code is reported as 0
- no debugger for macOS on Intel

Hit counts and logpoints are implemented by the IDE, so each hit pauses the program briefly.
