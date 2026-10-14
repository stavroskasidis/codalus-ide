# .NET MAUI: Android apps, devices and XAML

Codalus IDE runs .NET MAUI apps on Android devices and emulators, on Windows, macOS and Linux. It edits MAUI's XAML with completion and checks from your project's own types. Debugging on a device and iOS and Mac Catalyst come next (the rest of M20).

## What you need

- The .NET MAUI workloads: `dotnet workload install maui` (or `maui-android` on Linux, where MAUI has only the Android target).
- The **Android SDK** and a **JDK**. Codalus IDE finds them where the tools install them:
  - `ANDROID_HOME` (or `ANDROID_SDK_ROOT`) and `JAVA_HOME`, when set;
  - Android Studio's SDK folder, and its bundled JDK;
  - on Windows, the SDK and OpenJDK that Visual Studio's installer puts under `Program Files (x86)\Android`;
  - `~/.android/sdk`, where the workload's `InstallAndroidDependencies` target installs them:

```bash
dotnet build -t:InstallAndroidDependencies -f net10.0-android -p:AcceptAndroidSDKLicenses=true
```

The Devices window shows which SDK and JDK it uses. Builds use the same ones.

## Devices

When the startup project is a MAUI app (any app with an Android, iOS or Mac Catalyst target framework), the toolbar gets a **device list** next to it. It lists:
- connected devices and running emulators;
- the emulators you can start, marked *(not running)*.

**View → Devices** shows the same list, with each device's state and serial. Double-click a device to use it; an emulator that isn't running starts in its own window, and stays running after Codalus IDE closes. A device that says **unauthorized** is waiting for you to allow USB debugging on it.

Create emulators with Android Studio's Device Manager (or the SDK's `avdmanager`), then choose **Refresh**.

## Running

**Start Without Debugging** (Ctrl+F5) on a MAUI app:
1. starts the chosen emulator, if needed;
2. builds the app for Android only (the device's target framework) and installs it on the device;
3. starts it, and shows its log (`adb logcat` for the app's process) in its Run output.

**Stop** stops the app on the device. A build error stops the start, with the errors in the Error List as for any build.

## XAML

A `.xaml` file in a MAUI project (one with an Android, iOS or Mac Catalyst target framework) opens as **MAUI XAML**, with the same editing as Avalonia's XAML, from the types your project references:
- completion of elements, properties, events and attached properties (`Grid.Row`), named colors, and binding modes;
- binding paths from the page's `x:DataType` (compiled bindings);
- errors for unknown elements and properties, hover, Go to Definition, and renaming an `x:Name`.

The project must have been restored and loaded once, since the types come from its MAUI packages. Other `.xaml` files open as XML. A XAML previewer and XAML Hot Reload aren't available for MAUI.

## Debugging

Starting with the debugger (F5) isn't available for device apps yet: Start Without Debugging (Ctrl+F5) runs them. MAUI apps on Android run on the Mono runtime, which needs its own debugger. Codalus IDE has one, but it isn't dependable on Android yet.
