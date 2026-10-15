# Visual Basic fixture (M35)

A mixed Visual Basic and C# solution for Visual Basic editing ([ADR 0066](../../docs/adr/0066-visual-basic.md)).

| Project | What it has | Used by |
|---|---|---|
| `VbConsole` | A console app (`Option Strict On`, `Option Infer On`) that uses the VB library and the C# library, with a `[vb-loop]` marker. | Completion, navigation and diagnostics across languages; running and debugging |
| `VbLibrary` | `Product` (auto-properties, XML documentation) and `Inventory` (a `For Each`, `If … Then` blocks, and an XML literal with embedded expressions). | Classification (XML literals), outline, folding, rename, formatting |
| `CsLibrary` | `PriceFormatter`, a C# class the VB console app uses. | References between C# and VB projects in one workspace |
| `VbTests` | xUnit tests in Visual Basic. | The Test Explorer with VB tests |
