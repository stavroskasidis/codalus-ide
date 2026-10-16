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
- **Workloads:** when a project needs a workload that isn't installed, Codalus IDE offers **Install Workloads**. That runs `dotnet workload restore` for the project in the terminal, where you can enter a password if the .NET SDK is in a system folder.
- **The Android SDK and a JDK:** **Install the Android SDK** (in the Devices window, Help → Check Environment, or Debug → Other Debug Targets) installs them through the Android workload's own `InstallAndroidDependencies` target. Android Studio's folder is the default (`%LOCALAPPDATA%\Android\Sdk`, `~/Library/Android/sdk`, `~/Android/Sdk`). Installing accepts the [Android SDK's license](https://developer.android.com/studio/terms), so the dialog shows the exact command first.

Codalus IDE finds an SDK that's already installed through these settings, in this order:
1. **Android SDK Location** and **Java Development Kit Location** (Tools → Options → Mobile; `android.sdkDirectory`, `android.jdkDirectory`).
2. `ANDROID_HOME` (or `ANDROID_SDK_ROOT`) and `JAVA_HOME`.
3. The usual install folders.

Help → Check Environment lists each workload the solution needs, the Android SDK and the JDK, with a fix for anything missing.

## Running on a device

When the startup project is a MAUI app, the toolbar shows a **device** next to it. Click it to choose where the app runs:
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
- **Unpackaged** (`<WindowsPackageType>None</WindowsPackageType>`, as the templates have it): Codalus IDE builds the Windows framework alone (the other platforms' workloads aren't needed for it), then **Start Without Debugging** runs it, **Start** (`F5`) debugs it with netcoredbg (breakpoints, stepping, Locals, Watch: everything a .NET program gets), and **Start with Hot Reload** runs it under `dotnet watch`.
- **Packaged** (MSIX): Codalus IDE builds it, registers the build's layout with Windows (`Add-AppxPackage -Register`), and starts it the way Windows starts packaged apps; **Start** attaches the debugger to it. Registering needs [Developer Mode](windows-desktop.md#packaged-apps-and-developer-mode). Hot Reload isn't available for packaged apps.

Help → Check Environment lists the `maui-windows` workload, the Windows App SDK runtime, and Developer Mode when an app is packaged.

## The Devices window

View → Other Windows → **Devices** lists everything apps can run on. The device each platform's runs go to is marked ✓. From it you can:
- **Start** an emulator or boot a simulator;
- **Run Here** to make the selected device the one runs go to (double-click does the same, or starts it when it isn't running);
- **Refresh** the list;
- **Install the Android SDK** when it's missing.

The window also shows where the Android SDK and the JDK are. Create virtual devices with the SDK's Device Manager (`avdmanager`, or Android Studio).

## Editing MAUI XAML

`.xaml` files in a MAUI project get the same editing as [Avalonia's XAML](avalonia.md#editing-xaml), with MAUI's controls and your own:
- **Completion** of elements, properties, events, and attached properties (`Grid.Row`, and your controls' own). It also completes values (enums, `True`/`False`, named colors) and markup extensions. Binding paths come from the `x:DataType` in scope, which in MAUI also makes the binding compiled.
- **Live errors:** unknown types, properties and binding paths.
- **Hover** and **Go to Definition** into MAUI's types, your view models and controls, and the code-behind (`x:Class`).

Pages in a plain class library work too: MAUI's packages have `net10.0` versions, so a library of pages and view models loads and builds without the workloads.

## New MAUI projects

With the MAUI workload installed, File → New Project lists **.NET MAUI App**, **.NET MAUI Blazor Hybrid App** and **.NET MAUI Class Library**.

## Not available yet

- Debugging on iPhones and iPads, and debugging .NET 11 apps on CoreCLR.
- A XAML previewer and XAML Hot Reload for MAUI: no open-source ones exist.
- Creating virtual devices and managing SDK packages from the IDE. Use the SDK's `avdmanager` and `sdkmanager`, or Android Studio.
- Running the Windows target from Linux and macOS (through a Windows machine), and breakpoints in a packaged Windows app's start-up code.
