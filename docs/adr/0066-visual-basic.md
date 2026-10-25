# 0066. Visual Basic in the C# language host

- **Status:** accepted (M35, 2026-10-15)
- **Date:** 2026-10-15
- **Deciders:** maintainer (D13: Visual Basic is second to last), with the standing go-ahead to complete every milestone

## Context

Since the MVP, Visual Basic projects only loaded, built and ran. Their files were colored by a TextMate grammar, and nothing served them. Roslyn has Visual Basic's compiler, workspace and features (`Microsoft.CodeAnalysis.VisualBasic.Workspaces` and `.Features`, MIT) next to C#'s, in the same version. The C# host (`codalus-lshost`, ADR 0005) already gets its projects from the build host's design-time builds.

The research for M35 found the following:
- **Mixed solutions.** A C# project and a VB project in one Roslyn workspace reference each other as compilations. Definitions across the languages find metadata symbols, which `SymbolFinder.FindSourceDefinitionAsync` maps back to source.
- **The compiler's arguments.** The design-time build gives VB its `vbc` arguments. On Unix, MSBuild turns `\"` in an item into `/"`, which breaks VB's quoted `/define:` (`CONFIG=\"Debug\"`). The compilation then fails to load, and emitting it throws.
- **Editor behaviour.** the classic IDE's "pretty listing" (adding `End If`, fixing keyword casing, closing XML literals' tags) lives in Roslyn's the classic IDE's editor layers (`EditorFeatures`), which depend on the classic IDE's editor. It isn't in `Features`.
- **Debugging.** netcoredbg debugs VB programs through portable PDBs, but it evaluates expressions as C#.

## Options

| Question | Options | Verdict |
|---|---|---|
| Where VB is served | A new VB host; the C# host | **The C# host**, in the same workspace as C#, so references between C# and VB projects are live. The workbench's C# descriptor serves the `vb` language too and takes the build host's `VB` projects (`LanguageServiceDescriptor.MoreProjectLanguages`). |
| Features | Only what's language-neutral in our features; each feature for VB | **Each feature.** Most features call Roslyn's language-neutral services (completion, Quick Info, classification, rename, formatting, code fixes and refactorings, references), which have VB implementations. Our own syntax-based features have a VB version: document symbols, folding, signature help (invocations, `New`, attributes), test candidates (with the project's root namespace). Quick Info and completion descriptions are fenced as `vb`. |
| Pretty listing | Leave it out; Roslyn's `EditorFeatures`; our own | **Our own, through on-type formatting** (`VisualBasicPrettyListing`). Enter after a block's first line adds its end (`End If`, `Next`, `End Sub`…) when it's missing, indented like the block. Enter also fixes the casing of the keywords on the line it ends. `>` ending an XML literal's start tag adds the end tag. The editor sends Enter as `\n` and keeps the caret where it was. |
| The compiler's arguments | Ask MSBuild for different items; repair them | **Repair them:** the `/define:` arguments are split again with `/"` read as `\"`, before `VisualBasicCommandLineParser` parses them (`ProjectLoader.VisualBasicArguments`). Other arguments are left alone. |
| Projects and items | — | New Project lists every language's templates and asks which language a template is made in (C# first). The built-in item templates have VB versions (Class, Module, Interface, Structure, Enum, Code File), and the property pages show VB's options (`Option Explicit`, `Strict`, `Infer`, `Compare`, the VB language version, overflow checks) instead of C#'s language settings. VB snippets are built in. |

## Decision

- VB is served by the C# host, with the VB Roslyn packages at the same version as C#'s.
- Pretty listing is ours, served as on-type formatting, and documented in `docs/user-guide/visual-basic.md` as what it covers.
- Debugging uses netcoredbg unchanged. Watch and Immediate evaluate C# syntax, which the user guide states.

## Consequences

- C# and VB in one solution navigate, find references and rename across both.
- The language host is a few megabytes larger (the VB assemblies).
- Not built:
  - automatic line continuation (VB's implicit continuation needs nothing; removing `_` isn't offered);
  - `My` beyond what the compiler generates from the project's `MyType`.
  - Expressions in Watch and Immediate are C#. Translating simple VB expressions (`Nothing`, `AndAlso`, `Is`) before netcoredbg sees them is a candidate. `TODO(issue)`
  - Project-level imports (`Import` items) are edited in the project file. A page for them is a candidate. `TODO(issue)`
