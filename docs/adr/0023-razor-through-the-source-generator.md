# 0023. Razor through the Razor source generator

- **Status:** accepted (M16 plan, 2026-10-10; the maintainer's standing go-ahead for M9–M16)
- **Date:** 2026-10-10
- **Deciders:** maintainer

## Context

M16 asks for rich editing of `.razor` and `.cshtml` files: C# inside Razor (completion, diagnostics, navigation), Razor's own syntax (components, parameters, directives), and Blazor run, debug and Hot Reload. The roadmap recommends `dotnet/razor`'s language server, "cohosted" in the Roslyn host. Its packages are on the .NET engineering feeds and its APIs are `ExternalAccess`, the same availability risk that ADR 0022 avoided for Hot Reload.

What we already have:
- The C# host (ADR 0005) builds its projects from design-time compiler command lines. Those include the Razor source generator (`Microsoft.CodeAnalysis.Razor.Compiler`), the `.razor` and `.cshtml` files as additional files, and the generated `.editorconfig` with their metadata. So the workspace already generates each component's C#.
- The generator writes enhanced `#line (start)-(end)` directives around every piece of C# it copies: implicit and explicit expressions, event handlers, component parameter names and values, `@code` blocks. Roslyn exposes them (`SyntaxTree.GetLineMappings`, `Location.GetMappedLineSpan`).

M16 is split:
- **M16a:** Razor editing and Blazor run and Hot Reload. This ADR.
- **M16b:** HTML, CSS and JavaScript/TypeScript language services on an on-demand Node runtime (D3).
- **M16c:** Blazor WebAssembly, and browser debugging (WebAssembly and JavaScript) through `vscode-js-debug`.

## Decision

**Razor files are served by the C# host, from the generator's output.** No Razor language server.

**Documents**
- The C# language service also serves the `razor` language. An open Razor file updates its additional document's text, so the generator runs on unsaved edits.
- A Razor file never goes to Miscellaneous Files. Outside a loaded project it gets the Razor grammar and nothing else.
- Its generated document is the one whose `#pragma checksum` names the file. Every generated file also maps `_Imports.razor`'s usings, so mappings alone don't identify it.

**Mapping** (`RazorMapping`)
- Each `#line` mapping becomes a pair of spans, Razor and generated. The length is where the two texts agree: the generator's end column runs one character past the C#, and the C# is copied verbatim.
- A position in a mapped span becomes a position in the generated document. A result range in the generated document maps back when it falls in one span; otherwise it is dropped (code the generator wrote).
- A location in Razor-generated C# anywhere in the solution (Go to Definition, references, a component's class) moves to the Razor file: its mapped span, or the file's start for the generated class. This applies wherever locations are converted, so navigating from a `.cs` file into a component works too.

**Features**
- **C# in Razor:** completion (the edit range and the resolved edit mapped back; edits elsewhere in the generated file, such as an added `using`, are dropped), hover, signature help, Go to Definition, document highlights and references. These are the C# requests, run at the mapped position.
- **Diagnostics:** an open Razor file gets its generated document's diagnostics that map into it. The solution-wide pass moves the diagnostics of every Razor-generated document to its Razor file.
- **Razor markup:**
  - **completion** (`RazorContext`): component names after `<` (the project's components and those of the component libraries it references, from the compilation), with HTML elements; a component's `[Parameter]` properties and Blazor's directive attributes inside its tag; directives after `@` at the start of a line. There the generator first sees an implicit C# expression, so both are offered.
  - **hover and Go to Definition** on a component's tag and on its parameters.
- **Not served yet** (empty answers): rename, code actions, formatting, semantic tokens, folding and document symbols in Razor files. The Razor TextMate grammar colors the file.

**Blazor run and Hot Reload**
- A Blazor Web App or Server app is an ASP.NET Core project: Run, Debug, the Endpoints Explorer and Hot Reload (ADR 0022) apply as they are. `dotnet watch` applies markup and C# edits in components.

## Consequences

- **The good:** no dependency on unpublished Razor packages. Razor features follow the SDK's own generator, which is exactly what the build compiles, so the answers match the build.
- **The cost:** Razor tooling features that need the Razor syntax tree (tag helper and component formatting, HTML-aware rename, the ASP.NET Core tag helpers' attribute completion in `.cshtml`) are absent or approximate (`RazorContext` is a scanner). `TODO(issue)`.
- **Timing:** generator diagnostics (`RZ…`) appear after a build, not while typing, because the workspace doesn't surface them. `TODO(issue)`.
- **Revisit** if `dotnet/razor` publishes its cohosting packages on nuget.org with a stable API.
