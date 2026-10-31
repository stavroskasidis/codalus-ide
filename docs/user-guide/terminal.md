# Terminal and C# Interactive

## Terminal

**View > Terminal** (`` Ctrl+` ``) opens the integrated terminal at the bottom of the window. It starts in the solution's folder, or in your home folder when no solution is open.
- `` Ctrl+` `` again hides it.
- `` Ctrl+Shift+` `` opens another terminal.

**Shells (profiles)**
- Codalus IDE finds the shells installed on your machine: PowerShell (`pwsh`), Windows PowerShell and Command Prompt on Windows; bash, zsh and fish on Linux and macOS.
- **.NET Developer** starts your default shell with the .NET SDK of the open solution first on `PATH` (`DOTNET_ROOT` set).
- Choose the default with the `terminal.defaultProfile` setting. Or pick a shell with **Terminal: New Terminal** from the command palette.

**Splits and tabs**
- Each terminal is a tab in the Terminal window.
- **Split Terminal** (`Ctrl+Shift+5`, `Cmd+\` on macOS) puts a second terminal beside the current one.
- `Alt+Left` and `Alt+Right` move between terminals.
- **Kill Terminal** ends the shell and closes the tab.

**Shell integration** (bash, zsh and PowerShell; `terminal.shellIntegration`, on by default)
- The terminal knows the shell's current folder: a new split starts there.
- It knows where each command starts and ends, and its exit code: a mark in the gutter turns red when a command fails.
- Your own startup files (`.bashrc`, `.zshrc`, your PowerShell profile) still run.

**Links**
- `Ctrl`+click a URL to open it in your browser.
- `Ctrl`+click a `path(line,col)` or `path:line:col` reference, which compilers and test runners print, to open the file at that line. Relative paths are resolved against the terminal's current folder.

**Find, copy and paste**
- `Ctrl+F` finds text in the terminal and its scrollback. A long scrollback is searched a slice at a time, so typing and the program's output go on meanwhile, and a newer search replaces the one still running.
- Select with the mouse to copy:
  - On Windows and Linux, `Ctrl+C` copies when there is a selection; otherwise it interrupts the running program. `Ctrl+Shift+C` always copies.
  - On macOS, use `Cmd+C`.
- `Ctrl+V` pastes on Windows, `Cmd+V` on macOS and `Ctrl+Shift+V` on Linux, where `Ctrl+V` stays with the shell. Multi-line pastes are bracketed, so shells don't run them line by line.
- `terminal.copyOnSelect` copies as soon as you select.

**Full-screen programs**
- `vim`, `htop`, `less` and similar programs work: alternate screen, mouse reporting, true colour, wide characters and emoji.
- Hold `Shift` to select text while a program uses the mouse.

**Keys**
- Most keys go to the shell while a terminal has focus. A short list stays with the IDE: the search box (`Ctrl+Q`) and the command palette (`Ctrl+Shift+P`), build, debug and run, focus movement and the terminal's own commands.
- Change that list with `terminal.commandsToSkipShell`.

**Appearance**
- The terminal uses the IDE's theme colours, including High Contrast.
- It uses the editor's font, unless you set `terminal.fontFamily` and `terminal.fontSize`.
- `terminal.scrollback` sets how many lines each terminal keeps (default 10,000).

**Screen readers**
- The terminal exposes its visible text to screen readers (UI Automation on Windows, the accessibility API on macOS and AT-SPI on Linux).

**Running programs in the terminal**
- Set `run.console` to `integratedTerminal` to run console apps there with **Start Without Debugging** (`Ctrl+F5`), so they can read input.
- The default, `outputWindow`, shows their output in the Output window. `externalConsole` opens a separate console window.

## C# Interactive

**View > C# Interactive** opens a C# read-eval-print loop. Type C# and press `Enter`:
- statements run;
- an expression without a semicolon prints its value;
- variables, methods and types you declare stay available for later submissions.

**Input**

| Key | Action |
|---|---|
| `Enter` | Runs the input when it is a complete submission; otherwise starts a new line (for example after `if (x) {`). |
| `Ctrl+Enter` | Always runs. |
| `Shift+Enter` | Always starts a new line. |
| `Alt+Up`, `Alt+Down` | Previous and next submission from the history. |
| `Escape` | Clears the input. |
| `Ctrl+Space`, `Ctrl+Shift+Space` | Completion and parameter info. They know everything submitted so far. |

**Directives**
- `#r "path/to/Library.dll"` references an assembly.
- `#r "nuget: Newtonsoft.Json, 13.0.3"` downloads a package and its dependencies. Leave out the version for the latest stable one. Package sources and credentials come from the `nuget.config` around the working folder, as in the package manager.
- `#load "helpers.csx"` runs a script file.
- Relative paths are relative to the working folder.

**Starting over**
- **Reset** (the window's toolbar, or **C# Interactive: Reset**) discards everything and starts fresh. It also stops a submission that runs forever.
- **Clear** empties the window but keeps the state.

**Working with your code**
- **Send Selection to Interactive** (`Ctrl+E Ctrl+E`, or the editor's context menu) runs the selected code, or the current line, in C# Interactive.
- **Initialize Interactive with Project** (a project's context menu in Solution Explorer) builds the project, then resets C# Interactive with the project's assembly and references. The working folder becomes the project's folder. Then you can call your own code:

  ```csharp
  > Greetings.Greeter.Greet("the REPL")
  "Hello from the REPL"
  ```

**Isolation**
- Your code runs in a separate process (`codalus-replhost`), never in the IDE itself.
- If it crashes or calls `Environment.Exit`, C# Interactive restarts with an empty state and says so. The IDE keeps running.
