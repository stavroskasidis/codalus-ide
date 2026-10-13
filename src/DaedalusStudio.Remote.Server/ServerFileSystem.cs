using System.Collections.Concurrent;
using DaedalusStudio.Remote.Protocol;
using DaedalusStudio.Rpc;

namespace DaedalusStudio.Remote.Server;

/// <summary>The file-system provider (M21, ADR 0050): what the shell's editor and explorer read and write remotely.</summary>
public sealed partial class RemoteServer
{
    private readonly ConcurrentDictionary<int, Watcher> _watchers = new();

    private void RegisterFileSystem()
    {
        connection.OnRequest(RemoteMethods.Stat, Json.PathParams, Json.FileStat, (p, _) => Task.FromResult(Stat(p.Path)));
        connection.OnRequest(RemoteMethods.ReadFile, Json.PathParams, Json.ReadFileResult, async (p, ct) => new ReadFileResult(await Guard(p.Path, () => File.ReadAllBytesAsync(p.Path, ct)).ConfigureAwait(false)));
        connection.OnRequest(RemoteMethods.WriteFile, Json.WriteFileParams, Json.EmptyResult, async (p, ct) =>
        {
            await Guard(p.Path, async () =>
            {
                // Atomic: a temporary file in the same folder replaces the target, as the editor saves locally.
                var folder = Path.GetDirectoryName(p.Path)!;
                Directory.CreateDirectory(folder);
                var temporary = Path.Combine(folder, "." + Path.GetFileName(p.Path) + "." + Environment.ProcessId + ".tmp");
                await File.WriteAllBytesAsync(temporary, p.Content, ct).ConfigureAwait(false);
                File.Move(temporary, p.Path, overwrite: true);
                return 0;
            }).ConfigureAwait(false);
            return new EmptyResult();
        });
        connection.OnRequest(RemoteMethods.ReadDirectory, Json.PathParams, Json.ReadDirectoryResult, (p, _) => Guard(p.Path, () =>
        {
            var entries = new DirectoryInfo(p.Path).EnumerateFileSystemInfos()
                .Select(i => new DirectoryEntry(i.Name, i is DirectoryInfo, i is FileInfo f ? f.Length : 0, new DateTimeOffset(i.LastWriteTimeUtc).ToUnixTimeMilliseconds()))
                .OrderBy(e => e.Name, StringComparer.Ordinal)
                .ToList();
            return Task.FromResult(new ReadDirectoryResult(entries));
        }));
        connection.OnRequest(RemoteMethods.CreateDirectory, Json.PathParams, Json.EmptyResult, (p, _) =>
        {
            Directory.CreateDirectory(p.Path);
            return Task.FromResult(new EmptyResult());
        });
        connection.OnRequest(RemoteMethods.Delete, Json.DeleteParams, Json.EmptyResult, (p, _) => Guard(p.Path, () =>
        {
            if (Directory.Exists(p.Path))
            {
                Directory.Delete(p.Path, p.Recursive);
            }
            else if (File.Exists(p.Path))
            {
                File.Delete(p.Path);
            }
            else
            {
                throw new FileNotFoundException(p.Path);
            }

            return Task.FromResult(new EmptyResult());
        }));
        connection.OnRequest(RemoteMethods.Rename, Json.RenameParams, Json.EmptyResult, (p, _) => Guard(p.Source, () =>
        {
            if (Directory.Exists(p.Source))
            {
                Directory.Move(p.Source, p.Target);
            }
            else
            {
                File.Move(p.Source, p.Target, p.Overwrite);
            }

            return Task.FromResult(new EmptyResult());
        }));
        connection.OnRequest(RemoteMethods.Watch, Json.WatchParams, Json.EmptyResult, (p, _) => Task.FromResult(Watch(p)));
        connection.OnRequest(RemoteMethods.Unwatch, Json.UnwatchParams, Json.EmptyResult, (p, _) =>
        {
            if (_watchers.TryRemove(p.Id, out var watcher))
            {
                watcher.Dispose();
            }

            return Task.FromResult(new EmptyResult());
        });
    }

