using System.Runtime.InteropServices;
using System.Text;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Localization;
using Codalus.Workbench.Mobile;

namespace Codalus.Shell;

/// <summary>
/// The platforms' native tools (ADR 0104): the Android Device Manager, the Android SDK Manager, the adb Command Prompt,
/// the Device Log, and on a Mac the Simulator and Xcode. Installing SDK packages shows the licenses <c>sdkmanager</c>
/// would ask about and installs only after the user accepts them; a new emulator's system image is installed the same way.
/// </summary>
public sealed partial class ShellHost
{
    public AndroidDeviceManagerViewModel AndroidDeviceManager { get; private set; } = null!;

    public AndroidSdkManagerViewModel AndroidSdkManager { get; private set; } = null!;

    public DeviceLogViewModel DeviceLogWindow { get; private set; } = null!;

    private void InitializeAndroidTools()
    {
        AndroidDeviceManager = new AndroidDeviceManagerViewModel(this);
        AndroidSdkManager = new AndroidSdkManagerViewModel(this);
        DeviceLogWindow = new DeviceLogViewModel(this);
    }

    public void ShowAndroidDeviceManager()
    {
        Layout.ShowToolWindow(AndroidDeviceManagerViewModel.ToolWindowId, focus: true);
        _ = AndroidDeviceManager.RefreshAsync();
    }

    public void ShowAndroidSdkManager()
    {
        Layout.ShowToolWindow(AndroidSdkManagerViewModel.ToolWindowId, focus: true);
        _ = AndroidSdkManager.EnsureLoadedAsync();
    }

    public void ShowDeviceLog()
    {
        Layout.ShowToolWindow(DeviceLogViewModel.ToolWindowId, focus: true);
        _ = DeviceLogWindow.RefreshSourcesAsync();
    }

    /// <summary>
    /// A terminal with the Android SDK's tools on <c>PATH</c> (adb, the emulator, sdkmanager and avdmanager, the JDK's
    /// java) and <c>ANDROID_HOME</c> and <c>JAVA_HOME</c> set, in the startup project's folder, as the classic IDE's
    /// "Android Adb Command Prompt".
    /// </summary>
    public void OpenAdbCommandPrompt()
    {
        if (Mobile.Android().Adb is null)
        {
            OfferAndroidSdk(Localizer.Get("msg.android.noAdb", "The adb Command Prompt needs the Android SDK, which isn't installed (Tools → Options → Mobile sets its folder)."));
            return;
        }

        var directory = Runs.StartupProject is { } startup ? Path.GetDirectoryName(startup) : null;
        Layout.ShowToolWindow(TerminalViewModel.ToolWindowId);
        if (TerminalWindow.NewTerminal(null, directory ?? TerminalDirectory(), AndroidSdk.ToolEnvironment(path: true), Localizer.Get("msg.android.adbPrompt", "adb")) is null && TerminalWindow.Error is { } error)
        {
            Workbench.Notifications.Warning(error);
        }
    }

    /// <summary>Says what's missing and offers the Android SDK's install (through an Android project's workload).</summary>
    internal void OfferAndroidSdk(string message)
    {
        var install = Localizer.Get("msg.mobile.installAndroid", "Install…");
        _ = Workbench.Notifications.Warning(message, install).Result.ContinueWith(
            t =>
            {
                if (t.Result == install)
                {
                    Infrastructure.Ui.Post(() => _ = InstallAndroidDependenciesAsync());
                }
            },
            TaskScheduler.Default);
    }

    /// <summary>Opens Xcode's Simulator app.</summary>
    public Task OpenSimulatorAsync() => OpenMacAppAsync("Simulator");

    /// <summary>Opens Xcode.</summary>
    public Task OpenXcodeAsync() => OpenMacAppAsync("Xcode");

    private Task OpenMacAppAsync(string app)
    {
        if (!StartDetached("open", ["-a", app], null))
        {
            Workbench.Notifications.Warning(Localizer.Format("msg.ios.couldNotOpen", "{0} didn't open: is Xcode installed?", app));
        }

        return Task.CompletedTask;
    }

    // ------------------------------------------------------------------ packages

