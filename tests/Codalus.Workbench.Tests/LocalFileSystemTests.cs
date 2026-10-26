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

    [Fact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public async Task A_write_keeps_the_files_unix_permissions()
    {
        // Written through a temporary file and a rename: the file kept the temporary's 644, so an executable script lost
        // its x bit and git saw a mode change after a save (M51, on WSL, where files copied from Windows are 755).
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows has no Unix permissions.");
        var path = Path.Combine(_folder, "build.sh");
        await File.WriteAllTextAsync(path, "echo one\n", TestContext.Current.CancellationToken);
        const UnixFileMode Executable = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
        File.SetUnixFileMode(path, Executable);

        await FileSystems.Local.WriteAllBytesAsync(path, "echo two\n"u8.ToArray(), TestContext.Current.CancellationToken);
        Assert.Equal(Executable, File.GetUnixFileMode(path));
        Assert.Equal("echo two\n", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));

        // A new file gets the default.
        var fresh = Path.Combine(_folder, "new.txt");
        await FileSystems.Local.WriteAllBytesAsync(fresh, "x"u8.ToArray(), TestContext.Current.CancellationToken);
        Assert.False(File.GetUnixFileMode(fresh).HasFlag(UnixFileMode.UserExecute));
    }
}
