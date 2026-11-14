using Codalus.Shell.Infrastructure;
using Codalus.Workbench.Localization;

namespace Codalus.Shell.ViewModels;

/// <summary>
/// The main window's title, which the taskbar, Alt+Tab and the window list show: the open solution's name first, so
/// windows with different solutions tell apart, then the active document's ("Shop - Program.cs - Codalus IDE"); the
/// product name alone when neither is open.
/// </summary>
public sealed class WindowTitleViewModel : ObservableObject
{
    private string? _solution;
    private string? _document;

    public string Text => (_solution, _document) switch
    {
        ({ Length: > 0 } solution, { Length: > 0 } document) => Localizer.Format("main.windowTitleWithSolutionAndDocument", "{0} - {1} - Codalus IDE", solution, document),
        ({ Length: > 0 } solution, _) => Localizer.Format("main.windowTitleWithName", "{0} - Codalus IDE", solution),
        (_, { Length: > 0 } document) => Localizer.Format("main.windowTitleWithName", "{0} - Codalus IDE", document),
        _ => Localizer.Get("main.codalusStudio", "Codalus IDE"),
    };

    /// <summary>The open solution's name, or null when none is open.</summary>
    public void SetSolution(string? name) => SetField(ref _solution, name, nameof(Text));

    /// <summary>The active document's name ("Program.cs"), or null when no document is open.</summary>
    public void SetDocument(string? name) => SetField(ref _document, name, nameof(Text));
}
