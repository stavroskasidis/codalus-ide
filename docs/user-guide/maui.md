# .NET MAUI: devices, emulators and XAML

Codalus IDE builds and runs .NET MAUI apps on Android devices and emulators on Windows, Linux and macOS, on iOS simulators, iPhones and iPads and this Mac (Mac Catalyst) on macOS, and on this PC ("Windows Machine", MAUI's Windows target) on Windows.

## What you need

| For | You need |
|---|---|
| Any MAUI project | The MAUI workload for each platform: `maui-android`, `maui-ios`, `maui-maccatalyst`, `maui-windows` (`dotnet workload install maui-android`, or `maui` for all of them) |
| Windows | Windows 10 version 1809 or later. Packaged (MSIX) apps also need Developer Mode |
| Android | The Android SDK and a JDK (17 or later). To run on an emulator, also the SDK's emulator and a virtual device |
| iOS and Mac Catalyst | A Mac with the Xcode version the workload asks for |

You don't have to install these yourself:
- **Workloads:** when a project needs a workload that isn't installed, Codalus IDE offers **Install Workloads**. That runs `dotnet workload restore` for the project in the terminal. On macOS and Linux, when the .NET SDK is in a folder only an administrator can change (`/usr/local/share/dotnet`, a distribution's package), the command runs with `sudo`, which asks for your password there; on Windows the SDK's installer asks for permission itself. Installing a single workload from Help → Check Environment does the same.
- **The Android SDK and a JDK:** **Install the Android SDK** (in the Devices window, Tools → Android, Help → Check Environment, or Debug → Other Debug Targets) installs them through the Android workload's own `InstallAndroidDependencies` target. Android Studio's folder is the default (`%LOCALAPPDATA%\Android\Sdk`, `~/Library/Android/sdk`, `~/Android/Sdk`). Installing accepts the [Android SDK's license](https://developer.android.com/studio/terms), so the dialog shows the exact command first.
- **An SDK only an administrator can change:** the classic IDE installs the Android SDK in `C:\Program Files (x86)\Android\android-sdk`, which a normal user can't write. Codalus IDE checks the folder first and, on Windows, runs the same install elevated: Windows asks for an administrator's permission once, and the output still shows in the Android SDK channel. On macOS and Linux it runs the command with `sudo` in the terminal, where sudo asks for your password. The SDK Manager's installs, updates and uninstalls do the same.

Codalus IDE finds an SDK that's already installed through these settings, in this order:
1. **Android SDK Location** and **Java Development Kit Location** (Tools → Options → Mobile; `android.sdkDirectory`, `android.jdkDirectory`).
2. `ANDROID_HOME` (or `ANDROID_SDK_ROOT`) and `JAVA_HOME`.
3. The usual install folders.

Help → Check Environment lists each workload the solution needs, the Android SDK and the JDK, with a fix for anything missing.

## Running on a device

When the startup project is a MAUI app, the toolbar shows the **device** it runs on next to it, platform first: **Android: Pixel 9 Pro API 36**, **iOS: iPhone 17**. Its tooltip names the framework **Start** builds (`net10.0-android`). Click it to choose where the app runs. The list is grouped by platform, each group headed by the platform and its framework (**Android (net10.0-android)**, **iOS (net10.0-ios)**), the current platform first:
- **Android:** connected phones and tablets (with USB debugging on), running emulators, and the virtual devices you've created. A virtual device that isn't running starts when you run the app on it.
- **iOS** (on a Mac): simulators and connected iPhones and iPads.
- **This Mac:** Mac Catalyst.
- **Windows Machine** (on Windows): the Windows target, as a desktop app. On Windows it's where runs go until you choose another device. On Linux and macOS a project that targets Windows lists it as Windows only: running it from there comes later.

Choosing a device of another platform switches the project to that platform's framework (`net10.0-android`, `net10.0-ios`…). Each solution remembers its choices.

**Start Without Debugging** (`Ctrl+F5`) builds, deploys and starts the app on the device with the platform SDK's own run target, `dotnet build -t:Run -f <framework>`. Its output is in the Run channel of the Output window, and **Stop** ends it.

A device that can't be used yet says why:
- **unauthorized:** allow USB debugging on the phone when it asks;
- **offline:** reconnect it, or wait for the emulator to finish starting;
- an iPhone that isn't paired with this Mac.

## Debugging on a device

**Start** (`F5`) builds and deploys the app the same way, and debugs it on Android devices and emulators, iOS simulators and Mac Catalyst. The app runs on the Mono runtime there, so Codalus IDE debugs it with its own Mono debugger:
- **Breakpoints** bind when their code loads. A line without code moves to the next line that has some.
- **Stepping:** Step Over, Into and Out. With Just My Code on (the default), stepping into framework code steps out again.
- **The Call Stack and Threads windows.**
- **Locals,** with objects' fields and properties, collections and arrays.
- **Watch and the Debug Console** take names and member paths (`item.Title`, `this.Count`, `items[0]`).
- **Exceptions:** set in the Exception Settings, with the exception's message when it stops.

**Stop** ends the app. Debugging on an iPhone or iPad isn't available yet (debug in a simulator, or run on the device with `Ctrl+F5`). Neither are conditional breakpoints, changing values, or evaluating calls and operators.

.NET 11 runs MAUI apps on CoreCLR, which has no open-source debugger for mobile yet. To debug a .NET 11 MAUI app, set `<UseMonoRuntime>true</UseMonoRuntime>` in its project, as Microsoft's release notes suggest.

## Running on Windows

On Windows, MAUI's templates add a Windows framework (`net10.0-windows10.0.19041.0`), which runs on **Windows Machine**. It's a WinUI 3 app on .NET's own runtime (CoreCLR), so it runs and debugs like any desktop program, the way [WinUI 3 apps](windows-desktop.md#winui-3) do:
- **Unpackaged** (`<WindowsPackageType>None</WindowsPackageType>`, as the templates have it): Codalus IDE builds the Windows framework alone (the other platforms' workloads aren't needed for it), then **Start Without Debugging** runs it, **Start** (`F5`) debugs it with the .NET debugger (breakpoints, stepping, Locals, Watch: everything a .NET program gets), and **Start with Hot Reload** runs it under `dotnet watch`.
- **Packaged** (MSIX): Codalus IDE builds it, registers the build's layout with Windows (`Add-AppxPackage -Register`), and starts it the way Windows starts packaged apps; **Start** attaches the debugger to it. Registering needs [Developer Mode](windows-desktop.md#packaged-apps-and-developer-mode). Hot Reload isn't available for packaged apps.

Help → Check Environment lists the `maui-windows` workload, the Windows App SDK runtime, and Developer Mode when an app is packaged.

## The Devices window

View → Other Windows → **Devices** lists everything apps can run on. The device each platform's runs go to is marked ✓. From it you can:
- **Start** an emulator or boot a simulator;
- **Run Here** to make the selected device the one runs go to (double-click does the same, or starts it when it isn't running);
- **Refresh** the list;
- **Install the Android SDK** when it's missing.

The window also shows where the Android SDK and the JDK are. Create and change virtual devices in the [Android Device Manager](#android-device-manager).

## Android and iOS tools

Tools → **Android** has the Android SDK's own tools, and on a Mac Tools → **iOS** has Xcode's. While the solution has an Android project, the toolbar shows them as a group of buttons at its end: **Android Device Manager**, **Android SDK Manager**, **Android adb Command Prompt** and **Device Log**. On a Mac with an iOS project, a second group has **Open Simulator** and **Open Xcode**. You can remove any of these buttons in Tools → Customize.

They use the SDK's command-line tools (`sdkmanager` and `avdmanager`, in `cmdline-tools`). When the SDK doesn't have them, the windows offer **Install**, which runs the Android workload's install (it adds them).

### Android Device Manager

Tools → Android → **Android Device Manager** lists your emulators (Android virtual devices) with their device, API level, processor and system image, and whether each one is running.
- **New** creates one: pick a device profile (Pixel 7, a tablet…), then a system image for this machine's processor (Google Play or Google APIs, the newest first), then its name. A system image that isn't installed yet is downloaded first, after its license.
- **Start** starts it and waits until Android has booted. **Cold Boot** (in Start's menu) starts it from scratch instead of its last saved state. **Stop** ends it.
- **Wipe Data** erases everything installed and saved on it, as a factory reset does. **Delete** removes it. Both ask first, and only for a stopped emulator.
- **Open Folder** shows its files.
- Below the list, the selected emulator's **settings**: memory, internal storage, SD card, processor cores, graphics mode and the hardware keyboard. **Save** writes them to its `config.ini`; they apply at its next start. Settings change only while the emulator is stopped.

A double-click starts a stopped emulator.

### Android SDK Manager

Tools → Android → **Android SDK Manager** lists the SDK's packages, installed and available: platforms, system images, build tools, tools (platform tools, the emulator, the command-line tools, the NDK, CMake) and sources. Filter them by kind, search them, or show only the installed ones.
- **Check** a package to install it, **uncheck** an installed one to uninstall it, then **Apply Changes**. Each row says what will happen.
- **Update All** updates every installed package that has a newer version.
- Before installing, it shows each license the packages need that you haven't accepted yet, in full. Nothing installs until you click **Accept**, as in the classic IDE.

The output goes to the **Android SDK** channel of the Output window.

### Android adb Command Prompt

Tools → Android → **Android adb Command Prompt** opens a terminal in the startup project's folder with the SDK's tools on `PATH` (`adb`, `emulator`, `sdkmanager`, `avdmanager`, and the JDK's `java`) and `ANDROID_HOME` and `JAVA_HOME` set.

### Device Log

Tools → Android → **Device Log** (and Tools → iOS on a Mac) shows a device's log as it comes: a running Android device or emulator (`adb logcat`), or on a Mac a booted iOS simulator (its unified log).
- Pick the device at the top. The startup project's device is picked first.
- Filter by the lowest **level** (Verbose to Fatal) and by **text** in the tag or the message.
- **My App** shows only the startup project's app: on Android its processes (by the project's `ApplicationId`, including earlier runs), in a simulator its process name.
- **Pause** stops the list from moving (entries still come in, and show when you resume). **Clear** empties it. **Save** writes the entries shown to a file, in logcat's layout.

Errors show in red and warnings in yellow. The window keeps the last 50,000 entries, and stops following the device when you close it. The log of a physical iPhone or iPad isn't available.

### Simulator and Xcode

On a Mac, Tools → iOS → **Open Simulator** opens Xcode's Simulator app, and **Open Xcode** opens Xcode. Xcode's Devices and Simulators window has no command to open it from outside, so open it from Xcode's Window menu.

## Editing MAUI XAML

`.xaml` files in a MAUI project get the same editing as [Avalonia's XAML](avalonia.md#editing-xaml), with MAUI's controls and your own:
- **Completion** of elements, properties, events, and attached properties (`Grid.Row`, and your controls' own). It also completes values (enums, `True`/`False`, named colors) and markup extensions. Binding paths come from the `x:DataType` in scope, which in MAUI also makes the binding compiled.
- **Live errors:** unknown types, properties and binding paths.
- **Hover** and **Go to Definition** into MAUI's types, your view models and controls, and the code-behind (`x:Class`).

Pages in a plain class library work too: MAUI's packages have `net10.0` versions, so a library of pages and view models loads and builds without the workloads.

## New MAUI projects

With the MAUI workload installed, File → New Project lists **.NET MAUI App**, **.NET MAUI Blazor Hybrid App** and **.NET MAUI Class Library** (the **Mobile** project type filter shows them).

## Not available yet

- Debugging on iPhones and iPads, and debugging .NET 11 apps on CoreCLR.
- A XAML previewer and XAML Hot Reload for MAUI: no open-source ones exist.
- Changing an emulator's device profile after it's created (create a new one), and the log of a physical iPhone or iPad.
- Running the Windows target from Linux and macOS (through a Windows machine), and breakpoints in a packaged Windows app's start-up code.
