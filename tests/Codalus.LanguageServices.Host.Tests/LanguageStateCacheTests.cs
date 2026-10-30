using System.Collections.Concurrent;
using Codalus.LanguageServices.Client;
using Codalus.LanguageServices.Protocol;
using Codalus.Rpc;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Diagnostic = Codalus.LanguageServices.Protocol.Diagnostic;

namespace Codalus.LanguageServices.Host.Tests;

/// <summary>
/// What the language host keeps between sessions (M34c, ADR 0049): a second session takes an unchanged project's
/// diagnostics without compiling it and compiles the projects that reference it against its reference assembly; a
/// changed file compiles its project again. And CS4014 the build wouldn't report is left out.
/// </summary>
public sealed class LanguageStateCacheTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Codalus.Platform.RealPath.Resolve(Path.GetTempPath()), "codalus-state-" + Guid.NewGuid().ToString("N"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string LibSource = """
        namespace Lib;
        public class Context
        {
            public async System.Threading.Tasks.Task RefreshAsync() => await System.Threading.Tasks.Task.Yield();
            internal static int Hidden() => 1;
        }
        """;

    private const string LibWarning = "namespace Lib; static class Warnings { static void M() { int unused = 1; } }";

    private const string AppSource = """
        namespace App;
        public class Page
        {
            private readonly Lib.Context _context = new();
            public void OnRender() { _context.RefreshAsync(); }
            public async System.Threading.Tasks.Task OnRenderAsync() { _context.RefreshAsync(); await System.Threading.Tasks.Task.Yield(); }
        }
        """;

    public ValueTask InitializeAsync()
    {
        Write("Lib/Context.cs", LibSource);
        Write("Lib/Warnings.cs", LibWarning);
        Write("App/Page.cs", AppSource);
        Write("App/Closed.cs", "namespace App; static class Closed { static void M() { int alsoUnused = 2; } }");
        Directory.CreateDirectory(Path.Combine(_root, "Lib", "obj"));
        Directory.CreateDirectory(Path.Combine(_root, "App", "obj"));
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task A_second_session_keeps_unchanged_projects_and_compiles_changed_ones()
    {
        var page = Path.Combine(_root, "App", "Page.cs");

        // First session: everything compiles; the state is written next to each project's compiler output.
        await using (var first = await SessionAsync(open: page))
        {
            await first.ReadyAsync();
            await LanguageFixture.WaitUntilAsync(() => first.Has(page, "CS4014"), TimeSpan.FromSeconds(60));
            Assert.Single(first.For(page), d => d.Code == "CS4014"); // in the async method only: the build reports it there too
            Assert.Contains(first.For(Path.Combine(_root, "App", "Closed.cs")), d => d.Code == "CS0219");
            Assert.Contains(first.For(Path.Combine(_root, "Lib", "Warnings.cs")), d => d.Code == "CS0219");
        }

        Assert.True(File.Exists(Path.Combine(_root, "Lib", "obj", LanguageStateCache.StateFile)));
        Assert.True(File.Exists(Path.Combine(_root, "Lib", "obj", LanguageStateCache.ReferenceFile))); // App references it
        Assert.True(File.Exists(Path.Combine(_root, "App", "obj", LanguageStateCache.StateFile)));
        Assert.False(File.Exists(Path.Combine(_root, "App", "obj", LanguageStateCache.ReferenceFile))); // nothing references App

        // Second session, nothing changed: no project compiles, the diagnostics are the same, and the open document's
        // project compiles against Lib's reference assembly (internals included).
        await using (var second = await SessionAsync(open: page))
        {
            await second.ReadyAsync();
            await LanguageFixture.WaitUntilAsync(() => second.Logged("2 since the last session, 0 compiled"), TimeSpan.FromSeconds(60));
            // The open document's diagnostics build the analysis solution, in their own time (after the pass on macOS).
            await LanguageFixture.WaitUntilAsync(() => second.Logged("1 kept from the last session"), TimeSpan.FromSeconds(60));
            Assert.Contains(second.For(Path.Combine(_root, "App", "Closed.cs")), d => d.Code == "CS0219");
            Assert.Contains(second.For(Path.Combine(_root, "Lib", "Warnings.cs")), d => d.Code == "CS0219");
            await LanguageFixture.WaitUntilAsync(() => second.Has(page, "CS4014"), TimeSpan.FromSeconds(60));
            Assert.Single(second.For(page), d => d.Code == "CS4014");
        }

        // A changed file in Lib: Lib compiles again, and so does App, which references it.
        Write("Lib/Warnings.cs", LibWarning.Replace("int unused = 1;", "int unused = 1; int another = 2;", StringComparison.Ordinal));
        File.SetLastWriteTimeUtc(Path.Combine(_root, "Lib", "Warnings.cs"), DateTime.UtcNow.AddSeconds(5));
        await using (var third = await SessionAsync(open: null))
        {
            await third.ReadyAsync();
            await LanguageFixture.WaitUntilAsync(() => third.Logged("0 since the last session, 2 compiled"), TimeSpan.FromSeconds(60));
            Assert.Equal(2, third.For(Path.Combine(_root, "Lib", "Warnings.cs")).Count(d => d.Code == "CS0219"));
        }
    }

    [Fact]
    public async Task A_change_below_a_project_reuses_its_reference_assembly_then_checks_again_from_source()
    {
        // Top uses a constant of Mid that Mid takes from Base: a reference assembly reused across a change in Base keeps
        // the old value, the trade-off #208 accepts for an open after a pull. 1 / (1 - 1) is an error (CS0020); 1 / (2 - 1) isn't.
        Write("Base/Limits.cs", "namespace Base; public static class Limits { public const int Size = 1; }");
        Write("Mid/Buffer.cs", "namespace Mid; public static class Buffer { public const int Size = Base.Limits.Size; }");
        Write("Top/Use.cs", "namespace Top; static class Use { const int Ratio = 1 / (Mid.Buffer.Size - 1); }");
        foreach (var name in new[] { "Base", "Mid", "Top" })
        {
            Directory.CreateDirectory(Path.Combine(_root, name, "obj"));
        }

        // As MSBuild hands them over: Top references Base too, through Mid.
        ProjectData[] Projects() => [Project("Base", ["Limits.cs"]), Project("Mid", ["Buffer.cs"], ["Base"]), Project("Top", ["Use.cs"], ["Mid", "Base"])];
        var use = Path.Combine(_root, "Top", "Use.cs");
        await using (var first = await SessionAsync(null, Projects()))
        {
            await first.ReadyAsync();
            await LanguageFixture.WaitUntilAsync(() => first.Has(use, "CS0020"), TimeSpan.FromSeconds(60), () => Describe(first));
        }

        Assert.True(File.Exists(Path.Combine(_root, "Mid", "obj", LanguageStateCache.ReferenceFile)));

        // Base changes: Mid's own inputs don't. Its reference assembly from the last session stands for it, so Top
        // compiles at once, against the old constant; then the pass checks Top again from source.
        Write("Base/Limits.cs", "namespace Base; public static class Limits { public const int Size = 2; }");
        File.SetLastWriteTimeUtc(Path.Combine(_root, "Base", "Limits.cs"), DateTime.UtcNow.AddSeconds(5));
        await using (var second = await SessionAsync(null, Projects()))
        {
            await second.ReadyAsync();
            await LanguageFixture.WaitUntilAsync(() => second.Logged("1 reused across a change below them"), TimeSpan.FromSeconds(60), () => Describe(second));
            await LanguageFixture.WaitUntilAsync(() => second.Logged("1 project(s) compiled against reference assemblies reused across a change below them: checked again from source."), TimeSpan.FromSeconds(60), () => Describe(second));
            await LanguageFixture.WaitUntilAsync(() => !second.Has(use, "CS0020") && second.Logged("1 compiled"), TimeSpan.FromSeconds(60), () => Describe(second));
        }

        // Nothing changed since: what the next session takes from the cache is the check from source, not the stale error.
        await using (var third = await SessionAsync(null, Projects()))
        {
            await third.ReadyAsync();
            await LanguageFixture.WaitUntilAsync(() => third.Logged("3 since the last session, 0 compiled"), TimeSpan.FromSeconds(60), () => Describe(third));
            Assert.False(third.Has(use, "CS0020"));
        }
    }

    [Fact]
    public void CS4014_on_a_call_into_another_project_is_left_out_in_methods_that_are_not_async()
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Where(p => Path.GetFileName(p).StartsWith("System.", StringComparison.Ordinal) || Path.GetFileName(p) == "netstandard.dll")
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p)).ToList();
        var lib = CSharpCompilation.Create("Lib", [CSharpSyntaxTree.ParseText(LibSource, cancellationToken: Ct)], references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var local = "namespace App { class Local { async System.Threading.Tasks.Task Go() => await System.Threading.Tasks.Task.Yield(); void M() { Go(); } } }";
        var libReference = lib.ToMetadataReference();
        var app = CSharpCompilation.Create("App", [CSharpSyntaxTree.ParseText(AppSource, cancellationToken: Ct), CSharpSyntaxTree.ParseText(local, cancellationToken: Ct)], [.. references, libReference],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var cs4014 = app.GetDiagnostics(Ct).Where(d => d.Id == "CS4014").ToList();
        Assert.Equal(3, cs4014.Count); // the workspace's view: the non-async OnRender, the async OnRenderAsync, and Local.M (same project)
        var kept = cs4014.Where(d => !BuildDivergence.IsCompilationReferenceArtifact(d, app, Ct)).ToList();
        Assert.Equal(2, kept.Count);
        Assert.DoesNotContain(kept, d => d.Location.SourceTree!.ToString().Contains("OnRender()", StringComparison.Ordinal)
                                         && d.Location.GetLineSpan().StartLinePosition.Line == 4);

        // The build's view (Lib as an assembly) agrees: 2.
        using var image = new MemoryStream();
        Assert.True(lib.Emit(image, cancellationToken: Ct).Success);
        var built = app.ReplaceReference(libReference, MetadataReference.CreateFromImage(image.ToArray()));
        Assert.Equal(2, built.GetDiagnostics(Ct).Count(d => d.Id == "CS4014"));
    }

    // ------------------------------------------------------------------ sessions

    private static string Describe(Session session) => Environment.NewLine + string.Join(Environment.NewLine, session.Log.Where(l => !l.Contains("[trace]", StringComparison.Ordinal)).TakeLast(30));

    private void Write(string relative, string text)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private ProjectData Project(string name, string[] sources, string[]? projectReferences = null)
    {
        var directory = Path.Combine(_root, name);
        var output = Path.Combine(directory, "obj", name + ".dll");
        var framework = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Where(p => Path.GetFileName(p).StartsWith("System.", StringComparison.Ordinal) || Path.GetFileName(p) == "netstandard.dll");
        string[] arguments =
        [
            "/target:library", "/nullable:enable", $"/out:{output}",
            .. framework.Select(p => $"/reference:{p}"),
            .. (projectReferences ?? []).Select(r => $"/reference:{Path.Combine(_root, r, "obj", r + ".dll")}"),
            .. sources,
        ];
        return new ProjectData(Path.Combine(directory, name + ".csproj"), name, "C#", "net10.0", arguments, output, null,
            [.. (projectReferences ?? []).Select(r => Path.Combine(_root, r, r + ".csproj"))]);
    }

    private Task<Session> SessionAsync(string? open) =>
        SessionAsync(open, [Project("Lib", ["Context.cs", "Warnings.cs"]), Project("App", ["Page.cs", "Closed.cs"], ["Lib"])]);

    private async Task<Session> SessionAsync(string? open, ProjectData[] projects)
    {
        var session = new Session(new LanguageHostClient("C# language service", ProcessLaunchInfo.FromPath(RepoPaths.HostAssembly), policy: new SupervisionPolicy { InitialBackoff = TimeSpan.FromMilliseconds(50) }), projects.Length);
        await session.Client.StartAsync(Ct);
        if (open is not null)
        {
            await session.Client.DidOpenAsync(new OpenDocumentState(DocumentUris.FromPath(open), "csharp", 1, await File.ReadAllTextAsync(open, Ct)), Ct);
        }

        await session.Client.UpdateProjectsAsync(projects, Ct, loading: false);
        return session;
    }

    private sealed class Session : IAsyncDisposable
    {
        private readonly ConcurrentDictionary<string, Diagnostic[]> _diagnostics = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentQueue<WorkspaceStatusParams> _status = new();

        private readonly int _projects;

        public Session(LanguageHostClient client, int projects)
        {
            Client = client;
            _projects = projects;
            client.Diagnostics += (_, p) => _diagnostics[p.Uri] = p.Diagnostics;
            client.WorkspaceStatus += (_, s) => _status.Enqueue(s);
            client.Log += (_, line) => Log.Enqueue(line);
        }

        public LanguageHostClient Client { get; }

        public ConcurrentQueue<string> Log { get; } = new();

        public Diagnostic[] For(string path) => _diagnostics.GetValueOrDefault(DocumentUris.FromPath(path)) ?? [];

        public bool Has(string path, string code) => For(path).Any(d => d.Code == code);

        public bool Logged(string text) => Log.Any(l => l.Contains(text, StringComparison.Ordinal));

        public Task ReadyAsync() => LanguageFixture.WaitUntilAsync(() => _status.Any(s => s.State == WorkspaceStates.Ready && s.Projects == _projects), TimeSpan.FromSeconds(90));

        public ValueTask DisposeAsync() => Client.DisposeAsync();
    }
}
