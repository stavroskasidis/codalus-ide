using Codalus.Remote.Protocol;
using Codalus.Rpc;
using Codalus.Workbench.FileSystem;

namespace Codalus.Workbench.Remote;

/// <summary>
/// The server's file system (F2, M21): mounted under the session's local root, so the editor opens, saves and watches
/// remote files by their local paths (<see cref="RemotePathMap"/>). A missing file is a <see cref="FileNotFoundException"/>,
/// as locally.
/// </summary>
public sealed class RemoteFileSystem(RemoteConnection connection, RemotePathMap map) : IFileSystemProvider
{
    private static readonly RemoteJsonContext Json = RemoteJsonContext.Default;

    public bool IsRemote => true;

    public async Task<FileEntryInfo> StatAsync(string path, CancellationToken cancellationToken)
    {
        var stat = await Call(path, () => connection.Control.InvokeAsync(RemoteMethods.Stat, new PathParams(map.ToRemote(path)), Json.PathParams, Json.FileStat, cancellationToken)).ConfigureAwait(false);
        return stat.Exists ? new FileEntryInfo(true, stat.IsDirectory, stat.Size, DateTimeOffset.FromUnixTimeMilliseconds(stat.Modified).UtcDateTime) : FileEntryInfo.Missing;
    }

    public async Task<byte[]> ReadAllBytesAsync(string path, CancellationToken cancellationToken) =>
        (await Call(path, () => connection.Control.InvokeAsync(RemoteMethods.ReadFile, new PathParams(map.ToRemote(path)), Json.PathParams, Json.ReadFileResult, cancellationToken)).ConfigureAwait(false)).Content;

    public Task WriteAllBytesAsync(string path, byte[] content, CancellationToken cancellationToken) =>
        Call(path, () => connection.Control.InvokeAsync(RemoteMethods.WriteFile, new WriteFileParams(map.ToRemote(path), content), Json.WriteFileParams, Json.EmptyResult, cancellationToken));

    public async Task<IReadOnlyList<FileSystemEntry>> ReadDirectoryAsync(string path, CancellationToken cancellationToken)
    {
        var result = await Call(path, () => connection.Control.InvokeAsync(RemoteMethods.ReadDirectory, new PathParams(map.ToRemote(path)), Json.PathParams, Json.ReadDirectoryResult, cancellationToken)).ConfigureAwait(false);
        return [.. result.Entries.Select(e => new FileSystemEntry(e.Name, e.IsDirectory, e.Size, DateTimeOffset.FromUnixTimeMilliseconds(e.Modified).UtcDateTime))];
    }

    public Task CreateDirectoryAsync(string path, CancellationToken cancellationToken) =>
        Call(path, () => connection.Control.InvokeAsync(RemoteMethods.CreateDirectory, new PathParams(map.ToRemote(path)), Json.PathParams, Json.EmptyResult, cancellationToken));

    public Task DeleteAsync(string path, bool recursive, CancellationToken cancellationToken) =>
        Call(path, () => connection.Control.InvokeAsync(RemoteMethods.Delete, new DeleteParams(map.ToRemote(path), recursive), Json.DeleteParams, Json.EmptyResult, cancellationToken));

    public Task RenameAsync(string source, string target, bool overwrite, CancellationToken cancellationToken) =>
        Call(source, () => connection.Control.InvokeAsync(RemoteMethods.Rename, new RenameParams(map.ToRemote(source), map.ToRemote(target), overwrite), Json.RenameParams, Json.EmptyResult, cancellationToken));

    public IDisposable Watch(string path, bool recursive, Action<IReadOnlyList<FileChangeEvent>> changed)
    {
        var watch = connection.WatchAsync(map.ToRemote(path), recursive, changes => changed([.. changes.Select(c => new FileChangeEvent(map.ToLocal(c.Path), c.Kind))]), CancellationToken.None);
        return new Unwatch(watch);
    }

    private static async Task<T> Call<T>(string path, Func<Task<T>> call)
    {
        try
        {
            return await call().ConfigureAwait(false);
        }
        catch (JsonRpcRemoteException ex) when (ex.Code == RemoteMethods.FileNotFound)
        {
            throw new FileNotFoundException(ex.Message, path);
        }
        catch (JsonRpcRemoteException ex)
        {
            throw new IOException(ex.Message, ex);
        }
        catch (JsonRpcConnectionLostException ex)
        {
            throw new IOException($"The connection to the remote machine was lost ({path}).", ex);
        }
    }

    private sealed class Unwatch(Task<IAsyncDisposable> watch) : IDisposable
    {
        public void Dispose() => _ = watch.ContinueWith(t => t.IsCompletedSuccessfully ? t.Result.DisposeAsync().AsTask() : Task.CompletedTask, TaskScheduler.Default);
    }
}
