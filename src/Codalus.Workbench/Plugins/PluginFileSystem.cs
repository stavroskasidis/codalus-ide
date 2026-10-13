using Codalus.Plugins.Protocol;
using Codalus.Rpc;
using Codalus.Workbench.FileSystem;

namespace Codalus.Workbench.Plugins;

/// <summary>
/// A plugin's file system (plugin API 1.0, M22a) as the editor's: mounted at <see cref="FileSystemRoots.RootOf"/>, each
/// call a request to the plugin's host, with the provider's paths (<c>/a/b.txt</c>) under the root.
/// </summary>
internal sealed class PluginFileSystem(string scheme, Func<JsonRpcConnection?> connection) : IFileSystemProvider
{
    private readonly List<(string Path, bool Recursive, Action<IReadOnlyList<FileChangeEvent>> Changed)> _watches = [];

    public string Root { get; } = FileSystemRoots.RootOf(scheme);

    // Remote for the editor: reads and writes go through the provider, never the disk.
    public bool IsRemote => true;

    private string ToProvider(string path)
    {
        var relative = path.Length <= Root.Length ? string.Empty : path[(Root.Length + 1)..];
        return "/" + relative.Replace('\\', '/');
    }

    private string ToLocal(string providerPath) =>
        providerPath is "/" or "" ? Root : Root + (OperatingSystem.IsWindows() ? "\\" : "/") + providerPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);

    private async Task<TResult> CallAsync<TResult>(string method, FsRequestParams parameters, System.Text.Json.Serialization.Metadata.JsonTypeInfo<TResult> result, CancellationToken cancellationToken)
    {
        var rpc = connection() ?? throw new IOException($"The plugin serving '{scheme}' isn't running.");
        try
        {
            return await rpc.InvokeAsync(method, parameters, PluginJsonContext.Default.FsRequestParams, result, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonRpcRemoteException ex)
        {
            throw ex.Code switch
            {
                FsErrors.NotFound => new FileNotFoundException(ex.Message, parameters.Path),
                FsErrors.Denied => new UnauthorizedAccessException(ex.Message),
                _ => new IOException(ex.Message, ex),
            };
        }
        catch (JsonRpcConnectionLostException ex)
        {
            throw new IOException($"The plugin serving '{scheme}' stopped.", ex);
        }
    }

    public async Task<FileEntryInfo> StatAsync(string path, CancellationToken cancellationToken)
    {
        var stat = await CallAsync(PluginMethods.FsStat, new FsRequestParams(scheme, ToProvider(path)), PluginJsonContext.Default.FsStatResult, cancellationToken).ConfigureAwait(false);
        return stat.Exists ? new FileEntryInfo(true, stat.IsDirectory, stat.Size, stat.Modified.UtcDateTime) : FileEntryInfo.Missing;
    }

    public async Task<byte[]> ReadAllBytesAsync(string path, CancellationToken cancellationToken) =>
        (await CallAsync(PluginMethods.FsReadFile, new FsRequestParams(scheme, ToProvider(path)), PluginJsonContext.Default.FsReadFileResult, cancellationToken).ConfigureAwait(false)).Content;

    public async Task WriteAllBytesAsync(string path, byte[] content, CancellationToken cancellationToken) =>
        await CallAsync(PluginMethods.FsWriteFile, new FsRequestParams(scheme, ToProvider(path)) { Content = content }, PluginJsonContext.Default.EmptyResult, cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<FileSystemEntry>> ReadDirectoryAsync(string path, CancellationToken cancellationToken) =>
        [.. (await CallAsync(PluginMethods.FsReadDirectory, new FsRequestParams(scheme, ToProvider(path)), PluginJsonContext.Default.FsReadDirectoryResult, cancellationToken).ConfigureAwait(false))
            .Entries.Select(e => new FileSystemEntry(e.Name, e.IsDirectory, e.Size, e.Modified.UtcDateTime))];

    public async Task CreateDirectoryAsync(string path, CancellationToken cancellationToken) =>
        await CallAsync(PluginMethods.FsCreateDirectory, new FsRequestParams(scheme, ToProvider(path)), PluginJsonContext.Default.EmptyResult, cancellationToken).ConfigureAwait(false);

    public async Task DeleteAsync(string path, bool recursive, CancellationToken cancellationToken) =>
        await CallAsync(PluginMethods.FsDelete, new FsRequestParams(scheme, ToProvider(path)) { Recursive = recursive }, PluginJsonContext.Default.EmptyResult, cancellationToken).ConfigureAwait(false);

    public async Task RenameAsync(string source, string target, bool overwrite, CancellationToken cancellationToken) =>
        await CallAsync(PluginMethods.FsRename, new FsRequestParams(scheme, ToProvider(source)) { Target = ToProvider(target), Overwrite = overwrite }, PluginJsonContext.Default.EmptyResult, cancellationToken).ConfigureAwait(false);

    public IDisposable Watch(string path, bool recursive, Action<IReadOnlyList<FileChangeEvent>> changed)
    {
        var watch = (path, recursive, changed);
        lock (_watches)
        {
            _watches.Add(watch);
        }

        return new Disposer(() =>
        {
            lock (_watches)
            {
                _watches.Remove(watch);
            }
        });
    }

    /// <summary>The plugin said files changed: the watches over them hear of it, in this machine's paths.</summary>
    public void OnChanged(IReadOnlyList<string> providerPaths, string kind)
    {
        var events = providerPaths.Select(p => new FileChangeEvent(ToLocal(p), kind)).ToList();
        List<(string Path, bool Recursive, Action<IReadOnlyList<FileChangeEvent>> Changed)> watches;
        lock (_watches)
        {
            watches = [.. _watches];
        }

        foreach (var (path, recursive, changed) in watches)
        {
            var matching = events.Where(e => string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase)
                || string.Equals(Path.GetDirectoryName(e.Path), path, StringComparison.OrdinalIgnoreCase)
                || (recursive && FileSystems.IsUnder(e.Path, path))).ToList();
            if (matching.Count > 0)
            {
                changed(matching);
            }
        }
    }

    private sealed class Disposer(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}
