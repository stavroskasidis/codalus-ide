using System.Reflection;
using System.Text.Json.Nodes;
using Codalus.Projects.Protocol;
using Codalus.Workbench.Build;
using Codalus.Workbench.Containers;

namespace Codalus.Workbench.Tests;

/// <summary>Container tooling without a container engine (M17a, ADR 0042): parsing the CLI's output, Dockerfiles, Add Docker Support and the container plan.</summary>
public sealed class ContainerToolsTests
{
    internal static string RepoRoot => typeof(ContainerToolsTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "CodalusRepoRoot").Value!;

    private static string Sample(params string[] parts) =>
        Path.Combine([Path.Combine(RepoRoot, "samples", "Containers"), .. parts]);

    [Fact]
    public void Ps_and_images_output_is_read_from_both_engines()
    {
        // docker: names without a slash; podman: the same template, the id longer.
        var containers = ContainerEngine.ParseContainers(
            "0123456789abcdef0123\tcodalus-web-1a2b3c4d\tlocalhost/codalus-web-1a2b3c4d:dev\trunning\tUp 3 seconds\t127.0.0.1:49153->8080/tcp\n"
            + "fedcba987654\tdb\tdocker.io/library/postgres:17-alpine\texited\tExited (0) 2 hours ago\t\n");
        Assert.Equal(2, containers.Count);
        Assert.Equal("0123456789ab", containers[0].Id);
        Assert.Equal("codalus-web-1a2b3c4d", containers[0].Name);
        Assert.True(containers[0].IsRunning);
        Assert.Equal("127.0.0.1:49153->8080/tcp", containers[0].Ports);
        Assert.False(containers[1].IsRunning);
        Assert.Empty(containers[1].Ports);

        var images = ContainerEngine.ParseImages("mcr.microsoft.com/dotnet/aspnet\t10.0\tsha256:3eed6d1eb4064\t230MB\t2 weeks ago\n<none>\t<none>\tabcdef123456\t1MB\t1 hour ago\n");
        Assert.Equal("mcr.microsoft.com/dotnet/aspnet:10.0", images[0].Reference);
        Assert.Equal("3eed6d1eb406", images[0].Id);
        Assert.Equal("abcdef123456", images[1].Reference);

        var files = ContainerEngine.ParseFiles("./\n../\nWeb.dll\nruntimes/\n.hidden\n");
        Assert.Equal(["runtimes", ".hidden", "Web.dll"], files.Select(f => f.Name));
        Assert.True(files[0].IsDirectory);
    }

    [Fact]
    public void Inspect_json_gives_the_environment_ports_mounts_and_labels()
    {
        const string json = """
            [{
              "Id": "a7c08803b5de1a9704b2b22535040663",
              "Name": "/codalus-web",
              "State": { "Status": "running", "Pid": 4242, "ExitCode": 0 },
              "Config": {
                "Image": "codalus-web:dev",
                "Env": ["PATH=/usr/bin", "ASPNETCORE_HTTP_PORTS=8080", "EMPTY="],
                "WorkingDir": "/app",
                "Entrypoint": ["tail"],
                "Cmd": ["-f", "/dev/null"],
                "Labels": { "dev.codalus.project": "C:\\src\\Web\\Web.csproj", "a": "b" }
              },
              "NetworkSettings": { "Ports": { "8080/tcp": [{ "HostIp": "127.0.0.1", "HostPort": "49153" }], "8081/tcp": null } },
              "Mounts": [{ "Type": "bind", "Source": "/mnt/c/src/Web/bin/Debug/net10.0", "Destination": "/app", "RW": true }]
            }]
            """;
        var details = ContainerEngine.ParseInspect(json)!;
        Assert.Equal("a7c08803b5de", details.Id);
        Assert.Equal("codalus-web", details.Name);
        Assert.Equal(4242, details.ProcessId);
        Assert.Contains(details.Environment, e => e is { Key: "ASPNETCORE_HTTP_PORTS", Value: "8080" });
        Assert.Contains(details.Environment, e => e is { Key: "EMPTY", Value: "" });
        Assert.Equal([new ContainerPortMapping("8080/tcp", "127.0.0.1", "49153"), new ContainerPortMapping("8081/tcp", null, null)], details.Ports);
        Assert.Equal("/app", Assert.Single(details.Mounts).Destination);
        Assert.Equal(["a", "dev.codalus.project"], details.Labels.Select(l => l.Key));
        Assert.Equal("tail -f /dev/null", details.Command);
        Assert.Null(ContainerEngine.ParseInspect("not json"));
    }

