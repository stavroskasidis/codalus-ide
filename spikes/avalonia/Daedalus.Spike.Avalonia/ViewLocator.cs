using Avalonia.Controls;
using Avalonia.Controls.Templates;

namespace Daedalus.Spike.Avalonia;

/// <summary>Maps dockable view models to views without reflection, so it is safe under Native AOT.</summary>
public sealed class ViewLocator : IDataTemplate
{
    public Control? Build(object? param) => param switch
    {
        SolutionExplorerTool tool => new SolutionExplorerView { DataContext = tool },
        OutputTool tool => new OutputView { DataContext = tool },
        EditorDocument document => new EditorView(document),
        _ => null,
    };

    public bool Match(object? data) => data is SolutionExplorerTool or OutputTool or EditorDocument;
}
