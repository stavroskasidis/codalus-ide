using System.Collections.ObjectModel;
using DaedalusStudio.Shell.Infrastructure;
using DaedalusStudio.Workbench.Bookmarks;

namespace DaedalusStudio.Shell.ViewModels;

/// <summary>A row of the Bookmarks window.</summary>
public sealed class BookmarkRowViewModel(Bookmark bookmark)
{
    public Bookmark Bookmark { get; } = bookmark;

    public string Name => Bookmark.Name ?? string.Empty;

    public bool HasName => Bookmark.Name is { Length: > 0 };

    public string Location => $"{Path.GetFileName(Bookmark.Path)}:{Bookmark.Line}";

    public string Folder => Path.GetDirectoryName(Bookmark.Path) ?? string.Empty;

    public string AccessibleName => HasName ? $"{Name}, {Location}" : Location;
}

/// <summary>The Bookmarks window (M12b): every bookmark by file and line; Enter or double-click goes there; F2 renames; Delete removes.</summary>
public sealed class BookmarksViewModel : ObservableObject
{
    public const string ToolWindowId = "bookmarks";
    private readonly BookmarkService _bookmarks;
    private readonly Action<Bookmark> _open;
    private readonly Func<Bookmark, Task> _rename;

    public BookmarksViewModel(BookmarkService bookmarks, Action<Bookmark> open, Func<Bookmark, Task> rename)
    {
        _bookmarks = bookmarks;
        _open = open;
        _rename = rename;
        bookmarks.Changed += (_, _) => Ui.Post(Refresh);
        Refresh();
    }

    public ObservableCollection<BookmarkRowViewModel> Rows { get; } = [];

    public bool IsEmpty => Rows.Count == 0;

    private void Refresh()
    {
        Rows.Clear();
        foreach (var bookmark in _bookmarks.All)
        {
            Rows.Add(new BookmarkRowViewModel(bookmark));
        }

        OnPropertyChanged(nameof(IsEmpty));
    }

    public void Open(BookmarkRowViewModel row) => _open(row.Bookmark);

    public Task RenameAsync(BookmarkRowViewModel row) => _rename(row.Bookmark);

    public void Remove(BookmarkRowViewModel row) => _bookmarks.Remove(row.Bookmark);

    public void Clear() => _bookmarks.Clear();
}