    [Fact]
    public void The_engine_is_the_setting_else_docker_else_podman()
    {
        Assert.Equal(["wsl", "-d", "Ubuntu", "--exec", "podman"], ContainerEngine.Resolve("wsl -d Ubuntu --exec podman").Tool);
        Assert.Equal("podman", ContainerEngine.Resolve("wsl -d Ubuntu --exec podman").Name);
        Assert.Equal(["docker"], ContainerEngine.Resolve(null, p => p == "docker").Tool);
        Assert.Equal(["podman"], ContainerEngine.Resolve("", p => p == "podman").Tool);
        Assert.Equal(["docker"], ContainerEngine.Resolve(null, _ => false).Tool);
        Assert.Equal("containers", ContainerEngine.ComposeProjectName(Path.Combine("x", "Containers", "docker-compose.yml")));
    }

    [Fact]
    public void A_dockerfile_gives_its_stages_and_exposed_ports()
    {
        var info = DockerfileInfo.Parse("""
            ARG VERSION=10.0
            # a comment
            FROM mcr.microsoft.com/dotnet/aspnet:${VERSION} AS base
            EXPOSE 8080 8081/tcp
            FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:$VERSION AS build
            RUN dotnet build \
                -c Release
            EXPOSE 8080
            FROM base
            """);
        Assert.Equal([new DockerfileStage("mcr.microsoft.com/dotnet/aspnet:10.0", "base"), new DockerfileStage("mcr.microsoft.com/dotnet/sdk:10.0", "build"), new DockerfileStage("base", null)], info.Stages);
        Assert.Equal([8080, 8081], info.ExposedPorts);
        Assert.Equal("base", info.BaseStage!.Name);
    }

    [Fact]
    public void Add_docker_support_writes_the_templates_dockerfile_and_profile()
    {
        Assert.Equal("10.0", DockerSupport.ImageVersion("net10.0"));
        Assert.Equal("8.0", DockerSupport.ImageVersion("net8.0-windows"));
        Assert.Equal("3.1", DockerSupport.ImageVersion("netcoreapp3.1"));
        Assert.Null(DockerSupport.ImageVersion("netstandard2.0"));
        Assert.Null(DockerSupport.ImageVersion("net48"));

        // The fixture's Dockerfile is what Add Docker Support writes for it (context: the solution folder).
        var project = Sample("Web", "Web.csproj");
        var generated = DockerSupport.Dockerfile(new DockerProject(project, "net10.0", IsWeb: true, []), Sample());
        Assert.Equal(File.ReadAllText(Sample("Web", "Dockerfile")).ReplaceLineEndings("\n"), generated);
        Assert.Equal(Sample(), DockerSupport.BuildContext(project, Sample()));

        // A console app with a referenced library: the runtime image, both project files restored first.
        var root = Path.Combine(Path.GetTempPath(), "s");
        var console = DockerSupport.Dockerfile(new DockerProject(Path.Combine(root, "App", "App.csproj"), "net8.0", IsWeb: false, [Path.Combine(root, "Lib", "Lib.csproj")]), root);
        Assert.Contains("FROM mcr.microsoft.com/dotnet/runtime:8.0 AS base", console, StringComparison.Ordinal);
        Assert.Contains("COPY [\"Lib/Lib.csproj\", \"Lib/\"]", console, StringComparison.Ordinal);
        Assert.DoesNotContain("EXPOSE", console, StringComparison.Ordinal);

        var settings = DockerSupport.AddContainerProfile("""{ "profiles": { "http": { "commandName": "Project" } } }""", isWeb: true);
        var profiles = LaunchSettings.Parse(settings);
        Assert.Equal(["http", DockerSupport.ContainerProfileName], profiles.Select(p => p.Name));
        Assert.Equal(LaunchProfile.Docker, profiles[1].CommandName);
        Assert.True(profiles[1].PublishAllPorts);
        Assert.Equal(settings, DockerSupport.AddContainerProfile(settings, isWeb: true)); // kept once there
        Assert.Single(LaunchSettings.Parse(DockerSupport.AddContainerProfile(null, isWeb: false)));
    }

