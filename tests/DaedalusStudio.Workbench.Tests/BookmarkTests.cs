using DaedalusStudio.Workbench.Bookmarks;

namespace DaedalusStudio.Workbench.Tests;

/// <summary>M12b bookmarks: toggling, order across files, moves, renames and persistence.</summary>
public sealed class BookmarkTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "daedalus-bookmarks-" + Guid.NewGuid().ToString("N"));

    private string File => Path.Combine(_folder, "bookmarks.json");

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Fact]
    public void Bookmarks_toggle_order_move_rename_and_persist()
    {
        var a = Path.Combine(_folder, "A.cs");
        var b = Path.Combine(_folder, "B.cs");
        var service = new BookmarkService(File);
        Assert.True(service.Toggle(b, 3));
        Assert.True(service.Toggle(a, 10));
        Assert.True(service.Toggle(a, 2));
        Assert.False(service.Toggle(a, 10)); // removed
        Assert.True(service.Toggle(a, 10));

        // Next and previous go through files in order and wrap around.
        Assert.Equal((a, 10), (service.Next(a, 2, 1)!.Path, service.Next(a, 2, 1)!.Line));
        Assert.Equal((b, 3), (service.Next(a, 10, 1)!.Path, service.Next(a, 10, 1)!.Line));
        Assert.Equal((a, 2), (service.Next(b, 3, 1)!.Path, service.Next(b, 3, 1)!.Line));
        Assert.Equal((b, 3), (service.Next(a, 2, -1)!.Path, service.Next(a, 2, -1)!.Line));

        // Lines move with edits; two that meet become one.
        var inA = service.For(a);
        service.Move(a, [(inA[0], 5), (inA[1], 5)]);
        Assert.Equal([5], service.For(a).Select(x => x.Line));

        service.Rename(service.For(b)[0], "  setup  ");
        Assert.Equal("setup", service.For(b)[0].Display);

        // Persisted.
        var reloaded = new BookmarkService(File);
        Assert.Equal([(a, 5, (string?)null), (b, 3, "setup")], reloaded.All.Select(x => (x.Path, x.Line, x.Name)));
        reloaded.Clear();
        Assert.Empty(new BookmarkService(File).All);
    }
}
