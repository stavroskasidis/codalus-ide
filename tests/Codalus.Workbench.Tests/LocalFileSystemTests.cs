using Codalus.Workbench.FileSystem;

namespace Codalus.Workbench.Tests;

/// <summary>This machine's file system (F2): reading a file shares it with whoever writes it meanwhile.</summary>
public sealed class LocalFileSystemTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("codalus-localfs-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public async Task A_file_another_process_has_open_for_writing_is_read()
    {
        // An open document reloads its file when it changes on disk; git, a formatter or a test writing it again at
        // that moment holds it open for writing. On Windows a read that shares the file for reading only fails then
        // (and makes their next write fail while it reads).
        var path = Path.Combine(_folder, "Greeter.cs");
        await File.WriteAllTextAsync(path, "class Greeter { }", TestContext.Current.CancellationToken);
        await using var writer = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);

        var bytes = await FileSystems.Local.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        Assert.Equal("class Greeter { }", System.Text.Encoding.UTF8.GetString(bytes));
    }
}
