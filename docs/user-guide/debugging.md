# Debugging

Codalus IDE debugs .NET programs with **netcoredbg**, the open-source .NET debugger from Samsung, which is bundled with the packages. It debugs programs of every .NET version from .NET Core 2.0. On Apple silicon, the programs of .NET 5 and earlier run as x64 under Rosetta 2, and the macOS package bundles the x64 netcoredbg for them ([Older .NET versions](dotnet-versions.md)). Codalus IDE also has a .NET debugger of its own, a preview you can turn on: [the Codalus debugger](#the-codalus-debugger-preview).

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

On Windows a debugged console or web app gets a **console window** of its own, as in the classic IDE: it takes input, the window stays open after the program ends until you press a key, Stop closes it, and a web app's browser opens once its address answers. Elsewhere it writes to the Debug Console and gets no input. Set **`debug.console`** to `internalConsole`, `integratedTerminal` or `externalConsole` (Windows) to choose; Run Without Debugging has the same choice in **`run.console`** (`outputWindow`, `integratedTerminal`, `externalConsole`). With `integratedTerminal` to start it in a new integrated terminal instead: a real console that takes input, colors and all. Codalus IDE starts the program there, holds it before `Main` until the debugger has attached and set your breakpoints, then lets it run. Stop ends it; **Detach All** leaves it running in its terminal. On macOS, netcoredbg can't attach to a program held in the terminal, so programs use the Debug Console there, unless [the Codalus debugger](#the-codalus-debugger-preview) debugs them: it starts the program in the terminal itself, on every OS, and stops on `Main`'s first line when a breakpoint is there.

### Several startup projects

Debug → **Configure Startup Projects** (or right-click the solution) opens the Startup Projects page:
- **Single startup project**: the project F5 and Ctrl+F5 start, as Set as Startup Project chooses.
- **Multiple startup projects**: give each project an action (**Start**, **Start without debugging** or **None**) and a launch profile, and order them with Move Up and Move Down. Press **Apply**.

F5 then builds the solution once and starts the projects in that order. Those set to Start are debugged in **one session**:
- the Call Stack, Threads and Debug Location toolbar name each thread after its program (`Partner: Main Thread`);
- the Processes window lists every program;
- each program stops at its own breakpoints, and the others keep running;
- Break All stops every program except a later one that is still starting, which keeps running (Break All waits for the first program to be attached to, as above);
- the session ends when the last program ends.

The choice is saved with the solution, for you only, in `.codalus/state/` next to it (not in the solution file; see [What a solution reopens with](solutions.md#what-a-solution-reopens-with)).

### Debugging on other machines

Debug → **Attach to Process on a Remote Machine** debugs a .NET program running elsewhere:
- **SSH**: enter `user@host` (with `:port` if it isn't 22). Your own `ssh` is used, with your keys, agent and `~/.ssh/config`; passwords and passphrases are asked in a dialog.
- **Docker or Podman container**: pick a running container. It goes through the [Containers window's engine](containers.md#the-container-engine) (`containers.tool`, else `docker` if it's on your `PATH`, else `podman`). To attach through another one, set `debug.containerTool` to its command: `docker`, `podman`, or one inside WSL such as `wsl -d Ubuntu --exec podman`.
- **WSL distribution** (Windows): pick a distribution.

Then pick the process (.NET processes come first). The first time, Codalus IDE offers to copy the debugger (netcoredbg, about 15 MB) to the machine: into `~/.codalus/netcoredbg`, or `/tmp/.codalus/netcoredbg` in a container. The machine needs `tar` and `gzip`, but no internet access. Linux on x64 and Arm64, and macOS on Apple silicon, are supported.

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

## Running to a line

- **Run to Cursor** (`Ctrl+F10`): continues to the caret's line. It stops earlier at a breakpoint on the way. With no debug session, it starts debugging and stops at the line.
- **Force Run to Cursor** (Debug menu): continues to the line, past every breakpoint on the way.
- **Run to Click**: while paused, `Ctrl`+click a line in the breakpoint margin.

## While stopped

Codalus IDE comes to the front when the program stops, even when the program's window or the browser had the focus, so `F10` and `F11` go to the debugger (in the browser, `F11` is its full screen).

- **Stepping**: Step Over (`F10`), Step Into (`F11`), Step Out (`Shift+F11`), Continue (`F5`).
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
- **Threads** (`Ctrl+Alt+H`): every thread with its id, name and location. Double-click to switch to a thread; flag threads and show only the flagged ones.
- **Modules** (`Ctrl+Alt+U`): the loaded modules with their symbol status and path.
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

## The Codalus debugger (preview)

Codalus IDE has a .NET debugger of its own, on .NET's own debugging API (the one its .NET Framework debugger uses, through the runtime's `dbgshim`). netcoredbg stays the default while it matures: to try it, set **`debug.adapter`** to `codalus` (Options › Debugging › **Debugger for .NET programs**). It applies from the next session, to Start Debugging, Attach to Process, debugging tests and several startup projects, and the first line in the Debug Console says which debugger a session uses.

What it does that netcoredbg doesn't:
- **Hot Reload while debugging**: edits apply to the program you're debugging (below)
- the program's own exit code on macOS (netcoredbg reports 0 there)
- programs in the integrated terminal or a console window start under the debugger, so a breakpoint on `Main`'s first line stops there; the integrated terminal works on macOS too, and no startup hook is needed
- casts and C#'s operators in Watch, QuickWatch and conditions
- exception module conditions on .NET 11 programs

What it doesn't do yet, so netcoredbg debugs these and the Debug Console says why:
- x64 programs on Apple silicon (.NET 5 and earlier under Rosetta 2)
- remote targets: SSH, containers and WSL

Its expressions are the .NET Framework debugger's C# subset: names, members, indexers, method calls, operators, `?:`, `??` and casts, but no `new`, lambdas or generic methods. If a session goes wrong, start Codalus IDE with `CODALUS_CLR_DEBUGGER_LOG` set to a folder: each session writes its log there, to attach to a report.

### Hot Reload while debugging

Under the Codalus debugger, C#, Visual Basic and Razor edits apply to the program while you debug it ([ADR 0086](../adr/0086-a-managed-coreclr-debugger.md)):
- **Apply Code Changes** (`Alt+F10`, or the toolbar's Hot Reload button) applies the editors' text as it is, saved or not.
- **Continue**, a step or Run to Cursor applies the edits you made while stopped, before the program goes on.
- **Saving a file** applies it while the program runs, with **Hot Reload while debugging** (`debug.hotReload`) on, as it is by default.

The status bar's Hot Reload item says when the edits last applied, and the Debug Console says each time. As with `dotnet watch`, a changed method runs its new code from its next call: a method already running (the one you're stopped in, say) finishes in its old version. Breakpoints bind in the new code, so a breakpoint on a line you just added stops there.

Some edits can't be applied to a running program (changing a class's base type, say). Codalus IDE then asks: **Restart** builds and debugs the program again, and **Continue with the Old Code** goes on with the code it started with. The edit's diagnostics are in the Error List, under the source *Hot Reload*. Code that doesn't compile isn't applied: its errors are there too.

Edits apply to programs started in the Debug Console, the integrated terminal or a console window, to tests you debug, and to a library the program hasn't loaded yet (they apply as it loads). A **Blazor Web App**'s server, which `dotnet watch` runs, takes them in place too, while `dotnet watch` updates its WebAssembly components in the browser; an edit that needs a restart restarts the app, and debugging follows it. A program you **attach** to takes edits only if it was started with `DOTNET_MODIFIABLE_ASSEMBLIES=debug` (as `dotnet watch` starts programs); Apply Code Changes says so when it wasn't. F# and XAML edits run after a restart.

Under netcoredbg, the first edit you save while debugging offers the Codalus debugger, once; **Don't Show Again** turns the offer off (`debug.codalusOffer`).

## Settings

- `debug.adapter`: the debugger for .NET programs, `netcoredbg` (the default) or `codalus` ([the Codalus debugger](#the-codalus-debugger-preview), a preview).
- `debug.hotReload` (Hot Reload while debugging): saving a file applies the edit to the program being debugged ([under the Codalus debugger](#hot-reload-while-debugging); a Blazor app's WebAssembly components under either debugger).
- `debug.codalusOffer`: offer the Codalus debugger when you save an edit while netcoredbg debugs.
- `debug.justMyCode` (Just My Code): step through and break in your own code only.
- `debug.stepOverPropertiesAndOperators`: Step Into steps over property accessors and operators.
- `debug.perfTips`: the PerfTips at the end of the current line.
- `debug.console`: where a debugged console or web app's console is: `internalConsole` (the Debug Console), `integratedTerminal`, or `externalConsole` (a console window; Windows' default).
- `debug.containerTool`: the container command for attaching in containers (`docker`, `podman`, …); empty (the default) uses the Containers window's engine.
- `debug.sourceFileMap`: source folders of programs on other machines, as `"/folder/there=C:\\folder\\here"`.

## Known limitations

Blazor WebAssembly apps, and the pages of ASP.NET Core apps, are debugged in the browser (see [Web development](web.md#debugging-the-server-and-the-page-together)). There, function breakpoints, the Modules window and the .NET exception settings don't apply.

These come from netcoredbg:
- no Edit and Continue, and no hot reload while debugging a program netcoredbg runs: the .NET runtime refuses Hot Reload's updates while a debugger is attached, so Start with Hot Reload and Start Debugging are separate runs (see [ADR 0040](../adr/0040-hot-reload-while-debugging.md)). [The Codalus debugger](#hot-reload-while-debugging) applies edits while debugging. Blazor WebAssembly components are the exception, standalone or in a Blazor Web App: they run in the browser, and Hot Reload applies edits to them while you debug ([Web development](web.md#hot-reload)). A Blazor Web App's server still follows the runtime's rule: edits to it take effect after Restart.
- no data breakpoints
- no Set Next Statement and no Step Into Specific
- no Memory, Disassembly or Registers windows
- no mixed-mode debugging
- debugger type proxies (`[DebuggerTypeProxy]`) aren't applied: showing one means creating it, which netcoredbg can't evaluate. `List<T>`, `Dictionary<TKey, TValue>`, `HashSet<T>`, `Queue<T>` and `Stack<T>` show their elements anyway (see [Data tips](#data-tips)); other collections show their own members, and the Grid Visualizer shows the ones with a `ToArray()`. There's no Results View
- no Make Object ID, no return values after Step Out, and no freezing of threads
- no symbol servers or symbol paths: the debugger reads the PDBs next to the assemblies, and library sources come from embedded sources and Source Link
- Detach All applies to attached programs and to programs launched in the integrated terminal or a console window: a program launched with the Debug Console has the debugger's console, so it can't keep running without it
- dumps written on another operating system don't open
- no console input for a program debugged with the Debug Console (set `debug.console` to `integratedTerminal`)
- with several startup projects, a stop in one program doesn't pause the others
- remote debugging covers Linux and macOS targets with `sh`, `tar` and `gzip`; not Windows machines over SSH, nor Windows containers
- on macOS, the program's exit code is reported as 0
- no debugger for macOS on Intel
- .NET 11 programs debug with the bundled netcoredbg (3.2.0), except exception module conditions: it gives their stack frames no module, so a condition can't tell where an exception was thrown: one that names modules (`MyApp.dll`) doesn't break, and one that only leaves modules out (`!System.*`) always breaks

Hit counts, logpoints, filters, dependent and temporary breakpoints, exception module conditions and Run to Cursor are implemented by the IDE, so each hit pauses the program briefly.

The .NET Framework debugger (Windows) has these limits of its own: 64-bit processes only (not x86 or `Prefer32Bit` apps), portable PDBs only, no integrated terminal (the Debug Console or a console window), expressions limited to a C# subset (no `new`, lambdas, generic methods or assignments), and Edit and Continue, Set Next Statement, data breakpoints and mixed-mode debugging aren't available ([.NET Framework](net-framework.md#debugging-on-windows)).
