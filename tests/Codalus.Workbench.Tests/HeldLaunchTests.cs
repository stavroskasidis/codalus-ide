using Codalus.Workbench.Debugging;

namespace Codalus.Workbench.Tests;

/// <summary>
/// The startup hook's pid file, as a terminal launch under the debugger polls for it. On Windows another process (an
/// antivirus scanner) can hold the file open just after the hook renamed it into place: reading it then failed the
/// whole start with a sharing violation (TerminalLaunchTests on a Windows runner) instead of reading it on the next poll.
/// </summary>
public sealed class HeldLaunchTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "codalus-heldlaunch-" + Guid.NewGuid().ToString("N"));

    public HeldLaunchTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public async Task A_pid_file_another_process_holds_open_is_read_on_a_later_poll()
    {
        var pid = Path.Combine(_folder, "pid");
        Assert.Null(await HeldLaunch.ReadProcessIdAsync(pid, TestContext.Current.CancellationToken));

        await File.WriteAllTextAsync(pid, "4242", TestContext.Current.CancellationToken);
        using (new FileStream(pid, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Null(await HeldLaunch.ReadProcessIdAsync(pid, TestContext.Current.CancellationToken));
        }

        Assert.Equal(4242, await HeldLaunch.ReadProcessIdAsync(pid, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_pid_file_whose_folder_is_gone_reads_as_not_there_yet()
    {
        Assert.Null(await HeldLaunch.ReadProcessIdAsync(Path.Combine(_folder, "gone", "pid"), TestContext.Current.CancellationToken));
    }
}
