# Debugging

Codalus IDE debugs .NET programs with **its own debugger**, the Codalus debugger, built on .NET's own debugging API ([below](#the-codalus-debugger)). It debugs programs of every .NET version from .NET Core 2.0. On Apple silicon, the programs of .NET 5 and earlier run as x64 under Rosetta 2, and are debugged by the debugger's x64 build, which Codalus IDE downloads the first time you debug one; those of .NET Core 2.0 and 2.1 are attached to once they run, not held at their start ([Older .NET versions](dotnet-versions.md)). **netcoredbg**, the open-source .NET debugger from Samsung that Codalus IDE used before, is still bundled for one release as a fallback: set `debug.adapter` to `netcoredbg` if something goes wrong, and please report it.

On Windows, **.NET Framework** programs (and their tests) are debugged by Codalus IDE's own .NET Framework debugger, through the .NET Framework's debugging API: F5 on a .NET Framework app, Attach to Process on a .NET Framework process and Debug Test choose it by themselves, and the windows and commands below work the same. What it does and doesn't do is on the [.NET Framework](net-framework.md#debugging-on-windows) page. .NET MAUI apps on Android and iOS use the Mono debugger ([.NET MAUI](maui.md)).

## Starting

| Command | Shortcut |
|---|---|
| Start Debugging (builds the startup project first, using its launch profile) | `F5` |
| Attach to Process (the .NET and .NET Framework processes are listed first) | `Ctrl+Alt+P` |
| Reattach to Process (the last one again, by id while it runs, otherwise by name) | `Shift+Alt+P` |
| Attach to Process on a Remote Machine (over SSH, in a container, in WSL) | Debug menu |
| Stop (terminates a launched program, detaches from an attached one) | `Shift+F5` |
| Detach All (an attached program, or one launched in the terminal, keeps running) | Debug menu |
| Restart | `Ctrl+Shift+F5` |
| Break All | `Ctrl+Alt+Break` (`⌘⌥P` on macOS) |

**Break All** stops every thread, and the Call Stack shows the first thread running code of your own (or, when no thread is, the first that has managed frames). When no thread is running .NET code at all (a program that is still starting, or one running only native code), the Call Stack shows **[External Code]**: continue, and Break All again shows the program's code once it runs. Choose another thread in the Call Stack's thread list or the Threads window. Pressed while the debugger is still attaching to a program on this machine (one that is slow to attach to, such as a packaged WinUI app starting on a busy machine), Break All waits until the debugger has attached, up to a minute, then stops the program.

The program's output and the debugger's messages appear in the **Debug Console**.

### The program's console

