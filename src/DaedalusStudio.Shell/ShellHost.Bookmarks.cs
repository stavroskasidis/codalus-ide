using DaedalusStudio.Shell.Editor;
using DaedalusStudio.Shell.Infrastructure;
using DaedalusStudio.Shell.ViewModels;
using DaedalusStudio.Workbench.Bookmarks;

namespace DaedalusStudio.Shell;

/// <summary>Bookmarks (M12b): toggle, next and previous across files, rename, clear, and the Bookmarks window.</summary>
public sealed partial class ShellHost
{
    public BookmarkService Bookmarks { get; private set; } = null!;

    public BookmarksViewModel BookmarkList { get; private set; } = null!;

    BookmarkService IEditorServices.Bookmarks => Bookmarks;

    public const string TaskListTokensSetting = "taskList.tokens";

    public TaskListViewModel TaskList { get; private set; } = null!;

    private void InitializeBookmarks()
    {
        TaskList = new TaskListViewModel(SearchableFiles, OpenTextOf, () =>
        {
            var tokens = Workbench.Settings.GetStrings(TaskListTokensSetting);
            return tokens.Count > 0 ? tokens : ["TODO", "HACK", "UNDONE"];
        }, (path, position) => OpenFileAsync(path, preview: false, position));
        Bookmarks = new BookmarkService(Path.Combine(Workbench.Paths.State, "bookmarks.json"));
        BookmarkList = new BookmarksViewModel(Bookmarks, b => _ = GoToBookmarkAsync(b), RenameBookmarkAsync);
    }

    /// <summary>Ctrl+K Ctrl+K: a bookmark on the caret's line, or none.</summary>
    public void ToggleBookmark()
    {
        if (ActiveEditor is { Controller: { } controller } editor && !editor.Model.IsReadOnly)
        {
            var added = Bookmarks.Toggle(editor.Model.Path, controller.Caret.Line);
            StatusBar.ShowMessage(added ? $"Bookmark added on line {controller.Caret.Line}." : "Bookmark removed.", TimeSpan.FromSeconds(2));
        }
    }

    /// <summary>Ctrl+K Ctrl+N / Ctrl+K Ctrl+P: the next or previous bookmark, across files.</summary>
    public async Task NextBookmarkAsync(int direction)
    {
        var path = ActiveEditor?.Model.Path;
        var line = ActiveEditor?.Controller?.Caret.Line ?? 0;
        if (Bookmarks.Next(path, line, direction) is not { } bookmark)
        {
            ShowMessage("There are no bookmarks.");
            return;
        }

        await GoToBookmarkAsync(bookmark).ConfigureAwait(true);
    }

    private async Task GoToBookmarkAsync(Bookmark bookmark)
    {
        RememberLocation();
        await OpenFileAsync(bookmark.Path, preview: false, new LanguageServices.Protocol.Position(bookmark.Line - 1, 0)).ConfigureAwait(true);
    }

    private async Task RenameBookmarkAsync(Bookmark bookmark)
    {
        var choice = await Dialogs.ShowAsync(new DialogRequest("Rename Bookmark", $"Name for the bookmark at {Path.GetFileName(bookmark.Path)}:{bookmark.Line}:", ["Rename", "Cancel"])
        {
            Input = bookmark.Name ?? string.Empty,
        }).ConfigureAwait(true);
        if (choice.Button == "Rename")
        {
            Bookmarks.Rename(bookmark, choice.Input);
        }
    }
}
