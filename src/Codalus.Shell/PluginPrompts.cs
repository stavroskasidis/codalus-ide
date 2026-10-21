using Codalus.Plugins.Protocol;
using Codalus.Shell.Infrastructure;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Plugins;

namespace Codalus.Shell;

/// <summary>Plugin quick picks and input boxes, shown with the command palette and the dialog host.</summary>
internal sealed class PluginPrompts(ShellHost shell) : IPluginPrompts
{
    public Task<int?> PickAsync(IReadOnlyList<QuickPickItemDto> items, string? placeholder, string source, CancellationToken cancellationToken) =>
        Ui.RunAsync(async () =>
        {
            var entries = items.Select((item, index) => new QuickPickItem(item.Label)
            {
                Detail = item.Description,
                KeyLabel = item.Detail,
                Tag = index,
            }).ToList();
            using var registration = cancellationToken.Register(() => Ui.Post(shell.Palette.Cancel));
            var picked = await shell.Palette.ShowAsync(entries, placeholder ?? $"{source}: pick an item").ConfigureAwait(true);
            return picked?.Tag as int?;
        }).Unwrap();

    public Task<string?> InputAsync(string title, string? prompt, string? value, string? browse, string source, CancellationToken cancellationToken) =>
        Ui.RunAsync(async () =>
        {
            using var registration = cancellationToken.Register(() => Ui.Post(shell.Dialogs.Cancel));
            Controls.PathKind? kind = browse switch
            {
                "folder" => Controls.PathKind.Folder,
                "file" => Controls.PathKind.File,
                _ => null,
            };
            return await shell.Dialogs.InputAsync(title, prompt ?? source, value ?? string.Empty, browse: kind).ConfigureAwait(true);
        }).Unwrap();
}
