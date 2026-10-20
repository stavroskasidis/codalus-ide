using Codalus.Workbench.Debugging.Remote;

namespace Codalus.Workbench.Tests;

/// <summary>Debugging's remote targets (M14c) without a target: what a missing tool says.</summary>
public sealed class RemoteTargetsTests
{
    [Fact]
    public async Task A_missing_container_tool_says_how_to_get_one_not_dotnets_working_directory()
    {
        // Attach to a Process in a Container on a machine without Docker said "…: An error occurred trying to start process
        // 'docker' with working directory '/Volumes/…'. No such file or directory".
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => RemoteTargets.ContainersAsync(["codalus-no-such-container-tool"], TestContext.Current.CancellationToken));
        Assert.Equal($"codalus-no-such-container-tool isn't installed or isn't on the PATH. Install Docker or Podman, or set '{RemoteTargets.ContainerToolSetting}'.", error.Message);
    }
}
