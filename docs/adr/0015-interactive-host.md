# 0015. C# Interactive: the REPL host

- **Status:** accepted (M10 plan, 2026-10-09; the maintainer's standing go-ahead for M9–M16)
- **Date:** 2026-10-09
- **Deciders:** maintainer

## Context

M10 asks for a C# Interactive window like the classic IDE's:
- A REPL on Roslyn scripting, with its own editor input: highlighting, completion and signature help. The roadmap suggested serving these from `daedalus-lshost` as script documents.
- A history.
- `#r` and `#load`, with `#r "nuget:"` going through the M9 package machinery.
- "Initialize Interactive with Project" and "Send selection to Interactive".

The roadmap's architecture note is binding: user code must never run in the shell or in the language host. A crash or an `Environment.Exit` in a script must only restart the REPL.

Constraints:
- The shell is Native AOT, so it can't compile or load code at run time.
- Hosts are supervised processes speaking JSON-RPC over stdio (ADR 0005).

## Options

**Where the code runs**
1. In the language host, next to Roslyn's workspace. This breaks the "never run user code in the language host" rule: a script that hangs or exits would take IntelliSense down with it.
2. **A new host, `daedalus-replhost`**, supervised like the others: one process per Interactive window, restarted with an empty state when it dies.

**Editor services for the input**
1. The language host with a script document per submission chain. It doesn't know the REPL's state (earlier submissions, `#r` references), so it would have to replay every submission into its workspace and keep that in sync with the REPL.
2. **The REPL host itself.** It owns the submission chain, so completion, signature help and tokens run on the compilation of the pending text as the next submission. This needs Roslyn's compiler APIs only, not the Features layer.

**`#r "nuget:"`**
1. Call the build host's package service. That service needs an open, trusted solution, but the REPL works without one.
2. **Restore a throwaway project with the SDK's `dotnet restore`** in the REPL host. This uses the same `nuget.config` (sources, source mapping, credentials) the package manager uses (ADR 0012). The runtime assemblies of the package and its dependencies then come from `project.assets.json`.

## Decision

**The host**
- **`daedalus-replhost`** (option 2): a framework-dependent JIT process in `replhost/` next to the shell, on `Microsoft.CodeAnalysis.CSharp.Scripting` (MIT, the same Roslyn version as the language host).
- Its protocol is in `docs/interactive-protocol.md`:
  - `repl/reset`, `repl/execute` and `repl/output` (notifications);
  - `repl/isComplete`, `repl/completion`, `repl/signatureHelp` and `repl/tokens`.
- Positions are UTF-16 offsets into the submission. Tokens use the language host's semantic token legend, so the shell's colorizer and themes apply unchanged.

**State and references**
- A state starts with:
  - the whole shared framework the host runs on;
  - the usual usings (`System`, `System.IO`, `System.Collections.Generic`, `System.Linq`, `System.Text`, `System.Threading.Tasks`);
  - the configured references, usings and `#load` files.
- **Reset** always starts a new process. Loaded assemblies can't be unloaded, and an endless loop can't be interrupted any other way.
- **A crash** restarts the host with the same configuration and an empty state, and the window says so.
- A submission that throws still extends the state, so variables declared before the exception stay usable. A submission that doesn't compile leaves the state unchanged.
- Scripts' `Console.Out` and `Console.Error` go to the window. They read no input. The protocol uses plain streams on the stdio descriptors: on Unix, .NET's console streams lock `Console.Out`, which is the scripts' writer.

**`#r "nuget:"`**
- Option 2. A `#r` can bind only one assembly in Roslyn. So before compiling, the host restores the submission's packages and adds all their assemblies to the options. The directive then binds to the first of them.
- Editor services never restore: they see only packages already restored, so typing a package name doesn't start a restore.

**Initialize with Project**
- Builds the project. Then resets with:
  - its output assembly and the other assemblies in its output folder;
  - its compile references, except reference assemblies (`packs/`, `ref/`), which can't be loaded to run code.
- The working directory is the project folder. Native files, and copies of assemblies the framework already has, are skipped.

## Consequences

- The REPL host serves its own completion. It is simpler than the language host's (symbols and keywords, no snippets, no import completion). It doesn't serve hover or documentation.
- The first `#r "nuget:"` of a package costs a `dotnet restore` (seconds). Later ones in the same host are cached.
- `TODO(issue)`:
  - `#r` of a project (`.csproj`);
  - script-file debugging;
  - a WPF/WinForms dispatcher (Windows-only, like the classic IDE's `#reset` options);
  - showing rich values (tables, images) in the window.
