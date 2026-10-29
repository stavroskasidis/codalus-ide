using System.Globalization;
using Codalus.LanguageServices.Protocol;
using Codalus.Shell.Infrastructure;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Settings;
using Codalus.Workbench.ToolWindows;

namespace Codalus.Shell;

/// <summary>
/// The References windows (Find All References, 0.6): the registered one, and the ones Keep Results opened beside it.
/// A search goes to the first window that doesn't keep its results; when all of them do, to a new one.
/// </summary>
public sealed partial class ShellHost
{
    private readonly List<ReferencesViewModel> _extraReferences = [];

    /// <summary>The References window (the registered one).</summary>
    public ReferencesViewModel References { get; private set; } = null!;

    /// <summary>Every References window: the registered one, then those Keep Results opened.</summary>
    public IReadOnlyList<ReferencesViewModel> ReferenceWindows => [References, .. _extraReferences];

    private void InitializeReferences()
    {
        References = CreateReferences(ReferencesViewModel.ToolWindowId);
        Layout.ExtraToolWindowClosed += (_, tool) => _extraReferences.RemoveAll(r => r.Id == tool.Id);
        Workbench.Settings.Changed += (_, e) =>
        {
            if (e.Keys.Any(k => k is ReferencesViewModel.GroupBySetting or ReferencesViewModel.ColumnWidthsSetting))
            {
                Ui.Post(() =>
                {
                    foreach (var window in ReferenceWindows)
                    {
                        LoadReferenceSettings(window);
                    }
                });
            }
        };
    }

    private ReferencesViewModel CreateReferences(string id)
    {
        var references = new ReferencesViewModel(id, OpenFindResultAsync)
        {
            ActiveDocument = () => Layout is null ? null : ActiveEditor?.Model.Path,
            ActiveProject = () => (Layout is null ? null : ActiveEditor?.Model.Path) is { } path ? ErrorListViewModel.ProjectOf(Solutions.Projects.Values, path)?.Name : null,
            OpenDocuments = () => [.. Editors.Select(e => e.Model.Path).OfType<string>()],
            RootFolder = () => Solutions.Solution?.Directory,
        };
        LoadReferenceSettings(references);
        references.GroupByChanged = grouping => _ = SaveReferenceSettingAsync(ReferencesViewModel.GroupBySetting, ReferencesGrouping.ToId(grouping));
        references.WidthsChanged = widths => _ = SaveReferenceSettingAsync(ReferencesViewModel.ColumnWidthsSetting, ReferencesViewModel.FormatWidths(widths));
        references.TitleChanged += (_, _) =>
        {
            if (Layout.ToolWindow(references.Id) is { } tool)
            {
                tool.Title = references.Title;
            }
        };
        return references;
    }

    private void LoadReferenceSettings(ReferencesViewModel references)
    {
        references.LoadGroupBy(ReferencesGrouping.FromId(Workbench.Settings.GetString(ReferencesViewModel.GroupBySetting, ReferencesGrouping.Ids[0])));
        references.LoadWidths(Workbench.Settings.GetString(ReferencesViewModel.ColumnWidthsSetting, string.Empty));
    }

    private async Task SaveReferenceSettingAsync(string key, string value)
    {
        try
        {
            await Workbench.Settings.SetAsync(key, value, SettingScope.User, CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Workbench.Log.AppendLine($"[references] {key} wasn't saved: {ex.Message}");
        }
    }

    /// <summary>The window a search's references go to, shown: one that doesn't keep its results, or a new one.</summary>
    private ReferencesViewModel ShowReferencesWindow()
    {
        if (!References.KeepResults)
        {
            Layout.ShowToolWindow(ReferencesViewModel.ToolWindowId, focus: false);
            return References;
        }

        var window = _extraReferences.FirstOrDefault(r => !r.KeepResults);
        if (window is null)
        {
            var number = 2;
            while (_extraReferences.Any(r => r.Id == ExtraReferencesId(number)))
            {
                number++;
            }

            window = CreateReferences(ExtraReferencesId(number));
            _extraReferences.Add(window);
        }

        var descriptor = Workbench.ToolWindows.Find(ReferencesViewModel.ToolWindowId)!;
        Layout.ShowExtraToolWindow(descriptor with { Id = window.Id }, window, ReferencesViewModel.ToolWindowId);
        return window;

        static string ExtraReferencesId(int number) => ReferencesViewModel.ToolWindowId + ":" + number.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Shows classified references in a References window.</summary>
    public ReferencesViewModel ShowReferences(string symbol, IReadOnlyList<ReferenceItem> items)
    {
        var window = ShowReferencesWindow();
        window.Show(symbol, items);
        return window;
    }

    /// <summary>Shows another language's plain locations in a References window, their lines read from the files.</summary>
    public async Task<ReferencesViewModel> ShowReferenceLocationsAsync(string symbol, IReadOnlyList<Location> locations)
    {
        var lines = new Dictionary<string, string[]>(Codalus.Workbench.Projects.SolutionService.PathComparer);
        foreach (var path in locations.Select(l => DocumentUris.ToPath(l.Uri)).OfType<string>().Distinct(Codalus.Workbench.Projects.SolutionService.PathComparer))
        {
            lines[path] = (OpenTextOf(path) ?? await ReadAllTextOrEmptyAsync(path).ConfigureAwait(true)).Split('\n');
        }

        var window = ShowReferencesWindow();
        window.ShowLocations(
            symbol,
            locations,
            (path, line) => lines.TryGetValue(path, out var text) && line < text.Length ? text[line].TrimEnd('\r') : string.Empty,
            path => ErrorListViewModel.ProjectOf(Solutions.Projects.Values, path)?.Name);
        return window;
    }

    private static async Task<string> ReadAllTextOrEmptyAsync(string path)
    {
        try
        {
            return await File.ReadAllTextAsync(path).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }
}