    /// <summary>
    /// Installs and uninstalls SDK packages: the licenses the installs need first (shown in full; nothing installs
    /// without Accept), then <c>sdkmanager --install</c> and <c>--uninstall</c>, elevated when the SDK's folder needs it.
    /// Returns whether everything ran and succeeded.
    /// </summary>
    public async Task<bool> ChangeSdkPackagesAsync(IReadOnlyList<string> install, IReadOnlyList<string> uninstall)
    {
        if (AndroidSdk.Tools() is null || AndroidSdk.Locations().Sdk is not { } sdk)
        {
            OfferAndroidSdk(Localizer.Get("msg.android.noCommandLineTools", "Managing the Android SDK needs its command-line tools (sdkmanager), which aren't installed. The Android workload's install adds them."));
            return false;
        }

        if (install.Count > 0 && !await AcceptLicensesAsync(install).ConfigureAwait(true))
        {
            return false;
        }

        var ok = true;
        if (install.Count > 0)
        {
            var code = await RunSdkChangeAsync(AndroidSdk.InstallCommand(install), sdk, Localizer.Format("msg.android.installing", "Installing {0}", string.Join(", ", install))).ConfigureAwait(true);
            ok = code == 0;
        }

        if (ok && uninstall.Count > 0)
        {
            var code = await RunSdkChangeAsync(AndroidSdk.UninstallCommand(uninstall), sdk, Localizer.Format("msg.android.uninstalling", "Uninstalling {0}", string.Join(", ", uninstall))).ConfigureAwait(true);
            ok = code == 0;
        }

        if (!ok)
        {
            Workbench.Notifications.Error(Localizer.Get("msg.android.changeFailed", "Changing the Android SDK didn't succeed. The Android SDK output has the details."));
        }

        await RefreshDevicesAsync().ConfigureAwait(true);
        return ok;
    }

    /// <summary>Updates every installed package to its latest version, after its licenses.</summary>
    public async Task<bool> UpdateAllSdkPackagesAsync(IReadOnlyList<string> updatable)
    {
        if (AndroidSdk.Locations().Sdk is not { } sdk || AndroidSdk.Tools() is null || !await AcceptLicensesAsync(updatable).ConfigureAwait(true))
        {
            return false;
        }

        var code = await RunSdkChangeAsync(AndroidSdk.UpdateAllCommand(), sdk, Localizer.Get("msg.android.updatingAll", "Updating the Android SDK's packages")).ConfigureAwait(true);
        if (code is not 0)
        {
            Workbench.Notifications.Error(Localizer.Get("msg.android.changeFailed", "Changing the Android SDK didn't succeed. The Android SDK output has the details."));
        }

        return code == 0;
    }

    /// <summary>
    /// The license agreement, as the classic IDE shows it: the licenses the packages need that this SDK hasn't accepted,
    /// each in full. True when there are none or the user accepts.
    /// </summary>
    private async Task<bool> AcceptLicensesAsync(IReadOnlyList<string> packages)
    {
        IReadOnlyList<SdkLicense> licenses;
        StatusBar.ShowMessage(Localizer.Get("msg.android.readingLicenses", "Reading the Android SDK's licenses…"), TimeSpan.FromSeconds(10));
        try
        {
            licenses = await AndroidSdk.PendingLicensesAsync(packages, _lifetime.Token).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            Workbench.Notifications.Error(Localizer.Format("msg.android.licensesFailed", "Reading the licenses failed: {0}", ex.Message));
            return false;
        }
        catch (OperationCanceledException)
        {
            return false;
        }

        if (licenses.Count == 0)
        {
            return true;
        }

        var details = new List<DetailLine>();
        foreach (var license in licenses)
        {
            details.Add(new DetailLine(license.Id, "header"));
            details.AddRange(Wrap(license.Text, 100).Select(l => new DetailLine(l)));
        }

        var accept = Localizer.Get("msg.android.accept", "Accept");
        var result = await Dialogs.ShowAsync(new DialogRequest(
            Localizer.Get("msg.android.licenseTitle", "Android SDK - License Agreement"),
            Localizer.Format("msg.android.licenseMessage", "Installing {0} needs these licenses. Read and accept them to continue.", string.Join(", ", packages)),
            [accept, Localizer.Get("msg.android.cancel", "Cancel")])
        {
            DefaultButton = accept,
            Details = details,
            Icon = "package-download",
        }).ConfigureAwait(true);
        return result.Button == accept;
    }