On Windows a debugged console or web app gets a **console window** of its own, as in the classic IDE: it takes input, the window stays open after the program ends until you press a key, Stop closes it, and a web app's browser opens once its address answers. Elsewhere it writes to the Debug Console and gets no input. Set **`debug.console`** to `internalConsole`, `integratedTerminal` or `externalConsole` (Windows) to choose; Run Without Debugging has the same choice in **`run.console`** (`outputWindow`, `integratedTerminal`, `externalConsole`). With `integratedTerminal` to start it in a new integrated terminal instead: a real console that takes input, colors and all. Codalus IDE starts the program there, holds it before `Main` until the debugger has attached and set your breakpoints, then lets it run. Stop ends it; **Detach All** leaves it running in its terminal. On macOS, netcoredbg can't attach to a program held in the terminal, so programs use the Debug Console there, unless [the Codalus debugger](#the-codalus-debugger) debugs them: it starts the program in the terminal itself, on every OS, and stops on `Main`'s first line when a breakpoint is there.

### Several startup projects

Debug → **Configure Startup Projects** (or right-click the solution) opens the Startup Projects page:
- **Single startup project**: the project F5 and Ctrl+F5 start, as Set as Startup Project chooses.
- **Multiple startup projects**: give each project an action (**Start**, **Start without debugging** or **None**) and a launch profile, and order them with Move Up and Move Down. Press **Apply**.

F5 then builds the solution once and starts the projects in that order. Those set to Start are debugged in **one session**:
- the Call Stack, Threads and Debug Location toolbar name each thread after its program (`Partner: Main Thread`);
- the Processes window lists every program;
- each program stops at its own breakpoints. Under [the Codalus debugger](#the-codalus-debugger), a stop in one program (a breakpoint, a step's end, an exception) pauses the others too, as the classic IDE's "Break all processes when one process breaks": their threads and values can be read while you're stopped, and they go on when you continue or step (during a step they run, and pause again at its end). A program that is still starting isn't paused. Turn it off with `debug.breakAllProcesses`. Programs under netcoredbg keep running;
- Break All stops every program except a later one that is still starting, which keeps running (Break All waits for the first program to be attached to, as above);
- the session ends when the last program ends.

The choice is saved with the solution, for you only, in `.codalus/state/` next to it (not in the solution file; see [What a solution reopens with](solutions.md#what-a-solution-reopens-with)).

### Debugging on other machines

Debug → **Attach to Process on a Remote Machine** debugs a .NET program running elsewhere:
- **SSH**: enter `user@host` (with `:port` if it isn't 22). Your own `ssh` is used, with your keys, agent and `~/.ssh/config`; passwords and passphrases are asked in a dialog.
- **Docker or Podman container**: pick a running container. It goes through the [Containers window's engine](containers.md#the-container-engine) (`containers.tool`, else `docker` if it's on your `PATH`, else `podman`). To attach through another one, set `debug.containerTool` to its command: `docker`, `podman`, or one inside WSL such as `wsl -d Ubuntu --exec podman`.
- **WSL distribution** (Windows): pick a distribution.

Then pick the process (.NET processes come first). The first time, Codalus IDE offers to copy the debugger to the machine: into `~/.codalus/netcoredbg`, or `/tmp/.codalus/netcoredbg` in a container (the folder keeps its first name). The machine needs `tar` and `gzip`, but no internet access.
- On **Linux, x64 and Arm64**, it is the Codalus debugger, as a bundle with its own .NET runtime (about 15 MB to copy, 40 MB there), so the machine needs only the program's runtime. Everything the Codalus debugger does locally works there too, `[DebuggerDisplay]` and the members' icons included; the symbol settings and Hot Reload are for local programs.
- On **macOS on Apple silicon**, and with `debug.adapter` set to `netcoredbg`, it is netcoredbg (about 15 MB), and the Debug Console says so.

A **remote window** ([Remote development](remote.md)) debugs the same way: the first `F5` there asks to copy the debugger to the machine, and the server starts it.

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

Make the analyzer project the startup project and press F5. Codalus IDE runs the SDK's compiler on the target project, with the target's own command line, under the debugger. Breakpoints in your generator stop as it runs. The compiler's output goes to a temporary folder, so your project's build output isn't touched.

### Just-In-Time debugging

Debug → **Just-In-Time Debugging** turns crash dumps of your .NET programs on or off. It is off by default, and the dialog says exactly what it changes before you agree.

When it's on, a .NET program you start that crashes writes a full dump to the `logs/crash-dumps` folder of your user data. Codalus IDE then offers to **open the dump** (see [Dumps](#dumps)). It works through the .NET runtime's crash-dump variables, set for your user only, with no administrator rights:
- **Windows**: your user environment variables;
- **Linux**: `~/.config/environment.d` and `~/.profile`;
- **macOS**: `launchctl` and a login item.

Programs started after you turn it on are covered. Turning it off removes the variables. Attaching live to a crashing program, as a system-wide just-in-time debugger, would need administrator rights and isn't offered.

## Breakpoints

- **Add or remove**: click in the margin left of the line numbers (with the pointer over it, a faint circle shows the line a click adds one to), or press `F9` on the line. `Ctrl+F9` enables or disables it.
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
- **Who counts the hits**: under [the Codalus debugger](#the-codalus-debugger) the debugger itself applies hit counts, filters, dependencies and logpoints, so the program doesn't stop for a hit that doesn't count and a logpoint in a loop costs little. Under netcoredbg, Codalus IDE stops the program at each hit and continues it.

## Running to a line

- **Run to Cursor** (`Ctrl+F10`): continues to the caret's line. It stops earlier at a breakpoint on the way. With no debug session, it starts debugging and stops at the line.
- **Force Run to Cursor** (Debug menu): continues to the line, past every breakpoint on the way.
- **Run to Click**: while paused, `Ctrl`+click a line in the breakpoint margin.

## While stopped

Codalus IDE comes to the front when the program stops, even when the program's window or the browser had the focus, so `F10` and `F11` go to the debugger (in the browser, `F11` is its full screen).

- **Stepping**: Step Over (`F10`), Step Into (`F11`), Step Out (`Shift+F11`), Continue (`F5`).
- Under [the Codalus debugger](#the-codalus-debugger) also:
  - **Set Next Statement** (`Ctrl+Shift+F10`, or the editor's context menu): moves the current statement to the caret's line without running the code between. The line has to be in the method the thread is in, and the status bar says why when a line can't be chosen.
  - **Step Into Specific** (`Shift+Alt+F11`): lists the calls of the current line and steps into the one you pick, past the others (`Shout(Join("a", Twice("b")))` offers `Twice`, `Join` and `Shout`).
  - **Returned values**: after Step Out, or a step that leaves a method at its end, Locals shows what the method returned as its first row (`Shout returned`), and `$ReturnValue` reads it in Watch.
- **The current statement** is highlighted, with a yellow arrow in the margin.
- **PerfTips**: the time since the program was resumed appears in gray at the end of the current line, e.g. `≤ 12 ms elapsed`. It includes the debugger's own overhead, so use it to compare steps, not as a profiler. Turn it off with the `debug.perfTips` setting.
- **Locals** and **Autos**: Autos shows the variables used on the current and previous lines. Expand objects to see their members: they show as rows under the Name | Value | Type header, each level indented inside the Name column so values and types stay in their columns (drag the header's edges to resize them). What you expanded stays expanded when you step. `Right` and `Left` expand and collapse the selected row; `Ctrl+C` copies its value. At a session's first stop, Locals comes to the front of the tabs it shares with the Call Stack.
- **Watch**: add expressions in the box at the top, or with Debug → Add Watch on the selection. They are evaluated at every stop while the window shows (see [Windows behind other tabs](#windows-behind-other-tabs)) and saved with the solution. An expression that doesn't evaluate shows the error icon and the debugger's message in the error color; `Delete` removes the selected watch.
- **QuickWatch** (`Shift+F9`): evaluates the selection, or the expression under the caret, with its members as rows, as in Locals. Change the expression and press Enter to evaluate another; **Add Watch** copies it to Watch.
- **Changing values**: in Locals, Autos, Watch and QuickWatch, `F2`, `Enter` or a double-click on a value edits it in place, as in a data tip: a variable, a field or a property, or a watch expression that can be assigned. `Enter` sets it and `Escape` puts it back; when the debugger refuses the value, the box stays open with its message under it, and a value that can't be changed says why in its tooltip (see [Data tips](#data-tips)). Values that changed since the last stop are red.
- **The context menu** of a value copies it or its expression, adds a watch, or opens QuickWatch. Its **View** submenu opens a visualizer:
  - strings: **Text, JSON, XML or HTML Visualizer**, the whole string in a read-only, colored document, with JSON and XML indented;
  - arrays and collections (`List<T>`, `Queue<T>`, `Stack<T>`, `HashSet<T>`, `Dictionary<TKey, TValue>`, …): the **Grid Visualizer**, a table of up to 500 elements, with an object's properties and fields as columns (a dictionary's keys and values);
  - byte arrays: the **Image Visualizer**, which opens a PNG, JPEG, GIF, BMP, WebP or ICO image they hold in the image viewer;
  - plugins can add their own visualizers for other types (see [Plugins](../plugins.md#workbench-apis)).
- **Display attributes**: objects whose type has `[DebuggerDisplay]` show its text (`Order 7: Ada (1 lines)` instead of `{MyApp.Order}`), and members marked `[DebuggerBrowsable(Never)]` are hidden, in Locals, Autos, Watch, QuickWatch and data tips.
- **Call Stack** (`Ctrl+Alt+C`): the frames of the selected thread. Pick another thread at the top. Double-click a frame to see its source and its variables.
- **Threads** (`Ctrl+Alt+H`): every thread with its id, name and location. Double-click to switch to a thread; flag threads and show only the flagged ones. Under the Codalus debugger, the context menu's **Freeze** keeps a thread from running when the program goes on, and **Thaw** lets it run again.
- **Modules** (`Ctrl+Alt+U`): the loaded modules with their symbol status and path. Under the Codalus debugger, **Load Symbols** in a module's context menu looks its PDB up now (see [Symbols](#symbols)).
- **Object IDs**: under the Codalus debugger, a value's context menu has **Make Object ID**: the object gets a name (`$1`, shown as `{$1}` after its value) that Watch and conditions can use while it is out of scope, for as long as the program keeps it or you **Delete Object ID**. `$exception` is the thread's current exception.
- **Disassembly, Registers and Memory** (Debug → Windows), under the Codalus debugger: see [The machine's side](#the-machines-side).
- **Data tips**: hover over a variable or a member access to see its value; see [Data tips](#data-tips).
- **Pinned data tips**: the pin in a data tip's header or rows keeps that expression next to its line, as the data tip's header with a strip to unpin it. It is evaluated again at every pause, in red when the value changed, and keeps its last value, muted, after the program resumes or the session ends. While the program is paused, its chevron expands it into the same tips as the hover's, and its value is edited in place. Several pins on one line stack. Right-click it to add a comment (shown under it), copy, add a watch or unpin it; double-click its name for QuickWatch. Pinned tips follow your edits and are saved with the solution; Debug → Windows → Clear All DataTips removes them.
- **Parallel Stacks** (Debug → Windows): every thread's call stack in one tree. Threads that share callers are grouped until their stacks branch; the node with the active thread is marked.
- **Parallel Watch** (Debug → Windows): one expression evaluated on every thread, in the thread's topmost frame with source code.
- **Processes** (Debug → Windows): the process being debugged with its state, and Attach to Process, Detach All and Terminate All.

### Windows behind other tabs

Locals, Autos, Watch, QuickWatch, Threads, Parallel Stacks, Parallel Watch, Processes, Modules and Breakpoints ask the debugger nothing while they're hidden behind another tab or closed: at a stop, only the windows you can see read the program, as in the classic IDE. A window you bring forward while the program is paused reads it then, once, so it can take a moment to fill. Choosing another frame or thread moves the Threads window's marker without reading every thread again.
- **The Debug Location toolbar**: while paused, the standard toolbar shows the process and lets you pick the thread and the stack frame.
- **Debug Console**: type an expression and press Enter to evaluate it in the selected frame. Up and Down recall earlier ones.

## Data tips

Hover over a variable or a member access (`app`, `app.Properties`) while the program is stopped:
- **The header** sits right over the expression, its name on the code's: the icon of its kind (a local, a parameter, a field, a property), the name, and the value in a box. The type is in the name's tooltip. A string or a collection has a **View** drop-down: the magnifier opens the likely visualizer, the arrow lists them all (plugins' under **Plugins**).
- **Expanding**: rest the pointer on the chevron (about 400 ms), click it, or press `Right`. The members open in a tip of their own, under the header and 24 px in; each member with members of its own expands the same way, so each level is a tip. Moving to another row of a tip closes the tip the last row opened.
- **Rows** show the icon, the name and the value, trimmed at 400 px: rest on a value for all of it. A tip shows 15 rows, then scrolls with the wheel.
- **Members** are grouped as in the classic IDE, in Locals, Autos, Watch and QuickWatch too:
  - each has its kind's icon: a property's blue wrench, a field, a constant, an event;
  - the private, protected and internal members of your own code (the solution's projects) are listed with the others, in name order, with a lock on their icon; the name's tooltip says what each is ("Private field of WeatherForecastController (your code)");
  - a library type's non-public members are under **Non-Public members**, and **Static members** come last;
  - a property whose getter threw reads "threw *ExceptionType*: *message*" in the error color, and expands to the exception;
  - a value the debugger couldn't evaluate (a getter that ran past 5 seconds, or can't be called) reads "Couldn't evaluate": **Re-evaluate** asks again;
  - in a remote session (SSH, containers, WSL) the program's assemblies aren't on this machine: nothing is grouped, and icons are guessed from the names (`_name` and `name` are fields, `Name` a property).
- **Collections**: a `List<T>`, `Dictionary<TKey, TValue>`, `HashSet<T>`, `Queue<T>` or `Stack<T>` expands to its elements, then **Raw View**, which holds the collection's own members (its capacity, its count, its private fields). A dictionary's entries are named by their keys (`["server.Features"]`) and expand to Key and Value; a queue lists from its head, a stack from its top. netcoredbg can't build these types' debugger views (`[DebuggerTypeProxy]`), so the elements are read from the collections' fields, which differ between .NET versions, .NET Framework and Mono; a collection whose fields aren't known shows its own members. Other collections show their own members.
- **Long arrays**: up to 1,000 elements show at once. A longer array, and a collection's backing array under Raw View, shows ranges, `[0..999]`, `[1000..1999]` and so on, each read from the program when you expand it, in data tips, Locals, Autos, Watch, QuickWatch and pinned tips. Past 1,000,000 elements, ranges hold ranges. A debugger that sends a list whole shows its first 1,000 elements and a row saying how many more there are.
- **Type proxies and Results View**, under [the Codalus debugger](#the-codalus-debugger): a type with `[DebuggerTypeProxy]` shows its proxy's members (as the framework's other collections do: `LinkedList<T>`, `SortedSet<T>`, the concurrent and immutable ones), with **Raw View** for its own. A value that is only a sequence (`IEnumerable`, a LINQ query not yet run) has **Results View**, which runs it when you expand it.
- **Long lists**: the `[DebuggerDisplay]` text of the first 60 elements is shown at once; the others show their type, in gray, until they scroll into view. A tip builds only the rows on screen, and its Name column is as wide as the widest name of all its members, so it doesn't shift while you scroll.
- **From the keyboard**: `Up` and `Down` move in a tip, `Right` opens the selected row's tip, `Left` closes it or goes back, `F2` or `Enter` edits the value, `Ctrl+C` copies it, `Shift+F10` opens its menu and `Escape` closes the tips.
- **Editing**: click a value (in the header or a row), or press `F2` or `Enter`, to edit it in place; `Enter` sets it and `Escape` puts it back. When the debugger refuses what you typed (a name it doesn't know, a string without quotes), the box stays open with its message under it. A value that can't be changed opens no box: rest the pointer on it to see why (a property without a setter, a constant, a read-only field, or a Mono program, whose debugger can't change values).
- **Changed values** are red: a value that differs from what it was when the program last stopped (and you last saw it).
- **Pinning**: the pin at the end of the row under the pointer (and in the header) pins that expression to the line. Groups and members only the debugger names (`Name (Base)`) have no pin.
- **Near the edges**: near the editor's bottom the tips open upwards, and near its right edge they move left, their values trimmed sooner.
- **The code under the header**: a double-click on the header's name selects the word, `Ctrl+Click` goes to its definition, and the wheel scrolls the editor (and closes the tips). Hold `Ctrl` to fade the tips and click through them to the code; let go to bring them back.
- **Errors and warnings** at the hovered name show in a strip right above the header: the code (a link to its documentation), the message, **Fix** and **Other Fixes**, as in Quick Info. When the tips open upwards near the editor's bottom edge, the strip moves under the header.

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
- Dumps open on the operating system that wrote them, because the runtime's debugging library has to load on the machine reading them. It comes from a .NET installation with the dump's runtime version (`dotnet --list-runtimes`): on Linux and macOS, a dump of a runtime that isn't installed (a self-contained app's) doesn't open.
- On macOS, the first dump may make the system ask for your password (access to developer tools). The dump waits until you approve it, and is abandoned with a message after 3 minutes.

## The Codalus debugger

The Codalus debugger is Codalus IDE's own .NET debugger, on .NET's own debugging API (the one its .NET Framework debugger uses, through the runtime's `dbgshim`). It is the default: Start Debugging, Attach to Process, debugging tests, several startup projects, remote targets and containers use it, and the first line in the Debug Console says which debugger a session uses.

**netcoredbg is the fallback** for one release: set **`debug.adapter`** to `netcoredbg` (Options › Debugging › **Debugger for .NET programs**) and the next session uses it. It will be removed once the Codalus debugger has been the default for a release, so tell us what made you switch.

What the Codalus debugger does that netcoredbg doesn't:
- **Hot Reload and Edit and Continue**: edits apply to the program you're debugging, and the method you're stopped in goes on in its new code ([below](#hot-reload-while-debugging))
- hit counts, filters, dependencies and logpoints applied by the debugger, with no stop for a hit that doesn't count ([Breakpoints](#breakpoints))
- Set Next Statement, Step Into Specific, returned values, frozen threads and object IDs ([While stopped](#while-stopped))
- the whole of C# in Watch, QuickWatch, data tips and conditions, and Visual Basic in Visual Basic code ([Expressions](#expressions))
- `[DebuggerTypeProxy]`, Results View and Raw View ([Data tips](#data-tips))
- with several startup projects, a stop in one program pauses the others
- the Disassembly, Registers and Memory windows ([The machine's side](#the-machines-side))
- symbol paths and symbol servers ([Symbols](#symbols)), and Windows PDBs
- the program's own exit code on macOS (netcoredbg reports 0 there)
- programs in the integrated terminal or a console window start under the debugger, so a breakpoint on `Main`'s first line stops there; the integrated terminal works on macOS too, and no startup hook is needed
- exception module conditions on .NET 11 programs

Where netcoredbg still debugs, and the Debug Console says why:
- remote machines running macOS (the Codalus debugger's bundle ships for Linux x64 and Arm64)
- x64 programs on Apple silicon, when you decline the download of the debugger's x64 build

**The debugger's other builds** are downloaded when they are first needed, after you agree, from the release of your version (about 15 MB each); the [Components page](settings.md) lists them, and can install or remove them:
- on Apple silicon, the **x64 build**, for programs that run as x64 under Rosetta 2 (.NET 5 and earlier);
- on Windows, the **32-bit build**, for .NET Framework programs built for x86 or with `Prefer32Bit` ([.NET Framework](net-framework.md#debugging-on-windows)).

If a session goes wrong, start Codalus IDE with `CODALUS_CLR_DEBUGGER_LOG` set to a folder: each session writes its log there, to attach to a report.

### Expressions

The debugger reads most expressions itself, at once: names, members, indexers, method calls (generic ones too), operators, `?:`, `??`, casts, `new`, and its own names (`$exception`, `$ReturnValue`, object IDs like `$1`). What it doesn't read, it has the C# language service compile against the place you're stopped at and runs the result in your program: **lambdas and LINQ** (`orders.Where(o => o.Total > 100).Count()`), extension methods, collection and object initializers, pattern matching, string interpolation, and the rest of C#. Private members can be used, as everywhere in the debugger.

- In **Visual Basic** code, expressions are Visual Basic (`names.Where(Function(n) n.Length > 3).Count()`, `If(x Is Nothing, 0, x.Length)`).
- Compiling needs the program's project loaded in the solution. Attached to a program whose project isn't open, the debugger's own reader still works.
- A compiled expression is a small assembly loaded into your program; it stays until the program ends, and the Modules window leaves it out.
- F# expressions aren't compiled: F# code reads C#'s subset.

### The machine's side

Debug → Windows has three windows for the code as the JIT compiled it. They show managed code, in the stopped frame:

- **Disassembly**: the current method's instructions under their source lines, the frame's instruction marked. **Step Over Instruction** and **Step Into Instruction** step one instruction. x64 and arm64 are decoded.
- **Registers**: the frame's general registers, in red when a step changed them.
- **Memory**: the bytes at an address or at an expression's object (`text`, `0x7f3a10002000`), a page at a time, with their characters; **Write** puts the hex bytes you type at the address.

There are no data breakpoints and no mixed-mode (native) debugging.

### Symbols

The debugger reads the PDB next to a module, the one the module embeds, or the one still where the build wrote it, in the portable format and in Windows' older one. For a module without one, **Options › Debugging** has the classic IDE's symbol settings:

- **Symbol paths** (`debug.symbols.paths`): folders that hold PDBs, loose or in a symbol store's layout.
- **Symbol servers**: Microsoft's (`debug.symbols.microsoftServer`), NuGet.org's (`debug.symbols.nugetServer`) and your own URLs (`debug.symbols.servers`). **All are off or empty until you turn them on**: a server is asked nothing before that. What a server is sent is a module's PDB name and build ID, nothing of your code.
- **The cache** (`debug.symbols.cacheFolder`): where downloaded PDBs are kept, so each is fetched once.
- **Which modules**: every module but those in `debug.symbols.exclude`, or with `debug.symbols.loadOnlySpecified` only those in `debug.symbols.include` (`Newtonsoft.*.dll`). With Just My Code on, servers are asked by themselves only for the modules you list; **Load Symbols** in the Modules window asks for any module, and its symbol status says where the PDB was found or why not.

Symbol paths and the cache are read as a module loads; servers are asked in the background, and the module's status changes when its PDB arrives.

### Hot Reload while debugging

Under the Codalus debugger, C#, Visual Basic and Razor edits apply to the program while you debug it ([ADR 0086](../adr/0086-a-managed-coreclr-debugger.md)):
- **Apply Code Changes** (`Alt+F10`, or the toolbar's Hot Reload button) applies the editors' text as it is, saved or not.
- **Continue**, a step or Run to Cursor applies the edits you made while stopped, before the program goes on.
- **Saving a file** applies it while the program runs, with **Hot Reload while debugging** (`debug.hotReload`) on, as it is by default.

The status bar's Hot Reload item says when the edits last applied, and the Debug Console says each time. Breakpoints bind in the new code, so a breakpoint on a line you just added stops there.

**Edit and Continue.** An edit you make **while the program is stopped** also reaches the methods that are running:
- the method you're stopped in goes on in its new code, from the statement you were at, with its locals. The current statement moves with your edit as soon as it's applied, and a step from there steps through the new code;
- a method further down the call stack, waiting for a call to return, finishes that call's statement as it was and goes on in the new code from the next statement. You can't change the statement it is waiting in: that needs a restart, and Codalus IDE asks.
- When the code after such a call branches at once (the call is an `if`'s condition, say), the method goes on in its old version until it reaches the call again; its next call runs the new code either way.

An edit applied **while the program runs** is Hot Reload's: a changed method runs its new code from its next call, and a method already running finishes in its old version.

Some edits can't be applied to a running program (changing a class's base type, say). Codalus IDE then asks: **Restart** builds and debugs the program again, and **Continue with the Old Code** goes on with the code it started with. The edit's diagnostics are in the Error List, under the source *Hot Reload*. Code that doesn't compile isn't applied: its errors are there too.

Edits apply to programs started in the Debug Console, the integrated terminal or a console window, to tests you debug, and to a library the program hasn't loaded yet (they apply as it loads). After each one, the program's `[MetadataUpdateHandler]`s run, as under `dotnet watch`: Blazor components re-render with the new code, and other frameworks that refresh on Hot Reload do too.

A **Blazor Web App** takes edits in its server and in the browser ([ADR 0090](../adr/0090-blazor-web-app-edits-under-the-codalus-debugger.md)): `F5` starts the server under the Codalus debugger (no `dotnet watch`, so breakpoints in its startup code stop), and Codalus IDE sends edits to code that runs in the browser, its WebAssembly components and the projects they use, to every open page. The status bar says where the edit went: *(server and browser)*, *(no page open)* (a page opened later gets it), or that the browser couldn't apply it, with **Restart** offered. This needs WebAssembly components that target .NET 10 or later, and a .NET 10 SDK (SDK 11's browser tools aren't supported yet). Otherwise, edits to the server still apply, and edits to code that runs in the browser say they need a restart.

A program you **attach** to takes edits only if it was started with `DOTNET_MODIFIABLE_ASSEMBLIES=debug` (as `dotnet watch` starts programs); Apply Code Changes says so when it wasn't. F# and XAML edits run after a restart.

If you chose netcoredbg, the first edit you save while debugging offers the Codalus debugger again, once; **Don't Show Again** turns the offer off (`debug.codalusOffer`).

### Async code and runtime async (.NET 11)

The Codalus debugger steps through `async` methods as you read them, whether the compiler built them as state machines (every .NET version) or for **runtime async** (.NET 11 with `<Features>$(Features);runtime-async=on</Features>` in the project), where the runtime suspends and resumes the method itself ([ADR 0100](../adr/0100-debugging-runtime-async-code.md)):

- **Step Over an `await`** stops on the method's next line once it resumes, on whichever thread it resumes on: the Threads window and the Call Stack follow it there. Code that runs meanwhile (the caller, other requests) doesn't stop the step, but a breakpoint does.
- **Step Into** an awaited method stops on its opening brace; for a state machine the compiler's code that starts it is stepped through.
- **Step Out** of an async method, or a step over its closing brace, goes back to the method that awaited it once that resumes, also when the method itself had resumed after an await (its caller is then the thread pool, not the awaiting method). For a state machine, Locals' first row and `$ReturnValue` show the value it returned.
- **Breakpoints and Run to Cursor** after an `await` stop there, with the method's parameters, locals and `this`; so does an exception thrown after one, with the throwing method on top of the call stack.
- **The call stack** of a resumed method starts with it; the thread pool's and the runtime's frames below it are folded into [External Code] under Just My Code.
- **Set Next Statement** moves within the method after an `await` as anywhere else.
- Libraries built either way await each other: breakpoints and steps work across them.

What runtime async doesn't give the debugger yet (reported to the .NET runtime; until it ships):
- **Step Out of a runtime-async method that has resumed** (and a step over its closing brace) stops in the method that awaited it only where that method next calls code of yours, and without the returned value; when it calls none, the program runs on as with Continue. The same holds for a state machine awaited by runtime-async code.
- **The awaiting methods** aren't in the call stack once a method has resumed, for either kind (the classic IDE's async call stack): only what resumed it is.
- A step over an `await` in a **static** runtime-async method can stop in another call of the same method, on another thread, when that one reaches one of its lines first.

Edit and Continue after an `await` and the profiler's .NET Async view haven't been checked on runtime-async code yet.

## Settings

- `debug.adapter`: the debugger for .NET programs, `codalus` ([the Codalus debugger](#the-codalus-debugger), the default) or `netcoredbg` (the fallback, for one release).
- `debug.hotReload` (Hot Reload while debugging): saving a file applies the edit to the program being debugged ([under the Codalus debugger](#hot-reload-while-debugging); a Blazor app's WebAssembly components under either debugger). Off, a Blazor Web App's WebAssembly edits wait for a restart.
- `debug.codalusOffer`: offer the Codalus debugger when you save an edit while netcoredbg debugs.
- `debug.breakAllProcesses`: with several startup projects, a stop in one program pauses the others (the Codalus debugger's programs; on by default).
- `debug.symbols.*`: symbol paths, symbol servers (off by default), the cache and the modules to look up, for [the Codalus debugger](#symbols).
- `debug.justMyCode` (Just My Code): step through and break in your own code only.
- `debug.stepOverPropertiesAndOperators`: Step Into steps over property accessors and operators.
- `debug.perfTips`: the PerfTips at the end of the current line.
- `debug.console`: where a debugged console or web app's console is: `internalConsole` (the Debug Console), `integratedTerminal`, or `externalConsole` (a console window; Windows' default).
- `debug.containerTool`: the container command for attaching in containers (`docker`, `podman`, …); empty (the default) uses the Containers window's engine.
- `debug.sourceFileMap`: source folders of programs on other machines, as `"/folder/there=C:\\folder\\here"`.

## Known limitations

Blazor WebAssembly apps, and the pages of ASP.NET Core apps, are debugged in the browser (see [Web development](web.md#debugging-the-server-and-the-page-together)). There, function breakpoints, the Modules window and the .NET exception settings don't apply.

The Codalus debugger's own limits:
- no data breakpoints and no mixed-mode (native) debugging; the Disassembly, Registers and Memory windows show managed code
- remote machines running macOS are debugged by netcoredbg; Hot Reload and the symbol settings are for local programs
- symbols found on a server give a module its lines and names; its source files still come from your disk (embedded sources and Source Link aren't read by the debugger yet)
- F# expressions are read as C#'s subset
- Edit and Continue doesn't cover modules built with Windows PDBs
- no async call stack, and runtime async's limits for Step Out of a resumed method ([Async code](#async-code-and-runtime-async-net-11))

These are netcoredbg's, when you choose it as the fallback; [the Codalus debugger](#the-codalus-debugger) does the first eight:
- no Edit and Continue, and no hot reload while debugging a program netcoredbg runs: the .NET runtime refuses Hot Reload's updates while a debugger is attached, so Start with Hot Reload and Start Debugging are separate runs (see [ADR 0040](../adr/0040-hot-reload-while-debugging.md)). Blazor WebAssembly components are the exception, standalone or in a Blazor Web App: they run in the browser, and Hot Reload applies edits to them while you debug ([Web development](web.md#hot-reload)). A Blazor Web App's server still follows the runtime's rule: edits to it take effect after Restart.
- no Set Next Statement and no Step Into Specific
- no Memory, Disassembly or Registers windows
- debugger type proxies (`[DebuggerTypeProxy]`) aren't applied: showing one means creating it, which netcoredbg can't evaluate. `List<T>`, `Dictionary<TKey, TValue>`, `HashSet<T>`, `Queue<T>` and `Stack<T>` show their elements anyway (see [Data tips](#data-tips)); other collections show their own members, and the Grid Visualizer shows the ones with a `ToArray()`. There's no Results View
- no Make Object ID, no return values after Step Out, and no freezing of threads
- no symbol servers or symbol paths: the debugger reads the PDBs next to the assemblies, and library sources come from embedded sources and Source Link
- no `new`, lambdas or generic methods in expressions, and hit counts and logpoints stop the program at each hit
- with several startup projects, a stop in one program doesn't pause the others (on Windows, a program netcoredbg paused that way didn't always go on)
- no data breakpoints and no mixed-mode debugging
- Detach All applies to attached programs and to programs launched in the integrated terminal or a console window: a program launched with the Debug Console has the debugger's console, so it can't keep running without it
- dumps written on another operating system don't open
- no console input for a program debugged with the Debug Console (set `debug.console` to `integratedTerminal`)
- remote debugging covers Linux and macOS targets with `sh`, `tar` and `gzip`; not Windows machines over SSH, nor Windows containers
- on macOS, the program's exit code is reported as 0
- no debugger for macOS on Intel
- .NET 11 programs debug with the bundled netcoredbg (3.2.0), except exception module conditions: it gives their stack frames no module, so a condition can't tell where an exception was thrown: one that names modules (`MyApp.dll`) doesn't break, and one that only leaves modules out (`!System.*`) always breaks

Under netcoredbg, hit counts, logpoints, filters and dependent breakpoints are implemented by the IDE, so each hit pauses the program briefly; temporary breakpoints, exception module conditions and Run to Cursor are the IDE's under both debuggers.

The .NET Framework debugger (Windows) has these limits of its own: 32-bit apps (x86 or `Prefer32Bit`) need the debugger's 32-bit build, a download; no integrated terminal (the Debug Console or a console window), and Edit and Continue, data breakpoints and mixed-mode debugging aren't available. It shares the Codalus debugger's code, so it offers the same breakpoints, stepping, expressions and windows; of those, Codalus's CI tests on .NET Framework what the [.NET Framework](net-framework.md#debugging-on-windows) page lists.
