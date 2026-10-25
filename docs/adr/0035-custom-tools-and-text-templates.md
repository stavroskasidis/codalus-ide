# 0035. Custom tools and text templates

- **Status:** accepted (M26b, 2026-10-11)
- **Date:** 2026-10-11
- **Deciders:** maintainer

## Context

The classic IDE's single-file generators ("custom tools") turn a file into another: `ResXFileCodeGenerator` makes a `.resx` file's strongly typed class, `TextTemplatingFileGenerator` runs a T4 template. A file names its generator in its `Generator` metadata (M26a's Properties window edits it), the output is recorded as `LastGenOutput`, and the output nests under the input (`DependentUpon`). The generators are the classic IDE's; T4 runs template code, so it is the user's code running.

## Decisions

**Generators by name, run on demand and on save**
- **Run Custom Tool** (the Solution Explorer's context menu on a `.resx` or `.tt` file in a project, or the command on any file with a `Generator`) and saving such a file (`build.runCustomToolsOnSave`, on by default) run its generator (`CustomToolService` in the workbench). The output goes next to the input (`<name>.Designer.cs` for resources, the template's `output extension` for T4), and the project file gets what the classic IDE writes: `LastGenOutput` on the input, and `<Compile Update="X.Designer.cs"><DesignTime>True</DesignTime><AutoGen>True</AutoGen><DependentUpon>X.resx</DependentUpon></Compile>` for the output (M26a's `items/set`, which writes nothing a glob already gives). An unchanged output isn't rewritten.
- Errors and warnings go to Output → Custom Tool with their file and line, and a failure shows a notification with the first error.
- The namespace is the item's `CustomToolNamespace`, else the project's root namespace plus the folders.

**Built-in generators**
- **`ResXFileCodeGenerator` and `PublicResXFileCodeGenerator`** are our own (`ResXGenerator` in the workbench): they read the `.resx` (strings, file references and other resources' types) and write the class `StronglyTypedResourceBuilder` would (internal or public, properties in name order, values XML-escaped in the summaries), with a `ResourceManager` for the manifest resource name MSBuild gives the file, a `Culture` and a property per resource. Nothing runs from the workspace, so they need no trust.
- **`TextTemplatingFileGenerator`** runs templates with Mono.TextTemplating (MIT) and its in-process Roslyn compiler, in a **supervised process of its own**, `daedalus-t4host` (`t4host/` next to the shell, `DAEDALUS_T4HOST` overrides; protocol `initialize` and `t4/transform` over JSON-RPC on stdio): template code never runs in the shell or the build host, a crash restarts the host for the next template, and a template that runs over two minutes is stopped by restarting it. It starts on the first template, and only for trusted folders. Includes resolve from the template's folder and the project's; `assembly` directives from those and the runtime's folder. The template parser's positions are 1-based and the compiler's 0-based; the host parses first to tell them apart, so every error has the template's line and column.
- **Plugins** add generators with `contributes.customTools` (API 0.2.8): `{ name, command, extension?, description? }`. The command gets `{ path, content, namespace, project }` and returns the generated text (or `{ content, extension }`).

**T4 editing**
- `.tt`, `.ttinclude` and `.t4` files are the `t4` language: a TextMate grammar (directives, control, expression and class feature blocks, the generated text between them) and snippets for the directives (`template`, `output`, `include`, `import`, `assembly`, `parameter`) and blocks.
- Build-time transformation is Mono.TextTemplating's own MSBuild integration (`Mono.TextTemplating.Build` in the project); the IDE adds nothing to builds.

## Consequences

- Resources work without the classic IDE's generator; their output follows the shape of the classic IDE's, so existing projects keep their files.
- A new supervised host, started only when a template runs.
- `TODO(issue)`: custom tool errors in the Error List (they are in Output → Custom Tool); generators for Visual Basic and F# resources; `TextTemplatingFilePreprocessor` (runtime text templates); T4 debugging; the template's `hostspecific` services beyond `ResolvePath` and parameters.
