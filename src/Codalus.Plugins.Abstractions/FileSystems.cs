namespace Codalus.Plugins;

/// <summary>What a provider knows about a path.</summary>
/// <param name="Exists">Whether the path exists.</param>
/// <param name="IsDirectory">Whether it's a directory.</param>
/// <param name="Size">A file's size in bytes.</param>
/// <param name="Modified">When it last changed.</param>
public sealed record FileStat(bool Exists, bool IsDirectory, long Size, DateTimeOffset Modified)
{
    /// <summary>A path that doesn't exist.</summary>
    public static FileStat Missing { get; } = new(false, false, -1, default);
}

/// <summary>An entry of a directory.</summary>
/// <param name="Name">Its name (no separators).</param>
/// <param name="IsDirectory">Whether it's a directory.</param>
/// <param name="Size">A file's size in bytes.</param>
/// <param name="Modified">When it last changed.</param>
public sealed record DirectoryEntry(string Name, bool IsDirectory, long Size, DateTimeOffset Modified);

/// <summary>
/// Files a plugin serves under a scheme (API 1.0, M22a): an archive's contents, a cloud store, a database's scripts.
/// The IDE shows them under a root of their own (<see cref="IFileSystems.RootOf"/>) and its editor reads and saves
/// through the provider. Paths are the provider's own: <c>/</c>-separated, starting with <c>/</c> (<c>/</c> is the
/// root). A read-only provider leaves the write members as they are.
/// </summary>
public interface IFileSystemProvider
{
    /// <summary>What <paramref name="path"/> is; <see cref="FileStat.Missing"/> when it doesn't exist.</summary>
    Task<FileStat> StatAsync(string path, CancellationToken cancellationToken);

    /// <summary>The entries of the directory <paramref name="path"/>.</summary>
    Task<IReadOnlyList<DirectoryEntry>> ReadDirectoryAsync(string path, CancellationToken cancellationToken);

    /// <summary>The file's content.</summary>
    Task<byte[]> ReadFileAsync(string path, CancellationToken cancellationToken);

    /// <summary>Creates or replaces the file.</summary>
    Task WriteFileAsync(string path, byte[] content, CancellationToken cancellationToken) => throw new UnauthorizedAccessException("This file system is read-only.");

    /// <summary>Creates the directory (and its parents).</summary>
    Task CreateDirectoryAsync(string path, CancellationToken cancellationToken) => throw new UnauthorizedAccessException("This file system is read-only.");

    /// <summary>Deletes the file or directory (a non-empty directory only when <paramref name="recursive"/>).</summary>
    Task DeleteAsync(string path, bool recursive, CancellationToken cancellationToken) => throw new UnauthorizedAccessException("This file system is read-only.");

    /// <summary>Moves or renames.</summary>
    Task RenameAsync(string source, string target, bool overwrite, CancellationToken cancellationToken) => throw new UnauthorizedAccessException("This file system is read-only.");
}

/// <summary>File systems plugins provide (API 1.0, M22a).</summary>
public interface IFileSystems
{
    /// <summary>Serves <paramref name="scheme"/> (letters, digits, <c>-</c> and <c>.</c>) from <paramref name="provider"/> until disposed.</summary>
    IDisposable RegisterProvider(string scheme, IFileSystemProvider provider);

    /// <summary>Where the IDE shows <paramref name="scheme"/>'s files: a path on this machine's form, under which <c>/a/b.txt</c> is <c>a/b.txt</c>.</summary>
    string RootOf(string scheme);

    /// <summary>Tells the IDE files changed (<c>created</c>, <c>changed</c> or <c>deleted</c>), so open editors reload.</summary>
    void NotifyChanged(string scheme, IReadOnlyList<string> paths, string kind = "changed");
}
