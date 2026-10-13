using Codalus.Plugins.Protocol;
using Codalus.Workbench.Projects;

namespace Codalus.Workbench.Hosting;

/// <summary>A review thread of the current branch's pull request, for an editor (M13b).</summary>
public sealed record EditorReviewThread(string PullRequestReference, string PullRequestTitle, ReviewThreadDto Thread)
{
    /// <summary>The one-based line in the file.</summary>
    public int Line => Thread.Line;

    public IReadOnlyList<ReviewCommentDto> Comments => Thread.Comments;
}

/// <summary>
/// The review threads of the pull request of the current branch, by file (ADR 0031): the editors show them at their
/// lines. Resolved threads are left out unless asked for. Thread-safe; <see cref="Changed"/> comes on any thread.
/// </summary>
public sealed class ReviewThreadIndex
{
    private readonly Lock _gate = new();
    private Dictionary<string, List<EditorReviewThread>> _byFile = new(SolutionService.PathComparer);

    /// <summary>The pull request whose threads these are, or null.</summary>
    public PullRequestDto? PullRequest { get; private set; }

    public event EventHandler? Changed;

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _byFile.Values.Sum(l => l.Count);
            }
        }
    }

    /// <summary>Replaces the threads with a pull request's (paths relative to <paramref name="root"/>); null clears them.</summary>
    public void Set(string? root, PullRequestDetailsDto? details, bool includeResolved = false)
    {
        var byFile = new Dictionary<string, List<EditorReviewThread>>(SolutionService.PathComparer);
        if (root is not null && details is not null)
        {
            foreach (var thread in details.Threads.Where(t => includeResolved || !t.IsResolved))
            {
                var path = Path.GetFullPath(Path.Combine(root, thread.Path.Replace('/', Path.DirectorySeparatorChar)));
                if (!byFile.TryGetValue(path, out var list))
                {
                    byFile[path] = list = [];
                }

                list.Add(new EditorReviewThread(details.PullRequest.Reference, details.PullRequest.Title, thread));
            }
        }

        lock (_gate)
        {
            _byFile = byFile;
            PullRequest = details?.PullRequest;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public IReadOnlyList<EditorReviewThread> For(string path)
    {
        lock (_gate)
        {
            return _byFile.TryGetValue(Path.GetFullPath(path), out var list) ? [.. list] : [];
        }
    }
}