    /// <summary>Text wrapped at word boundaries to lines of at most <paramref name="width"/> characters (paragraphs kept).</summary>
    internal static IEnumerable<string> Wrap(string text, int width)
    {
        foreach (var paragraph in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = new StringBuilder();
            foreach (var word in paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.Length > 0 && line.Length + 1 + word.Length > width)
                {
                    yield return line.ToString();
                    line.Clear();
                }

                if (line.Length > 0)
                {
                    line.Append(' ');
                }

                line.Append(word);
            }

            yield return line.ToString();
        }
    }

    // ------------------------------------------------------------------ emulators

    /// <summary>
    /// A new emulator: a device profile, then a system image (installed, or downloaded after its licenses), then its
    /// name; <c>avdmanager</c> creates it. Returns its name, or null when cancelled or failed.
    /// </summary>
    public async Task<string?> NewEmulatorAsync()
    {
        if (AndroidSdk.Tools() is null)
        {
            OfferAndroidSdk(Localizer.Get("msg.android.noCommandLineTools", "Managing the Android SDK needs its command-line tools (sdkmanager), which aren't installed. The Android workload's install adds them."));
            return null;
        }

        IReadOnlyList<AndroidDeviceDefinition> devices;
        IReadOnlyList<SdkPackage> packages;
        StatusBar.ShowMessage(Localizer.Get("msg.android.readingProfiles", "Reading the device profiles and system images…"), TimeSpan.FromSeconds(10));
        try
        {
            var devicesTask = AndroidSdk.DeviceDefinitionsAsync(_lifetime.Token);
            var packagesTask = AndroidSdk.ListPackagesAsync(_lifetime.Token);
            devices = await devicesTask.ConfigureAwait(true);
            packages = await packagesTask.ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            Workbench.Notifications.Error(Localizer.Format("msg.android.newFailed", "Creating an emulator failed: {0}", ex.Message));
            return null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }

        var device = await Palette.ShowAsync(
            [.. devices.Select(d => new QuickPickItem(d.Name) { Detail = d.Tag is { Length: > 0 } tag ? tag : d.Oem, Icon = "phone", Tag = d })],
            Localizer.Get("msg.android.pickProfile", "The new emulator's device profile")).ConfigureAwait(true);
        if (device?.Tag is not AndroidDeviceDefinition profile)
        {
            return null;
        }

        var images = AndroidSdkService.SystemImages(packages, RuntimeInformation.OSArchitecture);
        if (images.Count == 0)
        {
            Workbench.Notifications.Warning(Localizer.Get("msg.android.noImages", "There's no system image for this machine's processor in the Android SDK's list."));
            return null;
        }

        var image = await Palette.ShowAsync(
            [.. images.Select(i => new QuickPickItem(SystemImageLabel(i)) { Detail = i.Installed ? Localizer.Get("msg.android.installed", "installed") : Localizer.Get("msg.android.download", "download"), Icon = i.Installed ? "package-download" : "download", Tag = i })],
            Localizer.Get("msg.android.pickImage", "The new emulator's system image")).ConfigureAwait(true);
        if (image?.Tag is not SdkPackage systemImage)
        {
            return null;
        }

        var existing = AndroidSdk.Emulators();
        var name = await Dialogs.InputAsync(
            Localizer.Get("msg.android.newTitle", "New Emulator"),
            Localizer.Format("msg.android.newMessage", "A {0} with {1}. Its name:", profile.Name, SystemImageLabel(systemImage)),
            AndroidEmulators.SuggestName(profile, systemImage.ApiLevel, existing),
            text => AndroidEmulators.ValidateName(text, existing)).ConfigureAwait(true);
        if (name is null)
        {
            return null;
        }

        if (!systemImage.Installed && !await ChangeSdkPackagesAsync([systemImage.Path], []).ConfigureAwait(true))
        {
            return null;
        }

        var log = AndroidSdkLog;
        log.AppendLine($"[{DateTime.Now:T}] avdmanager {string.Join(' ', AndroidEmulators.CreateArguments(name, systemImage.Path, profile.Id))}");
        var (code, output) = await AndroidSdk.CreateEmulatorAsync(name, systemImage.Path, profile.Id, _lifetime.Token).ConfigureAwait(true);
        log.Append(output);
        if (code != 0)
        {
            Workbench.Output.Show(log);
            Workbench.Notifications.Error(Localizer.Format("msg.android.createFailed", "avdmanager couldn't create {0} (exit code {1}). The Android SDK output has the details.", name, code));
            return null;
        }

        await RefreshDevicesAsync().ConfigureAwait(true);
        return name;
    }

    /// <summary>A system image as people read it: "Android API 36, Google Play, x86_64".</summary>
    internal static string SystemImageLabel(SdkPackage image) =>
        AndroidSdkPackages.SystemImageOf(image.Path) is { } parts
            ? $"API {parts.ApiLevel?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?"} · {AndroidSdkPackages.TagTitle(parts.Tag)} · {parts.Abi}"
            : image.Description;
}
