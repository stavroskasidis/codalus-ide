// Benchmarks: a BenchmarkDotNet project for the IDE's benchmark integration (M18b): the [Benchmark] methods get gutter
// glyphs to run them, and their results go to the Benchmarks window with a history across runs. Run it as any
// BenchmarkDotNet program: `dotnet run -c Release -- --filter *`.
using BenchmarkDotNet.Running;

BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
