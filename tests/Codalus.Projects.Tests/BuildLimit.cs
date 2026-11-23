namespace Codalus.Projects.Tests;

/// <summary>The limit of a real build in these tests: 5 minutes a build, with the test's own cancellation.</summary>
/// <remarks>
/// A build is as slow as the machine it runs on. The two-project samples/BuildAndRun builds in 3 to 4 s on an idle
/// machine, and took 2:48 in the 1.0 release candidate's Windows run (2026-11-19: the whole suite, 3 modules at a time,
/// beside another session's tests), where builds ran out the 2 or 3 minutes their classes gave every call; the CLI's
/// builds of the same sample, run beside the build host's under the same load, took as long (docs/ci-flakes.md). So a
/// build gets this limit, as the module's other build tests have, and the other calls keep their class's.
/// </remarks>
internal static class BuildLimit
{
    /// <summary>A token cancelled after <paramref name="builds"/> builds' time, or with the test.</summary>
    public static CancellationToken Token(int builds = 1) =>
        CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken, new CancellationTokenSource(TimeSpan.FromMinutes(5 * builds)).Token).Token;
}
