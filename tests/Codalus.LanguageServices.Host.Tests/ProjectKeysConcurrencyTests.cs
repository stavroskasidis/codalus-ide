using Codalus.LanguageServices.Protocol;

namespace Codalus.LanguageServices.Host.Tests;

/// <summary>The projects' keys asked from many threads at once are the keys asked one after another.</summary>
public sealed class ProjectKeysConcurrencyTests : IDisposable
{
    private readonly string _root = Path.Combine(Codalus.Platform.RealPath.Resolve(Path.GetTempPath()), "codalus-keys-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Concurrent_keys_equal_sequential_ones()
    {
        var framework = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Where(p => Path.GetFileName(p).StartsWith("System.Runtime", StringComparison.Ordinal));
        var names = Enumerable.Range(0, 12).Select(i => $"P{i}").ToList();
        var projects = new List<ProjectData>();
        foreach (var (name, i) in names.Select((n, i) => (n, i)))
        {
            var directory = Path.Combine(_root, name);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "C.cs"), $"namespace {name}; public class C {{ }}");
            var references = i == 0 ? [] : new[] { names[i - 1], names[i / 2] }.Distinct().ToArray();
            var output = Path.Combine(directory, "obj", name + ".dll");
            projects.Add(new ProjectData(Path.Combine(directory, name + ".csproj"), name, "C#", "net10.0",
                ["/target:library", $"/out:{output}", .. framework.Select(p => $"/reference:{p}"), .. references.Select(r => $"/reference:{Path.Combine(_root, r, "obj", r + ".dll")}"), "C.cs"],
                output, null, [.. references.Select(r => Path.Combine(_root, r, r + ".csproj"))]));
        }

        using var workspace = new WorkspaceManager();
        workspace.UpdateProjects(projects);
        var ids = workspace.CurrentSolution.Projects.Where(p => p.Id != workspace.MiscellaneousProjectId).Select(p => p.Id).ToList();
        var sequential = workspace.Keys();
        var expected = ids.Select(id => (sequential.Key(id), sequential.OwnKey(id))).ToList();
        Assert.All(expected, k => Assert.NotNull(k.Item1));

        for (var round = 0; round < 20; round++)
        {
            var concurrent = workspace.Keys();
            var found = new (string?, string?)[ids.Count];
            Parallel.For(0, ids.Count * 4, new ParallelOptions { MaxDegreeOfParallelism = 8 }, n =>
            {
                var i = (n * 7 + round) % ids.Count;
                found[i] = (concurrent.Key(ids[i]), concurrent.OwnKey(ids[i]));
            });
            Assert.Equal(expected, found);
        }
    }
}
