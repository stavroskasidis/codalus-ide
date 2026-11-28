namespace Codalus.Rpc.Tests;

/// <summary>
/// The <c>dotnet</c> the self-contained remote server runs its hosts on (ADR 0050, 0080), on fake .NET roots (folders
/// with a <c>dotnet</c> file and <c>shared/Microsoft.NETCore.App/&lt;version&gt;</c> with their deps.json). The machine's
/// own roots are never looked at: only files under the test's folder exist. Nothing real is installed.
/// </summary>
public sealed class DotnetMuxerLookupTests : IDisposable
{
    private const string Rc2 = "11.0.0-rc.2.26504.105";
    private const string Rc1 = "11.0.0-rc.1.26425.128";

    private readonly string _dir = Directory.CreateTempSubdirectory("codalus-muxer-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static FrameworkReference NetCore(string version, RollForwardPolicy policy = RollForwardPolicy.Major) =>
        FrameworkReference.Create(FrameworkReference.NetCoreApp, FrameworkVersion.Parse(version), policy);

    /// <summary>A root with a <c>dotnet</c> and these runtimes; its muxer's path.</summary>
    private string FakeRoot(string name, params string[] versions)
    {
        var root = Path.Combine(_dir, name);
        foreach (var version in versions)
        {
            var folder = Path.Combine(root, "shared", FrameworkReference.NetCoreApp, version);
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, FrameworkReference.NetCoreApp + ".deps.json"), "{}");
        }

