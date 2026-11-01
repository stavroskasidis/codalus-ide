using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Codalus.Projects.Protocol;

namespace Codalus.Workbench.Projects;

/// <summary>A solution (or a folder in folder mode) whose per-user state the <see cref="SolutionStateStore"/> keeps.</summary>
public readonly record struct SolutionStateKey(string Path, bool IsFolder)
{
    /// <summary>The folder the solution is in (the folder itself in folder mode).</summary>
    public string Folder => IsFolder ? System.IO.Path.TrimEndingDirectorySeparator(Path) : System.IO.Path.GetDirectoryName(Path)!;

    /// <summary>The solution's state folder: <c>.codalus/state/&lt;solution file name&gt;</c> or <c>.codalus/state/folder</c>.</summary>
    public string Directory => SolutionStateLayout.Directory(Path, IsFolder);
}

/// <summary>
/// Per-solution, per-user state next to the solution (ADR 0070, roadmap F7): <c>&lt;solution folder&gt;/.codalus/state/&lt;solution
/// file name&gt;/</c> (<c>folder/</c> in folder mode), git-ignored by its own <c>.gitignore</c>, like the classic IDE's <c>.vs</c>.
/// <list type="bullet">
/// <item>Writes are serialized on the caller's thread and written in the background, one after another, through a
/// temporary file and a rename. <see cref="FlushAsync"/> waits for them.</item>
/// <item>A folder that can't be written (read-only, a share that refuses it) falls back to the user's state folder
/// (<c>&lt;state&gt;/solution-state/&lt;hash of the solution path&gt;/</c>), so nothing is lost; the next write that succeeds next to the
/// solution deletes the fallback copy. Reads take the newer of the two.</item>
/// <item>A legacy file (the shell's old <c>sessions/&lt;hash&gt;.json</c>) is moved in the first time its state is read.</item>
/// </list>
/// </summary>
public sealed class SolutionStateStore(string userStateDirectory, Action<string>? log = null)
{
    /// <summary>The editor session: open documents, breakpoints, startup projects, the configuration, Solution Explorer's expansion…</summary>
    public const string SessionFile = "session.json";

    /// <summary>The solution's bookmarks.</summary>
    public const string BookmarksFile = "bookmarks.json";

    private readonly Lock _gate = new();
    private Task _writes = Task.CompletedTask;

    /// <summary>Where state goes when the solution's folder can't be written.</summary>
    public string FallbackDirectory(SolutionStateKey key) => Path.Combine(userStateDirectory, "solution-state", Hash(key.Path));

    /// <summary>The shell's session file before ADR 0070: <c>&lt;state&gt;/sessions/&lt;hash&gt;.json</c>.</summary>
    public string LegacySessionFile(string solutionPath) => Path.Combine(userStateDirectory, "sessions", Hash(solutionPath) + ".json");

    /// <summary>The first 16 hex digits of the SHA-256 of the path in upper case (the shell's session key since M2).</summary>
    public static string Hash(string path) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant())))[..16];

    /// <summary>Whether <paramref name="path"/> is in the solution's folder (or below).</summary>
    public static bool IsInFolder(SolutionStateKey key, string path) => FileSystem.FileSystems.IsUnder(path, key.Folder);

    /// <summary>
    /// Reads a state file (after the pending writes), off the calling thread: next to the solution or in the fallback
    /// folder, whichever is newer; else <paramref name="legacyFile"/>, which is then moved in. Null when there is none.
    /// </summary>
    public async Task<T?> ReadAsync<T>(SolutionStateKey key, string name, JsonTypeInfo<T> info, string? legacyFile = null)
        where T : class
    {
        await FlushAsync().ConfigureAwait(false);
        return await Task.Run(() => Read(key, name, info, legacyFile)).ConfigureAwait(false);
    }

    /// <summary>Serializes <paramref name="value"/> now and writes it in the background.</summary>
    public Task WriteAsync<T>(SolutionStateKey key, string name, T value, JsonTypeInfo<T> info)
    {
        var text = JsonSerializer.Serialize(value, info);
        lock (_gate)
        {
            _writes = _writes.ContinueWith(_ => WriteNow(key, name, text), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            return _writes;
        }
    }

    /// <summary>
    /// Queues a write of what <paramref name="value"/> makes, serialized in the background in its turn (it must be safe
    /// to call from another thread: an immutable snapshot's): <see cref="FlushAsync"/> waits for it like any other.
    /// </summary>
    public Task WriteAsync<T>(SolutionStateKey key, string name, Func<T> value, JsonTypeInfo<T> info)
    {
        lock (_gate)
        {
            _writes = _writes.ContinueWith(_ => WriteNow(key, name, JsonSerializer.Serialize(value(), info)), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            return _writes;
        }
    }

    /// <summary>Waits for the writes queued so far.</summary>
    public Task FlushAsync()
    {
        Task pending;
        lock (_gate)
        {
            pending = _writes;
        }

        return pending.IsCompleted ? Task.CompletedTask : pending.ContinueWith(_ => { }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    private T? Read<T>(SolutionStateKey key, string name, JsonTypeInfo<T> info, string? legacyFile)
        where T : class
    {
        var primary = Path.Combine(key.Directory, name);
        var fallback = Path.Combine(FallbackDirectory(key), name);
        var file = Newer(primary, fallback);
        if (file is null && legacyFile is not null && File.Exists(legacyFile))
        {
            file = legacyFile;
        }

        if (file is null)
        {
            return null;
        }

        string text;
        T? value;
        try
        {
            text = File.ReadAllText(file);
            value = JsonSerializer.Deserialize(text, info);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            log?.Invoke($"[state] Ignoring {file}: {ex.Message}");
            return null;
        }

        if (file == legacyFile && WriteNow(key, name, text) is { } moved)
        {
            TryDelete(legacyFile);
            log?.Invoke($"[state] Moved {legacyFile} to {moved}.");
        }

        return value;
    }

    private static string? Newer(string primary, string fallback)
    {
        var primaryTime = File.Exists(primary) ? File.GetLastWriteTimeUtc(primary) : (DateTime?)null;
        var fallbackTime = File.Exists(fallback) ? File.GetLastWriteTimeUtc(fallback) : (DateTime?)null;
        return (primaryTime, fallbackTime) switch
        {
            (null, null) => null,
            (not null, null) => primary,
            (null, not null) => fallback,
            var (p, f) => f > p ? fallback : primary,
        };
    }

    /// <summary>Writes next to the solution, else in the fallback folder. Returns the file written, or null.</summary>
    private string? WriteNow(SolutionStateKey key, string name, string text)
    {
        var fallback = Path.Combine(FallbackDirectory(key), name);
        try
        {
            var file = Path.Combine(SolutionStateLayout.EnsureDirectory(key.Path, key.IsFolder), name);
            SolutionStateLayout.WriteAllTextAtomic(file, text);
            if (File.Exists(fallback))
            {
                TryDelete(fallback);
            }

            return file;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            log?.Invoke($"[state] Could not write {name} in {key.Directory} ({ex.Message}); keeping it in {FallbackDirectory(key)}.");
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(fallback)!);
            SolutionStateLayout.WriteAllTextAtomic(fallback, text);
            return fallback;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            log?.Invoke($"[state] Could not write {fallback}: {ex.Message}");
            return null;
        }
    }

    private void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log?.Invoke($"[state] Could not delete {file}: {ex.Message}");
        }
    }
}
