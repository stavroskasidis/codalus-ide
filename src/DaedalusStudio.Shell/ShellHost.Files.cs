using DaedalusStudio.Shell.Views;
using Dock.Model.Core;

namespace DaedalusStudio.Shell;

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
