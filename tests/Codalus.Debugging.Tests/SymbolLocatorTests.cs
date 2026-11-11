using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using Codalus.Debugging.Symbols;
using Codalus.Tests;

namespace Codalus.Debugging.Tests;

/// <summary>
/// Symbol paths and symbol servers (M58): the built Debuggee.dll, copied away from its PDB, finds that PDB again in a
/// folder, in the cache or on a mock symbol server. Nothing here reaches a real server: the servers are the loopback
/// mock or a handler that answers in place of the network.
/// </summary>
public sealed class SymbolLocatorTests(DebuggeeFixture fixture) : IClassFixture<DebuggeeFixture>, IDisposable
{
    private static readonly byte[] s_windowsPdb = [.. "Microsoft C/C++ MSF 7.00\r\n\u001aDS\0\0\0"u8, .. new byte[64]];

    private readonly string _root = Path.Combine(Path.GetTempPath(), "codalus-symbols-" + Guid.NewGuid().ToString("N"));

    // No proxy (the mock is on this machine) and no redirects by the handler: the locator follows them itself.
    private readonly HttpClient _http = new(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false });
    private readonly List<SymbolLocator> _locators = [];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private string Cache => Path.Combine(_root, "cache");

    private string Pdb => Path.ChangeExtension(fixture.Dll, ".pdb");

    /// <summary>The key of Debuggee.pdb, from the GUID the PDB itself carries (not the one the module names).</summary>
    private string Key => $"debuggee.pdb/{PdbId(Pdb).Guid:N}FFFFFFFF/debuggee.pdb";

    private string CachedPdb => Path.Combine(Cache, Key.Replace('/', Path.DirectorySeparatorChar));

    public void Dispose()
    {
        foreach (var locator in _locators)
        {
            locator.Dispose();
        }

        _http.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public void Portable_pdb_identity_and_key_are_read_from_the_module()
    {
        var identity = SymbolIdentity.Read(fixture.Dll)!;

        Assert.Equal("Debuggee.pdb", identity.PdbFileName);
        Assert.True(identity.IsPortable);
        Assert.Equal(PdbId(Pdb).Guid, identity.Guid);
        Assert.Equal(Key, identity.Key);

        // The checksum the module records is the PDB's hash with the PDB's own id zeroed.
        var checksum = Assert.Single(identity.Checksums);
        Assert.Equal("SHA256", checksum.Algorithm);
        var pdb = File.ReadAllBytes(Pdb);
        var id = PdbId(Pdb).Bytes;
        pdb.AsSpan(pdb.AsSpan().IndexOf(id), id.Length).Clear();
        Assert.Equal("SHA256:" + Convert.ToHexStringLower(SHA256.HashData(pdb)), identity.ChecksumHeader);
    }

    [Fact]
    public void Keys_follow_the_symbol_server_conventions()
    {
        // The examples of the SSQP key conventions (dotnet/symstore).
        var guid = new Guid(0x497B72F6, 0x390A, 0x44FC, 0x87, 0x8E, 0x5A, 0x2D, 0x63, 0xB6, 0xCC, 0x4B);
        Assert.Equal("foo.pdb/497b72f6390a44fc878e5a2d63b6cc4b1/foo.pdb", SymbolIdentity.BuildKey("Foo.pdb", guid, age: 1, isPortable: false));
        Assert.Equal("foo.pdb/497b72f6390a44fc878e5a2d63b6cc4bFFFFFFFF/foo.pdb", SymbolIdentity.BuildKey("Foo.pdb", guid, age: 1, isPortable: true));

        // Only the file name of the build machine's path, whichever system wrote it; the age in hex without leading zeros.
        Assert.Equal("foo.pdb/497b72f6390a44fc878e5a2d63b6cc4b1a2/foo.pdb", SymbolIdentity.BuildKey(@"C:\build\obj\Release\Foo.pdb", guid, age: 0x1A2, isPortable: false));
        Assert.Equal("foo.pdb/497b72f6390a44fc878e5a2d63b6cc4bFFFFFFFF/foo.pdb", SymbolIdentity.BuildKey("/src/obj/Foo.pdb", guid, age: 1, isPortable: true));
    }

    [Fact]
    public void Windows_pdb_identity_and_key_are_read_from_the_module()
    {
        var guid = Guid.NewGuid();
        var identity = SymbolIdentity.Read(WindowsModule(guid, age: 0x1A2))!;

        Assert.Equal("Legacy.pdb", identity.PdbFileName);
        Assert.False(identity.IsPortable);
        Assert.Equal(guid, identity.Guid);
        Assert.Equal(0x1A2, identity.Age);
        Assert.Equal($"legacy.pdb/{guid:N}1a2/legacy.pdb", identity.Key);
        Assert.Null(identity.ChecksumHeader);
    }

    [Fact]
    public async Task A_module_that_names_no_pdb_has_no_symbols()
    {
        var module = Path.Combine(Directory.CreateDirectory(Path.Combine(_root, "app")).FullName, "NoPdb.dll");
        WriteModule(module, debug: null);

        Assert.Null(SymbolIdentity.Read(module));
        var result = await Locator(new SymbolOptions { SearchPaths = [_root] }).FindAsync(module, ignoreFilter: false, Token);
        Assert.Equal(new SymbolResult(null, "No symbols: the module doesn't name a PDB."), result);

        var missing = await Locator(new SymbolOptions()).FindAsync(Path.Combine(_root, "Missing.dll"), ignoreFilter: false, Token);
        Assert.Null(missing.PdbPath);
        Assert.StartsWith("No symbols: Missing.dll couldn't be read (", missing.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Found_in_a_search_path_next_to_other_pdbs()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_root, "symbols")).FullName;
        File.Copy(Pdb, Path.Combine(folder, "Debuggee.pdb"));

        var result = await Locator(new SymbolOptions { SearchPaths = [Path.Combine(_root, "empty"), folder] }).FindAsync(Module(), ignoreFilter: false, Token);

        Assert.Equal(new SymbolResult(Path.Combine(folder, "Debuggee.pdb"), $"Symbols loaded from {folder}."), result);
    }

    [Fact]
    public async Task Found_in_a_search_path_in_the_symbol_store_layout()
    {
        var folder = Path.Combine(_root, "store");
        var stored = Store(folder, Key, File.ReadAllBytes(Pdb));

        var result = await Locator(new SymbolOptions { SearchPaths = [folder] }).FindAsync(Module(), ignoreFilter: false, Token);

        Assert.Equal(new SymbolResult(stored, $"Symbols loaded from {folder}."), result);
    }

    [Fact]
    public async Task A_pdb_of_another_build_in_a_search_path_is_skipped()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_root, "symbols")).FullName;
        var other = Path.Combine(folder, "Debuggee.pdb");
        File.WriteAllBytes(other, OtherBuild());
        var notPdb = Store(folder, Key, "<html>Sign in</html>"u8.ToArray());

        var result = await Locator(new SymbolOptions { SearchPaths = [folder] }).FindAsync(Module(), ignoreFilter: false, Token);

        Assert.Equal(new SymbolResult(null, $"Skipped {other}: it belongs to another build of the module. Skipped {notPdb}: it isn't a portable PDB."), result);
    }

    [Fact]
    public async Task Without_paths_or_servers_nothing_is_found_and_nothing_is_asked()
    {
        using var network = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using var http = new HttpClient(network);
        using var locator = new SymbolLocator(new SymbolOptions { CacheFolder = Cache }, http);

        var result = await locator.FindAsync(Module(), ignoreFilter: true, Token);

        Assert.Equal(new SymbolResult(null, "No symbols: Debuggee.pdb isn't in the cache or the symbol paths."), result);
        Assert.Empty(network.Requests);
    }

    [Fact]
    public async Task Downloaded_from_a_server_into_the_cache_and_found_there_the_next_time()
    {
        await using var server = SymbolServer(File.ReadAllBytes(Pdb));
        var options = new SymbolOptions { Servers = [server.BaseUrl + "/symbols/"], CacheFolder = Cache };
        var module = Module();
        List<string> log = [];

        var first = await Locator(options, log).FindAsync(module, ignoreFilter: false, Token);

        Assert.Equal(new SymbolResult(CachedPdb, $"Symbols loaded from the cache (downloaded from {Host(server)})."), first);
        Assert.Equal(File.ReadAllBytes(Pdb), File.ReadAllBytes(CachedPdb));
        Assert.Equal([CachedPdb], Directory.GetFiles(Cache, "*", SearchOption.AllDirectories)); // The temporary file is gone.
        var request = Assert.Single(server.Requests);
        Assert.Equal("GET", request.Method);
        Assert.Equal("/symbols/" + Key, request.Path);

        // What NuGet.org's server needs: the checksum of the PDB, as the module records it.
        Assert.Equal(SymbolIdentity.Read(module)!.ChecksumHeader, request.Header("SymbolChecksum"));
        Assert.Matches("^SHA256:[0-9a-f]{64}$", request.Header("SymbolChecksum"));
        Assert.Contains($"Asking {Host(server)} for {Key}.", log);

        // Another session: the cache answers, the server isn't asked.
        var second = await Locator(options).FindAsync(module, ignoreFilter: false, Token);
        Assert.Equal(new SymbolResult(CachedPdb, "Symbols loaded from the cache."), second);
        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task A_server_without_the_pdb_is_asked_once_in_a_session()
    {
        await using var empty = SymbolServer(pdb: null);
        await using var server = SymbolServer(File.ReadAllBytes(Pdb));
        var module = Module();
        var locator = Locator(new SymbolOptions { Servers = [empty.BaseUrl + "/symbols"], CacheFolder = Cache });

        var first = await locator.FindAsync(module, ignoreFilter: false, Token);
        var second = await locator.FindAsync(module, ignoreFilter: true, Token);

        Assert.Equal(new SymbolResult(null, $"Not found on {Host(empty)} (404)."), first);
        Assert.Equal(first, second);
        Assert.Single(empty.Requests);
        Assert.False(Directory.Exists(Cache) && Directory.EnumerateFileSystemEntries(Cache).Any());

        // Asked again when told to forget; and the next server in the list has it.
        locator.ForgetFailures();
        await locator.FindAsync(module, ignoreFilter: false, Token);
        Assert.Equal(2, empty.Requests.Count);

        var both = await Locator(new SymbolOptions { Servers = [empty.BaseUrl + "/symbols", server.BaseUrl + "/symbols"], CacheFolder = Cache }).FindAsync(module, ignoreFilter: false, Token);
        Assert.Equal(CachedPdb, both.PdbPath);
    }

    [Fact]
    public async Task A_pdb_of_another_build_from_a_server_is_not_kept()
    {
        await using var server = SymbolServer(OtherBuild());
        var locator = Locator(new SymbolOptions { Servers = [server.BaseUrl + "/symbols"], CacheFolder = Cache });

        var result = await locator.FindAsync(Module(), ignoreFilter: false, Token);

        Assert.Equal(new SymbolResult(null, $"Skipped the file {Host(server)} sent: it belongs to another build of the module."), result);
        Assert.Empty(Directory.GetFiles(Cache, "*", SearchOption.AllDirectories));

        // Remembered like a missing one.
        Assert.Equal(result, await locator.FindAsync(Module(), ignoreFilter: false, Token));
        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task A_damaged_pdb_in_the_cache_is_downloaded_again()
    {
        await using var server = SymbolServer(File.ReadAllBytes(Pdb));
        Store(Cache, Key, File.ReadAllBytes(Pdb)[..100]);

        var result = await Locator(new SymbolOptions { Servers = [server.BaseUrl + "/symbols"], CacheFolder = Cache }).FindAsync(Module(), ignoreFilter: false, Token);

        Assert.Equal(CachedPdb, result.PdbPath);
        Assert.Equal(File.ReadAllBytes(Pdb), File.ReadAllBytes(CachedPdb));
    }

    [Theory]
    [InlineData("Microsoft.*.dll", "Microsoft.Extensions.Logging.dll", true)]
    [InlineData("Microsoft.*.dll", "/usr/share/dotnet/shared/microsoft.csharp.DLL", true)]
    [InlineData("microsoft.*.dll", @"C:\Program Files\dotnet\Microsoft.CSharp.dll", true)]
    [InlineData("Microsoft.*.dll", "Microsoft.dll", false)]
    [InlineData("System.??.dll", "System.IO.dll", true)]
    [InlineData("System.??.dll", "System.Xml.dll", false)]
    [InlineData("Debuggee.dll", "Debuggee.Tests.dll", false)]
    [InlineData("  ", "Debuggee.dll", false)]
    [InlineData("*", "Debuggee.dll", true)]
    public void Module_patterns_are_file_names_with_wildcards_without_case(string pattern, string module, bool matches)
    {
        Assert.Equal(!matches, new SymbolOptions { ExcludedModules = ["Other.dll", pattern] }.Includes(module));
        Assert.Equal(matches, new SymbolOptions { ModuleFilter = SymbolModuleFilter.OnlySpecified, IncludedModules = ["Other.dll", pattern] }.Includes(module));

        // Each list counts only in its own mode.
        Assert.True(new SymbolOptions { IncludedModules = ["Other.dll"] }.Includes(module));
        Assert.False(new SymbolOptions { ModuleFilter = SymbolModuleFilter.OnlySpecified, ExcludedModules = [pattern] }.Includes(module));
    }

    [Fact]
    public async Task Filtered_modules_are_skipped_unless_the_user_asks()
    {
        await using var server = SymbolServer(File.ReadAllBytes(Pdb));
        var module = Module();
        var excluding = Locator(new SymbolOptions { Servers = [server.BaseUrl + "/symbols"], CacheFolder = Cache, ExcludedModules = ["Debug*.dll"] });
        var including = Locator(new SymbolOptions
        {
            Servers = [server.BaseUrl + "/symbols"],
            CacheFolder = Cache,
            ModuleFilter = SymbolModuleFilter.OnlySpecified,
            IncludedModules = ["Contoso.*.dll"],
        });

        Assert.Equal(new SymbolResult(null, "Skipped: excluded by the symbol settings."), await excluding.FindAsync(module, ignoreFilter: false, Token));
        Assert.Equal(new SymbolResult(null, "Skipped: not among the modules the symbol settings include."), await including.FindAsync(module, ignoreFilter: false, Token));
        Assert.Empty(server.Requests);

        // "Load Symbols" on the module.
        Assert.Equal(CachedPdb, (await including.FindAsync(module, ignoreFilter: true, Token)).PdbPath);
        Assert.Equal(CachedPdb, (await excluding.FindAsync(module, ignoreFilter: true, Token)).PdbPath);
        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task Plain_http_to_another_machine_is_refused_without_a_request()
    {
        using var network = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(File.ReadAllBytes(Pdb)) });
        using var http = new HttpClient(network);
        var options = new SymbolOptions { Servers = ["http://symbols.contoso.example/symbols", "ftp://symbols.contoso.example", "symbols"], CacheFolder = Cache };
        using var locator = new SymbolLocator(options, http);

        var result = await locator.FindAsync(Module(), ignoreFilter: false, Token);

        Assert.Equal(
            new SymbolResult(null, "Skipped symbols.contoso.example: a symbol server must be https://. Skipped 'ftp://symbols.contoso.example': it isn't a symbol server's URL. Skipped 'symbols': it isn't a symbol server's URL."),
            result);
        Assert.Empty(network.Requests);
    }

    [Fact]
    public async Task The_well_known_servers_are_asked_for_the_key_under_their_url()
    {
        using var network = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var http = new HttpClient(network);
        var options = new SymbolOptions
        {
            // The last one as a private feed gives its URL: a trailing slash, and an access token that must stay on the request.
            Servers = [SymbolOptions.MicrosoftServer, SymbolOptions.NuGetServer, " https://symbols.contoso.example/store/?token=abc "],
            CacheFolder = Cache,
        };
        using var locator = new SymbolLocator(options, http);

        var result = await locator.FindAsync(Module(), ignoreFilter: false, Token);

        Assert.Equal(new SymbolResult(null, "Not found on msdl.microsoft.com (404). Not found on symbols.nuget.org (404). Not found on symbols.contoso.example (404)."), result);
        Assert.Equal(
            [
                "https://msdl.microsoft.com/download/symbols/" + Key,
                "https://symbols.nuget.org/download/symbols/" + Key,
                "https://symbols.contoso.example/store/" + Key + "?token=abc",
            ],
            network.Requests.Select(r => r.AbsoluteUri));
    }

    [Fact]
    public async Task Without_a_cache_folder_the_servers_are_not_asked()
    {
        using var network = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using var http = new HttpClient(network);
        using var locator = new SymbolLocator(new SymbolOptions { Servers = [SymbolOptions.NuGetServer] }, http);

        var result = await locator.FindAsync(Module(), ignoreFilter: false, Token);

        Assert.Equal(new SymbolResult(null, "Skipped symbols.nuget.org: there's no cache folder to download to."), result);
        Assert.Empty(network.Requests);
    }

    [Fact]
    public async Task An_unreachable_server_is_a_status_and_is_not_asked_again()
    {
        using var network = new StubHandler(_ => throw new HttpRequestException("Name or service not known (symbols.contoso.example:443)"));
        using var http = new HttpClient(network);
        using var locator = new SymbolLocator(new SymbolOptions { Servers = ["https://symbols.contoso.example/symbols"], CacheFolder = Cache }, http);

        var result = await locator.FindAsync(Module(), ignoreFilter: false, Token);
        var other = await locator.FindAsync(WindowsModule(Guid.NewGuid(), age: 1), ignoreFilter: false, Token);

        Assert.Equal(new SymbolResult(null, "symbols.contoso.example couldn't be reached: Name or service not known (symbols.contoso.example:443)"), result);
        Assert.Equal(new SymbolResult(null, "Skipped symbols.contoso.example: it didn't answer earlier in this session."), other);
        Assert.Single(network.Requests);
    }

    [Fact]
    public async Task Lookups_of_one_module_at_the_same_time_share_one_download()
    {
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pdb = File.ReadAllBytes(Pdb);
        await using var server = new MockHttpServer(async _ =>
        {
            arrived.TrySetResult();
            await release.Task;
            return new MockHttpResponse(200, pdb, "application/octet-stream");
        });
        var locator = Locator(new SymbolOptions { Servers = [server.BaseUrl], CacheFolder = Cache });
        var module = Module();

        try
        {
            var lookups = Enumerable.Range(0, 8).Select(_ => locator.FindAsync(module, ignoreFilter: false, Token)).ToArray();
            await arrived.Task.WaitAsync(TimeSpan.FromSeconds(60), Token);
            release.SetResult();
            var results = await Task.WhenAll(lookups);

            Assert.All(results, r => Assert.Equal(CachedPdb, r.PdbPath));
            Assert.Single(server.Requests);
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [Fact]
    public async Task A_server_that_does_not_answer_is_given_up_on_and_a_lookup_can_be_cancelled()
    {
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new MockHttpServer(async _ =>
        {
            arrived.TrySetResult();
            await release.Task;
            return MockHttpResponse.NotFound();
        });
        var options = new SymbolOptions { Servers = [server.BaseUrl], CacheFolder = Cache };
        var module = Module();

        try
        {
            // The caller stops waiting; the server's silence isn't an exception.
            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
            var waiting = Locator(options).FindAsync(module, ignoreFilter: false, cancel.Token);
            await arrived.Task.WaitAsync(TimeSpan.FromSeconds(60), Token);
            await cancel.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);

            var locator = Locator(options);
            locator.ServerTimeout = TimeSpan.FromMilliseconds(300);
            var result = await locator.FindAsync(module, ignoreFilter: false, Token);
            var again = await locator.FindAsync(module, ignoreFilter: false, Token);

            Assert.Equal(new SymbolResult(null, $"{Host(server)} didn't answer within 0.3 s."), result);
            Assert.Equal(new SymbolResult(null, $"Skipped {Host(server)}: it didn't answer earlier in this session."), again);
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [Fact]
    public async Task Redirects_are_followed_but_not_to_plain_http_elsewhere()
    {
        var pdb = File.ReadAllBytes(Pdb);
        var location = "/blobs/debuggee?sig=abc";
        await using var server = new MockHttpServer(request => request.Path switch
        {
            "/blobs/debuggee" => new MockHttpResponse(200, pdb, "application/octet-stream"),
            _ => new MockHttpResponse(302, [], "text/plain") { Headers = new Dictionary<string, string> { ["Location"] = location } },
        });
        var options = new SymbolOptions { Servers = [server.BaseUrl + "/symbols"], CacheFolder = Cache };

        var result = await Locator(options).FindAsync(Module(), ignoreFilter: false, Token);

        Assert.Equal(CachedPdb, result.PdbPath);
        Assert.Equal(["/symbols/" + Key, "/blobs/debuggee"], server.Requests.Select(r => r.Path));
        Assert.Equal("sig=abc", server.Requests.Last().Query);

        File.Delete(CachedPdb);
        location = "http://symbols.contoso.example/blobs/debuggee";
        var refused = await Locator(options).FindAsync(Module(), ignoreFilter: false, Token);

        Assert.Equal(new SymbolResult(null, $"{Host(server)} redirects to an address that isn't followed (302)."), refused);
        Assert.Equal(3, server.Requests.Count);
    }

    [Fact]
    public async Task A_windows_pdb_is_taken_by_its_key_only()
    {
        var guid = Guid.NewGuid();
        var module = WindowsModule(guid, age: 2);
        var key = $"legacy.pdb/{guid:N}2/legacy.pdb";
        var folder = Directory.CreateDirectory(Path.Combine(_root, "symbols")).FullName;
        var loose = Path.Combine(folder, "Legacy.pdb");
        File.WriteAllBytes(loose, s_windowsPdb);

        // A loose file's name says nothing about its build, and its content isn't read here.
        var skipped = await Locator(new SymbolOptions { SearchPaths = [folder] }).FindAsync(module, ignoreFilter: false, Token);
        Assert.Equal(new SymbolResult(null, $"Skipped {loose}: a Windows PDB outside a symbol store can't be checked against the module."), skipped);

        var stored = Store(folder, key, s_windowsPdb);
        var found = await Locator(new SymbolOptions { SearchPaths = [folder] }).FindAsync(module, ignoreFilter: false, Token);
        Assert.Equal(new SymbolResult(stored, $"Symbols loaded from {folder}."), found);

        // From a server: by the key, without the portable PDB's checksum header; an error page isn't kept as a PDB.
        var answer = "<html>Sign in</html>"u8.ToArray();
        await using var server = new MockHttpServer(request => request.Path == "/" + key ? new MockHttpResponse(200, answer, "application/octet-stream") : MockHttpResponse.NotFound());
        var options = new SymbolOptions { Servers = [server.BaseUrl], CacheFolder = Cache };
        var page = await Locator(options).FindAsync(module, ignoreFilter: false, Token);
        Assert.Equal(new SymbolResult(null, $"Skipped the file {Host(server)} sent: it isn't a Windows PDB."), page);
        Assert.Empty(Directory.GetFiles(Cache, "*", SearchOption.AllDirectories));

        answer = s_windowsPdb;
        var downloaded = await Locator(options).FindAsync(module, ignoreFilter: false, Token);
        Assert.Equal(Path.Combine(Cache, key.Replace('/', Path.DirectorySeparatorChar)), downloaded.PdbPath);
        Assert.All(server.Requests, r => Assert.Null(r.Header("SymbolChecksum")));
    }

    private SymbolLocator Locator(SymbolOptions options, List<string>? log = null)
    {
        var locator = new SymbolLocator(options, _http, log is null ? null : line =>
        {
            lock (log)
            {
                log.Add(line);
            }
        });
        _locators.Add(locator);
        return locator;
    }

    /// <summary>Debuggee.dll in a folder of its own, away from its PDB.</summary>
    private string Module()
    {
        var module = Path.Combine(Directory.CreateDirectory(Path.Combine(_root, "app")).FullName, "Debuggee.dll");
        File.Copy(fixture.Dll, module, overwrite: true);
        return module;
    }

    /// <summary>A symbol server under <c>/symbols</c> that has Debuggee.pdb's key (with <paramref name="pdb"/> as its file), or nothing.</summary>
    private MockHttpServer SymbolServer(byte[]? pdb) =>
        new(request => pdb is not null && request.Path == "/symbols/" + Key ? new MockHttpResponse(200, pdb, "application/octet-stream") : MockHttpResponse.NotFound());

    private static string Host(MockHttpServer server) => new Uri(server.BaseUrl).Authority;

    private static string Store(string folder, string key, byte[] content)
    {
        var path = Path.Combine(folder, key.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
        return path;
    }

    /// <summary>A portable PDB's id, read from the PDB: the GUID and the 20 bytes it is the start of.</summary>
    private static (Guid Guid, byte[] Bytes) PdbId(string pdb)
    {
        using var provider = MetadataReaderProvider.FromPortablePdbStream(File.OpenRead(pdb));
        byte[] id = [.. provider.GetMetadataReader().DebugMetadataHeader!.Id];
        return (new Guid(id.AsSpan(0, 16)), id);
    }

    /// <summary>Debuggee.pdb as another build would have written it: the same content under another id.</summary>
    private byte[] OtherBuild()
    {
        var pdb = File.ReadAllBytes(Pdb);
        var at = pdb.AsSpan().IndexOf(PdbId(Pdb).Bytes);
        Assert.True(at > 0, "The PDB's id wasn't found in its file.");
        pdb[at] ^= 0xFF;
        return pdb;
    }

    /// <summary>A module that names a Windows PDB (built on another machine, as its path shows) with this GUID and age.</summary>
    private string WindowsModule(Guid guid, int age)
    {
        var debug = new DebugDirectoryBuilder();
        debug.AddCodeViewEntry(@"C:\build\obj\Release\Legacy.pdb", new BlobContentId(guid, 0x5F000000), portablePdbVersion: 0, age);
        var module = Path.Combine(Directory.CreateDirectory(Path.Combine(_root, "legacy")).FullName, "Legacy.dll");
        WriteModule(module, debug);
        return module;
    }

    private static void WriteModule(string path, DebugDirectoryBuilder? debug)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var metadata = new MetadataBuilder();
        metadata.AddModule(0, metadata.GetOrAddString(name + ".dll"), metadata.GetOrAddGuid(Guid.NewGuid()), default, default);
        metadata.AddAssembly(metadata.GetOrAddString(name), new Version(1, 0, 0, 0), default, default, 0, AssemblyHashAlgorithm.None);
        var pe = new ManagedPEBuilder(new PEHeaderBuilder(imageCharacteristics: Characteristics.Dll), new MetadataRootBuilder(metadata), ilStream: new BlobBuilder(), debugDirectoryBuilder: debug);
        var image = new BlobBuilder();
        pe.Serialize(image);
        File.WriteAllBytes(path, image.ToArray());
    }

    /// <summary>Answers in place of the network, and records what was asked.</summary>
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public ConcurrentQueue<Uri> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Enqueue(request.RequestUri!);
            return Task.FromResult(answer(request));
        }
    }
}
