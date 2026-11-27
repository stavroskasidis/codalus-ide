# DotnetReleases

A trimmed stand-in for Microsoft's .NET release metadata (`https://builds.dotnet.microsoft.com/dotnet/release-metadata/`), in the same format: `releases-index.json` and each channel's `releases.json`, with only the properties the SDK manager reads (ADR 0113) and a few releases per channel. The versions and dates are illustrative, not a copy of the real files.

The SDK manager's tests (`tests/Codalus.Workbench.Tests/Sdks`) and the smoke step `setup.dotnetSdks` read it through `CODALUS_DOTNET_RELEASES` or a `DotnetReleaseSource` pointed at this folder, so nothing reaches the network.
