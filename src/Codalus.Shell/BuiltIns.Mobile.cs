using Codalus.Shell.ViewModels;
using Codalus.Workbench.Commands;
using Codalus.Workbench.Context;
using Codalus.Workbench.Menus;
using Codalus.Workbench.ToolWindows;

namespace Codalus.Shell;

/// <summary>.NET MAUI (M20a, ADR 0064): the Devices window, the device picker and the Android SDK install.</summary>
internal static partial class BuiltIns
{
    public static IEnumerable<ToolWindowDescriptor> MobileToolWindows()
    {
        yield return new ToolWindowDescriptor(DevicesViewModel.ToolWindowId, "Devices", ToolWindowLocation.Right, ToolWindowKind.BuiltIn, ContributionSource.Shell) { Icon = "phone" };
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
    }

    public static void RegisterMobileMenus(MenuRegistry menus)
    {
        var shell = ContributionSource.Shell;
        menus.Add(
        [
            new MenuItemRule(DebugTargetsMenu, "mobile.selectDevice", shell) { Group = "3-mobile", Order = 1, When = WhenClause.Parse("mobileStartup") },
            new MenuItemRule(DebugTargetsMenu, "mobile.installAndroidSdk", shell) { Group = "3-mobile", Order = 2, When = WhenClause.Parse("mobileProjects") },
            new MenuItemRule(OtherWindowsMenu, "mobile.showDevices", shell) { Group = "windows", Order = AlphabeticalOrder("Devices") },
        ]);
    }
}
