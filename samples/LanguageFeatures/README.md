# LanguageFeatures fixture

Exercises the editor and the C# language service (ADR 0005); tests copy it before editing.

- `Lib` multi-targets `net10.0` and `netstandard2.0` (`Conditional.cs` differs per framework), with an interface, an abstract class, overrides, a partial class split over two files, overloads with documentation comments, and a `#region`.
- `App` (`net11.0`) references `Lib`, uses the System.Text.Json source generator (`AppJsonContext`), and has a deliberate `CA2200` warning (an SDK analyzer, enabled by default).
- `App/CSharp15.cs` uses C# 15 (M30b): a union type with an exhaustive switch, collection expression arguments (`with(...)`) and labeled `break`/`continue`.
- `.editorconfig` sets indentation and raises `CA1822` to a warning.
