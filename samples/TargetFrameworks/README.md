# TargetFrameworks fixture

Every .NET (Core) version from 2.0 to 10.0 (M33, ADR 0030), with the current SDK. The runtimes come from `eng/get-runtimes.sh`; `tests/Codalus.TargetFrameworks.Tests` runs this fixture through Codalus IDE, and `eng/compat-containers.sh` runs its programs in containers of the Linux distributions they supported.

- One folder per framework: `Core20` (`netcoreapp2.0`), `Core21`, `Core22`, `Core30`, `Core31`, `Net50` (`net5.0`) up to `Net100` (`net10.0`). Each has:
  - `<Id>.App`, a console app. Its `Program.cs` uses the newest C# feature of the framework's default C# version (nothing sets `LangVersion`), prints the runtime it runs on, the frameworks of its libraries and a greeting, and has a `// [next]` line where tests put a feature of the next C# version. `Net60.App` has a `Watch` launch profile that keeps printing `Message()`, for Hot Reload.
  - `<Id>.Lib`, a library for the same framework, which references `Standard20` (and `Standard21` from .NET Core 3.0 on).
  - `<Id>.Tests`, xUnit on VSTest with the test SDK and adapter versions that framework supports (Microsoft.NET.Test.Sdk 15.9 for 2.0, 16.11 for 2.1 to 3.0, 17.12 for 3.1 to 7, 18.x from 8).
- `Standard20` and `Standard21`: `netstandard2.0` and `netstandard2.1` libraries.
- `Multi`: a library for `netstandard2.0;netcoreapp3.1;net8.0`; each app gets the nearest of the three.
- `Web21`: ASP.NET Core 2.1 with the web host and a `Startup` class (`Microsoft.AspNetCore.App` package); `Web31`: ASP.NET Core 3.1 with the generic host and endpoint routing. Both answer `/` on HTTP (ports 5021 and 5031).
- `Sdks/`: small solutions whose `global.json` pins an older SDK: `Sdk60` (6.0.428), `Sdk50` (5.0.408), `Sdk31` (3.1.426), `Sdk21` (2.1.818), `Sdk202` (2.1.202, the floor) and `Sdk20` (2.0.3, below the floor). They aren't in `TargetFrameworks.sln`.
- With `TARGETFRAMEWORKS_CRYPTO=1`, each app also hashes with SHA-256, which goes through OpenSSL on Linux.
- `Directory.Build.props` turns off the out-of-support warning (NETSDK1138) and NuGet's audit of the old framework packages, which would bury the rest of the build output. Nothing else is set.
