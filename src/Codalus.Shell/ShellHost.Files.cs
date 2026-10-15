using Codalus.Shell.Views;
using Dock.Model.Core;

namespace Codalus.Shell;

/// <summary>Files and tabs (M12b): Compare Files, Close All to the Right, and the image viewer.</summary>
public sealed partial class ShellHost
{
    /// <summary>
    /// File > Compare Files…: asks for two files (or, <paramref name="withActive"/>, one to compare the active document
    /// with) and opens them in the diff viewer.
    /// </summary>
    public async Task CompareFilesAsync(bool withActive)
    {
        if (Window is null)
        {
            return;
        }

        var left = withActive ? ActiveEditor?.Model.Path : await Window.PickFileAsync("Compare Files: First File", "All files", ["*"]).ConfigureAwait(true);
        if (left is null)
        {
            return;
        }

        if (await Window.PickFileAsync($"Compare {Path.GetFileName(left)} With", "All files", ["*"]).ConfigureAwait(true) is not { } right)
        {
            return;
        }

        await OpenFileDiffAsync(left, right).ConfigureAwait(true);
    }

    /// <summary>Window > Close All to the Right: the tabs after the active one in its group.</summary>
    public void CloseDocumentsToTheRight()
    {
        if (Layout.ActiveDocument is not { Owner: IDock { VisibleDockables: { } tabs } } active)
        {
            return;
        }

        var index = tabs.IndexOf(active);
        foreach (var document in tabs.Skip(index + 1).OfType<Docking.ShellDocument>().ToList())
        {
            Layout.CloseDocument(document);
        }
    }

    /// <summary>Opens a file in the hex viewer (read-only).</summary>
    public HexDocument OpenHex(string path, bool preview = false)
    {
        path = Path.GetFullPath(path);
        if (Layout.FindDocument<HexDocument>(HexDocument.IdFor(path)) is not { } document)
        {
            document = new HexDocument(path, EditorFont);
        }

        Layout.OpenDocument(document, preview, focus: !preview);
        return document;
    }

    /// <summary>
    /// Open With Hex Viewer: the file selected in Solution Explorer when it has the focus, else the active document's.
    /// </summary>
    public void OpenWithHexViewer()
    {
        var selected = Explorer.Selected?.Node is { Kind: Codalus.Workbench.Projects.ExplorerNodeKind.File, Path: { } file } ? file : null;
        var path = Workbench.Context.GetValue("focusedView") is "solutionExplorer" ? selected ?? Layout.ActiveDocument?.FilePath : Layout.ActiveDocument?.FilePath ?? selected;
        if (path is not null && File.Exists(path))
        {
            OpenHex(path);
        }
    }

    /// <summary>Opens a <c>.resx</c> file in the resource editor (deferred from M11).</summary>
    public ResxEditorDocument OpenResx(string path, bool preview = false)
    {
        path = Path.GetFullPath(path);
        if (Layout.FindDocument<ResxEditorDocument>(ResxEditorDocument.IdFor(path)) is { } open)
        {
            Layout.OpenDocument(open, preview, focus: !preview);
            return open;
        }

        var document = new ResxEditorDocument(path, file => _ = OpenFileAsync(file, preview: false, at: null, initialContent: null, textEditor: true), RunCustomToolOnSave);
        document.CloseRequested += (_, _) => _ = ConfirmCloseAsync(document);
        Layout.OpenDocument(document, preview, focus: !preview);
        _ = document.LoadAsync();
        return document;
    }

    /// <summary>Open With Resource Editor: the selected or active <c>.resx</c> file.</summary>
    public void OpenWithResourceEditor()
    {
        var selected = Explorer.Selected?.Node is { Kind: Codalus.Workbench.Projects.ExplorerNodeKind.File, Path: { } file } ? file : null;
        var path = Workbench.Context.GetValue("focusedView") is "solutionExplorer" ? selected ?? Layout.ActiveDocument?.FilePath : Layout.ActiveDocument?.FilePath ?? selected;
        if (path is not null && ResxEditorDocument.IsResx(path) && File.Exists(path))
        {
            OpenResx(path);
        }
    }

    /// <summary>Opens an image file in the image viewer.</summary>
    public ImageDocument OpenImage(string path, bool preview = false)
    {
        path = Path.GetFullPath(path);
        if (Layout.FindDocument<ImageDocument>(ImageDocument.IdFor(path)) is not { } document)
        {
            document = new ImageDocument(path);
        }

        Layout.OpenDocument(document, preview, focus: !preview);
        return document;
    }
}
