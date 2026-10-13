# 0064. Mobile devices and running on Android

- **Status:** accepted (M20a)
- **Date:** 2026-10-14
- **Deciders:** maintainer (D6: .NET MAUI for Android, iOS and Mac Catalyst; the roadmap's M20)

## Context

M20 brings .NET MAUI apps: build, deploy, debug and edit them for Android, iOS and Mac Catalyst. M20a, the first part, covers:
- where the Android tools are;
- which devices and emulators there are;
- running an app on one.

Debugging (a Mono soft-debugger adapter) is M20b. iOS and Mac Catalyst are M20c. MAUI XAML and Hot Reload are M20d.

The Android workload doesn't ship the Android SDK or a JDK. Users get them from:
- Android Studio;
- Visual Studio's installer (`%ProgramFiles(x86)%\Android\android-sdk` and `…\Android\openjdk`);
- the workload's own `InstallAndroidDependencies` target (`~/.android/sdk`);
- `ANDROID_HOME`/`JAVA_HOME`.

The workload finds the SDK itself only through MSBuild properties or those variables.

## Decision

**Finding the tools** (`AndroidSdk`, workbench `Mobile/`).
- The SDK is the first folder with `platform-tools/` among, in order:
  - `ANDROID_HOME`, `ANDROID_SDK_ROOT`;
  - Android Studio's folder for the OS;
  - Visual Studio's installer folder (Windows);
  - `~/.android/sdk`.
- The JDK is the first with `bin/` among:
  - `JAVA_HOME`;
  - the newest `jdk-*` of Visual Studio's OpenJDK, Microsoft's and Adoptium's (Windows);
  - `/Library/Java/JavaVirtualMachines` (macOS) or `/usr/lib/jvm` (Linux);
  - Android Studio's bundled runtime;
  - `~/.android/jdk`.
- Builds pass both as `AndroidSdkDirectory` and `JavaSdkDirectory`, so the workload uses the same tools the IDE found.

**Devices** (`AndroidDevices`), through the SDK's own tools, one process per question, like git and docker:
- `adb devices -l` gives running devices and emulators, with their state; `unauthorized` and `offline` are shown, not hidden.
- `adb -s <serial> emu avd name` tells which virtual device an emulator is.
- `emulator -list-avds` gives the virtual devices that could start. Their names come from `config.ini`'s `avd.ini.displayname`.
- Starting an emulator runs `emulator -avd <name>` in its own window. It stays running when the IDE closes, as Android Studio's does. The IDE waits for `sys.boot_completed`.

**Running** (`AndroidRunner`).
- The device is the toolbar's choice: the list appears while the startup project is a mobile app, an executable with an `-android`, `-ios` or `-maccatalyst` framework.
- The build host builds the device's framework only, with the workload's `Install` target, `TargetFramework=net10.0-android` and `AdbTarget=-s <serial>`. A MAUI project's iOS target never builds on Windows or Linux this way.
- adb then:
  - stops a previous instance (`am force-stop`);
  - starts the launcher activity (`monkey -p <ApplicationId> -c android.intent.category.LAUNCHER 1`);
  - finds its process (`pidof`).
- `adb logcat --pid` is the program's output, in its Run channel.
- Stop ends that reader and stops the app on the device.

**The Devices window** lists everything with its state, chooses the device, and starts an emulator (double-click). Creating virtual devices and installing SDK packages stay with Android Studio and the workload's `InstallAndroidDependencies`.

## Consequences

- Nothing Android-specific runs until a mobile project is the startup project or the Devices window opens.
- The app's own `ApplicationId` (from the project) names it on the device. A project without one gets the workload's default.
- **Tests.** The end-to-end test boots an emulator and builds for Android, so it runs only with `CODALUS_TEST_ANDROID=1`. CI's hosted runners don't have the workloads, and their emulators would cost minutes per run. Parsing, detection and the device list are unit-tested with stand-in tools.
