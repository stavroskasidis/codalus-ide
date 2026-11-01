using Codalus.LanguageServices.Protocol;
using Codalus.Shell.Docking;
using Codalus.Shell.Editor;
using Codalus.Workbench.Build;
using Codalus.Workbench.Projects;
using Codalus.Workbench.Testing;

namespace Codalus.Shell;

/// <summary>
/// Per-solution, per-user state next to the solution (ADR 0070, roadmap F7): <c>.codalus/state/&lt;solution file name&gt;/</c>
/// (<c>folder/</c> in folder mode) holds <c>session.json</c> (open documents, breakpoints, Watch, startup projects, the
/// configuration and platform, Solution Explorer's expansion, the Error List's filters…), <c>bookmarks.json</c>, and the
/// build host's <c>solution.json</c> (unloaded projects). Remote windows keep none of it.
/// </summary>
public sealed partial class ShellHost
{
    private SolutionStateStore? _solutionState;

    /// <summary>The solution whose state is saved (set once its session is restored; null while none is open).</summary>
    private SolutionStateKey? _stateKey;

    public SolutionStateStore SolutionState => _solutionState ??= new SolutionStateStore(Paths.State, line => Workbench.Log.AppendLine(line));

    /// <summary>The state folder of the open solution (null with none, or in a remote window).</summary>
    public string? SolutionStateDirectory => _stateKey?.Directory;

    /// <summary>Reads the session kept with a solution before it opens (it holds the configuration to open with); moves a session of the old layout in.</summary>
    private Task<EditorSession?> ReadSolutionSessionAsync(SolutionStateKey key) =>
        SolutionState.ReadAsync(key, SolutionStateStore.SessionFile, ShellJsonContext.Default.EditorSession, SolutionState.LegacySessionFile(key.Path));

    /// <summary>What applies before the tree loads: Solution Explorer's expansion, and the Error List's and Test Explorer's views.</summary>
    private void ApplySolutionViewState(EditorSession? session)
    {
        Explorer.LoadExpansion(session?.ExpandedNodes, session?.CollapsedNodes);
        LoadRepositories(session?.GitRepositories);
        if (session?.ErrorList is { } errorList)
        {
            ErrorList.ShowErrors = errorList.ShowErrors;
            ErrorList.ShowWarnings = errorList.ShowWarnings;
            ErrorList.ShowMessages = errorList.ShowMessages;
            ErrorList.SourceFilter = errorList.Source;
            ErrorList.CurrentDocumentOnly = errorList.CurrentDocumentOnly;
        }

        if (session?.TestGrouping is { } grouping && Enum.TryParse<TestGrouping>(grouping, out var testGrouping))
        {
            TestExplorer.Grouping = testGrouping;
        }
    }

    /// <summary>After the solution opened: its bookmarks, then the session (documents last); from then on its state is saved.</summary>
    private async Task RestoreSolutionStateAsync(SolutionStateKey key, EditorSession? session)
    {
        await Bookmarks.OpenSolutionAsync(SolutionState, key).ConfigureAwait(true);
        await RestoreEditorSessionAsync(session).ConfigureAwait(true);
        _stateKey = key;
    }

    /// <summary>The solution closes: its state is saved one last time, and bookmarks go back to the user's.</summary>
    private void CloseSolutionState()
    {
        SaveEditorSession();
        _stateKey = null;
        LoadRepositories(null);
        Bookmarks.CloseSolution();
    }

    /// <summary>Waits (a little) for the state writes, before the app exits.</summary>
    private async Task FlushSolutionStateAsync()
    {
        if (_solutionState is not { } store)
        {
            return;
        }

        try
        {
            await store.FlushAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Workbench.Log.AppendLine("[state] The solution's state is still being written.");
        }
    }

