using Codalus.LanguageServices.Client;
using Codalus.LanguageServices.Protocol;
using Codalus.Projects.Protocol;
using Codalus.Rpc;
using Codalus.Workbench.Languages;
using Codalus.Workbench.Projects;

namespace Codalus.LanguageServices.Host.Tests;

/// <summary>
/// The real build host and language host on a copy of samples/LanguageFeatures, wired as the shell wires them: design-
/// time builds flow through <see cref="SolutionService"/> into <see cref="LanguageService"/>.
/// </summary>
public class LanguageFixture : IAsyncLifetime
{
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(3);

    private readonly string _root = Path.Combine(Codalus.Platform.RealPath.Resolve(Path.GetTempPath()), "codalus-language-" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, (string Text, int Version)> _texts = new(StringComparer.OrdinalIgnoreCase);

    public string Fixture { get; private set; } = null!;

    public SolutionService Solutions { get; private set; } = null!;

    public LanguageService Languages { get; private set; } = null!;

    public LanguageHostClient Client => Languages.RunningClient("csharp") ?? throw new InvalidOperationException("The C# service is not running.");

    public List<string> Log { get; } = [];

    protected virtual bool Trusted => true;

    /// <summary>The sample under samples/ and its solution file.</summary>
    protected virtual (string Sample, string Solution, int Projects) Sample => ("LanguageFeatures", "LanguageFeatures.slnx", 2);

    public string File(params string[] relative) => Path.Combine([Fixture, .. relative]);

    public async ValueTask InitializeAsync()
    {
        Assert.True(System.IO.File.Exists(RepoPaths.HostAssembly), $"Language host not built: {RepoPaths.HostAssembly}");
        Assert.True(System.IO.File.Exists(RepoPaths.BuildHostAssembly), $"Build host not built: {RepoPaths.BuildHostAssembly}");
        Fixture = RepoPaths.CopyFixture(Sample.Sample, _root);
        var policy = new SupervisionPolicy { InitialBackoff = TimeSpan.FromMilliseconds(50) };
        Solutions = new SolutionService(() => RepoPaths.BuildHostAssembly, policy);
        var registry = new LanguageServiceRegistry();
        registry.Register(new LanguageServiceDescriptor("csharp", "C#", [".cs"], "C#", () => ProcessLaunchInfo.FromPath(RepoPaths.HostAssembly)) { Languages = ["csharp", "razor", "axaml"] });
        Languages = new LanguageService(registry, Solutions, policy);
        Solutions.Log += (_, line) => Record("[build] " + line);
        Languages.Log += (_, line) => Record("[lang] " + line);

        using var cts = new CancellationTokenSource(Timeout);
        await Solutions.OpenAsync(File(Sample.Solution), Trusted, restore: Trusted, cts.Token);
        if (Trusted)
        {
            var projects = Sample.Projects;
            await WaitUntilAsync(() => Solutions.CompilerInputs.Count(i => i.Value.State == CompilerInputStates.Ok) == projects
                                       && Languages.Status("csharp") is { State: WorkspaceStates.Ready } status && status.Projects == projects);
            await ReadyAsync();
        }
        else
        {
            await Languages.ClientAsync("csharp");
        }
    }

    /// <summary>More to wait for once the projects are loaded (a fixture's own conditions).</summary>
    protected virtual Task ReadyAsync() => Task.CompletedTask;

    private void Record(string line)
    {
        lock (Log)
        {
            Log.Add(line);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await Languages.DisposeAsync();
        await Solutions.DisposeAsync();
        GC.SuppressFinalize(this);
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    public static async Task WaitUntilAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        var started = System.Diagnostics.Stopwatch.StartNew(); // monotonic: immune to the machine sleeping
        while (!condition())
        {
            Assert.True(started.Elapsed < (timeout ?? Timeout), "Timed out.");
            await Task.Delay(50);
        }
    }

    // ------------------------------------------------------------------ documents

    public async Task<string> OpenAsync(string path)
    {
        var languageId = path.EndsWith(".razor", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase) ? "razor"
            : path.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase) ? "axaml"
            : "csharp";
        var text = await System.IO.File.ReadAllTextAsync(path);
        lock (_texts)
        {
            _texts[path] = (text, 1);
        }

        await Languages.OpenAsync(new LanguageDocument(path, languageId, () => Text(path)));
        return text;
    }

    public (string Text, int Version) Text(string path)
    {
        lock (_texts)
        {
            return _texts[path];
        }
    }

    /// <summary>Replaces <paramref name="oldText"/> with <paramref name="newText"/> as an editor would (one incremental change).</summary>
    public async Task EditAsync(string path, string oldText, string newText)
    {
        var (text, version) = Text(path);
        var offset = text.IndexOf(oldText, StringComparison.Ordinal);
        Assert.True(offset >= 0, $"'{oldText}' not found");
        var range = new Protocol.Range(PositionOf(text, offset), PositionOf(text, offset + oldText.Length));
        text = text[..offset] + newText + text[(offset + oldText.Length)..];
        lock (_texts)
        {
            _texts[path] = (text, version + 1);
        }

        await Languages.ChangeAsync(path, version + 1, [new TextDocumentContentChangeEvent(range, newText)]);
    }

    public static Position PositionOf(string text, int offset)
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

    /// <summary>The position of <paramref name="marker"/> in the file's current text, plus <paramref name="delta"/> characters.</summary>
    public Position At(string path, string marker, int delta = 0)
    {
        var text = Text(path).Text;
        var offset = text.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(offset >= 0, $"'{marker}' not found");
        return PositionOf(text, offset + delta);
    }
}

/// <summary>The same fixture, opened without trust: no design-time builds, every file in Miscellaneous Files.</summary>
public sealed class UntrustedLanguageFixture : LanguageFixture
{
    protected override bool Trusted => false;
}
