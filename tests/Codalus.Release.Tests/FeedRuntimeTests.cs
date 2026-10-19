using System.Text.Json;

namespace Codalus.Release.Tests;

/// <summary>M49 (ADR 0080): the hosts' .NET runtime goes into the release's feed entries before the feed is signed.</summary>
public sealed class FeedRuntimeTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("codalus-feed-runtime-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void The_releases_entries_get_the_highest_runtime_its_hosts_ask_for()
    {
        var app = Path.Combine(_dir, "codalus");
        Config(app, "pluginhost", "codalus-pluginhost", """{ "runtimeOptions": { "rollForward": "Major", "framework": { "name": "Microsoft.NETCore.App", "version": "11.0.0-rc.2.26504.105" } } }""");
        Config(app, "lshost", "codalus-lshost", """{ "runtimeOptions": { "rollForward": "Major", "framework": { "name": "Microsoft.NETCore.App", "version": "11.0.0-rc.1.26425.128" } } }""");
        Config(app, "wpfpreviewer", "codalus-wpfpreviewer", """{ "runtimeOptions": { "framework": { "name": "Microsoft.NETCore.App", "version": "12.0.0" } } }"""); // not an IDE host
        var frameworks = FeedRuntime.Frameworks(app);
        Assert.Equal([("Microsoft.NETCore.App", "11.0.0-rc.2.26504.105", "Major")], frameworks);

        var feed = Path.Combine(_dir, "releases.osx-arm64-nightly.json");
        File.WriteAllText(feed, """
            {"Assets":[
              {"PackageId":"Codalus","Version":"0.4.0-nightly.20261018.223","Type":"Full","FileName":"old-full.nupkg","SHA1":"a","SHA256":"b","Size":1,"NotesMarkdown":"old"},
              {"PackageId":"Codalus","Version":"0.4.0-nightly.20261018.224","Type":"Full","FileName":"full.nupkg","SHA1":"a","SHA256":"b","Size":2,"NotesMarkdown":"new"},
              {"PackageId":"Codalus","Version":"0.4.0-nightly.20261018.224","Type":"Delta","FileName":"delta.nupkg","SHA1":"a","SHA256":"b","Size":3}
            ]}
            """);
        Assert.Equal(2, FeedRuntime.Annotate(feed, "0.4.0-nightly.20261018.224", frameworks));

        using var document = JsonDocument.Parse(File.ReadAllText(feed));
        var assets = document.RootElement.GetProperty("Assets").EnumerateArray().ToList();
        Assert.False(assets[0].TryGetProperty(FeedRuntime.Property, out _)); // an older release: as it was
        var required = Assert.Single(assets[1].GetProperty(FeedRuntime.Property).EnumerateArray());
        Assert.Equal(("Microsoft.NETCore.App", "11.0.0-rc.2.26504.105", "Major"), (required.GetProperty("Name").GetString(), required.GetProperty("Version").GetString(), required.GetProperty("RollForward").GetString()));
        Assert.Equal("full.nupkg", assets[1].GetProperty("FileName").GetString()); // Velopack's own fields are kept
        Assert.Equal("new", assets[1].GetProperty("NotesMarkdown").GetString());
        Assert.Equal(0, FeedRuntime.Annotate(feed, "9.9.9", frameworks));
    }

    private static void Config(string app, string folder, string name, string json)
    {
        Directory.CreateDirectory(Path.Combine(app, folder));
        File.WriteAllText(Path.Combine(app, folder, name + ".runtimeconfig.json"), json);
    }
}
