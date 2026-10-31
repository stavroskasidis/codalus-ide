using Codalus.Projects.Protocol;
using Codalus.Workbench.Packages;

namespace Codalus.Workbench.Tests;

/// <summary>Which evaluations change a project's packages, announced once per burst (M56).</summary>
public sealed class PackageChangesTests
{
    private static ProjectInfo Project(int i, params ReferenceInfo[] references) =>
        new($"/solution/P{i}/P{i}.csproj", $"P{i}", ProjectStates.Loaded, null, ["net10.0"], null, [], references, [], true);

    private static ReferenceInfo Package(string id, string version) => new(ReferenceKinds.Package, id, version, null, "net10.0", true);

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var waited = 0; !condition(); waited += 20)
        {
            Assert.True(waited < 10_000, "nothing was announced within 10 s");
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task An_open_announces_once_and_evaluations_with_the_same_packages_not_at_all()
    {
        var announced = 0;
        var changes = new PackageChanges(() => Interlocked.Increment(ref announced));

        // A large solution's open: every project's first evaluation, in a burst.
        for (var i = 0; i < 300; i++)
        {
            Assert.True(changes.Observe(Project(i, Package("Serilog", "4.0.0"))));
        }

        await WaitUntil(() => Volatile.Read(ref announced) == 1);

        // Evaluated again with the same packages (design-time builds, an edit of something else): nothing to read again.
        for (var i = 0; i < 300; i++)
        {
            Assert.False(changes.Observe(Project(i, Package("Serilog", "4.0.0")) with { Diagnostics = ["edited"] }));
        }

        await Task.Delay(600, TestContext.Current.CancellationToken);
        Assert.Equal(1, Volatile.Read(ref announced));

        // A package updated, then a project that lost its restore: each announces.
        Assert.True(changes.Observe(Project(7, Package("Serilog", "4.1.0"))));
        await WaitUntil(() => Volatile.Read(ref announced) == 2);
        Assert.True(changes.Observe(Project(8, Package("Serilog", "4.0.0")) with { Restored = false }));
        await WaitUntil(() => Volatile.Read(ref announced) == 3);

        // Another solution: its projects' first evaluations announce again.
        changes.Clear();
        Assert.True(changes.Observe(Project(1, Package("Serilog", "4.0.0"))));
        await WaitUntil(() => Volatile.Read(ref announced) == 4);
    }
}
