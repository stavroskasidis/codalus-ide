using Codalus.Shell.Infrastructure;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Commands;
using Codalus.Workbench.FileSystem;

namespace Codalus.Shell;

/// <summary>
/// Browsing plugins' file systems (plugin API 1.0, M22a): a quick pick over a scheme's folders; a file opens in the
/// editor, which reads and saves it through the plugin.
/// </summary>
public sealed partial class ShellHost
{
    public const string BrowsePluginFilesCommand = "workbench.browsePluginFiles";

    private void InitializePluginFiles() =>
        Workbench.Commands.Register(
            new CommandDescriptor(BrowsePluginFilesCommand, "Browse a Plugin's Files…", ContributionSource.Shell) { Category = "File" },
            async (_, _) =>
            {
                await Ui.RunAsync(BrowsePluginFilesAsync).ConfigureAwait(false);
                return null;
            });

    public async Task BrowsePluginFilesAsync()
    {
        var systems = Plugins.FileSystems;
        if (systems.Count == 0)
        {
            StatusBar.ShowMessage("No plugin serves files.", TimeSpan.FromSeconds(4));
            return;
        }

        var folder = systems.Count == 1
            ? systems[0].Root
            : (await Palette.ShowAsync([.. systems.Select(s => new QuickPickItem(s.Scheme) { Detail = s.PluginId, Tag = s.Root })], "Browse a plugin's files").ConfigureAwait(true))?.Tag as string;
        while (folder is not null)
        {
            IReadOnlyList<FileSystemEntry> entries;
            try
            {
                entries = await FileSystems.For(folder).ReadDirectoryAsync(folder, CancellationToken.None).ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Workbench.Notifications.Error($"{folder} couldn't be listed: {ex.Message}");
                return;
            }

            var root = systems.First(s => FileSystems.IsUnder(folder, s.Root)).Root;
            var items = entries.OrderByDescending(e => e.IsDirectory).ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(e => new QuickPickItem((e.IsDirectory ? "📁 " : string.Empty) + e.Name) { Tag = Path.Combine(folder, e.Name), Detail = e.IsDirectory ? null : $"{e.Size} bytes" })
                .ToList();
            if (!string.Equals(folder, root, StringComparison.OrdinalIgnoreCase))
            {
                items.Insert(0, new QuickPickItem("..") { Tag = Path.GetDirectoryName(folder) });
            }

            if (await Palette.ShowAsync(items, folder).ConfigureAwait(true) is not { Tag: string chosen })
            {
                return;
            }

            if (entries.FirstOrDefault(e => string.Equals(Path.Combine(folder, e.Name), chosen, StringComparison.Ordinal)) is { IsDirectory: false })
            {
                await OpenFileAsync(chosen).ConfigureAwait(true);
                return;
            }

            folder = chosen;
        }
    }
}
