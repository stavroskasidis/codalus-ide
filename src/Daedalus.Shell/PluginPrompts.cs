using Daedalus.Plugins.Protocol;
using Daedalus.Shell.Infrastructure;
using Daedalus.Shell.ViewModels;
using Daedalus.Workbench.Plugins;

namespace Daedalus.Shell;

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

    public Task<string?> InputAsync(string title, string? prompt, string? value, string source, CancellationToken cancellationToken) =>
        Ui.RunAsync(async () =>
        {
            using var registration = cancellationToken.Register(() => Ui.Post(shell.Dialogs.Cancel));
            return await shell.Dialogs.InputAsync(title, prompt ?? source, value ?? string.Empty).ConfigureAwait(true);
        }).Unwrap();
}
