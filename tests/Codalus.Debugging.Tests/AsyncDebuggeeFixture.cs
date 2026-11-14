namespace Codalus.Debugging.Tests;

/// <summary>
/// samples/Debugging/Async.slnx (M61, ADR 0100), copied to a temp folder and built once for the test class: the same
/// async code as state machines (AsyncDebuggee) and as runtime async (AsyncDebuggee.RuntimeAsync), each calling the
/// library built the other way.
/// </summary>
public sealed class AsyncDebuggeeFixture : IAsyncLifetime
{
    /// <summary>The state-machine build.</summary>
    public const string StateMachines = "AsyncDebuggee";

    /// <summary>The runtime-async build of the same files.</summary>
    public const string RuntimeAsync = "AsyncDebuggee.RuntimeAsync";

    private readonly string _root = Path.Combine(Codalus.Platform.RealPath.Resolve(Path.GetTempPath()), "codalus-async-" + Guid.NewGuid().ToString("N"));

    /// <summary>The folder of the sources both builds compile (the PDBs of both name these files).</summary>
    public string Folder => Path.Combine(_root, "Debugging", "AsyncDebuggee");

    public string Library => Path.Combine(_root, "Debugging", "AsyncLib", "Library.cs");

    /// <summary>A build's program and the folder it runs in.</summary>
    public (string Dll, string Folder) Program(string build)
    {
        var folder = Path.Combine(_root, "Debugging", build);
        return (Path.Combine(folder, "bin", "Debug", "net11.0", build + ".dll"), folder);
    }

    /// <summary>A source file of the program (<c>Worker.cs</c>).</summary>
    public string Source(string name) => Path.Combine(Folder, name);

    public ValueTask InitializeAsync() => DebuggeeFixture.BuildAsync(_root, "Async.slnx");

    public ValueTask DisposeAsync() => DebuggeeFixture.Delete(_root);
}
