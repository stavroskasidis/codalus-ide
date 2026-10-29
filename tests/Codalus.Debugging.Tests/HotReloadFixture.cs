using System.Diagnostics;
using System.Reflection;
using Codalus.LanguageServices.Client;
using Codalus.LanguageServices.Protocol;
using Codalus.Projects.Protocol;
using Codalus.Rpc;
using Codalus.Workbench.Languages;
using Codalus.Workbench.Projects;

namespace Codalus.Debugging.Tests;

/// <summary>
/// samples/Debugging/HotReload.slnx (M55), copied and built, with the real build host and language host loaded on it
/// as the shell wires them: the language host's Hot Reload sessions give the deltas the Codalus debugger applies. The
/// C# and Visual Basic programs' sources are open in the language host; tests edit them there and put them back with
/// <see cref="RestoreAsync"/>.
/// </summary>
public sealed class HotReloadFixture : IAsyncLifetime
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromMinutes(3);

    private readonly string _root = Path.Combine(Codalus.Platform.RealPath.Resolve(Path.GetTempPath()), "codalus-hotreload-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _log = [];

    public HotReloadFixture()
    {
        CSharp = new Source(this, Path.Combine(_root, "Debugging", "HotReload"), "HotReload", ".csproj", "Program.cs", "csharp");
        VisualBasic = new Source(this, Path.Combine(_root, "Debugging", "HotReloadVb"), "HotReloadVb", ".vbproj", "Program.vb", "vb");
    }

    public Source CSharp { get; }

    public Source VisualBasic { get; }

    public SolutionService Solutions { get; private set; } = null!;

    public LanguageService Languages { get; private set; } = null!;

    public LanguageHostClient Client => Languages.RunningClient("csharp") ?? throw new InvalidOperationException("The C# service is not running.");

    /// <summary>A program of the fixture: its project, its build, and its source as the language host has it.</summary>
    public sealed class Source(HotReloadFixture fixture, string folder, string name, string extension, string file, string languageId)
    {
        private string _text = string.Empty;
        private int _version = 1;

        public string Folder => folder;

        public string Project => System.IO.Path.Combine(folder, name + extension);

        public string Dll => System.IO.Path.Combine(folder, "bin", "Debug", "net11.0", name + ".dll");

        public string Path => System.IO.Path.Combine(folder, file);

        public string Original { get; private set; } = string.Empty;

        /// <summary>The 1-based line of the <c>[name]</c> marker in the edited text.</summary>
        public int Line(string marker) =>
            Array.FindIndex(_text.Split('\n'), l => l.Contains($"[{marker}]", StringComparison.Ordinal)) + 1 is > 0 and var line
                ? line
                : throw new InvalidOperationException($"No [{marker}] marker.");

        public async Task OpenAsync()
        {
            Original = _text = await File.ReadAllTextAsync(Path);
            await fixture.Languages.OpenAsync(new LanguageDocument(Path, languageId, () => (_text, _version)));
        }

        /// <summary>Replaces <paramref name="oldText"/> with <paramref name="newText"/>, as an editor would.</summary>
        public async Task EditAsync(string oldText, string newText)
        {
            var offset = _text.IndexOf(oldText, StringComparison.Ordinal);
            Assert.True(offset >= 0, $"'{oldText}' not found");
            await SetAsync(_text[..offset] + newText + _text[(offset + oldText.Length)..]);
        }

        /// <summary>The source as it was built (the next test's baseline).</summary>
        public Task RestoreAsync() => _text == Original ? Task.CompletedTask : SetAsync(Original);

        private async Task SetAsync(string text)
        {
            var end = Position(_text, _text.Length);
            _text = text;
            _version++;
            await fixture.Languages.ChangeAsync(Path, _version, [new TextDocumentContentChangeEvent(new LanguageServices.Protocol.Range(new Position(0, 0), end), text)]);
        }
    }

    private static string Host(string project, string assembly) =>
        Path.Combine(DebuggeeFixture.RepoRoot, "src", project, "bin", Metadata("CodalusConfiguration"), Codalus.Tests.RepoBuild.TargetFramework, assembly);

    private static string Metadata(string key) =>
        typeof(HotReloadFixture).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == key).Value!;

    public async ValueTask InitializeAsync()
    {
        var samples = Path.Combine(DebuggeeFixture.RepoRoot, "samples");
        Copy(Path.Combine(samples, "Debugging"), Path.Combine(_root, "Debugging"));
        File.Copy(Path.Combine(samples, "Directory.Build.props"), Path.Combine(_root, "Directory.Build.props"));
        Codalus.Tests.FixtureSdk.Pin(_root);
        File.Copy(Path.Combine(samples, "Directory.Packages.props"), Path.Combine(_root, "Directory.Packages.props"));
        var solution = Path.Combine(_root, "Debugging", "HotReload.slnx");
        using (var build = Process.Start(new ProcessStartInfo("dotnet", ["build", solution, "-nologo", "-v", "quiet"]) { RedirectStandardOutput = true, UseShellExecute = false })!)
        {
            var output = build.StandardOutput.ReadToEndAsync();
            await build.WaitForExitAsync();
            Assert.True(build.ExitCode == 0, await output);
        }

        var policy = new SupervisionPolicy { InitialBackoff = TimeSpan.FromMilliseconds(50) };
        Solutions = new SolutionService(() => Host("Codalus.Projects.Host", "codalus-buildhost.dll"), policy);
        var registry = new LanguageServiceRegistry();
        registry.Register(new LanguageServiceDescriptor("csharp", "C#", [".cs", ".vb"], "C#", () => ProcessLaunchInfo.FromPath(Host("Codalus.LanguageServices.Host", "codalus-lshost.dll"))) { Languages = ["csharp", "vb"], MoreProjectLanguages = ["VB"] });
        Languages = new LanguageService(registry, Solutions, policy);
        Solutions.Log += (_, line) => Record("[build] " + line);
        Languages.Log += (_, line) => Record("[lang] " + line);

        using var cts = new CancellationTokenSource(s_timeout);
        await Solutions.OpenAsync(solution, true, restore: true, cts.Token);
        var started = Stopwatch.StartNew();
        while (!(Solutions.CompilerInputs.Count(i => i.Value.State == CompilerInputStates.Ok) == 2 && Languages.Status("csharp") is { State: WorkspaceStates.Ready, Projects: 2 }))
        {
            Assert.True(started.Elapsed < s_timeout, "HotReload.slnx didn't load: " + string.Join('\n', Log().TakeLast(40)));
            await Task.Delay(50);
        }

        await CSharp.OpenAsync();
        await VisualBasic.OpenAsync();
    }

    /// <summary>Both programs' sources as they were built.</summary>
    public async Task RestoreAsync()
    {
        await CSharp.RestoreAsync();
        await VisualBasic.RestoreAsync();
    }

    private static Position Position(string text, int offset)
    {
        var line = 0;
        var lineStart = 0;
        for (var i = 0; i < offset; i++)
        {
            if (text[i] == '\n')
            {
                line++;
                lineStart = i + 1;
            }
        }

        return new Position(line, offset - lineStart);
    }

    public List<string> Log()
    {
        lock (_log)
        {
            return [.. _log];
        }
    }

    private void Record(string line)
    {
        lock (_log)
        {
            _log.Add(line);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await Languages.DisposeAsync();
        await Solutions.DisposeAsync();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void Copy(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        }

        foreach (var directory in Directory.EnumerateDirectories(source).Where(d => Path.GetFileName(d) is not ("bin" or "obj")))
        {
            Copy(directory, Path.Combine(target, Path.GetFileName(directory)));
        }
    }
}
