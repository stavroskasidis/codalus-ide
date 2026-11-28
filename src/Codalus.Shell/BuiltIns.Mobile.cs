using Codalus.Shell.ViewModels;
using Codalus.Workbench.Commands;
using Codalus.Workbench.Context;
using Codalus.Workbench.Localization;
using Codalus.Workbench.Menus;
using Codalus.Workbench.ToolWindows;

namespace Codalus.Shell;

/// <summary>
/// .NET MAUI (M20a, ADR 0064): the Devices window, the device picker and the Android SDK install; the platforms' native
/// tools (ADR 0104): the Android Device Manager and SDK Manager, the adb Command Prompt, the Device Log, and on a Mac the
/// Simulator and Xcode, in Tools → Android and Tools → iOS and as toolbar groups while the solution targets them.
/// </summary>
internal static partial class BuiltIns
{
    public const string ToolsAndroidMenu = "menuBar/tools/android";
    public const string ToolsIosMenu = "menuBar/tools/ios";

    public static IEnumerable<ToolWindowDescriptor> MobileToolWindows()
    {
        var shell = ContributionSource.Shell;
        yield return new ToolWindowDescriptor(DevicesViewModel.ToolWindowId, "Devices", ToolWindowLocation.Right, ToolWindowKind.BuiltIn, shell) { Icon = "phone" };
        yield return new ToolWindowDescriptor(AndroidDeviceManagerViewModel.ToolWindowId, "Android Device Manager", ToolWindowLocation.Document, ToolWindowKind.BuiltIn, shell) { Icon = "phone-settings" };
        yield return new ToolWindowDescriptor(AndroidSdkManagerViewModel.ToolWindowId, "Android SDK Manager", ToolWindowLocation.Document, ToolWindowKind.BuiltIn, shell) { Icon = "package-download" };
        yield return new ToolWindowDescriptor(DeviceLogViewModel.ToolWindowId, "Device Log", ToolWindowLocation.Bottom, ToolWindowKind.BuiltIn, shell) { Icon = "phone-log" };
    }

    public static void RegisterMobileCommands(ShellHost shell)
    {
        var commands = shell.Workbench.Commands;
        void Add(string id, string title, Func<Task> handler, string? enablement = null) =>
            commands.Register(
                new CommandDescriptor(id, title, ContributionSource.Shell) { Category = "Mobile", Enablement = WhenClause.Parse(enablement) },
                async (_, _) =>
                {
                    await Infrastructure.Ui.RunAsync(handler).ConfigureAwait(false);
                    return null;
                });

        Add("mobile.selectDevice", "Select Device…", shell.SelectDeviceAsync, "mobileStartup");
        Add("mobile.showDevices", "Devices", () =>
        {
            shell.ShowDevices();
            return Task.CompletedTask;
        });
        Add("mobile.refreshDevices", "Refresh Devices", shell.RefreshDevicesAsync);
        Add("mobile.installAndroidSdk", "Install the Android SDK…", shell.InstallAndroidDependenciesAsync, "solutionOpen");

        // A MAUI app's Publish (ADR 0114): its archives, and Distribute.
        Add("mobile.archiveManager", "Archive Manager", () =>
        {
            shell.OpenArchiveManager();
            return Task.CompletedTask;
        }, "mobileProjects && solutionTrusted");

        // The native tools (ADR 0104).
        Add("android.deviceManager", "Android Device Manager", () =>
        {
            shell.ShowAndroidDeviceManager();
            return Task.CompletedTask;
        });
        Add("android.sdkManager", "Android SDK Manager", () =>
        {
            shell.ShowAndroidSdkManager();
            return Task.CompletedTask;
        });
        Add("android.adbPrompt", "Android adb Command Prompt", () =>
        {
            shell.OpenAdbCommandPrompt();
            return Task.CompletedTask;
        });
        Add("mobile.deviceLog", "Device Log", () =>
        {
            shell.ShowDeviceLog();
            return Task.CompletedTask;
        });
        // macOS only, so their titles are declared through the localizer: en.json, the same on every OS, gets them from
        // the code (LocalizationCatalogTests) where the commands aren't registered.
        if (shell.Workbench.Platform == Codalus.Platform.PlatformKind.MacOS)
        {
            Add("ios.openSimulator", Localizer.Get("command.ios.openSimulator", "Open Simulator"), shell.OpenSimulatorAsync);
            Add("ios.openXcode", Localizer.Get("command.ios.openXcode", "Open Xcode"), shell.OpenXcodeAsync);
        }
    }

