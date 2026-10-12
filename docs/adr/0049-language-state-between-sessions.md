# 0049. Language state between sessions, reference assemblies for unchanged projects, and diagnostics the build agrees with

- **Status:** accepted (M34c, 2026-10-13; the maintainer's standing go-ahead for the milestones)
- **Date:** 2026-10-13
- **Deciders:** maintainer

## Context

After M34b, a warm open of a 96-project web solution (mostly Blazor and ASP.NET Core) had every project's compiler inputs in about 4 s, and then waited on Roslyn: its open documents got their first real diagnostics after 35–38 s, and the whole solution was ready after 53–58 s. Measured in the language host:

- Every session compiled every project again (about 45 s of the background pass on half the processors, of which the source generators, run alone, cost about 13 s: the Razor generator 12 s over 28 projects, the rest under 1 s together). Nothing of it outlived the process.
- The open documents' project sits at the top of a chain of Razor projects. In the workspace a project compiles against its references' *compilations* (ADR 0005), so its first semantic model waits for every project below it, generators included, one level after another.
- The language host's memory with the solution open was 3.7 GB, mostly compilations no open document needs.

Three errors remained in the Error List that `dotnet build` doesn't report: CS4014 ("this call is not awaited") in methods that aren't `async`. M34b put it down to the host's Roslyn (5.9) being newer than the solution's SDK compiler (5.6). That is wrong: both compilers report the warning the same way. The compiler reports CS4014 outside an `async` method only for a call to a method it sees as `async`. A method of another project is `async` only in source; in an assembly, no method is. The build compiles a project against its references' assemblies, so it never sees those methods as `async`; the workspace compiles against their compilations and does. The same three-line program gives the warning with a compilation reference and none with the assembly, on every SDK from 10.0.1xx to 11.0 preview.

## Decisions

**The language host keeps a project's results between sessions** (`LanguageStateCache`), in the folder of the compiler's output (`obj/<configuration>/<framework>/`, as the design-time build cache of M34b keeps its own): `daedalus.language.json` with the background pass's diagnostics of the project's files, and, for a project other projects reference, `daedalus.language.ref.dll` and `.xml`: its metadata (internals included, for `InternalsVisibleTo`) and its documentation comments, emitted from the compilation the pass made. Both are stored under a **key** (`ProjectKeys`): a SHA-256 of the compiler command line, every source, additional and `.editorconfig` file and every reference and analyzer outside the solution by path, length and time written (an open document by its text when that differs from the file), the host's Roslyn version, the analysis scope, and the keys of the projects it references. A corrupt or missing file, or another key, is a miss. `DAEDALUS_LANGUAGE_CACHE=0` turns it off.

**A project whose key matches isn't compiled.** The first background pass of a set of projects takes its diagnostics from the file; later passes follow edits as before (M34b's in-memory reuse). A project whose key changed compiles, and writes its new state.

**The analysis solution** (`AnalysisSolution`): the workspace's solution with every reference to an unchanged project replaced by that project's reference assembly, from the cache, or, failing that, from the project's last build when its output is newer than the project file and every input. Projects that must compile, and the open documents' features (diagnostics, completion, Quick Info, signature help, highlights, semantic tokens, inlay hints), use it while the projects their project references haven't compiled in the workspace. A project then compiles with its own sources and generators only. Edits to open documents carry over to it as they come, so its compilations stay incremental; an edit to a document of a project that stands as an assembly rebuilds it with that project from source. Navigation, references, rename, code actions and everything else that needs the projects' source symbols keep the workspace's solution.

**Then the workspace catches up**: after the pass, and once the open documents' own diagnostics are done, the projects the open documents reference compile in the workspace on a quarter of the processors (a change of projects stops it, edits don't), and the analysis solution is dropped. The other projects compile when something asks for them.

**CS4014 that the build wouldn't report is left out** (`BuildDivergence`): in a method or lambda that isn't `async`, when the method called belongs to another assembly. The analysis solution needs no filter (its references are assemblies, like the build's). We keep compilation references in the workspace: they give live cross-project semantics while editing, which assemblies can't.

## Consequences

- On the 96-project solution, a warm open is ready in about 7 s (was 53–58 s), the open documents' first diagnostics come after about 12 s (35–38 s), the first completion after about 9 s (34 s), and the language host holds 2.2 GB instead of 3.7 GB. The first session after this change, with no state yet, takes its reference assemblies from the projects' builds (open documents after about 20 s).
- A file changed outside the editor within the same tick and to the same length as before keeps the old state until anything else changes; environment variables aren't in the key (as for M34b's cache).
- Quick Info and signature help on a symbol of a project that stands as a build's assembly show no documentation unless that build wrote a documentation file, until the workspace catches up. The cache's assemblies carry theirs.
- The state files are about the size of a reference assembly per referenced project, in `obj/`, which source control already ignores; deleting `obj/` starts over.
- Not done: Roslyn's frozen partial semantics (internal, and its semantic-model reuse kept the frozen model after the compilation completed: completion returned nothing for the rest of the session), and caching source generator outputs themselves (Roslyn has no public way to seed a generator driver's state across processes).
