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

    private static Control BuildToolContent(object? content) => content switch
    {
        OutputViewModel vm => new OutputView { DataContext = vm },
        NotificationsViewModel vm => new NotificationsView { DataContext = vm },
        PluginsViewModel vm => new PluginsView { DataContext = vm },
        TreeToolViewModel vm => new TreeToolView { DataContext = vm },
        MarkdownToolViewModel vm => new MarkdownToolView { DataContext = vm },
        SolutionExplorerViewModel vm => new SolutionExplorerView { DataContext = vm },
        TestExplorerViewModel vm => new TestExplorerView { DataContext = vm },
        ErrorListViewModel vm => new ErrorListView { DataContext = vm },
        FindResultsViewModel vm => new FindResultsView { DataContext = vm },
        VariablesViewModel vm => new VariablesView { DataContext = vm },
        WatchViewModel vm => new WatchView { DataContext = vm },
        CallStackViewModel vm => new CallStackView { DataContext = vm },
        BreakpointsViewModel vm => new BreakpointsView { DataContext = vm },
        DebugConsoleViewModel vm => new DebugConsoleView { DataContext = vm },
        GitChangesViewModel vm => new GitChangesView { DataContext = vm },
        _ => new TextBlock { Text = "This tool window is not available.", Margin = new Thickness(12) },
    };
}
