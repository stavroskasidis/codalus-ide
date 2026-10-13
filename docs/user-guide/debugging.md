# Debugging

Daedalus Studio debugs .NET programs with **netcoredbg**, the open-source .NET debugger from Samsung, which is bundled with the packages. It debugs programs of every .NET version from .NET Core 2.0. On Apple silicon, the programs of .NET 5 and earlier run as x64 under Rosetta 2, and the macOS package bundles the x64 netcoredbg for them ([Older .NET versions](dotnet-versions.md)).

## Starting

| Command | Shortcut |
|---|---|
| Start Debugging (builds the startup project first, using its launch profile) | `F5` |
| Attach to Process (the .NET processes are listed first) | `Ctrl+Alt+P` |
| Reattach to Process (the last one again, by id while it runs, otherwise by name) | `Shift+Alt+P` |
| Attach to Process on a Remote Machine (over SSH, in a container, in WSL) | Debug menu |
| Stop (terminates a launched program, detaches from an attached one) | `Shift+F5` |
| Detach All (an attached program, or one launched in the terminal, keeps running) | Debug menu |
| Restart | `Ctrl+Shift+F5` |
| Break All | `Ctrl+Alt+Break` |

The program's output and the debugger's messages appear in the **Debug Console**.

### The program's console

On Windows a debugged console or web app gets a **console window** of its own, as in Visual Studio: it takes input, the window stays open after the program ends until you press a key, Stop closes it, and a web app's browser opens once its address answers. Elsewhere it writes to the Debug Console and gets no input. Set **`debug.console`** to `internalConsole`, `integratedTerminal` or `externalConsole` (Windows) to choose; Run Without Debugging has the same choice in **`run.console`** (`outputWindow`, `integratedTerminal`, `externalConsole`). With `integratedTerminal` to start it in a new integrated terminal instead: a real console that takes input, colors and all. Daedalus Studio starts the program there, holds it before `Main` until the debugger has attached and set your breakpoints, then lets it run. Stop ends it; **Detach All** leaves it running in its terminal. On macOS the setting has no effect yet: the debugger can't attach to a program held in the terminal there, so programs use the Debug Console.

### Several startup projects

Debug → **Configure Startup Projects** (or right-click the solution) opens the Startup Projects page:
- **Single startup project**: the project F5 and Ctrl+F5 start, as Set as Startup Project chooses.
- **Multiple startup projects**: give each project an action (**Start**, **Start without debugging** or **None**) and a launch profile, and order them with Move Up and Move Down. Press **Apply**.

F5 then builds the solution once and starts the projects in that order. Those set to Start are debugged in **one session**:
- the Call Stack, Threads and Debug Location toolbar name each thread after its program (`Partner: Main Thread`);
- the Processes window lists every program;
- each program stops at its own breakpoints, and the others keep running;
- the session ends when the last program ends.

The choice is saved with the solution (in your user data, not in the solution file).

### Debugging on other machines

