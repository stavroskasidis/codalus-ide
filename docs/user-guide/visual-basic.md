# Visual Basic

Codalus IDE edits Visual Basic as it edits C#, with the same language service. A solution can mix C# and VB projects: references between them work both ways, and so do navigation, Find All References and rename.

## Editing

`.vb` files get:
- completion, Quick Info (in VB's syntax, with the documentation comments) and signature help;
- errors and warnings as you type (`BC…` from the compiler, `IDE…` from the code style rules);
- semantic coloring, including XML literals;
- Go to Definition, Find All References and rename, across C# and VB projects;
- quick fixes and refactorings (`Ctrl+.`);
- Format Document, the outline (document symbols) and folding;
- snippets: type `If`, `ForEach`, `Select`, `Try`, `Sub`, `Property`, `Class`… and press `Tab`, or use Insert Snippet and Surround With.

### What the editor adds as you type

Visual Basic's editor behaviour is Codalus IDE's own:

| You type | You get |
|---|---|
| Enter after a block's first line (`If … Then`, `For Each …`, `While …`, `Select Case …`, `Try`, `Using …`, `Sub …`, `Function …`, `Property …`, `Class …`, `Module …`…) | The block's end on the next line (`End If`, `Next`, `End While`, `End Select`, `End Try`, `End Using`, `End Sub`…), indented like the block, unless it's already there |
| Enter at the end of any line | The keywords on that line in their usual casing: `dim x as integer` becomes `Dim x As Integer` |
| `>` at the end of an XML literal's start tag | The end tag: `<item>` becomes `<item></item>` |

Two things Visual Basic editors sometimes do aren't offered:
- removing line continuation characters (`_`), which VB no longer needs in most places;
- the WinForms application framework (`My.Forms`, the designer's `My.Settings`). The rest of `My` is what the compiler generates for the project's `MyType` property.

## Projects

- **New Project** lists the templates of every installed language. When a template exists in several (Console App, Class Library, xUnit…), it asks which language: C#, F# or Visual Basic.
- **Add New Item** offers VB's own Class, Module, Interface, Structure, Enum and Code File in a VB project, in the project's root namespace.
- **Properties** (`Alt+Enter` on the project) has VB's options on the Application page: Option Explicit, Option Strict, Option Infer, Option Compare and the language version. The Build page has "Remove integer overflow checks", and conditional compilation constants are separated by commas (`DEBUG=-1,TRACE=-1`). Imports for the whole project are `Import` items in the project file (`<Import Include="System.Text" />`); edit them there.

## Tests

The Test Explorer finds VB tests (xUnit, NUnit, MSTest) from the source as soon as the file opens. Their names include the project's root namespace, as the compiler writes them (`MyTests.MyTests.InventoryTests`), so they match what `dotnet test` reports.

## Running and debugging

VB programs build, run and debug like C# ones: breakpoints, stepping, the call stack, locals and data tips work through the program's symbols.

The debugger reads expressions as C#. In Watch and Immediate, and in breakpoint conditions:
- write `null` for `Nothing`, `&&` and `||` for `AndAlso` and `OrElse`, `==` for `=` and `Is`, and `!=` for `<>` and `IsNot`;
- use square brackets for indexers (`items[0]`).

Names and member paths (`product.Price`) work as they are; write `this` for `Me`.
