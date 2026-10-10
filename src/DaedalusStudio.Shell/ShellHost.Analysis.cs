using DaedalusStudio.LanguageServices.Protocol;
using DaedalusStudio.Shell.Editor;
using DaedalusStudio.Shell.ViewModels;

namespace DaedalusStudio.Shell;

/// <summary>Analysis tool windows (M12b): the Syntax Visualizer and Code Metrics.</summary>
public sealed partial class ShellHost
{
    public SyntaxVisualizerViewModel SyntaxVisualizer { get; private set; } = null!;

    public CodeMetricsViewModel CodeMetrics { get; private set; } = null!;

    private void InitializeAnalysis()
    {
        SyntaxVisualizer = new SyntaxVisualizerViewModel(SyntaxTreeAsync) { IsShown = () => Layout?.IsVisible(SyntaxVisualizerViewModel.ToolWindowId) == true };
        CodeMetrics = new CodeMetricsViewModel(CodeMetricsAsync, OpenLocationWithHistoryAsync, async text =>
        {
            if (Window?.Clipboard is { } clipboard)
            {
                await Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(clipboard, text).ConfigureAwait(true);
            }
        });
    }

    private async Task<SyntaxTreeResult?> SyntaxTreeAsync(CodeEditorController editor, CancellationToken cancellationToken)
    {
        if (Languages.RunningClient(CSharpLanguageId) is not { } client)
        {
            return null;
        }

        var position = editor.Model.PositionAt(editor.Editor.CaretOffset);
        return await client.SyntaxTreeAsync(editor.Model.Uri, position, cancellationToken).ConfigureAwait(true);
    }

    private async Task<CodeMetricsResult?> CodeMetricsAsync(bool projectOnly, CancellationToken cancellationToken)
    {
        if (Languages.RunningClient(CSharpLanguageId) is not { } client)
        {
            return null;
        }

        string? path = null;
        if (projectOnly)
        {
            if (ActiveEditor is not { Model: { LanguageId: CSharpLanguageId } model })
            {
                ShowMessage("Open a C# file of the project to measure.");
                return new CodeMetricsResult([]);
            }

            path = model.Uri;
        }

        return await client.CodeMetricsAsync(path, cancellationToken).ConfigureAwait(true);
    }

    /// <summary>View > Syntax Visualizer: shows the tree for the active C# document.</summary>
    public void ShowSyntaxVisualizer()
    {
        Layout.ShowToolWindow(SyntaxVisualizerViewModel.ToolWindowId, focus: false);
        SyntaxVisualizer.SetEditor(ActiveEditor?.Controller);
        _ = SyntaxVisualizer.RefreshAsync(force: true);
    }

    /// <summary>Analyze > Calculate Code Metrics (for the solution, or the active project).</summary>
    public Task CalculateCodeMetricsAsync(bool projectOnly)
    {
        Layout.ShowToolWindow(CodeMetricsViewModel.ToolWindowId, focus: false);
        return CodeMetrics.CalculateAsync(projectOnly);
    }
}
