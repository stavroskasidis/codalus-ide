using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Daedalus.Shell.Controls;
using Daedalus.Shell.Docking;
using Daedalus.Shell.Editor;
using Daedalus.Shell.ViewModels;
using Daedalus.Shell.Views;

namespace Daedalus.Shell;

/// <summary>Maps dockables to views without reflection, so it is safe under Native AOT.</summary>
public sealed class ViewLocator : IDataTemplate
{
    public Control? Build(object? param) => param switch
    {
        ToolWindowDockable tool => new PaneHost(tool, BuildToolContent(tool.Content)),
        SettingsDocument document => new PaneHost(document, new SettingsView { DataContext = document.ViewModel }),
        KeyboardShortcutsDocument document => new PaneHost(document, new KeyboardShortcutsView { DataContext = document.ViewModel }),
        CodeEditorDocument document => EditorHost(document),
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
        ErrorListViewModel vm => new ErrorListView { DataContext = vm },
        FindResultsViewModel vm => new FindResultsView { DataContext = vm },
        _ => new TextBlock { Text = "This tool window is not available.", Margin = new Thickness(12) },
    };
}
