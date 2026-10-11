using DaedalusStudio.Projects.Protocol;
using DaedalusStudio.Workbench.Projects;

namespace DaedalusStudio.Workbench.Build;

/// <summary>
/// Build depth on the build host (ADR 0034): the fast up-to-date check on its own, the last build's timings, a project's
/// pre-build and post-build events, and binary logs (opened, browsed a level at a time, searched).
/// </summary>
public sealed class BuildDepthService(SolutionService solutions)
{
    private static BuildDepthJsonContext Json => BuildDepthJsonContext.Default;

    public Task<UpToDateResult> CheckUpToDateAsync(string? project, CancellationToken cancellationToken) =>
        solutions.RequestAsync(BuildDepthMethods.UpToDate, new UpToDateParams(project), Json.UpToDateParams, Json.UpToDateResult, cancellationToken, TimeSpan.FromMinutes(5));

    public Task<BuildTimingsResult> GetTimingsAsync(CancellationToken cancellationToken) =>
        solutions.RequestAsync(BuildDepthMethods.Timings, new EmptyParams(), Json.EmptyParams, Json.BuildTimingsResult, cancellationToken);

    public Task<BuildEventsResult> GetBuildEventsAsync(string project, CancellationToken cancellationToken) =>
        solutions.RequestAsync(BuildDepthMethods.GetBuildEvents, new BuildEventsParams(project), Json.BuildEventsParams, Json.BuildEventsResult, cancellationToken);

    /// <summary>Writes the events (null keeps one, an empty command removes it) and returns them as the project now has them.</summary>
    public Task<BuildEventsResult> SetBuildEventsAsync(string project, string? preBuild, string? postBuild, string? runPostBuildEvent, CancellationToken cancellationToken) =>
        solutions.RequestAsync(BuildDepthMethods.SetBuildEvents, new SetBuildEventsParams(project, preBuild, postBuild, runPostBuildEvent), Json.SetBuildEventsParams, Json.BuildEventsResult, cancellationToken);

    public Task<BinaryLogInfo> OpenBinaryLogAsync(string path, CancellationToken cancellationToken) =>
        solutions.RequestAsync(BuildDepthMethods.OpenBinaryLog, new OpenBinaryLogParams(path), Json.OpenBinaryLogParams, Json.BinaryLogInfo, cancellationToken, TimeSpan.FromMinutes(5));

    public async Task<IReadOnlyList<BinaryLogNode>> GetChildrenAsync(string log, int node, CancellationToken cancellationToken) =>
        (await solutions.RequestAsync(BuildDepthMethods.BinaryLogChildren, new BinaryLogChildrenParams(log, node), Json.BinaryLogChildrenParams, Json.BinaryLogChildrenResult, cancellationToken).ConfigureAwait(false)).Children;

    public Task<SearchBinaryLogResult> SearchAsync(string log, string query, CancellationToken cancellationToken, int maxResults = 500) =>
        solutions.RequestAsync(BuildDepthMethods.SearchBinaryLog, new SearchBinaryLogParams(log, query, maxResults), Json.SearchBinaryLogParams, Json.SearchBinaryLogResult, cancellationToken);

    public Task CloseBinaryLogAsync(string log, CancellationToken cancellationToken) =>
        solutions.RequestAsync(BuildDepthMethods.CloseBinaryLog, new CloseBinaryLogParams(log), Json.CloseBinaryLogParams, Json.EmptyResult, cancellationToken);
}
