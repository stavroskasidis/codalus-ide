using Codalus.Projects.Protocol;
using Codalus.Rpc;
using Codalus.Workbench.Projects;

namespace Codalus.Projects.Tests;

/// <summary>
/// F#'s compile order (M36, ADR 0067) through the real build host, on a copy of samples/FSharp: items come in compile
/// order, <c>items/move</c> reorders <c>Compile</c> items in the project file, a new file goes before a program's last
/// file, and a signature file is a <c>Compile</c> item.
/// </summary>
public sealed class FSharpOrderIntegrationTests : IAsyncLifetime
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(120);
    private readonly string _root = Path.Combine(Codalus.Platform.RealPath.Resolve(Path.GetTempPath()), "codalus-fsorder-" + Guid.NewGuid().ToString("N"));
    private SolutionService _service = null!;
    private ProjectItemService _items = null!;

    private string Fixture => Path.Combine(_root, "FSharp");

    private string Library => Path.Combine(Fixture, "FsLibrary", "FsLibrary.fsproj");

    private string Console => Path.Combine(Fixture, "FsConsole", "FsConsole.fsproj");

    private string LibraryFile(string name) => Path.Combine(Fixture, "FsLibrary", name);

    private static CancellationToken Timeout() =>
        CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken, new CancellationTokenSource(s_timeout).Token).Token;

    public async ValueTask InitializeAsync()
    {
        Copy(Path.Combine(BuildHostIntegrationTests.RepoRoot, "samples", "FSharp"), Fixture);
        File.WriteAllText(Path.Combine(_root, "Directory.Build.props"), "<Project />");
        Codalus.Tests.FixtureSdk.Pin(_root);
        File.WriteAllText(Path.Combine(_root, "Directory.Packages.props"), "<Project><PropertyGroup><ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally></PropertyGroup></Project>");
        _service = new SolutionService(() => BuildHostIntegrationTests.HostAssemblyPath, new SupervisionPolicy { InitialBackoff = TimeSpan.FromMilliseconds(50) });
        _service.Log += (_, line) => System.Console.Error.WriteLine("[host] " + line);
        _items = new ProjectItemService(_service);
        await _service.OpenAsync(Path.Combine(Fixture, "FSharp.slnx"), trusted: true, restore: false, Timeout());
        await WaitUntilAsync(() => _service.Progress is { Completed: true } && _service.Projects.Count == 5);
    }

    public async ValueTask DisposeAsync()
    {
        await _service.DisposeAsync();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private IReadOnlyList<string> Order(string project) => [.. CompileOrder.Of(_service.Projects[project]).Select(Path.GetFileName)!];

    [Fact]
    public async Task Compile_items_come_in_compile_order_and_move_in_the_project_file()
    {
        Assert.Equal(["Shapes.fsi", "Shapes.fs", "Geometry.fs"], Order(Library));

        // Geometry.fs up: before the Shapes pair (CompileOrder keeps the signature with its implementation).
        foreach (var (path, before) in CompileOrder.Step(CompileOrder.Of(_service.Projects[Library]), LibraryFile("Geometry.fs"), down: false))
        {
            await _items.MoveAsync(Library, path, before, after: false, Timeout());
        }

        await WaitUntilAsync(() => Order(Library) is ["Geometry.fs", ..]);
        Assert.Equal(["Geometry.fs", "Shapes.fsi", "Shapes.fs"], Order(Library));
        var xml = await File.ReadAllTextAsync(Library, TestContext.Current.CancellationToken);
        Assert.True(xml.IndexOf("Geometry.fs", StringComparison.Ordinal) < xml.IndexOf("Shapes.fsi", StringComparison.Ordinal), xml);

        // And after: Geometry.fs after Shapes.fs again.
        await _items.MoveAsync(Library, LibraryFile("Geometry.fs"), LibraryFile("Shapes.fs"), after: true, Timeout());
        await WaitUntilAsync(() => Order(Library) is [.., "Geometry.fs"]);

        // A file without its own Compile item can't move.
        await Assert.ThrowsAsync<JsonRpcRemoteException>(() => _items.MoveAsync(Library, LibraryFile("Missing.fs"), LibraryFile("Shapes.fs"), after: true, Timeout()));
    }

    [Fact]
    public async Task New_files_go_before_a_programs_last_file_and_signature_files_compile()
    {
        // A program's last file holds its entry point: a new file goes before it.
        var helper = Path.Combine(Fixture, "FsConsole", "Helpers.fs");
        await File.WriteAllTextAsync(helper, "module Helpers\n", TestContext.Current.CancellationToken);
        await _items.SetAsync(Console, helper, "Compile", null, Timeout());
        await WaitUntilAsync(() => Order(Console) is ["Helpers.fs", "Program.fs"]);

        // In a library, a new file goes last; an included .fsi is a Compile item.
        var signature = LibraryFile("Geometry.fsi");
        await File.WriteAllTextAsync(signature, "module FsLibrary.Geometry\n\nval totalArea: shapes: FsLibrary.Shapes.Shape list -> float\n", TestContext.Current.CancellationToken);
        await _items.IncludeAsync(Library, [signature], Timeout());
        await WaitUntilAsync(() => Order(Library) is [.., "Geometry.fsi"]);
        await _items.MoveAsync(Library, signature, LibraryFile("Geometry.fs"), after: false, Timeout());
        await WaitUntilAsync(() => Order(Library) is [.., "Geometry.fsi", "Geometry.fs"]);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (started.Elapsed > s_timeout)
            {
                throw new TimeoutException("The condition was not met in time.");
            }

            await Task.Delay(50, TestContext.Current.CancellationToken);
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