Debug → **Attach to Process on a Remote Machine** debugs a .NET program running elsewhere:
- **SSH**: enter `user@host` (with `:port` if it isn't 22). Your own `ssh` is used, with your keys, agent and `~/.ssh/config`; passwords and passphrases are asked in a dialog.
- **Docker or Podman container**: pick a running container. The `debug.containerTool` setting is the container command: `docker` (default), `podman`, or one inside WSL such as `wsl -d Ubuntu --exec podman`.
- **WSL distribution** (Windows): pick a distribution.

Then pick the process (.NET processes come first). The first time, Daedalus Studio offers to copy the debugger (netcoredbg, about 15 MB) to the machine: into `~/.daedalus/netcoredbg`, or `/tmp/.daedalus/netcoredbg` in a container. The machine needs `tar` and `gzip`, but no internet access. Linux on x64 and Arm64, and macOS on Apple silicon, are supported.

**Source files**: frames of a program built elsewhere carry that machine's paths (`/src/App/Program.cs`). A file that isn't here is found in your solution by its folder and name, and its folder is then mapped for your breakpoints. To map folders yourself, add entries to **`debug.sourceFileMap`**, such as `"/src=C:\\work\\App"`.

Reattach to Process (`Shift+Alt+P`) attaches to the same program on the same machine again.

### The WSL launch profile

A launch profile with `"commandName": "WSL2"` runs your project in a WSL distribution from Windows:

```json
"WSL": {
  "commandName": "WSL2",
  "distributionName": "Ubuntu",
  "environmentVariables": { "ASPNETCORE_ENVIRONMENT": "Development" }
}
```

Leave `distributionName` empty for the default distribution. The project builds on Windows, and the distribution's `dotnet` runs the build output through `/mnt/…`, so .NET must be installed in the distribution. Ctrl+F5 runs it, and F5 debugs it with the debugger inside the distribution. Breakpoints work as usual.

### Analyzers and source generators

To debug an analyzer or a source generator, give its project a profile that runs the compiler on a project that uses it:

```json
"Generator": {
  "commandName": "DebugRoslynComponent",
  "targetProject": "..\\App\\App.csproj"
}
```

Make the analyzer project the startup project and press F5. Daedalus Studio runs the SDK's compiler on the target project, with the target's own command line, under the debugger. Breakpoints in your generator stop as it runs. The compiler's output goes to a temporary folder, so your project's build output isn't touched.

### Just-In-Time debugging

Debug → **Just-In-Time Debugging** turns crash dumps of your .NET programs on or off. It is off by default, and the dialog says exactly what it changes before you agree.

When it's on, a .NET program you start that crashes writes a full dump to the `logs/crash-dumps` folder of your user data. Daedalus Studio then offers to **open the dump** (see [Dumps](#dumps)). It works through the .NET runtime's crash-dump variables, set for your user only, with no administrator rights:
- **Windows**: your user environment variables;
- **Linux**: `~/.config/environment.d` and `~/.profile`;
- **macOS**: `launchctl` and a login item.

Programs started after you turn it on are covered. Turning it off removes the variables. Attaching live to a crashing program, as a system-wide just-in-time debugger, would need administrator rights and isn't offered.

## Breakpoints

- **Add or remove**: click in the margin left of the line numbers, or press `F9` on the line. `Ctrl+F9` enables or disables it.
- **How they look**: red circles. Disabled breakpoints, and those that haven't bound to code yet, are hollow. A dot marks a condition or hit count; a diamond marks a logpoint.
- **Conditions and actions**: Debug → Breakpoint Conditions and Actions sets them on the caret's line, or right-click the breakpoint in the margin:
  - **Condition**: a C# expression; the debugger breaks only when it is true.
  - **Hit count**: `5` (the 5th hit), `>= 5` (from the 5th hit on) or `% 5` (every 5th hit).
  - **Filter**: break only on some threads or processes: `ThreadName = "Worker"`, `ThreadId = 12`, `ProcessId = 4321` or `ProcessName = "MyApp"`, combined with `&`, `||`, `!` and parentheses (`!=` negates one test).
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
- **The context menu** of a value copies it or its expression, adds a watch, or opens QuickWatch. Its **View** submenu opens a visualizer:
  - strings: **Text, JSON, XML or HTML Visualizer**, the whole string in a read-only, colored document, with JSON and XML indented;
  - arrays and collections (`List<T>`, `Queue<T>`, `Stack<T>`, `HashSet<T>`, …): the **Grid Visualizer**, a table of up to 500 elements, with an object's properties and fields as columns;
  - byte arrays: the **Image Visualizer**, which opens a PNG, JPEG, GIF, BMP, WebP or ICO image they hold in the image viewer;
  - plugins can add their own visualizers for other types (see [Plugins](../plugins.md#proposed-apis)).
- **Display attributes**: objects whose type has `[DebuggerDisplay]` show its text (`Order 7: Ada (1 lines)` instead of `{MyApp.Order}`), and members marked `[DebuggerBrowsable(Never)]` are hidden, in Locals, Autos, Watch, QuickWatch and data tips.
- **Call Stack** (`Ctrl+Alt+C`): the frames of the selected thread. Pick another thread at the top. Double-click a frame to see its source and its variables.
- **Threads** (`Ctrl+Alt+H`): every thread with its id, name and location. Double-click to switch to a thread; flag threads and show only the flagged ones.
- **Modules** (`Ctrl+Alt+U`): the loaded modules with their symbol status and path.
- **Data tips**: hover over a variable or member access to see its value as a tree: expand members, and edit values with `F2` or double-click.
- **Pinned data tips**: the pin button in a data tip keeps it next to its line. It is evaluated again at every pause, in red when the value changed, and keeps its last value after the session ends. Right-click it to add a comment, copy, add a watch or unpin it; double-click for QuickWatch. Pinned tips follow your edits and are saved with the solution; Debug → Windows → Clear All DataTips removes them.
- **Parallel Stacks** (Debug → Windows): every thread's call stack in one tree. Threads that share callers are grouped until their stacks branch; the node with the active thread is marked.
- **Parallel Watch** (Debug → Windows): one expression evaluated on every thread, in the thread's topmost frame with source code.
- **Processes** (Debug → Windows): the process being debugged with its state, and Attach to Process, Detach All and Terminate All.
- **The Debug Location toolbar**: while paused, the standard toolbar shows the process and lets you pick the thread and the stack frame.
- **Debug Console**: type an expression and press Enter to evaluate it in the selected frame. Up and Down recall earlier ones.

## Exceptions

- **The exception helper** opens at the statement when the program stops on an exception. It shows the type, the message and the chain of inner exceptions. From there:
  - **View Details** opens `$exception` in QuickWatch.
  - **Copy Details** copies the types, messages and stack traces.
  - **Break when this exception type is thrown** turns that type on or off in the exception settings.
- **Exception Settings** (`Ctrl+Alt+E`) lists the common runtime exceptions. A checked type breaks when it is thrown, even if it is caught. **All other exception types** covers the types not in the list. Add your own types by their full name. User-unhandled exceptions always break. The settings are saved with the solution.
- Types match exactly: checking `System.ArgumentException` doesn't break on `System.ArgumentNullException`.
- **Module conditions**: the box next to a type limits it to some modules: `MyApp.dll`, `MyApp.*`, several separated by commas, or `!System.*` to leave modules out. A thrown exception then breaks only when the code that threw it is in a matching module.

## Dumps

- **Debug → Create Dump** writes a full dump of the program being debugged into the `logs/dumps` folder of your user data, and offers to open it.
- **Debug → Open Dump File** opens a dump (`.dmp` on Windows, a core dump on Linux and macOS) of a .NET program:
  - **Threads**: each managed thread with its exception, if it had one, and its call stack. Frames map to source lines when the program's PDBs are next to its assemblies; double-click a frame to open the line.
  - **Heap**: the objects by type, with their count and size. Pick a type to list its instances, and an instance to see its fields; follow references to other objects, and go back.
- Dumps open on the operating system that wrote them, because the runtime's debugging library has to load on the machine reading them.
- On macOS, the first dump may make the system ask for your password (access to developer tools). The dump waits until you approve it, and is abandoned with a message after 3 minutes.

## Settings

- `debug.justMyCode` (Just My Code): step through and break in your own code only.
- `debug.stepOverPropertiesAndOperators`: Step Into steps over property accessors and operators.
- `debug.perfTips`: the PerfTips at the end of the current line.
- `debug.console`: where a debugged console or web app's console is: `internalConsole` (the Debug Console), `integratedTerminal`, or `externalConsole` (a console window; Windows' default).
- `debug.containerTool`: the container command for attaching in containers (`docker`, `podman`, …).
- `debug.sourceFileMap`: source folders of programs on other machines, as `"/folder/there=C:\\folder\\here"`.

## Known limitations

Blazor WebAssembly apps, and the pages of ASP.NET Core apps, are debugged in the browser (see [Web development](web.md#debugging-the-server-and-the-page-together)). There, function breakpoints, the Modules window and the .NET exception settings don't apply.

These come from netcoredbg:
- no Edit and Continue, and no hot reload while debugging: the .NET runtime refuses Hot Reload's updates while a debugger is attached, so Start with Hot Reload and Start Debugging are separate runs (see [ADR 0040](../adr/0040-hot-reload-while-debugging.md))
- no data breakpoints
- no Set Next Statement and no Step Into Specific
- no Memory, Disassembly or Registers windows
- no mixed-mode debugging
- debugger type proxies (`[DebuggerTypeProxy]`) aren't applied: showing one means creating it, which netcoredbg can't evaluate. Use the Grid Visualizer for collections
- no Make Object ID, no return values after Step Out, and no freezing of threads
- no symbol servers or symbol paths: the debugger reads the PDBs next to the assemblies, and library sources come from embedded sources and Source Link
- Detach All applies to attached programs and to programs launched in the integrated terminal or a console window: a program launched with the Debug Console has the debugger's console, so it can't keep running without it
- dumps written on another operating system don't open
- no console input for a program debugged with the Debug Console (set `debug.console` to `integratedTerminal`)
- with several startup projects, a stop in one program doesn't pause the others
- remote debugging covers Linux and macOS targets with `sh`, `tar` and `gzip`; not Windows machines over SSH, nor Windows containers
- on macOS, the program's exit code is reported as 0
- no debugger for macOS on Intel

Hit counts, logpoints, filters, dependent and temporary breakpoints, exception module conditions and Run to Cursor are implemented by the IDE, so each hit pauses the program briefly.
