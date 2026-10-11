using System.Net;
using System.Security.Cryptography;
using System.Text;
using DaedalusStudio.Workbench.Updates;

namespace DaedalusStudio.Workbench.Tests;

/// <summary>The signed update feed (M32): verification, and finding a channel's feed and packages among GitHub releases.</summary>
public sealed class ReleaseFeedTests : IDisposable
{
    private const string Feed = """{"Assets":[{"PackageId":"DaedalusStudio","Version":"0.3.0","Type":"Full","FileName":"DaedalusStudio-0.3.0-win-x64-stable-full.nupkg","SHA256":"0EFD53C02037B6A0D3C72C91466D9AF27A230DB58163BF452FDEB0BCD778F0AE","Size":10}]}""";

    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly string _publicKey;
    private readonly string _folder = Directory.CreateTempSubdirectory("daedalus-feed-").FullName;

    public ReleaseFeedTests() => _publicKey = Convert.ToBase64String(_key.ExportSubjectPublicKeyInfo());

    public void Dispose()
    {
        _key.Dispose();
        Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public void A_feed_signed_with_the_key_verifies_and_anything_else_is_refused()
    {
        var bytes = Encoding.UTF8.GetBytes(Feed);
        var signature = FeedSignature.Sign(bytes, _key);

        FeedSignature.Verify(bytes, signature, _publicKey);

        Assert.Throws<FeedVerificationException>(() => FeedSignature.Verify(bytes, null, _publicKey));
        Assert.Throws<FeedVerificationException>(() => FeedSignature.Verify(bytes, "not base64!", _publicKey));
        var tampered = Encoding.UTF8.GetBytes(Feed.Replace("0.3.0", "0.3.1", StringComparison.Ordinal));
        Assert.Throws<FeedVerificationException>(() => FeedSignature.Verify(tampered, signature, _publicKey));
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.Throws<FeedVerificationException>(() => FeedSignature.Verify(bytes, FeedSignature.Sign(bytes, other), _publicKey));
    }

    [Fact]
    public void A_signed_feed_without_package_hashes_is_refused_too()
    {
        var bytes = Encoding.UTF8.GetBytes("""{"Assets":[{"FileName":"x-full.nupkg","SHA1":"9D0269DB4A9524879DBCA63FC32985391C057630"}]}""");

        var refused = Assert.Throws<FeedVerificationException>(() => FeedSignature.Verify(bytes, FeedSignature.Sign(bytes, _key), _publicKey));

        Assert.Contains("x-full.nupkg", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_built_in_key_is_a_P256_public_key()
    {
        using var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(FeedSignature.ProjectKey), out _);

        Assert.Equal(256, key.KeySize);
    }

    [Fact]
    public async Task A_folder_store_serves_its_verified_feed_and_packages()
    {
        File.WriteAllText(Path.Combine(_folder, "releases.win-x64-stable.json"), Feed);
        File.WriteAllText(Path.Combine(_folder, "releases.win-x64-stable.json.sig"), FeedSignature.Sign(Encoding.UTF8.GetBytes(Feed), _key));
        File.WriteAllText(Path.Combine(_folder, "DaedalusStudio-0.3.0-win-x64-stable-full.nupkg"), "0123456789");
        var store = new FolderReleaseStore(_folder, _publicKey);
        var target = Path.Combine(_folder, "downloaded.nupkg");

        var feed = await store.GetFeedAsync("win-x64-stable", TestContext.Current.CancellationToken);
        await store.DownloadAsync("DaedalusStudio-0.3.0-win-x64-stable-full.nupkg", target, null, TestContext.Current.CancellationToken);

        Assert.Equal(Feed, feed?.Json);
        Assert.Equal("0123456789", File.ReadAllText(target));
        Assert.Null(await store.GetFeedAsync("win-x64-nightly", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task On_GitHub_a_channel_s_feed_is_in_the_newest_release_of_its_kind_that_isn_t_pulled()
    {
        var github = new FakeGitHub();
        github.Release("v0.4.0-nightly.20261012.3", "Daedalus Studio 0.4.0-nightly.20261012.3", ("releases.win-x64-nightly.json", "{}"));
        github.Release("v0.3.1", "Daedalus Studio 0.3.1 (pulled)", ("releases.win-x64-stable.json", "{}"), ("releases.win-x64-stable.json.sig", "x"));
        github.Release("v0.3.0", "Daedalus Studio 0.3.0",
            ("releases.win-x64-stable.json", Feed),
            ("releases.win-x64-stable.json.sig", FeedSignature.Sign(Encoding.UTF8.GetBytes(Feed), _key)),
            ("DaedalusStudio-0.3.0-win-x64-stable-full.nupkg", "0123456789"));
        github.Release("v0.2.0", "Daedalus Studio 0.2.0", ("DaedalusStudio-0.2.0-win-x64-stable-full.nupkg", "older"));
        using var http = new HttpClient(github);
        var store = new GitHubReleaseStore(http, "owner/repo", _publicKey, _ => Task.FromResult<string?>("token-123"));
        var target = Path.Combine(_folder, "package.nupkg");

        var feed = await store.GetFeedAsync("win-x64-stable", TestContext.Current.CancellationToken);
        await store.DownloadAsync("DaedalusStudio-0.2.0-win-x64-stable-full.nupkg", target, null, TestContext.Current.CancellationToken);

        Assert.Equal(("v0.3.0", Feed), (feed?.Tag, feed?.Json));
        Assert.Equal("older", File.ReadAllText(target)); // a package from an older release, found by name
        Assert.All(github.Requests, r => Assert.Equal(("Bearer", "token-123"), (r.Headers.Authorization?.Scheme, r.Headers.Authorization?.Parameter)));
        Assert.All(github.Requests, r => Assert.NotEmpty(r.Headers.UserAgent));
        Assert.Contains(github.Requests, r => r.RequestUri!.AbsolutePath.EndsWith("/assets/v0.2.0/DaedalusStudio-0.2.0-win-x64-stable-full.nupkg", StringComparison.Ordinal));
    }

    [Fact]
    public async Task On_GitHub_an_unsigned_feed_is_refused_and_without_a_token_the_public_urls_are_used()
    {
        var github = new FakeGitHub();
        github.Release("v0.3.0", "Daedalus Studio 0.3.0", ("releases.linux-x64-stable.json", Feed));
        using var http = new HttpClient(github);
        var store = new GitHubReleaseStore(http, "owner/repo", _publicKey, _ => Task.FromResult<string?>(null));

        await Assert.ThrowsAsync<FeedVerificationException>(() => store.GetFeedAsync("linux-x64-stable", TestContext.Current.CancellationToken));

        Assert.Contains(github.Requests, r => r.RequestUri!.Host == "downloads.example" && r.Headers.Authorization is null);
    }

    /// <summary>GitHub's releases API and asset downloads, in memory.</summary>
    private sealed class FakeGitHub : HttpMessageHandler
    {
        private readonly List<(string Tag, string Name, (string Name, string Content)[] Assets)> _releases = [];

        public List<HttpRequestMessage> Requests { get; } = [];

        public void Release(string tag, string name, params (string Name, string Content)[] assets) => _releases.Add((tag, name, assets));

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/repos/owner/repo/releases")
            {
                var json = new StringBuilder("[");
                foreach (var (tag, name, assets) in _releases)
                {
                    json.Append(json.Length > 1 ? "," : string.Empty)
                        .Append(System.Globalization.CultureInfo.InvariantCulture, $$"""{"tag_name":"{{tag}}","name":"{{name}}","draft":false,"html_url":"https://github.example/{{tag}}","assets":[""")
                        .Append(string.Join(',', assets.Select(a =>
                            $$"""{"name":"{{a.Name}}","url":"https://api.github.com/repos/owner/repo/releases/assets/{{tag}}/{{a.Name}}","browser_download_url":"https://downloads.example/{{tag}}/{{a.Name}}"}""")))
                        .Append("]}");
                }

                return Task.FromResult(Ok(json.Append(']').ToString()));
            }

            // Asset URLs name their release (as GitHub's do, by id): /…/<tag>/<name>.
            var segments = path.Split('/');
            var (tag2, name2) = (segments[^2], segments[^1]);
            var content = _releases.Where(r => r.Tag == tag2).SelectMany(r => r.Assets).FirstOrDefault(a => a.Name == name2).Content;
            return Task.FromResult(content is null ? new HttpResponseMessage(HttpStatusCode.NotFound) : Ok(content));
        }

        private static HttpResponseMessage Ok(string content) => new(HttpStatusCode.OK) { Content = new StringContent(content) };
    }
}
