using Codalus.Workbench.Projects;

namespace Codalus.Workbench.Tests;

/// <summary>What New Project remembers between starts (ADR 0082).</summary>
public sealed class NewProjectHistoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "codalus-history-" + Guid.NewGuid().ToString("N"));

    private string File => Path.Combine(_root, "state", "new-project.json");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public void Recent_templates_keep_ten_pinned_first()
    {
        var history = new NewProjectHistory(File);
        for (var i = 0; i < 12; i++)
        {
            history.AddRecentTemplate("t" + i);
        }

        Assert.Equal(10, history.RecentTemplates.Count);
        Assert.Equal("t11", history.RecentTemplates[0].Id);
        Assert.DoesNotContain(history.RecentTemplates, t => t.Id is "t0" or "t1");

        history.SetPinned("t5", pinned: true);
        history.AddRecentTemplate("t12");
        Assert.Equal(("t5", true), (history.RecentTemplates[0].Id, history.RecentTemplates[0].Pinned));
        Assert.Equal("t12", history.RecentTemplates[1].Id);
        Assert.Equal(10, history.RecentTemplates.Count);

        // Using a pinned template keeps it pinned; removing takes it off the list.
        history.AddRecentTemplate("t5");
        Assert.True(history.RecentTemplates.Single(t => t.Id == "t5").Pinned);
        history.RemoveRecentTemplate("t5");
        Assert.DoesNotContain(history.RecentTemplates, t => t.Id == "t5");
        history.SetPinned("t12", pinned: false);
        Assert.False(history.RecentTemplates.Single(t => t.Id == "t12").Pinned);

        // Another reader of the file sees the same.
        Assert.Equal(history.RecentTemplates, new NewProjectHistory(File).RecentTemplates);
    }

    [Fact]
    public void Recent_locations_keep_ten_compared_by_the_platforms_rules()
    {
        var history = new NewProjectHistory(File);
        for (var i = 0; i < 11; i++)
        {
            history.AddRecentLocation(Path.Combine(_root, "loc" + i));
        }

        Assert.Equal(10, history.RecentLocations.Count);
        Assert.Equal(Path.Combine(_root, "loc10"), history.RecentLocations[0]);
        history.AddRecentLocation(Path.Combine(_root, "loc3") + Path.DirectorySeparatorChar);
        Assert.Equal(Path.Combine(_root, "loc3"), history.RecentLocations[0]);
        Assert.Single(history.RecentLocations, l => l.EndsWith("loc3", StringComparison.Ordinal));
        Assert.Equal(Path.Combine(_root, "loc3"), history.DefaultLocation("/configured"));

        // Where paths ignore case (Windows, macOS), another casing is the same location: it moves first, once.
        if (SolutionService.PathComparer == StringComparer.OrdinalIgnoreCase)
        {
            history.AddRecentLocation(Path.Combine(_root, "LOC4"));
            Assert.Single(history.RecentLocations, l => l.EndsWith("loc4", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(Path.Combine(_root, "LOC4"), history.DefaultLocation("/configured"));
        }

        var empty = new NewProjectHistory(Path.Combine(_root, "other.json"));
        Assert.Equal("/configured", empty.DefaultLocation("/configured"));
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "source", "repos"), empty.DefaultLocation(null));
    }

    [Fact]
    public void Values_by_their_persistence_keys_filters_and_frameworks_round_trip()
    {
        var history = new NewProjectHistory(File);
        history.SetValue("group:Microsoft.Web.Blazor", "InteractivityPlatform", "Auto");
        history.SetValues([("shared:Microsoft", "UseProgramMain", "true"), ("shared::XUnitVersion", "XUnitVersion", "v3")]);
        history.SetFilters("C#", "Linux", null);
        history.SetSameDirectory(true);
        history.SetFramework("dotnet:Microsoft.Test.xUnit|C#", "net11.0");

        var again = new NewProjectHistory(File);
        Assert.Equal("Auto", again.Value("group:Microsoft.Web.Blazor", "InteractivityPlatform"));
        Assert.Equal("true", again.Value("shared:Microsoft", "UseProgramMain"));
        Assert.Equal("v3", again.Value("shared::XUnitVersion", "XUnitVersion"));
        Assert.Null(again.Value("shared:Microsoft", "XUnitVersion"));
        Assert.Equal(("C#", "Linux", (string?)null), (again.LanguageFilter, again.PlatformFilter, again.ProjectTypeFilter));
        Assert.True(again.SameDirectory);
        Assert.Equal("net11.0", again.Framework("dotnet:Microsoft.Test.xUnit|C#"));

        again.SetValue("shared:Microsoft", "UseProgramMain", null);
        again.SetFramework("dotnet:Microsoft.Test.xUnit|C#", null);
        again.SetSameDirectory(false);
        var third = new NewProjectHistory(File);
        Assert.Null(third.Value("shared:Microsoft", "UseProgramMain"));
        Assert.Null(third.Framework("dotnet:Microsoft.Test.xUnit|C#"));
        Assert.False(third.SameDirectory);
    }

    [Fact]
    public void Filters_that_dont_change_write_nothing()
    {
        var history = new NewProjectHistory(File);
        var changes = 0;
        history.Changed += (_, _) => changes++;
        history.SetFilters("C#", null, null);
        Assert.Equal(1, changes);
        var written = System.IO.File.GetLastWriteTimeUtc(File);
        System.IO.File.SetLastWriteTimeUtc(File, written.AddMinutes(-5));

        // The wizard says the filters again as the list is filtered (each search, each load): nothing is written then.
        history.SetFilters("C#", null, null);
        history.SetFilters("C#", null, null);
        Assert.Equal(1, changes);
        Assert.Equal(written.AddMinutes(-5), System.IO.File.GetLastWriteTimeUtc(File));
        history.SetFilters("F#", null, null);
        Assert.Equal(2, changes);
        Assert.Equal("F#", new NewProjectHistory(File).LanguageFilter);
    }

    [Fact]
    public void A_corrupt_file_reads_as_empty_and_writes_go_through_the_state_file()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(File)!);
        System.IO.File.WriteAllText(File, "{ not json");
        var history = new NewProjectHistory(File);
        Assert.Empty(history.RecentTemplates);
        Assert.Empty(history.RecentLocations);
        var changed = 0;
        history.Changed += (_, _) => changed++;
        history.AddRecentTemplate("dotnet:Microsoft.Common.Console|C#");
        Assert.Equal(1, changed);
        Assert.Contains("\"recentTemplates\"", System.IO.File.ReadAllText(File), StringComparison.Ordinal);
        Assert.False(System.IO.File.Exists(File + ".tmp")); // written atomically
    }
}
