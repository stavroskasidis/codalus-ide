namespace Codalus.Workbench.FileSystem;

/// <summary>What a file system knows about a path.</summary>
public sealed record FileEntryInfo(bool Exists, bool IsDirectory, long Size, DateTime ModifiedUtc)
{
    public static FileEntryInfo Missing { get; } = new(false, false, -1, default);
}

public sealed record FileSystemEntry(string Name, bool IsDirectory, long Size, DateTime ModifiedUtc);

/// <summary>A change a watch saw: <c>created</c>, <c>changed</c> or <c>deleted</c>.</summary>
public sealed record FileChangeEvent(string Path, string Kind);

/// <summary>
/// Where files under a root live (F2, M21, ADR 0050): this machine, or a remote session's server. Paths are this
/// machine's form; a remote provider maps them to the other machine's.
/// </summary>
public interface IFileSystemProvider
{
    /// <summary>True when every operation crosses the network (callers avoid polling and local-only APIs).</summary>
    bool IsRemote { get; }

    Task<FileEntryInfo> StatAsync(string path, CancellationToken cancellationToken);

    Task<byte[]> ReadAllBytesAsync(string path, CancellationToken cancellationToken);

    /// <summary>Writes through a temporary file in the same folder, then replaces the target.</summary>
    Task WriteAllBytesAsync(string path, byte[] content, CancellationToken cancellationToken);

    Task<IReadOnlyList<FileSystemEntry>> ReadDirectoryAsync(string path, CancellationToken cancellationToken);

    Task CreateDirectoryAsync(string path, CancellationToken cancellationToken);

    Task DeleteAsync(string path, bool recursive, CancellationToken cancellationToken);

    Task RenameAsync(string source, string target, bool overwrite, CancellationToken cancellationToken);

    /// <summary>Watches a file or a folder; changes arrive in batches on any thread until the result is disposed.</summary>
    IDisposable Watch(string path, bool recursive, Action<IReadOnlyList<FileChangeEvent>> changed);
}

/// <summary>
/// The file systems by root (F2): a remote window mounts its server's under the folder its paths map to (ADR 0050),
/// everything else is this machine's. Process-wide, as a remote window is a process of its own.
/// </summary>
public static class FileSystems
{
    private static readonly Lock s_gate = new();
    private static readonly List<(string Root, IFileSystemProvider Provider)> s_mounts = [];

    public static IFileSystemProvider Local { get; } = new LocalFileSystem();

    private static readonly StringComparison s_comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>Serves paths under <paramref name="root"/> from <paramref name="provider"/> until the result is disposed.</summary>
    public static IDisposable Mount(string root, IFileSystemProvider provider)
    {
        var entry = (Path.TrimEndingDirectorySeparator(root), provider);
        lock (s_gate)
        {
            s_mounts.Add(entry);
            s_mounts.Sort((a, b) => b.Root.Length.CompareTo(a.Root.Length));
        }

        return new Unmount(() =>
        {
            lock (s_gate)
            {
                s_mounts.Remove(entry);
            }
        });
    }

    /// <summary>The file system of <paramref name="path"/>.</summary>
    public static IFileSystemProvider For(string path)
    {
        lock (s_gate)
        {
            foreach (var (root, provider) in s_mounts)
            {
                if (IsUnder(path, root))
                {
                    return provider;
                }
            }
        }

        return Local;
    }

    /// <summary>True when a remote file system serves <paramref name="path"/>.</summary>
    public static bool IsRemote(string path) => For(path).IsRemote;

    public static bool IsUnder(string path, string root) =>
        path.StartsWith(root, s_comparison) && (path.Length == root.Length || path[root.Length] is '/' or '\\' || root.EndsWith('/') || root.EndsWith('\\'));

    /// <summary>Whether <paramref name="path"/> is a folder (on whichever file system holds it).</summary>
    public static async Task<bool> DirectoryExistsAsync(string path, CancellationToken cancellationToken) =>
        For(path) is { IsRemote: true } remote
            ? (await remote.StatAsync(path, cancellationToken).ConfigureAwait(false)) is { Exists: true, IsDirectory: true }
            : Directory.Exists(path);