    /// <summary>Remembers the open solution's session (documents on disk only), written in the background.</summary>
    public void SaveEditorSession()
    {
        if (_stateKey is not { } key)
        {
            return;
        }

        var documents = Editors
            .Where(e => !e.Model.IsReadOnly && File.Exists(e.Model.Path))
            .DistinctBy(e => e.Model)
            .Select(e =>
            {
                var caret = e.Controller is { } controller ? e.Model.PositionAt(controller.Editor.CaretOffset) : new Position(0, 0);
                return new SessionDocument(e.Model.Path, caret.Line, caret.Character) { Pinned = e.IsPinned ? true : null };
            })
            .ToList();
        var (expanded, collapsed) = Explorer.CaptureExpansion();
        var errorList = new SessionErrorList(ErrorList.ShowErrors, ErrorList.ShowWarnings, ErrorList.ShowMessages, ErrorList.SourceFilter, ErrorList.CurrentDocumentOnly);
        _ = SolutionState.WriteAsync(key, SolutionStateStore.SessionFile, new EditorSession(documents, ActiveEditor?.Model.Path)
        {
            StartupProject = Runs.ChosenStartupProject,
            Breakpoints = Debugger.Breakpoints.All.Count == 0 ? null : [.. Debugger.Breakpoints.All.Select(ToSession)],
            FunctionBreakpoints = Debugger.Breakpoints.Functions.Count == 0 ? null : [.. Debugger.Breakpoints.Functions.Select(ToSession)],
            BreakOnOtherExceptions = Debugger.ExceptionSettings.BreakOnOthers ? true : null,
            AspNetEnvironment = Runs.EnvironmentOverride,
            ExceptionTypes = Debugger.ExceptionSettings.Changes() is { Count: > 0 } exceptionTypes ? exceptionTypes : null,
            ExceptionConditions = Debugger.ExceptionSettings.Conditions() is { Count: > 0 } conditions ? conditions : null,
            Watches = Watch.Expressions.Count == 0 ? null : Watch.Expressions,
            DataTips = Debugger.PinnedTips.All is { Count: > 0 } tips ? [.. tips.Select(t => new SessionDataTip(t.Path, t.Line, t.Expression, t.Comment))] : null,
            LaunchProfiles = Runs.ChosenProfiles.Count == 0 ? null : new Dictionary<string, string>(Runs.ChosenProfiles),
            MobileDevices = Mobile.Selections.Count == 0 ? null : new Dictionary<string, string>(Mobile.Selections),
            MultipleStartupProjects = Runs.MultipleStartup ? true : null,
            StartupProjects = Runs.StartupEntries is { Count: > 0 } entries ? [.. entries.Select(e => new SessionStartupProject(e.Project, e.Action.ToString()))] : null,
            Configuration = Solutions.Solution?.Active?.Configuration,
            Platform = Solutions.Solution?.Active?.Platform,
            ActiveFrameworks = Solutions.ActiveFrameworks is { Count: > 0 } frameworks ? new Dictionary<string, string>(frameworks) : null,
            ExpandedNodes = expanded.Count == 0 ? null : expanded,
            CollapsedNodes = collapsed.Count == 0 ? null : collapsed,
            ErrorList = errorList == new SessionErrorList() ? null : errorList,
            TestGrouping = TestExplorer.Grouping == TestGrouping.Hierarchy ? null : TestExplorer.Grouping.ToString(),
            GitRepositories = CaptureRepositories(),
        }, ShellJsonContext.Default.EditorSession);
    }

    /// <summary>Restores the debugger's and the runs' state, and reopens the documents that were open the last time the solution closed.</summary>
    private async Task RestoreEditorSessionAsync(EditorSession? session)
    {
        if (session is null)
        {
            Runs.Load(null, null);
            Mobile.LoadSelections(null);
            Debugger.Breakpoints.Clear();
            Debugger.ExceptionSettings.Reset();
            Runs.EnvironmentOverride = null;
            Watch.Load(null);
            Debugger.PinnedTips.Load([]);
            return;
        }

        Runs.Load(
            session.StartupProject,
            session.LaunchProfiles,
            session.MultipleStartupProjects == true,
            [.. (session.StartupProjects ?? []).Select(p => new StartupEntry(p.Path, Enum.TryParse<StartupAction>(p.Action, out var action) ? action : StartupAction.None))]);
        Mobile.LoadSelections(session.MobileDevices);
        Debugger.Breakpoints.Load((session.Breakpoints ?? []).Select(FromSession), (session.FunctionBreakpoints ?? []).Select(FromSession));
        Debugger.ExceptionSettings.Load(session.BreakOnOtherExceptions ?? false, session.ExceptionTypes, session.ExceptionConditions);
        Runs.EnvironmentOverride = session.AspNetEnvironment;
        Watch.Load(session.Watches);
        Debugger.PinnedTips.Load((session.DataTips ?? []).Select(t => (t.Path, t.Line, t.Expression, t.Comment)));

        // The tabs come back behind the active one: each other's view (its editor, and what it asks the language service
        // for) is made when it's first shown. Activating every document in turn made them all at startup. They open
        // together, their files read first: opened one by one, a layout pass between two of them (a file read on a slow
        // runner) made the first tab of the empty group active, and built its editor (macOS CI, run 41237010138).
        var documents = session.Documents.Where(d => File.Exists(d.Path)).ToList();
        var active = session.Active is { } activePath ? documents.FindIndex(d => SolutionPaths.Comparer.Equals(Path.GetFullPath(d.Path), Path.GetFullPath(activePath))) : -1;
        await OpenFilesAsync([.. documents.Select(d => (d.Path, (Position?)new Position(d.Line, d.Character)))], active: active >= 0 ? active : null, focus: false).ConfigureAwait(true);
        foreach (var document in documents.Where(d => d.Pinned == true))
        {
            if (Editors.FirstOrDefault(e => SolutionPaths.Comparer.Equals(e.Model.Path, Path.GetFullPath(document.Path)) && e.Owner is not null) is { } pinned)
            {
                Layout.SetPinned(pinned, true);
            }
        }
    }
}
