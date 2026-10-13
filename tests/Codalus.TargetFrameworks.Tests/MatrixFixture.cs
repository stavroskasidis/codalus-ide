using Codalus.Projects.Protocol;
using Codalus.Rpc;
using Codalus.Workbench.Build;
using Codalus.Workbench.Output;
using Codalus.Workbench.Projects;

namespace Codalus.TargetFrameworks.Tests;

/// <summary>
/// samples/TargetFrameworks opened through the real build host on the matrix's dotnet root, restored, design-time
/// built and built once for the tests of a class (M33).
/// </summary>
public sealed class MatrixFixture : IAsyncLifetime
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromMinutes(15);
    private readonly List<string> _log = [];

    public string Root { get; } = Path.Combine(Codalus.Platform.RealPath.Resolve(Path.GetTempPath()), "codalus-matrix-" + Guid.NewGuid().ToString("N"));

    public string Folder { get; private set; } = null!;

    public SolutionService Solutions { get; private set; } = null!;

    public OutputService Output { get; } = new();

    public BuildResultInfo? Build { get; private set; }

    public string BuildOutput { get; private set; } = string.Empty;

    /// <summary>Why the fixture couldn't open the solution (the tests skip or fail with it), or null.</summary>
    public string? Problem { get; private set; }

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

    public string Project(string name) => Directory.EnumerateFiles(Folder, name + ".csproj", SearchOption.AllDirectories).Single();

    public async ValueTask InitializeAsync()
    {
        if (CompatRoot.Path is null)
        {
            Problem = "no compat root";
            return;
        }

        Folder = Matrix.CopySample(Root);
        Solutions = new SolutionService(() => Matrix.BuildHost, new SupervisionPolicy { InitialBackoff = TimeSpan.FromMilliseconds(50) });
        Solutions.Log += (_, line) => Add("[host] " + line);
        Solutions.RestoreOutput += (_, line) => Add("[restore] " + line);
        using var timeout = new CancellationTokenSource(s_timeout);
        var solution = await Solutions.OpenAsync(Path.Combine(Folder, "TargetFrameworks.sln"), trusted: true, restore: true, timeout.Token);
        await WaitUntilAsync(() => Solutions.Progress is { Completed: true } && Solutions.Projects.Count == solution.Projects.Length
                                   && Solutions.RestoreState is "succeeded" or "failed", timeout.Token);
        if (Solutions.RestoreState != "succeeded")
        {
            Problem = "The restore failed:\n" + Log;
            return;
        }

        // Every project's design-time build after the restore (the one before it has no package references; .NET Core
        // 2.x and 3.0 get even their framework from packages) feeds the language service.
        await WaitUntilAsync(() => solution.Projects.All(p => HasReferences(Solutions, p.Path)), timeout.Token);
        var builds = new BuildService(Solutions, Output);
        Build = await builds.BuildAsync(BuildTargets.Build, null, timeout.Token);
        BuildOutput = builds.Channel.GetText();
    }

    public async ValueTask DisposeAsync()
    {
        if (Solutions is not null)
        {
            await Solutions.DisposeAsync();
        }

        Matrix.DeleteQuietly(Root);
    }

    /// <summary>Skips or fails a test when the matrix isn't there or didn't open.</summary>
    public void Require()
    {
        CompatRoot.Require();
        Assert.True(Problem is null, Problem);
    }

    /// <summary>Requires a successful build too.</summary>
    public void RequireBuild()
    {
        Require();
        Assert.True(Build?.Succeeded == true, "The build failed:\n" + BuildOutput);
    }

    private void Add(string line)
    {
        lock (_log)
        {
            _log.Add(line);
        }
    }

    /// <summary>Whether a project's latest design-time build references its framework (<c>System.Runtime.dll</c>).</summary>
    public static bool HasReferences(SolutionService solutions, string project) =>
        solutions.CompilerInputs.TryGetValue(project, out var inputs)
        && inputs.Arguments.Any(a => a.StartsWith("/reference:", StringComparison.OrdinalIgnoreCase) && a.EndsWith("System.Runtime.dll", StringComparison.OrdinalIgnoreCase));

    public static async Task WaitUntilAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        while (!condition())
        {
            await Task.Delay(50, cancellationToken);
        }
    }
}
