using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using DaedalusStudio.Shell.Controls;
using DaedalusStudio.Shell.Docking;
using DaedalusStudio.Shell.Editor;
using DaedalusStudio.Shell.ViewModels;
using DaedalusStudio.Shell.Views;

namespace DaedalusStudio.Shell;

/// <summary>Maps dockables to views without reflection, so it is safe under Native AOT.</summary>
public sealed class ViewLocator : IDataTemplate
{
    public Control? Build(object? param) => param switch
    {
        ToolWindowDockable tool => new PaneHost(tool, BuildToolContent(tool.Content)),
        SettingsDocument document => new PaneHost(document, new SettingsView { DataContext = document.ViewModel }),
        KeyboardShortcutsDocument document => new PaneHost(document, new KeyboardShortcutsView { DataContext = document.ViewModel }),
        CodeEditorDocument document => EditorHost(document),
        DiffDocument document => new PaneHost(document, new DiffView(document.ViewModel, document.Font)),
        PackageManagerDocument document => new PaneHost(document, new PackageManagerView { DataContext = document.ViewModel }),
        MarkdownPreviewDocument document => new PaneHost(document, new MarkdownPreviewView(document)),
        ImageDocument document => new PaneHost(document, new ImageViewerView(document)),
        GitHistoryDocument document => new PaneHost(document, new GitHistoryView { DataContext = document.ViewModel }),
        DumpDocument document => new PaneHost(document, new DumpView { DataContext = document.ViewModel }),
        MergeEditorDocument document => new PaneHost(document, new MergeEditorView(document.ViewModel, document.Font)),
        InteractiveRebaseDocument document => new PaneHost(document, new InteractiveRebaseView { DataContext = document.ViewModel }),
        PackageSourcesDocument document => new PaneHost(document, new PackageSourcesView { DataContext = document.ViewModel }),
        DotnetToolsDocument document => new PaneHost(document, new DotnetToolsView { DataContext = document.ViewModel }),
        ProjectPropertiesDocument document => new PaneHost(document, new ProjectPropertiesView { DataContext = document.ViewModel }),
        SolutionManagementDocument document => new PaneHost(document, BuildManagementContent(document.ViewModel)),
        AccountsDocument document => new PaneHost(document, new AccountsView { DataContext = document.ViewModel }),
        CreatePullRequestDocument document => new PaneHost(document, new CreatePullRequestView { DataContext = document.ViewModel }),
        EfModelDocument document => new PaneHost(document, new EfModelView(document.ViewModel)),
        EfScaffoldDocument document => new PaneHost(document, new EfScaffoldView { DataContext = document.ViewModel }),
        _ => null,
    };

    /// <summary>Editor views are kept by their document: rebuilding them on every tab switch would lose state.</summary>
    private static Control EditorHost(CodeEditorDocument document)
    {
        if (document.Host is { Parent: null } host)
        {
            return host;
        }

        document.View?.Close();
        document.View = new CodeEditorView(document, document.Services, document.Font);
        document.Host = new PaneHost(document, document.View);
        return document.Host;
    }

    public bool Match(object? data) => data is ToolWindowDockable or ShellDocument;

    private static Control BuildManagementContent(object viewModel) => viewModel switch
    {
        ReferenceManagerViewModel vm => new ReferenceManagerView { DataContext = vm },
        ConfigurationManagerViewModel vm => new ConfigurationManagerView { DataContext = vm },
        ProjectDependenciesViewModel vm => new ProjectDependenciesView { DataContext = vm },
        SdkViewModel vm => new SdkView { DataContext = vm },
        _ => new TextBlock { Text = "This view is not available.", Margin = new Thickness(12) },
    };

    private static Control BuildToolContent(object? content) => content switch
    {
        OutputViewModel vm => new OutputView { DataContext = vm },
        NotificationsViewModel vm => new NotificationsView { DataContext = vm },
        PluginsViewModel vm => new PluginsView { DataContext = vm },
        TreeToolViewModel vm => new TreeToolView { DataContext = vm },
        MarkdownToolViewModel vm => new MarkdownToolView { DataContext = vm },
        SolutionExplorerViewModel vm => new SolutionExplorerView { DataContext = vm },
        TestExplorerViewModel vm => new TestExplorerView { DataContext = vm },
        CoverageResultsViewModel vm => new CoverageResultsView { DataContext = vm },
        ErrorListViewModel vm => new ErrorListView { DataContext = vm },
        FindResultsViewModel vm => new FindResultsView { DataContext = vm },
        DocumentOutlineViewModel vm => new DocumentOutlineView { DataContext = vm },
        HierarchyViewModel vm => new HierarchyView { DataContext = vm },
        BookmarksViewModel vm => new BookmarksView { DataContext = vm },
        TaskListViewModel vm => new TaskListView { DataContext = vm },
        SyntaxVisualizerViewModel vm => new SyntaxVisualizerView { DataContext = vm },
        CodeMetricsViewModel vm => new CodeMetricsView { DataContext = vm },
        CodeDefinitionViewModel vm => new CodeDefinitionView(vm, vm.Services, vm.Font),
        VariablesViewModel vm => new VariablesView { DataContext = vm },
        WatchViewModel vm => new WatchView { DataContext = vm },
        QuickWatchViewModel vm => new QuickWatchView { DataContext = vm },
        EndpointsExplorerViewModel vm => new EndpointsExplorerView { DataContext = vm },
        TaskRunnerViewModel vm => new TaskRunnerView { DataContext = vm },
        HttpResponseViewModel vm => new HttpResponseView { DataContext = vm },
        ThreadsViewModel vm => new ThreadsView { DataContext = vm },
        ModulesViewModel vm => new ModulesView { DataContext = vm },
        ParallelStacksViewModel vm => new ParallelStacksView { DataContext = vm },
        ParallelWatchViewModel vm => new ParallelWatchView { DataContext = vm },
        ProcessesViewModel vm => new ProcessesView { DataContext = vm },
        ExceptionSettingsViewModel vm => new ExceptionSettingsView { DataContext = vm },
        CallStackViewModel vm => new CallStackView { DataContext = vm },
        BreakpointsViewModel vm => new BreakpointsView { DataContext = vm },
        DebugConsoleViewModel vm => new DebugConsoleView { DataContext = vm },
        GitChangesViewModel vm => new GitChangesView { DataContext = vm },
        PullRequestsViewModel vm => new PullRequestsView { DataContext = vm },
        WorkItemsViewModel vm => new WorkItemsView { DataContext = vm },
        WorktreesViewModel vm => new WorktreesView { DataContext = vm },
        TerminalViewModel vm => new TerminalView { DataContext = vm },
        InteractiveViewModel vm => new InteractiveView { DataContext = vm },
        EntityFrameworkViewModel vm => new EntityFrameworkView { DataContext = vm },
        _ => new TextBlock { Text = "This tool window is not available.", Margin = new Thickness(12) },
    };
}
