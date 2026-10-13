# Changelog

Hand-written notes for each release. The release pages add the list of changes, grouped by milestone, from the commits.

## 0.1.0

The first preview release of Daedalus Studio, an open-source (MIT), cross-platform IDE for .NET. It covers the editor, the project system, NuGet, build and run, debugging, testing, Git, the terminal and C# Interactive, profiling, data tools, containers and Aspire, web and Blazor, and Avalonia XAML: see the [features](https://github.com/stavroskasidis/daedalus-studio#features) and the [user guide](https://github.com/stavroskasidis/daedalus-studio/blob/master/docs/user-guide/README.md). It's a 0.x release: expect rough edges, and please report what you find in the [issues](https://github.com/stavroskasidis/daedalus-studio/issues).

**Before you install**: you need the .NET SDK 10.0 or newer and git 2.30 or newer ([installing and first start](https://github.com/stavroskasidis/daedalus-studio/blob/master/docs/user-guide/getting-started.md)).

**The installers aren't code-signed yet**, so each OS asks once:
- **Windows** ("Windows protected your PC"): choose **More info**, then **Run anyway**.
- **macOS** ("Apple could not verify…"): close the message, open System Settings → Privacy & Security, and choose **Open Anyway** next to Daedalus Studio (or the installer); then open it again.
- **Linux**: the `.deb` and `.rpm` packages and our apt and dnf repositories are signed with the [project's key](https://github.com/stavroskasidis/daedalus-studio/blob/master/eng/packaging/linux/daedalus-studio-packages.asc).

Installed builds update themselves from signed feeds (the Velopack installers and the AppImage), and `SHA256SUMS.txt` lists every file's checksum.

**Platforms**: Windows 11 x64, macOS 15 or newer on Apple silicon, and Linux x64 and arm64. Windows on Arm and Intel Macs aren't built yet.

**Not in this release yet**: remote development (SSH, WSL, containers), Hot Reload while debugging, .NET MAUI, Visual Basic and F#; see the [roadmap](https://github.com/stavroskasidis/daedalus-studio/blob/master/docs/roadmap.md).
