using Codalus.Workbench.Components;

namespace Codalus.Workbench.Tests;

/// <summary>The Components page's catalog (M22d): status, removing, and offline layouts.</summary>
public sealed class ComponentCatalogTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("codalus-components-").FullName;

    private static Component Folder(string id, string version, string relative, string cache) => new()
    {
        Id = id, Name = id, Version = version, License = "MIT", Description = id,
        Folders = () => [relative],
        IsInstalled = () => File.Exists(Path.Combine(cache, relative, "ok")),
    };

    private static void Make(string folder, int bytes)
    {
        Directory.CreateDirectory(Path.Combine(folder, "sub"));
        File.WriteAllText(Path.Combine(folder, "ok"), "");
        File.WriteAllBytes(Path.Combine(folder, "sub", "data.bin"), new byte[bytes]);
    }

    [Fact]
    public void Status_sizes_and_removing()
    {
        var cache = Path.Combine(_root, "cache");
        Make(Path.Combine(cache, "node", "24.0.0"), 2048);
        var catalog = new ComponentCatalog(cache, [Folder("node", "24.0.0", Path.Combine("node", "24.0.0"), cache), Folder("js-debug", "1.0.0", Path.Combine("js-debug", "1.0.0"), cache)]);

        var status = catalog.Status();
        Assert.True(status[0].Installed);
        Assert.Equal(2048, status[0].Size);
        Assert.False(status[1].Installed);
        Assert.Equal(0, status[1].Size);

        catalog.Remove(catalog.Components[0]);
        Assert.False(catalog.Status()[0].Installed);
        Assert.False(Directory.Exists(Path.Combine(cache, "node", "24.0.0")));
    }

    [Fact]
    public void An_offline_layout_carries_the_components_to_another_machine()
    {
        var online = Path.Combine(_root, "online");
        Make(Path.Combine(online, "node", "24.0.0"), 100);
        Make(Path.Combine(online, "js-debug", "1.0.0"), 50);
        var layout = Path.Combine(_root, "layout");
        var exported = new ComponentCatalog(online, [Folder("node", "24.0.0", Path.Combine("node", "24.0.0"), online), Folder("js-debug", "1.0.0", Path.Combine("js-debug", "1.0.0"), online)]).Export(layout);
        Assert.Equal(["node", "js-debug"], exported.Select(e => e.Id));
        Assert.True(File.Exists(Path.Combine(layout, ComponentCatalog.LayoutIndex)));

        // The air-gapped machine: js-debug of another version isn't taken; node is.
        var offline = Path.Combine(_root, "offline");
        var catalog = new ComponentCatalog(offline, [Folder("node", "24.0.0", Path.Combine("node", "24.0.0"), offline), Folder("js-debug", "2.0.0", Path.Combine("js-debug", "2.0.0"), offline)]);
        Assert.Equal(["node"], catalog.Import(layout));
        Assert.True(catalog.Status()[0].Installed);
        Assert.Equal(100, catalog.Status()[0].Size);
        Assert.Empty(catalog.Import(layout)); // already there

        Assert.Throws<InvalidDataException>(() => catalog.Import(_root)); // not a layout
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
