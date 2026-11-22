using System.Text;

namespace Codalus.Workbench.FileSystem;

/// <summary>
/// Reads a user's file without keeping a writer out. <c>File.ReadAllText</c> shares the file for reading only, so on
/// Windows an editor's or a tool's save fails while the read lasts ("being used by another process"): a test's write of
/// <c>launchSettings.json</c> failed while the Debug Properties page read it (Windows CI, PR #508's run 44963672088).
/// </summary>
public static class SharedFile
{
    public static string ReadAllText(string path)
    {
        using var stream = Open(path, useAsync: false);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    public static async Task<string> ReadAllTextAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = Open(path, useAsync: true);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }

    private static FileStream Open(string path, bool useAsync) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 4096, useAsync);
}
