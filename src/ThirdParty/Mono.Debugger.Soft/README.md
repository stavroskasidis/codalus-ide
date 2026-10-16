# Mono.Debugger.Soft (vendored)

Mono's soft debugger client library, the protocol client of the Mono debugger adapter (M20b, [ADR 0065](../../../docs/adr/0065-mono-debugger-adapter.md)).

- **Source:** [mono/debugger-libs](https://github.com/mono/debugger-libs), folder `Mono.Debugger.Soft/Mono.Debugger.Soft`, commit `e7fbb713d156d11193ed404783ad6fe9c4042a6d` (2026-02-11).
- **License:** MIT (Novell, Xamarin), in [LICENSE](LICENSE). debugger-libs publishes no NuGet package, so the files are copied here as dotnet/android and vscode-mono-debug use them (as a submodule).
- **Left out:** `Locale.cs` stays but nothing else of the repository: not Mono.Debugging (the evaluator), not the Windows debugger, not `eula.rtf` (a Microsoft EULA template the repository carries, which none of these files reference).
- **Changes:**
  - Built without `ENABLE_CECIL`, so it doesn't use Mono.Cecil.
  - The `using Microsoft.FileFormats.PE;` lines in `AssemblyMirror.cs` and `Connection.cs` are removed, and `PdbChecksum.cs` (ours) replaces the one type they used, so it doesn't use Microsoft.FileFormats or Microsoft.SymbolStore.
  - Nothing else is edited, so a newer commit can be copied over the same way.
