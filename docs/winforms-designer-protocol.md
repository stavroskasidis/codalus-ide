# Shell ↔ Windows Forms designer host protocol

`codalus-winformsdesigner` ([ADR 0073](adr/0073-windows-forms-designer.md), Windows only) hosts Windows Forms' own `DesignSurface` for one form, on the designed project's app's runtime (`dotnet exec --runtimeconfig App.runtimeconfig.json --depsfile App.deps.json codalus-winformsdesigner.dll`; a library with no app that references it runs on the host's own runtimeconfig with `DOTNET_ROLL_FORWARD=Major`). It speaks JSON-RPC 2.0 with LSP framing (`Codalus.Rpc`, linked) over stdio; stdout carries only the protocol (the designed code's console output goes to stderr, the IDE's **Windows Forms Designer** output). The shell starts one per open designer (`WinFormsDesignerService` and `WinFormsDesignerProcess` in `src/Codalus.Workbench/WinForms`) under `SupervisedProcess`: after a crash it restarts and the designer loads the form again. `CODALUS_WINFORMS_DESIGNER` points at another build. Contracts: `src/Codalus.Workbench/WinForms/WinFormsDesignerProtocol.cs` (the records and their source-generated serializer, linked into the host) and the model in `src/Codalus.LanguageServices.Protocol/WinFormsDesign.cs` (what the C# host's `codalus/winforms/read` returns, [protocol](protocol.md)).

## Conventions

- **Coordinates** are pixels of the frame: the root (the form, with its border and caption) drawn at 96 DPI from its top-left corner. A unit of the code (`Location`, `Size`) is a pixel: the host is DPI unaware.
- **Components** are named as in the code; the root by the class's name (or empty in requests); a `SplitContainer`'s panel as `splitContainer1.Panel1` (a part, not a component of the class).
- **Values** in changes are the model's expression trees (`DesignerValue`), as Visual Studio's CodeDOM serializers would write them; a property back to its default has a null value (its assignment goes).
- Every request has a 60-second limit in the shell; a failed request is a JSON-RPC error.

## Methods

| Method | Params | Result | Notes |
|---|---|---|---|
| `designer/load` | `{model, projectAssembly, classFullName?, searchFolders}` | `DesignerRenderResult` | A fresh surface: the root (an instance of `model.baseType`, named `model.className`), the components created through the designer host, and `model.statements` run in order. `classFullName` names the class whose compiled resources `resources` reads. A statement that fails is in `errors` (`Line n: …`); the others apply. |
| `designer/render` | `{}` | `DesignerRenderResult` | Draws again. |
| `designer/create` | `{type, name, parent?, x, y, width?, height?}` | `DesignerChangeResult` with `created` | A Toolbox item: created and initialized by its designer (`InitializeNewComponent`), a control at the front of `parent` (null: the root; a `TableLayoutPanel`'s cell under the point), at the point (and drawn size), with the next tab index. `created` is `{name, type, creationArguments, parent, parentPath, cell, properties, supportsInitialize, isControl}`: what the C# host's `addComponent` edit takes (`creationArguments` is `[components]` for a component with an `IContainer` constructor). |
| `designer/setProperties` | `{edits: [{component, property, text?, value?, reset}]}` | `DesignerChangeResult` | Sets each property through its descriptor: `text` converted by its type converter (invariant culture, as the Properties window shows it), or a `value`, or `reset` to its default. An invalid value: `error`, nothing set. |
| `designer/setParent` | `{component, parent?, x, y}` | `DesignerChangeResult` with `reparented` | Moves a control into another container at a point; `reparented` is `{component, parent, parentPath, cell}`. |
| `designer/delete` | `{components}` | `DesignerChangeResult` with `removed` | Destroys the components and the controls inside them (`removed` lists them all). |
| `designer/properties` | `{components}` | `{properties: [{name, category, type, value?, description?, isReadOnly, isSet, editor, standardValues?, mixed}]}` | The browsable properties (and `Name`), merged over several components (`mixed` when their values differ). `editor` is `text`, `bool`, `enum`, `flags`, `anchor`, `dock`, `color`, `font` or `readonly`; `standardValues` are the converter's (or a flags enum's names); `isSet` is `ShouldSerializeValue`. |
| `designer/events` | `{components: [component]}` | `{events: [{name, category, delegateType, parameters, description?}], defaultEvent?}` | `parameters` are `Type name` (`System.Object sender`); `defaultEvent` is `[DefaultEvent]`'s (a double-click hooks it). |
| `designer/select` | `{components}` | null | The IDE's selection, for designers that follow it. |
| `shutdown` (request), `exit` (notification) | | | |

`DesignerRenderResult` is `{frame?: {png (base64), width, height}, components: [{name, type, parent?, x, y, width, height, isControl, isContainer, clientX, clientY, visible, defaultEvent?, isPart}], errors}`: the root first, then its controls front first and parents before children, then the non-visual components (no bounds). `clientX`/`clientY` is where a control's client area starts (its children's `Location` counts from there).

`DesignerChangeResult` is `{changes: [{component, property, value?}], render, created?, removed, reparented?, error?}`. `changes` are the properties whose code changed, over every component (a docking that moves siblings lists them too): every component's serializable properties (not hidden from serialization, not design-only, not extender-provided, `ShouldSerializeValue`; content properties one level down, as `FlatAppearance.BorderSize`; collections are left to the code's calls) are compared before and after.
