# 0064. .NET MAUI: devices, emulators, simulators and runs

- **Status:** accepted (M20a, 2026-10-15; D6: go, for Android, iOS and Mac Catalyst)
- **Date:** 2026-10-15
- **Deciders:** maintainer (D6), with the standing go-ahead to complete every milestone

## Context

M20 makes Codalus IDE create, build, deploy and edit .NET MAUI apps for Android, iOS and Mac Catalyst. The Windows target is WinUI, a non-goal technology (M44 proposes it, D31). M20a covers everything except the debugger, which is M20b's Mono adapter (ADR 0065).

Five things have to be settled:
- where devices come from;
- how an app gets onto one;
- how the IDE handles the large prerequisites (workloads, the Android SDK, a JDK, Xcode);
- how MAUI's XAML is edited;
- what the IDE has to know about a project.

The tools are all command-line tools: `adb`, `emulator`, `avdmanager` and `sdkmanager` from the Android SDK; `xcrun simctl` and `xcrun devicectl` from Xcode; `dotnet workload`. The platform SDKs (the `android`, `ios` and `maccatalyst` workloads) know how to build, sign, install and start an app. Their `Run` target does all of it: `dotnet build -t:Run -f net10.0-android`.

## Options

| Question | Options | Verdict |
|---|---|---|
| Deploying and starting | Our own `adb install` / `am start` / `mlaunch` sequence; the platform SDK's `Run` target | **The `Run` target.** It builds what's needed (fast deployment included), signs and installs. It takes the device from properties: `AdbTarget=-s <serial>` on Android, `_DeviceName=:v2:udid=<udid>` for an iOS simulator, `RuntimeIdentifier=ios-arm64` plus `_DeviceName=<udid>` for an iPhone. MAUI's own docs give the same commands, and the SDK keeps them up to date with each Xcode and Android release. M20b adds the debugger's properties to the same command. |
| Devices | A device-manager library; the tools' own output | **The tools' output, parsed:** `adb devices -l`, `adb emu avd name`, `emulator -list-avds`, and JSON from `simctl list devices available --json` and `devicectl list devices --json-output`. Each parser is a pure function, tested with recorded output. |
| Android SDK and JDK | Our own downloader; Android Studio; the workload's `InstallAndroidDependencies` target | **The workload's own target.** It installs the SDK packages the workload needs, and a JDK. Installing accepts the Android SDK's licenses, so the IDE shows the command and links the license first, and runs nothing without the user's yes. The target needs a project, so the offer comes from a project with an Android framework. |
| Workloads | Install silently; offer them | **Offer:** `dotnet workload restore <project>` in the terminal, where `sudo` can ask for a password when the SDK is in a system folder. The build host reports a project's missing packs (`ProjectInfo.MissingWorkloadPacks`, from the SDK's `MissingWorkloadPack` items). Check Environment lists each workload a solution needs, and `dotnet workload install <id>` installs it. |
| MAUI XAML | A separate engine; the M19 engine with a dialect | **The M19 engine (ADR 0047), with MAUI's dialect** when the compilation references `Microsoft.Maui.Controls`. That means bindable properties (`BindableProperty` fields, with or without CLR wrappers), attached properties (a field plus a static `Get…`), `[ContentProperty]` on the class, `IMarkupExtension`, named `Colors` fields, and the XAML 2009 `x:` namespace (read as 2006's). Bindings under `x:DataType` are always compiled. `.xaml` files become the `xaml` language, served by the C# host; their projects pass them as additional files (`MauiXaml`). |
| What a project needs to tell | Read `UseMaui` in the shell; report it | **The build host reports `UsesMaui`** and the missing workload packs. A project's platform frameworks map to platforms (`net10.0-ios18.0` → iOS). Android runs on every OS; iOS and Mac Catalyst only on macOS. |

## Decision

- **`MobileService`** (workbench `Mobile/`) does the following:
  - lists devices from the tools, through a `ProcessRunner`;
  - remembers the device each platform runs on (an emulator by its AVD, so the choice holds when it starts and gets a serial; saved in the solution's session);
  - starts an emulator (`emulator -avd`, then waits for `sys.boot_completed`);
  - boots a simulator (`simctl boot`, then opens the Simulator app);
  - reads the installed workloads (`dotnet workload list`);
  - makes the run and install plans.

  **`AndroidToolchain`** finds the SDK and the JDK. It looks first at the settings (`android.sdkDirectory`, `android.jdkDirectory`), then at `ANDROID_HOME`/`ANDROID_SDK_ROOT` and `JAVA_HOME`, then in the usual folders. **`AppleSigning`** reads the code-signing identities and the installed provisioning profiles.
- **Runs:** Start and Start Without Debugging on a mobile startup project go through the following steps:
  1. The project's workloads must be installed.
  2. For Android, the SDK and JDK must be there.
  3. The device is the chosen one (an emulator that isn't running is started first).
  4. Then `dotnet build -t:Run` runs in the project's Run channel. The IDE doesn't build first, since the SDK builds only what the platform needs.

  Until M20b, Start runs without the debugger and says so.
- **The UI:**
  - the toolbar's device picker next to the startup project (choosing another platform's device switches the active framework);
  - the Devices window: start, run here, and the toolchain with an install;
  - Debug → Other Debug Targets → Select Device;
  - the MAUI rows in Check Environment.
- **Templates:** the MAUI workload brings `maui`, `maui-blazor` and `mauilib`. The New Project dialog lists them with readable names, as it lists every `dotnet new` template.

## Consequences

- Nothing is downloaded or accepted without the user. The workloads are large (about 1 GB for Android) and so is the SDK (about 0.5 GB).
- On Linux in a container without `USER`, the workload's SDK installer fails. The IDE sets `USER` for Android tools and builds.
- The IDE depends on the tools' output formats. JSON is used wherever a tool offers it (`simctl`, `devicectl`). `adb`'s and the emulator's text output has been stable for years, and each parser is tested with recorded output.
- MAUI's XAML Hot Reload and a XAML previewer don't exist in open source. They are gaps (candidates). C# Hot Reload on devices comes with .NET 11's `dotnet watch` (M30b).
- Creating AVDs (`avdmanager`) and managing SDK packages (`sdkmanager`) are left to the SDK's own tools for now. `TODO(issue)`
- MAUI's Windows target came with M44 ([ADR 0074](0074-maui-on-windows-and-winui.md), "MAUI on Windows and WinUI 3"): "Windows Machine" in the device picker, on Windows (D31).