        Directory.CreateDirectory(root);
        var muxer = Path.Combine(root, "dotnet");
        File.WriteAllText(muxer, "never started");
        return muxer;
    }

    /// <summary>A Linux machine whose only files are the test's, with <paramref name="links"/> as its symbolic links.</summary>
    private DotnetMuxerLookup Machine(Dictionary<string, string?> environment, string? onPath = null, Dictionary<string, string>? links = null) => new()
    {
        IsWindows = false,
        Environment = name => environment.GetValueOrDefault(name),
        Home = Path.Combine(_dir, "home"),
        OnPath = () => onPath,
        FileExists = path => (path.StartsWith(_dir, StringComparison.Ordinal) && File.Exists(path)) || (links?.ContainsKey(path) ?? false),
        RealPath = path => links is not null && links.TryGetValue(path, out var target) ? target : path,
    };

    [Fact]
    public void The_paths_dotnet_with_the_runtime_wins_over_an_older_dotnet_root_and_home()
    {
        // The reported machine: DOTNET_ROOT has .NET 10, ~/.dotnet an earlier release candidate, and the PATH's dotnet
        // (a link, as /usr/bin/dotnet is) the runtime the hosts ask for. Before, DOTNET_ROOT won and every host exited.
        var dotnetRoot = FakeRoot("dotnet-root", "10.0.9");
        Directory.CreateDirectory(Path.Combine(_dir, "home"));
        var home = FakeRoot(Path.Combine("home", ".dotnet"), Rc1);
        var system = FakeRoot("system", "10.0.9", Rc2);
        var link = Path.Combine(_dir, "bin", "dotnet");
        var lookup = Machine(new Dictionary<string, string?> { ["DOTNET_ROOT"] = Path.GetDirectoryName(dotnetRoot) }, onPath: link, links: new Dictionary<string, string> { [link] = system });

        Assert.Equal([dotnetRoot, home, link], lookup.Candidates().Where(lookup.FileExists));
        Assert.Equal(link, lookup.Find([NetCore(Rc2)])); // the PATH's own path; the muxer resolves its link itself
        // A runtime both have: the first, as before.
        Assert.Equal(dotnetRoot, lookup.Find([NetCore("10.0.0")]));
    }

    [Fact]
    public void No_root_with_the_runtime_or_nothing_to_check_falls_back_to_the_first_that_exists()
    {
        var dotnetRoot = FakeRoot("dotnet-root", "10.0.9");
        var other = FakeRoot("other", Rc1);
        var lookup = Machine(new Dictionary<string, string?> { ["DOTNET_ROOT"] = Path.GetDirectoryName(dotnetRoot) }, onPath: other);

        Assert.Equal(dotnetRoot, lookup.Find([NetCore(Rc2)])); // starting a host then names what's missing
        Assert.Equal(dotnetRoot, lookup.Find([]));
        Assert.Null(Machine(new Dictionary<string, string?>()).Find([NetCore(Rc2)]));
    }

    [Fact]
    public void Candidates_keep_their_order_and_missing_ones_are_passed_over()
    {
        // No DOTNET_ROOT: ~/.dotnet first, then the PATH's.
        Directory.CreateDirectory(Path.Combine(_dir, "home"));
        var home = FakeRoot(Path.Combine("home", ".dotnet"), Rc2);
        var onPath = FakeRoot("path", Rc2);
        var lookup = Machine(new Dictionary<string, string?> { ["DOTNET_ROOT"] = Path.Combine(_dir, "missing") }, onPath: onPath);
        Assert.Equal(home, lookup.Find([NetCore(Rc2)]));
        Assert.Equal(
            [Path.Combine(_dir, "missing", "dotnet"), home, onPath, "/usr/share/dotnet/dotnet", "/usr/lib/dotnet/dotnet", "/usr/local/share/dotnet/dotnet", "/opt/homebrew/opt/dotnet/libexec/dotnet"],
            lookup.Candidates());

        // Every framework must resolve under the one root; the environment's disabled versions don't count.
        var lookupDisabled = Machine(new Dictionary<string, string?> { ["DOTNET_DISABLE_RUNTIME_VERSIONS"] = Rc2 }, onPath: onPath);
        Assert.Equal(home, lookupDisabled.Find([NetCore(Rc2)])); // nothing resolves: the first that exists
        var newer = FakeRoot("newer", Rc2, "11.0.0");
        Assert.Equal(newer, Machine(new Dictionary<string, string?> { ["DOTNET_DISABLE_RUNTIME_VERSIONS"] = Rc2 }, onPath: newer).Find([NetCore(Rc2)]));

        var windows = new DotnetMuxerLookup { IsWindows = true, Environment = n => n == "ProgramFiles" ? @"D:\Programs" : null, Home = @"C:\Users\u", OnPath = () => null };
        Assert.Equal([Path.Combine(@"C:\Users\u", ".dotnet", "dotnet.exe"), Path.Combine(@"D:\Programs", "dotnet", "dotnet.exe")], windows.Candidates());
    }

    [Fact]
    public void The_hosts_frameworks_come_from_the_bundles_runtimeconfig_files()
    {
        var bundle = Path.Combine(_dir, "bundle");
        Directory.CreateDirectory(Path.Combine(bundle, "server"));
        File.WriteAllText(Path.Combine(bundle, "server", "codalus-server.runtimeconfig.json"), """{ "runtimeOptions": { "includedFrameworks": [ { "name": "Microsoft.NETCore.App", "version": "11.0.0" } ] } }""");
        foreach (var host in new[] { "buildhost", "lshost" })
        {
            Directory.CreateDirectory(Path.Combine(bundle, host));
            File.WriteAllText(Path.Combine(bundle, host, $"codalus-{host}.runtimeconfig.json"), $$"""
                { "runtimeOptions": { "rollForward": "Major", "framework": { "name": "Microsoft.NETCore.App", "version": "{{Rc2}}" } } }
                """);
        }

        var fallback = NetCore("10.0.0");
        var references = DotnetMuxerLookup.HostFrameworks(bundle, _ => null, fallback);
        Assert.Equal([NetCore(Rc2)], references); // the self-contained server's own file names no framework; duplicates merged
        // The environment's roll-forward, as the hosts would read it.
        Assert.Equal(RollForwardPolicy.Disable, Assert.Single(DotnetMuxerLookup.HostFrameworks(bundle, n => n == RuntimeConfig.RollForwardVariable ? "Disable" : null, fallback)).RollForward);

        // No hosts (development) or no bundle: the server's own runtime.
        Assert.Equal([fallback], DotnetMuxerLookup.HostFrameworks(Path.Combine(bundle, "server"), _ => null, fallback));
        Assert.Equal([fallback], DotnetMuxerLookup.HostFrameworks(Path.Combine(_dir, "missing"), _ => null, fallback));
        Assert.Empty(DotnetMuxerLookup.HostFrameworks(null, _ => null, null));
    }

    [Fact]
    public void The_servers_own_runtime_is_asked_for_with_roll_forward_major()
    {
        var own = DotnetMuxerLookup.OwnRuntime();
        Assert.Equal((FrameworkReference.NetCoreApp, RollForwardPolicy.Major), (own.Name, own.RollForward));
        Assert.Equal((Environment.Version.Major, Environment.Version.Minor, Environment.Version.Build), (own.Version.Major, own.Version.Minor, own.Version.Patch));
        // This test's own runtime resolves for it.
        Assert.NotNull(FrameworkResolver.Resolve(own, [FrameworkVersion.Parse(System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription.Split(' ')[^1])]));
    }
}
