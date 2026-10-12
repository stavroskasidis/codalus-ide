using Avalonia.Media;
using DaedalusStudio.Shell.Docking;
using DaedalusStudio.Shell.Infrastructure;
using DaedalusStudio.Workbench.Projects;

namespace DaedalusStudio.Shell;

/// <summary>
/// Document tabs colored by project (M34d, <c>workbench.editor.colorTabsByProject</c>): each project of the solution
/// takes one of the eight <c>Shell.TabColor.N</c> tokens in name order, and its files' tabs show it as a stripe.
/// </summary>
public sealed partial class ShellHost
{
    public const string ColorTabsByProjectSetting = "workbench.editor.colorTabsByProject";

    private const int TabColors = 8;

    private static readonly StringComparison s_pathComparison =
        SolutionService.PathComparer == StringComparer.OrdinalIgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private void InitializeTabColors()
    {
        Layout.ActiveDocumentChanged += (_, _) => UpdateTabColors();
        Solutions.SolutionChanged += (_, _) => Ui.Post(UpdateTabColors);
        Solutions.ProjectChanged += (_, _) => Ui.Post(UpdateTabColors);
        Theme.Changed += (_, _) => UpdateTabColors();
        Workbench.Settings.Changed += (_, e) =>
        {
            if (e.Affects(ColorTabsByProjectSetting))
            {
                Ui.Post(UpdateTabColors);
            }
        };
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
