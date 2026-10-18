using System.Diagnostics;
using Codalus.Workbench.Containers;
using Codalus.Workbench.Debugging.Remote;

namespace Codalus.Workbench.Tests;

/// <summary>
/// Dev Containers (M21b): reading <c>devcontainer.json</c>, and bringing one up with Podman and the local Ubuntu image
/// (in WSL on Windows, here on Linux; skipped without them; never pulled).
/// </summary>
public sealed class DevContainerTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("codalus-devc-").FullName;

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(_folder, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(_folder, recursive: true);
    }

    private string Write(string relative, string text)
    {
        var path = Path.Combine([_folder, .. relative.Split('/')]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    [Fact]
    public void A_devcontainer_json_is_read_with_its_variables_and_what_isnt_supported_is_named()
    {
        var app = Path.Combine(_folder, "MyApp");
        Directory.CreateDirectory(app);
        var file = Write("MyApp/.devcontainer/devcontainer.json", """
            {
              // JSON with comments, as the spec allows
              "name": "My app",
              "build": { "dockerfile": "Dockerfile", "context": "..", "args": { "VARIANT": "${localWorkspaceFolderBasename}" } },
              "forwardPorts": [5000, 5001],
              "postCreateCommand": ["dotnet", "restore"],
              "containerEnv": { "WORKSPACE": "${containerWorkspaceFolder}" },
              "runArgs": ["--cap-add=SYS_PTRACE"],
              "mounts": ["source=cache,target=/root/.nuget,type=volume"],
              "remoteUser": "vscode",
              "features": { "ghcr.io/devcontainers/features/node:1": {} },
            }
            """);

        Assert.Equal(file, DevContainers.Find(app));
        var config = DevContainers.Parse(file, app);
        Assert.Equal("My app", config.Name);
        Assert.Equal(Path.Combine(app, ".devcontainer", "Dockerfile"), config.Dockerfile);
        Assert.Equal(app, config.BuildContext);
        Assert.Equal("MyApp", config.BuildArguments["VARIANT"]);
        Assert.Equal("/workspaces/MyApp", config.WorkspaceFolder);
        Assert.Equal([5000, 5001], config.ForwardPorts);
        Assert.Equal("dotnet restore", config.PostCreateCommand);
        Assert.Equal("/workspaces/MyApp", config.ContainerEnv["WORKSPACE"]);
        Assert.Equal(["--cap-add=SYS_PTRACE"], config.RunArgs);
        Assert.Equal("vscode", config.RemoteUser);
        Assert.Equal(["features"], config.Unsupported);

        // The container: named after the folder (stable), labelled, the folder mounted at the workspace.
        var name = DevContainers.ContainerName(app);
        Assert.StartsWith("codalus-dev-myapp-", name, StringComparison.Ordinal);
        Assert.Equal(name, DevContainers.ContainerName(app + Path.DirectorySeparatorChar));
        var spec = DevContainers.RunSpec(config, app, new ContainerEngine(["docker"]), "image:tag");
        Assert.Contains($"{app}:/workspaces/MyApp", spec.Mounts);
        Assert.Contains(new KeyValuePair<string, string>(DevContainers.FolderLabel, Path.GetFullPath(app)), spec.Labels);
        Assert.Contains("--mount", spec.ExtraArguments);
        Assert.Equal("/workspaces/MyApp", spec.WorkingDirectory);

        // A .devcontainer.json at the top, and an image instead of a Dockerfile.
        var other = Path.Combine(_folder, "Other");
        Directory.CreateDirectory(other);
        var top = Write("Other/.devcontainer.json", """{ "image": "mcr.microsoft.com/dotnet/sdk:10.0", "workspaceFolder": "/src" }""");
        Assert.Equal(top, DevContainers.Find(other));
        Assert.Equal(("mcr.microsoft.com/dotnet/sdk:10.0", "/src"), (DevContainers.Parse(top, other).Image, DevContainers.Parse(top, other).WorkspaceFolder));
        Assert.Null(DevContainers.Find(_folder));
    }

    [Fact]
    public async Task A_dev_container_comes_up_with_the_folder_mounted_runs_postcreate_once_and_is_reused()
    {
        IReadOnlyList<string> tool;
        if (OperatingSystem.IsWindows())
        {
            var distribution = Environment.GetEnvironmentVariable("CODALUS_TEST_WSL") is { Length: > 0 } d ? d : "Ubuntu";
            if (!(await RemoteTargets.WslDistributionsAsync(CancellationToken.None)).Contains(distribution, StringComparer.OrdinalIgnoreCase))
            {
                Assert.Skip("No WSL distribution with Podman.");
            }

            tool = ["wsl.exe", "-d", distribution, "--exec", "podman"];
        }
        else if (OperatingSystem.IsLinux())
        {
            tool = ["podman"];
        }
        else
        {
            Assert.Skip("Podman stand-in: Linux, or WSL on Windows.");
            return;
        }

        var engine = new ContainerEngine(tool);
        try
        {
            if (!(await engine.RunAsync(["image", "exists", "docker.io/library/ubuntu:24.04"], TestContext.Current.CancellationToken)).Succeeded)
            {
                Assert.Skip("No docker.io/library/ubuntu:24.04 image (tests never pull).");
            }
        }
        catch (InvalidOperationException e) when (e.InnerException is System.ComponentModel.Win32Exception)
        {
            // ContainerEngine reports a tool that won't start as "isn't installed". Locally that skips; CI's Linux
            // runner has Podman, so there it fails (CLAUDE.md: prerequisites skip locally, fail in CI).
            if (Environment.GetEnvironmentVariable("CI") == "true" && OperatingSystem.IsLinux())
            {
                Assert.Fail(e.Message);
            }

            Assert.Skip("No podman.");
        }

        var app = Path.Combine(_folder, "Web App");
        Write("Web App/hello.txt", "from the host\n");
        var file = Write("Web App/.devcontainer/devcontainer.json", """
            { "image": "docker.io/library/ubuntu:24.04", "postCreateCommand": "echo created >> postcreate.txt && cat hello.txt > seen.txt" }
            """);
        var config = DevContainers.Parse(file, app);
        var lines = new List<string>();
        string? name = null;
        try
        {
            name = await DevContainers.UpAsync(engine, config, app, lines.Add, TestContext.Current.CancellationToken);
            Assert.Equal(DevContainers.ContainerName(app), name);

            // The folder is mounted at the workspace: postCreateCommand ran there, once, and read the host's file.
            Assert.Equal("from the host\n", await File.ReadAllTextAsync(Path.Combine(app, "seen.txt"), TestContext.Current.CancellationToken));
            Assert.Equal("created\n", await File.ReadAllTextAsync(Path.Combine(app, "postcreate.txt"), TestContext.Current.CancellationToken));
            var inside = await engine.RunAsync(["exec", name, "sh", "-c", "pwd; ls"], TestContext.Current.CancellationToken);
            Assert.Contains("/workspaces/Web App", inside.Output, StringComparison.Ordinal);

            // Again: the running container is reused, postCreateCommand doesn't run again.
            Assert.Equal(name, await DevContainers.UpAsync(engine, config, app, lines.Add, TestContext.Current.CancellationToken));
            Assert.Equal("created\n", await File.ReadAllTextAsync(Path.Combine(app, "postcreate.txt"), TestContext.Current.CancellationToken));
            Assert.Contains(lines, l => l.Contains("is running", StringComparison.Ordinal));
        }
        finally
        {
            if (name is not null)
            {
                await engine.RunAsync(["rm", "-f", "-v", name], CancellationToken.None);
            }
        }
    }
}