    [Fact]
    public void A_fast_mode_plan_mounts_the_build_output_on_the_base_stage()
    {
        var project = Sample("Web", "Web.csproj");
        var output = Sample("Web", "bin", "Debug", "net10.0");
        var info = new RunInfo(project, "net10.0", "Exe", Path.Combine(output, "Web.dll"), "dotnet", "exec Web.dll", output, Sample("Web", "Properties", "launchSettings.json"));
        var profile = LaunchSettings.Read(info.LaunchSettings, out _).Single(p => p.CommandName == LaunchProfile.Docker) with { LaunchBrowser = true, HttpPort = 5099 };
        var environment = new Dictionary<string, string> { ["ASPNETCORE_HTTP_PORTS"] = "8080", ["DOTNET_LAUNCH_PROFILE"] = "x", ["ASPNETCORE_URLS"] = "http://localhost:5000" };
        var ports = new Queue<int>([6001, 6002]);
        var launch = ContainerLaunch.Plan(info, profile, Sample("Web", "Dockerfile"), Sample(), fastMode: true, environment, freePort: ports.Dequeue);

        Assert.True(launch.FastMode);
        Assert.Equal("base", launch.Target);
        Assert.Equal(DockerSupport.ContainerName(project) + ":dev", launch.Image);
        Assert.StartsWith("codalus-web-", launch.Name, StringComparison.Ordinal);
        Assert.Equal(Sample(), launch.Context);
        Assert.Equal(["127.0.0.1:5099:8080"], launch.Run.Ports); // 8081 (HTTPS) isn't published without HTTPS ports
        Assert.Equal([output + ":/app"], launch.Run.Mounts);
        Assert.Equal("tail", launch.Run.Entrypoint);
        Assert.Contains(launch.Run.Labels, l => l.Key == ContainerEngine.ProjectLabel && l.Value == project);
        Assert.DoesNotContain(launch.Run.Environment, e => e.Key is "DOTNET_LAUNCH_PROFILE" or "ASPNETCORE_URLS");
        Assert.Contains(launch.Run.Environment, e => e is { Key: "ASPNETCORE_ENVIRONMENT", Value: "Development" });
        Assert.Equal("http://localhost:5099", launch.BrowserUrl);
        Assert.Equal(["exec", "-i", "-w", "/app", launch.Name, "dotnet", "/app/Web.dll"], launch.ExecArguments());

        var arguments = ContainerEngine.RunArguments(launch.Run);
        Assert.Equal(["run", "-d", "--name", launch.Name], arguments.Take(4));
        Assert.Equal(["--entrypoint", "tail", launch.Image, "-f", "/dev/null"], arguments.TakeLast(5));

        // Regular mode: the whole Dockerfile, its own entry point.
        var regular = ContainerLaunch.Plan(info, profile, Sample("Web", "Dockerfile"), Sample(), fastMode: false, environment, freePort: () => 6003);
        Assert.False(regular.FastMode);
        Assert.Null(regular.Target);
        Assert.Equal("web:latest", regular.Image);
        Assert.Empty(regular.Run.Mounts);
        Assert.Null(regular.Run.Entrypoint);

        Assert.Equal("http://localhost:7000/swagger", ContainerLaunch.BrowserUrlFor("swagger", 7000));
        Assert.Equal("http://localhost:7000", ContainerLaunch.BrowserUrlFor("{Scheme}://{ServiceHost}:{ServicePort}", 7000));
    }

