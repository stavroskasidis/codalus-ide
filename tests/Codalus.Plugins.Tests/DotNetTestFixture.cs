using System.Diagnostics;
using Codalus.Debugging;
using Codalus.Platform;
using Codalus.Rpc;
using Codalus.Workbench;
using Codalus.Workbench.Build;
using Codalus.Workbench.Debugging;
using Codalus.Workbench.Languages;
using Codalus.Workbench.Plugins;
using Codalus.Workbench.Projects;
using Codalus.Workbench.Testing;

namespace Codalus.Plugins.Tests;

/// <summary>
/// The .NET test plugin end to end (ADR 0010): a copy of samples/Testing, built once, opened in the real build host and
/// language host, with the plugin in a real dedicated plugin host feeding a <see cref="TestService"/>, as the shell
/// wires them.
/// </summary>
public sealed class DotNetTestFixture : IAsyncLifetime
{
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(4);

    private readonly string _root = Path.Combine(RealPath.Resolve(Path.GetTempPath()), "codalus-dotnettest-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _log = [];

    public string Folder => Path.Combine(_root, "Testing");

    public WorkbenchServices Workbench { get; private set; } = null!;

    public SolutionService Solutions { get; private set; } = null!;

    public LanguageService Languages { get; private set; } = null!;

    public BuildService Builds { get; private set; } = null!;

    public DebugService Debug { get; private set; } = null!;

    public TestService Tests { get; private set; } = null!;

    public PluginManager Plugins { get; private set; } = null!;

    private WorkbenchProposedApi _proposed = null!;

    internal static string Configuration => PluginHostIntegrationTests.Configuration;

    private static string Bin(string project, string assembly) =>
        Path.Combine(PluginHostIntegrationTests.RepoRoot, "src", project, "bin", Configuration, "net10.0", assembly);

    public static string PluginDir => Path.Combine(PluginHostIntegrationTests.RepoRoot, "src", "Plugins", "Codalus.Plugins.DotNetTest", "bin", Configuration, "net10.0");

    public static string? Netcoredbg => DebugAdapterLocator.Resolve();

    public string Log
    {
        get
        {
            lock (_log)
            {
                return string.Join('\n', _log);
            }
        }
    }

    public void Record(string line)
    {
        lock (_log)
        {
            _log.Add(line);
        }
    }

    public string File(params string[] relative) => Path.Combine([Folder, .. relative]);

    public async ValueTask InitializeAsync()
    {
        using var timeout = new CancellationTokenSource(Timeout);
        Copy(Path.Combine(PluginHostIntegrationTests.RepoRoot, "samples", "Testing"), Folder);
        System.IO.File.Copy(Path.Combine(PluginHostIntegrationTests.RepoRoot, "samples", "Directory.Packages.props"), Path.Combine(_root, "Directory.Packages.props"));
        System.IO.File.WriteAllText(Path.Combine(_root, "Directory.Build.props"), "<Project />");
        Codalus.Tests.FixtureSdk.Pin(_root);
        await DotnetAsync(timeout.Token, "build", Path.Combine(Folder, "Testing.slnx"), "-nologo", "-v", "quiet");

        var paths = AppPaths.Resolve(PlatformInfo.Current, _ => null, Path.Combine(_root, ".userdata"));
        paths.EnsureCreated();
        Workbench = new WorkbenchServices(paths, PlatformInfo.Current, watchFiles: false);
        var policy = new SupervisionPolicy { InitialBackoff = TimeSpan.FromMilliseconds(50) };
        Solutions = new SolutionService(() => Bin("Codalus.Projects.Host", "codalus-buildhost.dll"), policy);
        var registry = new LanguageServiceRegistry();
        registry.Register(new LanguageServiceDescriptor("csharp", "C#", [".cs"], "C#", () => ProcessLaunchInfo.FromPath(Bin("Codalus.LanguageServices.Host", "codalus-lshost.dll"))));
        Languages = new LanguageService(registry, Solutions, policy);
        Builds = new BuildService(Solutions, Workbench.Output);
        Debug = new DebugService(() => Netcoredbg);
        Tests = new TestService();
        _proposed = new WorkbenchProposedApi(Solutions, Builds, Debug, Languages);
        Plugins = new PluginManager(Workbench, _ => ProcessLaunchInfo.FromPath(Bin("Codalus.Plugins.Host", "codalus-pluginhost.dll")), "test", policy)
        {
            ProposedApi = _proposed,
            TestSink = Tests,
        };
        Solutions.Log += (_, line) => Record("[build host] " + line);
        Builds.Channel.Appended += (_, text) => Record("[build] " + text?.TrimEnd());
        Workbench.Output.GetOrCreate("codalus.plugins", "Plugins").Appended += (_, text) => Record("[plugins] " + text?.TrimEnd());
        Plugins.AddPlugins(PluginDiscovery.Scan([(PluginDir, true)], PlatformInfo.Current));

        await Solutions.OpenAsync(File("Testing.slnx"), trusted: true, restore: false, timeout.Token);
        await WaitUntilAsync(() => Solutions.Projects.Count == 8 && Solutions.Projects.Values.All(p => p.State != "loading"), timeout.Token);
        await Plugins.ActivateByEventAsync("onTestProjects", timeout.Token);
        await WaitUntilAsync(() => Tests.Read(roots => roots.Count) == 7 && Tests.Read(roots => roots.SelectMany(r => r.SelfAndDescendants()).Count(n => n.Label == "Divides")) == 6, timeout.Token);
    }

    public async ValueTask DisposeAsync()
    {
        await Plugins.DisposeAsync();
        _proposed.Dispose();
        await Debug.DisposeAsync();
        await Languages.DisposeAsync();
        await Solutions.DisposeAsync();
        Tests.Dispose();
        Workbench.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>A node by the labels on its path from the project root (for example "XunitV2Tests", "XunitV2Tests", "CalculatorTests", "Adds").</summary>
    public TestNode Node(params string[] labels) => Tests.Read(roots =>
    {
        IReadOnlyList<TestNode> level = roots;
        TestNode? node = null;
        foreach (var label in labels)
        {
            node = level.FirstOrDefault(n => n.Label == label) ?? throw new InvalidOperationException($"No '{label}' under {node?.FullLabel ?? "the roots"}: {string.Join(", ", level.Select(n => n.Label))}");
            level = node.Children;
        }

        return node!;
    });

    public async Task WaitUntilAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        while (!condition())
        {
            try
            {
                await Task.Delay(50, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException("Timed out. Log:\n" + Log);
            }
        }
    }

    private static async Task DotnetAsync(CancellationToken cancellationToken, params string[] arguments)
    {
        var start = new ProcessStartInfo("dotnet", arguments) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        Assert.True(process.ExitCode == 0, await output);
    }

    private static void Copy(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            System.IO.File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        }

        foreach (var directory in Directory.EnumerateDirectories(source).Where(d => Path.GetFileName(d) is not ("bin" or "obj")))
        {
            Copy(directory, Path.Combine(target, Path.GetFileName(directory)));
        }
    }
}