    internal static FileStat Stat(string path)
    {
        if (Directory.Exists(path))
        {
            return new FileStat(true, true, 0, new DateTimeOffset(Directory.GetLastWriteTimeUtc(path)).ToUnixTimeMilliseconds());
        }

        var file = new FileInfo(path);
        return file.Exists ? new FileStat(true, false, file.Length, new DateTimeOffset(file.LastWriteTimeUtc).ToUnixTimeMilliseconds()) : new FileStat(false, false, 0, 0);
    }

    /// <summary>File-system errors as JSON-RPC errors the client turns back into exceptions.</summary>
    private static async Task<T> Guard<T>(string path, Func<Task<T>> action)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new JsonRpcHandlerException(RemoteMethods.FileNotFound, $"'{path}' doesn't exist.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new JsonRpcHandlerException(JsonRpcConnection.InternalError, ex.Message);
        }
    }

    private EmptyResult Watch(WatchParams p)
    {
        var folder = Directory.Exists(p.Path) ? p.Path : Path.GetDirectoryName(p.Path);
        if (folder is null || !Directory.Exists(folder))
        {
            throw new JsonRpcHandlerException(RemoteMethods.FileNotFound, $"'{p.Path}' doesn't exist.");
        }

        var filter = Directory.Exists(p.Path) ? "*" : Path.GetFileName(p.Path);
        var watcher = new Watcher(p.Id, new FileSystemWatcher(folder, filter) { IncludeSubdirectories = p.Recursive && filter == "*" }, Send);
        if (_watchers.TryRemove(p.Id, out var old))
        {
            old.Dispose();
        }

        _watchers[p.Id] = watcher;
        return new EmptyResult();

        void Send(int id, IReadOnlyList<FileChange> changes) =>
            _ = Notify(RemoteMethods.Changed, new FileChangesParams(id, changes), Json.FileChangesParams);
    }

    private void StopWatchers()
    {
        foreach (var watcher in _watchers.Values)
        {
            watcher.Dispose();
        }

        _watchers.Clear();
    }

    /// <summary>A watched file or folder: events gathered for 100 ms, then sent as one notification.</summary>
    private sealed class Watcher : IDisposable
    {
        private readonly FileSystemWatcher _watcher;
        private readonly Lock _gate = new();
        private readonly List<FileChange> _pending = [];
        private readonly Action<int, IReadOnlyList<FileChange>> _send;
        private readonly int _id;
        private bool _scheduled;

        public Watcher(int id, FileSystemWatcher watcher, Action<int, IReadOnlyList<FileChange>> send)
        {
            _id = id;
            _watcher = watcher;
            _send = send;
            watcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size;
            watcher.Changed += (_, e) => Add(e.FullPath, "changed");
            watcher.Created += (_, e) => Add(e.FullPath, "created");
            watcher.Deleted += (_, e) => Add(e.FullPath, "deleted");
            watcher.Renamed += (_, e) =>
            {
                Add(e.OldFullPath, "deleted");
                Add(e.FullPath, "created");
            };
            watcher.EnableRaisingEvents = true;
        }

        private void Add(string path, string kind)
        {
            lock (_gate)
            {
                if (!_pending.Contains(new FileChange(path, kind)))
                {
                    _pending.Add(new FileChange(path, kind));
                }

                if (_scheduled)
                {
                    return;
                }

                _scheduled = true;
            }

            _ = Task.Delay(100).ContinueWith(_ =>
            {
                List<FileChange> changes;
                lock (_gate)
                {
                    changes = [.. _pending];
                    _pending.Clear();
                    _scheduled = false;
                }

                _send(_id, changes);
            }, TaskScheduler.Default);
        }

        public void Dispose() => _watcher.Dispose();
    }
}