    public static void RegisterMobileMenus(MenuRegistry menus, Codalus.Platform.PlatformKind platform)
    {
        var shell = ContributionSource.Shell;
        var tools = MenuLocations.MenuBar("tools");
        var mac = platform == Codalus.Platform.PlatformKind.MacOS;
        var rules = new List<MenuItemRule>
        {
            new(DebugTargetsMenu, "mobile.selectDevice", shell) { Group = "3-mobile", Order = 1, When = WhenClause.Parse("mobileStartup") },
            new(DebugTargetsMenu, "mobile.installAndroidSdk", shell) { Group = "3-mobile", Order = 2, When = WhenClause.Parse("mobileProjects") },
            new(MenuLocations.MenuBar("build"), "mobile.archiveManager", shell) { Group = "2-project", Order = 10, When = WhenClause.Parse("mobileProjects") },

            // Tools → Android ▸, as the classic IDE has it.
            MenuItemRule.ForSubmenu(tools, ToolsAndroidMenu, "Android", shell) with { Group = "1-mobile", Order = 1 },
            new(ToolsAndroidMenu, "android.deviceManager", shell) { Group = "1-tools", Order = 1 },
            new(ToolsAndroidMenu, "android.sdkManager", shell) { Group = "1-tools", Order = 2 },
            new(ToolsAndroidMenu, "android.adbPrompt", shell) { Group = "1-tools", Order = 3 },
            new(ToolsAndroidMenu, "mobile.deviceLog", shell) { Group = "1-tools", Order = 4 },
            new(ToolsAndroidMenu, "mobile.installAndroidSdk", shell) { Group = "2-install", Order = 1, When = WhenClause.Parse("mobileProjects") },

            // Not in View → Other Windows: it's at a classic IDE's length (UxPassTests' 32 entries), and Tools → Android has them.

            // The standard toolbar's Android group, while the solution has an Android project (each removable in Customize).
            new(MenuCustomizations.StandardToolbar, "android.deviceManager", shell) { Group = "mobile-android", Order = 1, When = WhenClause.Parse("androidProjects") },
            new(MenuCustomizations.StandardToolbar, "android.sdkManager", shell) { Group = "mobile-android", Order = 2, When = WhenClause.Parse("androidProjects") },
            new(MenuCustomizations.StandardToolbar, "android.adbPrompt", shell) { Group = "mobile-android", Order = 3, When = WhenClause.Parse("androidProjects") },
            new(MenuCustomizations.StandardToolbar, "mobile.deviceLog", shell) { Group = "mobile-android", Order = 4, When = WhenClause.Parse("androidProjects || iosProjects") },
        };

        if (mac)
        {
            rules.AddRange(
            [
                MenuItemRule.ForSubmenu(tools, ToolsIosMenu, Localizer.Get("menu.menuBar/tools/ios", "iOS"), shell) with { Group = "1-mobile", Order = 2 },
                new(ToolsIosMenu, "ios.openSimulator", shell) { Group = "1-tools", Order = 1 },
                new(ToolsIosMenu, "ios.openXcode", shell) { Group = "1-tools", Order = 2 },
                new(ToolsIosMenu, "mobile.deviceLog", shell) { Group = "1-tools", Order = 3 },
                new(MenuCustomizations.StandardToolbar, "ios.openSimulator", shell) { Group = "mobile-ios", Order = 1, When = WhenClause.Parse("iosProjects") },
                new(MenuCustomizations.StandardToolbar, "ios.openXcode", shell) { Group = "mobile-ios", Order = 2, When = WhenClause.Parse("iosProjects") },
            ]);
        }

        menus.Add(rules);
    }
}