    public static async Task<bool> FileExistsAsync(string path, CancellationToken cancellationToken) =>
        For(path) is { IsRemote: true } remote
            ? (await remote.StatAsync(path, cancellationToken).ConfigureAwait(false)) is { Exists: true, IsDirectory: false }
            : File.Exists(path);

    private sealed class Unmount(Action action) : IDisposable
    {
        private Action? _action = action;

        public void Dispose() => Interlocked.Exchange(ref _action, null)?.Invoke();
    }
}

/// <summary>This machine's file system.</summary>
public sealed class LocalFileSystem : IFileSystemProvider
{
    public bool IsRemote => false;

    public Task<FileEntryInfo> StatAsync(string path, CancellationToken cancellationToken)
    {
        if (Directory.Exists(path))
        {
            return Task.FromResult(new FileEntryInfo(true, true, 0, Directory.GetLastWriteTimeUtc(path)));
        }

        var file = new FileInfo(path);
        return Task.FromResult(file.Exists ? new FileEntryInfo(true, false, file.Length, file.LastWriteTimeUtc) : FileEntryInfo.Missing);
    }

    /// <summary>
    /// Reads the file sharing it for writing and deleting (File.ReadAllBytesAsync shares it for reading only): on
    /// Windows that read fails while git or another tool has the file open to write it, and their write fails while
    /// the read lasts, as an open document reloading after a change on disk did in the middle of git's next one.
    /// </summary>
    public async Task<byte[]> ReadAllBytesAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 1, useAsync: true);
        using var content = new MemoryStream(stream.CanSeek ? (int)Math.Min(stream.Length, int.MaxValue) : 0);
        await stream.CopyToAsync(content, cancellationToken).ConfigureAwait(false);
        return content.ToArray();
    }

    public async Task WriteAllBytesAsync(string path, byte[] content, CancellationToken cancellationToken)
    {
        var folder = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(folder);
        var temporary = Path.Combine(folder, "." + Path.GetFileName(path) + "." + Environment.ProcessId + ".tmp");
        await File.WriteAllBytesAsync(temporary, content, cancellationToken).ConfigureAwait(false);
        File.Move(temporary, path, overwrite: true);
    }

    public Task<IReadOnlyList<FileSystemEntry>> ReadDirectoryAsync(string path, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<FileSystemEntry>>([.. new DirectoryInfo(path).EnumerateFileSystemInfos()
            .Select(i => new FileSystemEntry(i.Name, i is DirectoryInfo, i is FileInfo f ? f.Length : 0, i.LastWriteTimeUtc))
            .OrderBy(e => e.Name, StringComparer.Ordinal)]);

    public Task CreateDirectoryAsync(string path, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(path);
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string path, bool recursive, CancellationToken cancellationToken)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive);
        }
        else
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    public Task RenameAsync(string source, string target, bool overwrite, CancellationToken cancellationToken)
    {
        if (Directory.Exists(source))
        {
            Directory.Move(source, target);
        }
        else
        {
            File.Move(source, target, overwrite);
        }

        return Task.CompletedTask;
    }

    public IDisposable Watch(string path, bool recursive, Action<IReadOnlyList<FileChangeEvent>> changed)
    {
        var folder = Directory.Exists(path) ? path : Path.GetDirectoryName(path)!;
        var watcher = new FileSystemWatcher(folder, Directory.Exists(path) ? "*" : Path.GetFileName(path))
        {
            IncludeSubdirectories = recursive && Directory.Exists(path),
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
        };
        watcher.Changed += (_, e) => changed([new FileChangeEvent(e.FullPath, "changed")]);
        watcher.Created += (_, e) => changed([new FileChangeEvent(e.FullPath, "created")]);
        watcher.Deleted += (_, e) => changed([new FileChangeEvent(e.FullPath, "deleted")]);
        watcher.Renamed += (_, e) => changed([new FileChangeEvent(e.OldFullPath, "deleted"), new FileChangeEvent(e.FullPath, "created")]);
        watcher.EnableRaisingEvents = true;
        return watcher;
    }
}
