using Avalonia.Media;
using Codalus.Shell.Docking;
using Codalus.Shell.Infrastructure;
using Codalus.Workbench.Projects;

namespace Codalus.Shell;

/// <summary>
/// Document tabs colored by project (M34d, <c>workbench.editor.colorTabsByProject</c>): each project of the solution
/// takes one of the eight <c>Shell.TabColor.N</c> tokens in name order, and its files' tabs show it as a stripe.
/// Pinned tabs on a row of their own (<c>workbench.editor.pinnedTabsOnSeparateRow</c>, on by default as in the classic
/// IDE): the setting goes to the application's resource <c>Shell.PinnedTabsOnSeparateRow</c>, which every group's
/// <see cref="Controls.DocumentTabPanel"/> takes, the floating windows' too.
/// </summary>
public sealed partial class ShellHost
{
    public const string ColorTabsByProjectSetting = "workbench.editor.colorTabsByProject";

    public const string PinnedTabsOnSeparateRowSetting = "workbench.editor.pinnedTabsOnSeparateRow";

    private const int TabColors = 8;

    private static readonly StringComparison s_pathComparison =
        SolutionService.PathComparer == StringComparer.OrdinalIgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private void InitializeTabs()
    {
        Layout.ActiveDocumentChanged += (_, _) => UpdateTabColors();
        Solutions.SolutionChanged += (_, _) => Ui.Post(UpdateTabColors);
        var tabColors = new UiDebouncer(UpdateTabColors, TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(1));
        Solutions.ProjectChanged += (_, _) => tabColors.Request();
        Theme.Changed += (_, _) => UpdateTabColors();
        Workbench.Settings.Changed += (_, e) =>
        {
            if (e.Affects(ColorTabsByProjectSetting))
            {
                Ui.Post(UpdateTabColors);
            }

            if (e.Affects(PinnedTabsOnSeparateRowSetting))
            {
                Ui.Post(UpdatePinnedTabsRow);
            }
        };
        UpdatePinnedTabsRow();
    }

    private void UpdatePinnedTabsRow()
    {
        if (Avalonia.Application.Current is { } app)
        {
            app.Resources["Shell.PinnedTabsOnSeparateRow"] = Workbench.Settings.GetBoolean(PinnedTabsOnSeparateRowSetting, true);
        }
    }

    /// <summary>Sets every open document's <see cref="ShellDocument.TabAccent"/> from its project, or clears them.</summary>
    public void UpdateTabColors()
    {
        var enabled = Workbench.Settings.GetBoolean(ColorTabsByProjectSetting, false);
        var projects = enabled
            ? Solutions.Projects.Values.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).Select((p, i) => (Folder: Path.GetDirectoryName(p.Path) + Path.DirectorySeparatorChar, Index: i)).ToList()
            : [];
        foreach (var document in Layout.Documents)
        {
            IBrush? brush = null;
            if (enabled && document.FilePath is { } path)
            {
                // The innermost project folder that holds the file.
                var owner = projects.Where(p => path.StartsWith(p.Folder, s_pathComparison)).OrderByDescending(p => p.Folder.Length).FirstOrDefault();
                if (owner.Folder is not null
                    && Avalonia.Application.Current?.TryGetResource($"Shell.TabColor.{owner.Index % TabColors + 1}", Avalonia.Application.Current.ActualThemeVariant, out var value) == true)
                {
                    brush = value as IBrush;
                }
            }

            document.TabAccent = brush;
        }
    }
}
