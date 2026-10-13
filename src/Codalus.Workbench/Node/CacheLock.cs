namespace Codalus.Workbench.Node;

/// <summary>
/// A lock file that keeps two processes (two windows, or test assemblies sharing <c>CODALUS_NODE_CACHE</c>) from
/// installing into the same cache folder at once. <see cref="FileShare.None"/> is a sharing lock on Windows and
/// <c>flock</c> on Unix; the OS releases it when its process ends. The file itself stays (deleting it would let a waiter
/// lock an unlinked file while a third process locks a new one).
/// </summary>
internal static class CacheLock
{
    public static async Task<IDisposable> AcquireAsync(string path, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        while (true)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, bufferSize: 1);
            }
            catch (IOException)
            {
                // Another process is installing.
                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
