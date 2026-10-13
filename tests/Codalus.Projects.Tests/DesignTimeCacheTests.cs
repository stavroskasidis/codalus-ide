using Codalus.Projects.Host;
using Codalus.Projects.Protocol;

namespace Codalus.Projects.Tests;

/// <summary>The persisted design-time build cache (M34b) on its own: keys, invalidation and the files it checks.</summary>
public sealed class DesignTimeCacheTests : IDisposable
{
    private readonly string _root = Path.Combine(Codalus.Platform.RealPath.Resolve(Path.GetTempPath()), "codalus-dtcache-" + Guid.NewGuid().ToString("N"));

    public DesignTimeCacheTests() => Directory.CreateDirectory(Path.Combine(_root, "src", "App", "obj"));

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Project => Path.Combine(_root, "src", "App", "App.csproj");

    private string Obj => Path.Combine(_root, "src", "App", "obj");

    private CompilerInputsInfo Inputs(params string[] arguments) =>
        new(Project, "net10.0", "C#", CompilerInputStates.Ok, null, arguments, [], null, null);

    private static readonly Dictionary<string, string> s_properties = new() { ["SolutionDir"] = "x" };

    [Fact]
    public void Keys_change_with_the_project_its_references_framework_and_properties()
    {
        var key = DesignTimeCache.Key("A", "net10.0", s_properties, [("lib", "L1")]);
        Assert.Equal(key, DesignTimeCache.Key("A", "net10.0", new Dictionary<string, string> { ["SolutionDir"] = "x" }, [("lib", "L1")]));
        Assert.NotEqual(key, DesignTimeCache.Key("B", "net10.0", s_properties, [("lib", "L1")]));
        Assert.NotEqual(key, DesignTimeCache.Key("A", "net8.0", s_properties, [("lib", "L1")]));
        Assert.NotEqual(key, DesignTimeCache.Key("A", "net10.0", s_properties, [("lib", "L2")]));
        Assert.NotEqual(key, DesignTimeCache.Key("A", "net10.0", s_properties, [("lib", null)]));
        Assert.NotEqual(key, DesignTimeCache.Key("A", "net10.0", new Dictionary<string, string> { ["SolutionDir"] = "y" }, [("lib", "L1")]));
    }

    [Fact]
    public void Entries_are_read_back_only_under_their_key_and_while_their_files_exist()
    {
        File.WriteAllText(Path.Combine(_root, "src", "App", "Program.cs"), "class P { }");
        var inputs = Inputs("/noconfig", "/debug+", "/reference:" + Path.Combine(_root, "out", "Lib.dll"), "Program.cs");
        var entry = DesignTimeCache.Entry("Debug", "AnyCPU", null);
        DesignTimeCache.Write(Obj, entry, "K1", inputs);

        Assert.Equal(inputs.Arguments, DesignTimeCache.TryRead(Obj, entry, "K1", _root)?.Arguments);
        Assert.Null(DesignTimeCache.TryRead(Obj, entry, "K2", _root)); // inputs changed
        Assert.Null(DesignTimeCache.TryRead(Obj, DesignTimeCache.Entry("Release", "AnyCPU", null), "K1", _root));

        // A source file gone (a generated one in obj, deleted by a clean): built again.
        File.Delete(Path.Combine(_root, "src", "App", "Program.cs"));
        Assert.Null(DesignTimeCache.TryRead(Obj, entry, "K1", _root));
    }

    [Fact]
    public void Failed_builds_are_not_cached_and_corrupt_files_are_misses()
    {
        var entry = DesignTimeCache.Entry("Debug", "AnyCPU", null);
        DesignTimeCache.Write(Obj, entry, "K", Inputs() with { State = CompilerInputStates.Failed });
        Assert.False(File.Exists(Path.Combine(Obj, DesignTimeCache.FileName)));

        File.WriteAllText(Path.Combine(Obj, DesignTimeCache.FileName), "{ broken");
        Assert.Null(DesignTimeCache.TryRead(Obj, entry, "K", _root));
        DesignTimeCache.Write(Obj, entry, "K", Inputs("/noconfig"));
        Assert.NotNull(DesignTimeCache.TryRead(Obj, entry, "K", _root));
    }

    [Fact]
    public void Options_and_absolute_unix_paths_are_told_apart()
    {
        // References outside the solution (packages) must exist; inside it they may be outputs not built yet.
        Assert.True(DesignTimeCache.InputsExist(Inputs("/reference:" + Path.Combine(_root, "bin", "Lib.dll"), "-nowarn:1701"), _root));
        Assert.False(DesignTimeCache.InputsExist(Inputs("/reference:" + Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "Package.dll")), _root));
        Assert.False(DesignTimeCache.InputsExist(Inputs("/analyzer:" + Path.Combine(_root, "Missing.dll")), _root));

        // "/home/me/a.cs" is a source file, not an option named "home/me/a.cs".
        Assert.False(DesignTimeCache.InputsExist(Inputs("/" + Guid.NewGuid().ToString("N") + "/Missing.cs"), _root));
    }
}
