# 0104. .NET MAUI's native tools, and an SDK only an administrator can change

- **Status:** accepted (2026-11-15)
- **Date:** 2026-11-15
- **Deciders:** maintainer
- **Amends:** [ADR 0064](0064-maui-devices-and-mobile-runs.md) (creating AVDs and managing SDK packages were left to the SDK's own tools)

## Context

The classic IDE puts the platforms' own tools beside a MAUI app: an Android Device Manager, an Android SDK Manager, an "Android Adb Command Prompt" and a Device Log, as toolbar groups and in Tools → Android, and on a Mac the iOS simulator and Xcode. Codalus IDE had the Devices window (list, start, choose) and the Android SDK's install through the workload (ADR 0064), and left everything else to `avdmanager`, `sdkmanager` and Android Studio.

The maintainer's report: on Windows, **Install the Android SDK** failed with `UnauthorizedAccessException: Access to the path 'C:\Program Files (x86)\Android\android-sdk\build-tools\36.0.0' is denied`. That is the classic IDE's SDK, which the IDE found through `ProgramFiles(x86)`; only an administrator can write there. The classic IDE's own installer runs elevated.

The tools are the SDK's command-line programs: `sdkmanager --list` prints three tables (installed, available, updates); `sdkmanager --install` asks about each license it needs (`License <id>:`, the text between dashed rules, `Accept? (y/N):`) before downloading, and records an accepted one as a file in the SDK's `licenses` folder; `avdmanager list device` prints the hardware profiles and `create avd` makes an AVD from a system image and a profile; an AVD is a `<name>.ini` pointing at a folder with a `config.ini` the emulator reads at each start; `adb logcat -v threadtime` prints the log; a booted simulator's unified log streams as JSON lines (`xcrun simctl spawn <udid> log stream --style ndjson`).

## Options

| Question | Options | Verdict |
|---|---|---|
| How far | Launchers (a terminal running `adb logcat`, `open -a Simulator`); the tools as windows | **Windows**, as the classic IDE has them (the maintainer's choice): a Device Manager, an SDK Manager and a Device Log; the adb Command Prompt is a terminal; the Simulator and Xcode open as apps. Xcode's Devices and Simulators window has no way to be opened from outside, so it isn't offered. |
| Where | Menus only; toolbar groups always; toolbar groups by context | **Tools → Android ▸ (and Tools → iOS ▸ on a Mac) and toolbar groups shown while the solution has a project for the platform** (`androidProjects`, `iosProjects`). They are menu rules at `toolbar/standard` with when-clauses, so Customize lists each button and can remove it. Not View → Other Windows, which is at a classic IDE's length (32 entries). |
| A folder the user can't write | Install a per-user SDK instead and switch to it; elevate; ask each time | **Elevate** (the maintainer's choice). A per-user SDK would leave the user's emulators behind: an AVD names its system image relative to the SDK. The IDE checks the folder first (creating a file there). On Windows the same command runs elevated (`runas`, one UAC prompt) through a `cmd` script that takes its input from a file and writes its output to a log, which the IDE follows into the Android SDK channel, and its exit code to a file (the redirection first: `echo 1> file` would redirect handle 1). Elsewhere the command shows under `sudo env ANDROID_HOME=… JAVA_HOME=…` and runs in the terminal, where sudo asks. The workload's `InstallAndroidDependencies` run as an administrator writes its restore to a temporary `BaseIntermediateOutputPath`, so the project's `obj` doesn't end up owned by root. |
| Licenses | Link the license page (ADR 0064's install does); show each license's text | **Show each license in full, accept before installing**, as the classic IDE's dialog does. `sdkmanager --install` runs first in an empty temporary SDK folder, answering no to every prompt: it asks about exactly the licenses the packages (and their dependencies) need, and installs nothing. The ones already in the SDK's `licenses` folder are left out. On Accept, the install runs with yes answers on its input. |
| Missing command-line tools | Our own downloader of Google's repository; the workload's install; explain only | **The workload's install** (the maintainer's choice): `InstallAndroidDependencies` adds `cmdline-tools`. The tools are found in `cmdline-tools/latest`, then the newest `cmdline-tools/<version>`, then the old `tools/bin`. |
| AVDs | Through `avdmanager` only; the files | **Creating through `avdmanager`** (it copies the system image's and the profile's hardware settings); **everything else on the files**, as the emulator reads them: the list (`ANDROID_AVD_HOME`, `ANDROID_USER_HOME/avd`, `ANDROID_SDK_HOME/.android/avd`, `~/.android/avd`), settings written into `config.ini` line by line (memory, internal storage, SD card, cores, GPU mode, keyboard), Wipe Data (the user data, cache and snapshots, as `-wipe-data`), Delete. Cold Boot is `-no-snapshot-load`; Stop is `adb emu kill`. A running emulator can't be edited, wiped or deleted. The device profile isn't editable afterwards: the profile's screen and density are written at creation. |
| Device Log | Raw text; parsed columns with filters | **Parsed** (time, level, process, tag, message), filtered by the lowest level, text, and "my app": the startup project's `ApplicationId`, whose process ids `adb shell pidof` gives every two seconds (each run of the app is a new process, and earlier ones stay in), or its `AssemblyName` as a simulator's process name. It keeps the last 50,000 entries, batches them to the UI thread a few times a second, and stops following when its window closes. A physical iPhone's log has no open tool. |

## Decision

- **Workbench (`Mobile/`):** `ToolProcess` (a tool with an environment and standard input, its output as lines; tests replay recorded output), `Elevation` (the writable check, the `cmd` script, the `sudo` command), `AndroidSdkPackages` (the `--list` tables, the license prompts, accepted licenses, categories and system images), `AndroidEmulators` (AVD files, `config.ini` updates, `avdmanager list device`), `DeviceLogs` (logcat and unified-log lines, filters), `AndroidSdkService` (the commands and the license probe), and `AndroidToolchain.CommandLineTools`.
- **Shell:** `ShellHost.AndroidSdk.cs` runs a change to the SDK where its folder allows (`RunSdkChangeAsync`: here, elevated, or with sudo in the terminal); `ShellHost.AndroidTools.cs` has the windows' flows (licenses, New Emulator through two pickers and a name). The windows: **Android Device Manager** and **Android SDK Manager** open as document tabs, **Device Log** at the bottom. The adb Command Prompt is a terminal with the SDK's tool folders in front of `PATH`.
- **Commands:** `android.deviceManager`, `android.sdkManager`, `android.adbPrompt`, `mobile.deviceLog`, and on macOS `ios.openSimulator` and `ios.openXcode`.

## Consequences

- The SDK installs that failed in an SDK under Program Files now ask for an administrator's permission and succeed. Checking the folder costs one file created and deleted.
- Reading the licenses runs `sdkmanager` once more before each install, which reads Google's repository again (it's cached for a while in `~/.android/cache`).
- The smoke step `maui.devices` also shows the toolbar's Android group and opens the Device Manager and the Device Log in the shipped app (`maui-android-tools.png`).
- The command sweep skips the SDK Manager (its list asks Google's repository) and, on a Mac, Open Simulator and Open Xcode (they start other apps).
- An elevated install can't be cancelled from the IDE; it goes on if the IDE closes. `TODO(issue)`
- The license text is wrapped to lines in the dialog's preview box; a dialog with the licenses listed beside their text, as the classic IDE has, would read better. `TODO(issue)`
- Not done: Xcode's Devices and Simulators window, a physical iPhone's log, changing an emulator's device profile, and the classic IDE's "Pair to Mac" (Windows driving a Mac). `TODO(issue)`
