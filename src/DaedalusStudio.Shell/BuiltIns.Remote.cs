using System.Text.Json;
using DaedalusStudio.Shell.ViewModels;
using DaedalusStudio.Workbench.Commands;
using DaedalusStudio.Workbench.Context;
using DaedalusStudio.Workbench.Menus;
using DaedalusStudio.Workbench.Settings;
using DaedalusStudio.Workbench.ToolWindows;

namespace DaedalusStudio.Shell;

/// <summary>Remote development (M21, ADR 0050): Open Remote, the remote status menu, port forwarding and its settings.</summary>
internal static partial class BuiltIns
{
    public static IReadOnlyList<SettingDescriptor> RemoteSettings() =>
    [
        SettingDescriptor.Create(ShellHost.AutoForwardPortsSetting, "true", SettingType.Boolean, ContributionSource.Shell) with
        {
            Title = "Forward Ports Automatically",
            Category = "Remote",
            Description = "In a remote window, forward ports that start listening on the remote machine (a web app's, a debug session's) to the same port here, or a free one.",
        },
    ];

    public static IEnumerable<ToolWindowDescriptor> RemoteToolWindows()
    {
        yield return new ToolWindowDescriptor(RemotePortsViewModel.ToolWindowId, "Ports", ToolWindowLocation.Bottom, ToolWindowKind.BuiltIn, ContributionSource.Shell) { Icon = "link" };
    }

    public static void RegisterRemoteCommands(ShellHost shell)
    {
        var commands = shell.Workbench.Commands;
        void Add(string id, string title, Func<string?, Task> handler, string? enablement = null) =>
            commands.Register(
                new CommandDescriptor(id, title, ContributionSource.Shell) { Category = "Remote", Enablement = WhenClause.Parse(enablement) },
                async (args, _) =>
                {
                    var argument = args.Count > 0 && args[0].ValueKind == JsonValueKind.String ? args[0].GetString() : null;
                    await Infrastructure.Ui.RunAsync(() => handler(argument)).ConfigureAwait(false);
                    return null;
                });

        Add("remote.open", "Open Remote Window…", shell.OpenRemoteWindowAsync);
        Add("remote.menu", "Remote Actions…", _ => shell.ShowRemoteMenuAsync());
        Add("remote.close", "Close Remote Connection", _ => shell.DisconnectRemoteAsync(), "remoteConnected");
        Add("remote.ports", "Forwarded Ports", _ =>
        {
            shell.ShowPorts();
            return Task.CompletedTask;
        });
        Add("remote.forwardPort", "Forward a Port…", shell.ForwardPortAsync, "remoteConnected");
        Add("remote.showLog", "Show the Remote Log", _ =>
        {
            shell.ShowRemoteLog();
            return Task.CompletedTask;
        });
    }

    public static void RegisterRemoteMenus(MenuRegistry menus)
    {
        var shell = ContributionSource.Shell;
        MenuItemRule Item(string location, string command, string group, int order = 0, string? when = null) =>
            new(location, command, shell) { Group = group, Order = order, When = WhenClause.Parse(when) };

        menus.Add(
        [
            // In the menu tree (M34a): File ▸ Open ▸ Remote Window…, and Close Remote Connection with the other closes.
            Item(FileOpenMenu, "remote.open", "2-remote", 5) with { Title = "Remote Window…" },
            Item(MenuLocations.MenuBar("file"), "remote.close", "2-close", 6, "remoteConnected"),
            Item(OtherWindowsMenu, "remote.ports", "windows", AlphabeticalOrder("Ports")),
        ]);
    }
}
