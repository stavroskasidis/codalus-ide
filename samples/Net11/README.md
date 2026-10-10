# Net11 fixture

.NET 11 projects (M30, ADR 0025). `global.json` pins the .NET 11 SDK (release candidates included), so this fixture needs it installed: tests skip without it locally and fail without it in CI.

- `Shapes` is a library for `net10.0` and `net11.0`. `Circle.Framework` says which one it was built for.
- `App` is a `net11.0` console app. It prints the runtime's major version, `Shapes`' framework and an area. With `--watch` it prints `Message()` every 250 ms, for Hot Reload.
- `Components` is a `net11.0` Razor class library: `CircleView.razor` shows a `Circle`.
- `Shapes.Tests` (xUnit, VSTest) runs on `net10.0` and `net11.0`; `Shapes.MtpTests` (xUnit v3, Microsoft.Testing.Platform) on `net11.0`.
- Tests find lines by their `// [name]` markers.
