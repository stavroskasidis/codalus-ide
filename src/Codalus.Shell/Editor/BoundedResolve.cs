using Codalus.LanguageServices.Protocol;

namespace Codalus.Shell.Editor;

/// <summary>
/// CodeLens resolves a few at a time, in line order, on the caller's (UI) thread: one after another, a screen of
/// lenses waited for each round trip in turn, as many as the view has lenses.
/// </summary>
internal static class BoundedResolve
{
    /// <summary>How many resolves are in flight at once.</summary>
    public const int Window = 1; // bisect V2 (was 4)

    /// <summary>
    /// Resolves <paramref name="items"/>, applying each answer while <paramref name="current"/> holds; returns the first
    /// failure (a cancellation included), once every request it started has ended, or null.
    /// </summary>
    public static async Task<Exception?> RunAsync<T>(IReadOnlyList<T> items, Func<T, Task<CodeLens>> resolve, Func<bool> current, Action<T, CodeLens> apply)
    {
        var running = new List<(T Item, Task<CodeLens> Task)>(Window);
        var next = 0;
        Exception? failure = null;
        while (running.Count > 0 || (failure is null && next < items.Count))
        {
            while (failure is null && running.Count < Window && next < items.Count)
            {
                var item = items[next++];
                running.Add((item, Start(item)));
            }

            var done = await Task.WhenAny(running.Select(r => r.Task)).ConfigureAwait(true);
            var index = running.FindIndex(r => ReferenceEquals(r.Task, done));
            var (finished, task) = running[index];
            running.RemoveAt(index);
            if (task.IsCompletedSuccessfully)
            {
                if (failure is null && current())
                {
                    apply(finished, task.Result);
                }
            }
            else
            {
                failure ??= task.Exception?.InnerException ?? new OperationCanceledException();
            }
        }

        return failure ?? (current() ? null : new OperationCanceledException());

        Task<CodeLens> Start(T item)
        {
            try
            {
                return resolve(item);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                return Task.FromException<CodeLens>(ex);
            }
        }
    }
}