    [Fact]
    public void A_fast_mode_plan_mounts_the_static_web_assets_and_points_the_app_at_their_container_paths()
    {
        // As a Debug build has it (the .NET 11 SDK gives every web app some: dotnet watch's browser scripts in obj).
        var root = Directory.CreateTempSubdirectory("codalus-swa-").FullName;
        try
        {
            var project = Path.Combine(root, "Web", "Web.csproj");
            var output = Path.Combine(root, "Web", "bin", "Debug", "net10.0");
            var wwwroot = Path.Combine(root, "Web", "wwwroot");
            var watch = Path.Combine(root, "Web", "obj", "Debug", "net10.0", "dotnet-watch");
            var inOutput = Path.Combine(output, "assets");
            var missing = Path.Combine(root, "Web", "obj", "missing");
            foreach (var folder in new[] { wwwroot, watch, inOutput })
            {
                Directory.CreateDirectory(folder);
            }

            var manifest = new JsonObject
            {
                ["ContentRoots"] = new JsonArray(wwwroot + Path.DirectorySeparatorChar, watch + Path.DirectorySeparatorChar, output + Path.DirectorySeparatorChar, inOutput + Path.DirectorySeparatorChar, missing + Path.DirectorySeparatorChar),
                ["Root"] = new JsonObject { ["Children"] = null, ["Asset"] = null, ["Patterns"] = null },
            };
            File.WriteAllText(Path.Combine(output, "Web.staticwebassets.runtime.json"), manifest.ToJsonString());
            var info = new RunInfo(project, "net10.0", "Exe", Path.Combine(output, "Web.dll"), "dotnet", "exec Web.dll", output, Sample("Web", "Properties", "launchSettings.json"));
            var profile = LaunchSettings.Read(info.LaunchSettings, out _).Single(p => p.CommandName == LaunchProfile.Docker);
            var launch = ContainerLaunch.Plan(info, profile, Sample("Web", "Dockerfile"), root, fastMode: true, new Dictionary<string, string> { ["ASPNETCORE_HTTP_PORTS"] = "8080" }, freePort: () => 6001);

            // Folders outside the output are mounted read-only (a missing one isn't: the engine would create it).
            Assert.Equal([output + ":/app", wwwroot + ":/staticwebassets/0:ro", watch + ":/staticwebassets/1:ro"], launch.Run.Mounts);
            const string Container = "/app/Web.staticwebassets.runtime.container.json";
            Assert.Contains(launch.Run.Environment, e => e is { Key: ContainerLaunch.StaticWebAssetsVariable, Value: Container });
            Assert.Equal(Container, launch.Environment[ContainerLaunch.StaticWebAssetsVariable]); // the debugger's launch too

            var written = JsonNode.Parse(File.ReadAllText(Path.Combine(output, "Web.staticwebassets.runtime.container.json")))!;
            Assert.Equal(["/staticwebassets/0/", "/staticwebassets/1/", "/app/", "/app/assets/", "/staticwebassets/4/"], written["ContentRoots"]!.AsArray().Select(r => r!.GetValue<string>()));
            Assert.NotNull(written["Root"]);

            // Without a manifest (not a web app), only the output.
            File.Delete(Path.Combine(output, "Web.staticwebassets.runtime.json"));
            var plain = ContainerLaunch.Plan(info, profile, Sample("Web", "Dockerfile"), root, fastMode: true, new Dictionary<string, string>(), freePort: () => 6001);
            Assert.Equal([output + ":/app"], plain.Run.Mounts);
            Assert.DoesNotContain(plain.Run.Environment, e => e.Key == ContainerLaunch.StaticWebAssetsVariable);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
