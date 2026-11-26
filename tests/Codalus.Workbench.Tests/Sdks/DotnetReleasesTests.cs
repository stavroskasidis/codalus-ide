using System.Reflection;
using Codalus.Workbench.Sdks;

namespace Codalus.Workbench.Tests.Sdks;

/// <summary>
/// Microsoft's release metadata (ADR 0112), read from the samples' trimmed copy (<c>samples/DotnetReleases</c>): the
/// index and a channel's releases, and the cache: a day's reuse, Refresh, and the stale copy when a fetch fails.
/// Nothing reaches the network: the fetch is a stand-in or a folder.
/// </summary>
public sealed class DotnetReleasesTests
{
    internal static string Samples => Path.Combine(
        typeof(DotnetReleasesTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "CodalusRepoRoot").Value!, "samples", "DotnetReleases");

    private static readonly DateOnly s_today = new(2026, 11, 26);

    [Fact]
    public void The_index_lists_every_channel_newest_first_with_its_phase_and_end_of_support()
    {
        var channels = DotnetReleaseParser.ParseIndex(File.ReadAllText(Path.Combine(Samples, "releases-index.json")));

        Assert.Equal(["12.0", "11.0", "10.0", "9.0", "8.0"], channels.Select(c => c.Version));
        var ten = channels.Single(c => c.Version == "10.0");
        Assert.Equal(("10.0.401", "10.0.12", "lts", new DateOnly(2028, 11, 14), true), (ten.LatestSdk, ten.LatestRuntime, ten.ReleaseType, ten.EndOfLife, ten.Security));
        Assert.True(channels.Single(c => c.Version == "12.0").IsPreview);
        Assert.Null(channels.Single(c => c.Version == "12.0").EndOfLife);
        Assert.True(channels.Single(c => c.Version == "9.0").IsOutOfSupport(s_today));
        Assert.False(ten.IsOutOfSupport(s_today));
    }

    [Fact]
    public void A_channels_file_gives_each_releases_runtimes_and_its_sdks_of_every_band()
    {
        var ten = DotnetReleaseParser.ParseChannel(File.ReadAllText(Path.Combine(Samples, "10.0", "releases.json")));

        Assert.Equal("10.0", ten.Channel);
        Assert.Equal(["10.0.12", "10.0.11", "10.0.10", "10.0.0"], ten.Releases.Select(r => r.Version));
        Assert.Equal(["10.0.401", "10.0.112"], ten.Releases[0].Sdks);
        Assert.True(ten.Releases[0].Security);
        Assert.Equal(("10.0.11", "10.0.11", "10.0.11"), (ten.ReleaseOfSdk("10.0.111")!.Runtime, ten.ReleaseOfSdk("10.0.111")!.AspNetCore, ten.ReleaseOfSdk("10.0.111")!.WindowsDesktop));
        Assert.Null(ten.ReleaseOfSdk("10.0.199"));
        Assert.Equal("10.0.10", ten.ReleaseOfRuntime(DotnetFrameworks.AspNetCore, "10.0.10")!.Version);
    }

    [Fact]
    public async Task A_fetched_file_is_kept_for_a_day_and_refresh_asks_again()
    {
        var cache = Directory.CreateTempSubdirectory("codalus-releases-").FullName;
        try
        {
            var now = DateTimeOffset.UtcNow;
            var asked = new List<Uri>();
            var source = new DotnetReleaseSource(cache, "https://releases.example/meta", (uri, _) =>
            {
                asked.Add(uri);
                return Task.FromResult(File.ReadAllText(Path.Combine(Samples, "releases-index.json")));
            }, () => now);

            var first = await source.GetIndexAsync(refresh: false, CancellationToken.None);
            Assert.False(first.FromCache);
            Assert.Equal("https://releases.example/meta/releases-index.json", Assert.Single(asked).AbsoluteUri);

            now += TimeSpan.FromHours(23);
            Assert.True((await source.GetIndexAsync(refresh: false, CancellationToken.None)).FromCache);
            Assert.Single(asked);

            Assert.False((await source.GetIndexAsync(refresh: true, CancellationToken.None)).FromCache);
            Assert.Equal(2, asked.Count);

            now += TimeSpan.FromDays(2);
            Assert.False((await source.GetIndexAsync(refresh: false, CancellationToken.None)).FromCache);
            Assert.Equal(3, asked.Count);
        }
        finally
        {
            Directory.Delete(cache, recursive: true);
        }
    }

    [Fact]
    public async Task A_failed_fetch_falls_back_on_the_cache_whatever_its_age_and_without_one_it_says_so()
    {
        var cache = Directory.CreateTempSubdirectory("codalus-releases-").FullName;
        try
        {
            var now = DateTimeOffset.UtcNow;
            var fail = false;
            var source = new DotnetReleaseSource(cache, "https://releases.example/", (uri, _) => fail
                ? throw new HttpRequestException("No route to host")
                : Task.FromResult(File.ReadAllText(Path.Combine(Samples, "10.0", "releases.json"))), () => now);

            await Assert.ThrowsAsync<IOException>(() => new DotnetReleaseSource(Path.Combine(cache, "empty"), "https://releases.example/", (_, _) => throw new HttpRequestException("offline")).GetIndexAsync(false, CancellationToken.None));

            var fetched = await source.GetChannelAsync("10.0", refresh: false, CancellationToken.None);
            fail = true;
            now += TimeSpan.FromDays(30);
            var stale = await source.GetChannelAsync("10.0", refresh: false, CancellationToken.None);

            Assert.True(stale.FromCache);
            Assert.Equal("No route to host", stale.FetchError);
            Assert.Equal(fetched.Value.Releases.Count, stale.Value.Releases.Count);
            await Assert.ThrowsAsync<ArgumentException>(() => source.GetChannelAsync("../10.0", false, CancellationToken.None));
        }
        finally
        {
            Directory.Delete(cache, recursive: true);
        }
    }

    [Fact]
    public async Task A_folder_location_reads_the_same_files_from_disk()
    {
        var cache = Directory.CreateTempSubdirectory("codalus-releases-").FullName;
        try
        {
            var source = new DotnetReleaseSource(cache, Samples, (_, _) => throw new InvalidOperationException("no network in tests"));

            Assert.Contains((await source.GetIndexAsync(false, CancellationToken.None)).Value, c => c.Version == "11.0");
            Assert.Equal("11.0.0", (await source.GetChannelAsync("11.0", false, CancellationToken.None)).Value.Releases[0].Version);
        }
        finally
        {
            Directory.Delete(cache, recursive: true);
        }
    }
}
