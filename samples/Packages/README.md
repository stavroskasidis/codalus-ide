# Packages fixture (M9, ADR 0012)

NuGet package management tests run on a copy of this solution.

- `src/Plain`: a `PackageReference` with a version.
- `src/Multi`: multi-targeted (`net10.0;net9.0`), no packages.
- `cpm/`: Central Package Management (`cpm/Directory.Packages.props`), two projects sharing a central version.
- `nuget.config`: the local feed `feed/` and nuget.org, with package source mapping (`Codalus.Sample.*` only from `local`).
- `feed/`: fixture packages, generated with `dotnet run --project benchmarks/Codalus.Benchmarks.FixtureGen -- packages samples/Packages/feed`:
  `Codalus.Sample.Greeting` 1.0.0, 1.1.0 (depends on `Codalus.Sample.Text`) and 2.0.0-beta.1, `Codalus.Sample.Text` 1.0.0,
  and `Codalus.Sample.Licensed` 1.0.0 (requires licence acceptance).
