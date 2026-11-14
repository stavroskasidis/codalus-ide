using System.Text.Json.Nodes;
using Codalus.Workbench.Containers;

namespace Codalus.Workbench.Tests;

/// <summary>
/// Compose in fast mode without a container engine (M63, ADR 0102): reading compose files (the engine's JSON and the
/// files themselves), finding the .NET services, the IDE's override, and which services a rerun restarts. The fixture
/// is <c>samples/Containers/Compose</c>: two .NET services and one that isn't.
/// </summary>
public sealed class ComposeFastModeTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "codalus-compose-" + Guid.NewGuid().ToString("N")[..8]);

    private static string Sample(params string[] parts) =>
        Path.Combine([Path.Combine(ContainerToolsTests.RepoRoot, "samples", "Containers"), .. parts]);

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Fact]
    public void The_yaml_reader_takes_what_compose_files_are_written_with()
    {
        var root = ComposeYaml.Parse("""
            # a comment
            name: shop
            x-defaults: &defaults
              restart: unless-stopped
            services:
              web:
                <<: *defaults
                image: "registry.example/web:1.0"   # trailing comment
                build:
                  context: ./src
                  dockerfile: Web/Dockerfile
                  args: { CONFIG: Debug, "QUOTED": 'it''s' }
                ports: ["127.0.0.1::8080", 9000]
                environment:
                  - A=1
                  - B=two words
                command: >
                  dotnet
                  Web.dll
                healthcheck:
                  test: |
                    curl -f http://localhost/
                    exit 0
                labels:
                  dev.codalus.project: Web/Web.csproj
              db:
                image: postgres
                volumes:
                  - type: volume
                    source: data
                    target: /var/lib/data
            volumes:
              data: {}
            """) as JsonObject;

        Assert.NotNull(root);
        Assert.Equal("shop", (string?)root["name"]);
        var web = root["services"]!["web"]!;
        Assert.Equal("registry.example/web:1.0", (string?)web["image"]);
        Assert.Null(web["<<"]);
        Assert.Equal("./src", (string?)web["build"]!["context"]);
        Assert.Equal("Debug", (string?)web["build"]!["args"]!["CONFIG"]);
        Assert.Equal("it's", (string?)web["build"]!["args"]!["QUOTED"]);
        Assert.Equal(["127.0.0.1::8080", "9000"], web["ports"]!.AsArray().Select(p => (string?)p));
        Assert.Equal(["A=1", "B=two words"], web["environment"]!.AsArray().Select(p => (string?)p));
        Assert.Equal("dotnet Web.dll\n", (string?)web["command"]);
        Assert.Equal("curl -f http://localhost/\nexit 0\n", (string?)web["healthcheck"]!["test"]);
        Assert.Equal("Web/Web.csproj", (string?)web["labels"]!["dev.codalus.project"]);
        var volume = root["services"]!["db"]!["volumes"]![0]!;
        Assert.Equal("volume", (string?)volume["type"]);
        Assert.Equal("/var/lib/data", (string?)volume["target"]);
        Assert.Empty(root["volumes"]!["data"]!.AsObject());
    }

    [Fact]
    public void A_compose_file_and_its_override_merge_as_compose_merges_them()
    {
        var model = ComposeModel.ReadFiles([Sample("docker-compose.yml"), Sample("docker-compose.override.yml")]);
        var web = Assert.Single(model.Services);
        Assert.Equal("web", web.Name);
        Assert.Equal("${DOCKER_REGISTRY-}codalus-sample-web", web.Image);
        Assert.Equal(Sample(), web.Build!.Context);
        Assert.Equal(Sample("Web", "Dockerfile"), web.Build.Dockerfile);
        Assert.Equal("Development", web.Environment["ASPNETCORE_ENVIRONMENT"]); // the override's list, as a map
        Assert.Equal("8080", web.Environment["ASPNETCORE_HTTP_PORTS"]);
    }

    [Fact]
    public void The_engines_json_is_read_with_its_full_paths()
    {
        // What docker compose config --format json writes (recorded, shortened), after a warning line.
        var context = Path.Combine(_folder, "app");
        var json = "time=\"2026-11-13T10:00:00Z\" level=warning msg=\"The \\\"TAG\\\" variable is not set.\"\n" + new JsonObject
        {
            ["name"] = "compose",
            ["services"] = new JsonObject
            {
                ["back"] = new JsonObject
                {
                    ["build"] = new JsonObject { ["context"] = context, ["dockerfile"] = "Back/Dockerfile" },
                    ["command"] = null,
                    ["environment"] = new JsonObject { ["ASPNETCORE_ENVIRONMENT"] = "Staging" },
                    ["networks"] = new JsonObject { ["default"] = null },
                },
                ["idle"] = new JsonObject
                {
                    ["command"] = new JsonArray("sleep", "infinity"),
                    ["image"] = "mcr.microsoft.com/dotnet/runtime-deps:10.0",
                    ["container_name"] = "compose-idle",
                    ["labels"] = new JsonArray("a=b"),
                },
            },
        }.ToJsonString();

        var model = ComposeModel.FromJson(json, _folder)!;
        Assert.Equal("compose", model.Name);
        var back = model.Services.Single(s => s.Name == "back");
        Assert.Equal(context, back.Build!.Context);
        Assert.Equal(Path.Combine(context, "Back", "Dockerfile"), back.Build.Dockerfile);
        Assert.Equal("Staging", back.Environment["ASPNETCORE_ENVIRONMENT"]);
        var idle = model.Services.Single(s => s.Name == "idle");
        Assert.Null(idle.Build);
        Assert.True(idle.HasCommand);
        Assert.Equal("compose-idle", idle.ContainerName);
        Assert.Equal("b", idle.Labels["a"]);
        Assert.Null(ComposeModel.FromJson("no compose app here", _folder));
    }

    [Fact]
    public void The_dotnet_services_are_found_by_their_dockerfiles_folder_a_label_or_the_dcproj()
    {
        var file = Sample("Compose", "compose.yaml");
        var model = ComposeModel.ReadFiles([file]);
        Assert.Equal(["front", "back", "idle"], model.Services.Select(s => s.Name));

        var front = Sample("Compose", "Front", "Front.csproj");
        var back = Sample("Compose", "Back", "Back.csproj");
        var services = ComposeFastMode.DotnetServices(model, file, [front, back], startupService: "front");
        Assert.Equal(
            [new ComposeDotnetService("front", front, Sample("Compose", "Front", "Dockerfile"), "base", IsStartup: true), new ComposeDotnetService("back", back, Sample("Compose", "Back", "Dockerfile"), "base", IsStartup: false)],
            services);

        // Not in the solution: the one project on disk next to the Dockerfile.
        Assert.Equal([front, back], ComposeFastMode.DotnetServices(model, file, []).Select(s => s.Project));

        // A label names the project where the Dockerfile's folder doesn't tell; a Dockerfile without a base stage
        // runs as the compose file says.
        Directory.CreateDirectory(Path.Combine(_folder, "docker"));
        Directory.CreateDirectory(Path.Combine(_folder, "src", "Api"));
        File.WriteAllText(Path.Combine(_folder, "src", "Api", "Api.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk.Web\" />");
        File.WriteAllText(Path.Combine(_folder, "docker", "Dockerfile"), "FROM mcr.microsoft.com/dotnet/aspnet:10.0\nENTRYPOINT [\"dotnet\", \"Api.dll\"]\n");
        var compose = Path.Combine(_folder, "compose.yaml");
        File.WriteAllText(compose, """
            services:
              api:
                build:
                  context: .
                  dockerfile: docker/Dockerfile
                labels:
                  dev.codalus.project: src/Api/Api.csproj
              cache:
                build: ./docker
            """);
        var labelled = Assert.Single(ComposeFastMode.DotnetServices(ComposeModel.ReadFiles([compose]), compose, []));
        Assert.Equal(Path.Combine(_folder, "src", "Api", "Api.csproj"), labelled.Project);
        Assert.Null(labelled.BaseStage);
    }

    [Fact]
    public void The_override_runs_each_dotnet_service_on_its_base_stage_with_the_build_mounted()
    {
        var file = Sample("Compose", "compose.yaml");
        var model = ComposeModel.ReadFiles([file]);
        var services = ComposeFastMode.DotnetServices(model, file, []);
        var output = Path.Combine(_folder, "bin", "Debug", "net10.0");
        Directory.CreateDirectory(output);
        var front = ComposeFastMode.Plan(services[0], model.Services[0], Path.Combine(output, "Front.dll"), debug: true);
        var back = ComposeFastMode.Plan(services[1], model.Services[1] with { Environment = new Dictionary<string, string> { ["ASPNETCORE_ENVIRONMENT"] = "Staging" }, HasCommand = true }, Path.Combine(output, "Back.exe"), debug: false);
        Assert.Equal("/app/Front.dll", front.ContainerProgram);
        Assert.Equal("/app/Back.dll", back.ContainerProgram);

        var text = ComposeFastMode.Override(file, "compose", [front, back]);
        Assert.StartsWith("# Codalus IDE's fast-mode override of compose.yaml", text, StringComparison.Ordinal);
        var root = (JsonObject)ComposeYaml.Parse(text)!;
        var debugged = root["services"]!["front"]!;
        Assert.Equal("compose-front:dev", (string?)debugged["image"]);
        Assert.Equal("base", (string?)debugged["build"]!["target"]);
        Assert.Equal(["tail", "-f", "/dev/null"], debugged["entrypoint"]!.AsArray().Select(e => (string?)e)); // the debugger starts the program
        Assert.Null(debugged["command"]);
        Assert.Equal("/app", (string?)debugged["working_dir"]);
        Assert.Equal("Development", (string?)debugged["environment"]!["ASPNETCORE_ENVIRONMENT"]);
        var mount = debugged["volumes"]![0]!;
        Assert.Equal(("bind", output, "/app"), ((string?)mount["type"], (string?)mount["source"], (string?)mount["target"]));
        Assert.Equal(services[0].Project, (string?)debugged["labels"]![ComposeFastMode.ProjectLabel]);
        Assert.Equal("debug", (string?)debugged["labels"]![ComposeFastMode.ModeLabel]);

        var run = root["services"]!["back"]!;
        Assert.Equal(["dotnet", "/app/Back.dll"], run["entrypoint"]!.AsArray().Select(e => (string?)e));
        Assert.Empty(run["command"]!.AsArray()); // the compose file's command would follow the entry point
        Assert.Null(run["environment"]); // the compose file's own ASPNETCORE_ENVIRONMENT stays
        Assert.Equal("run", (string?)run["labels"]![ComposeFastMode.ModeLabel]);
        Assert.Null(root["services"]!["idle"]); // not .NET: as the compose file says

        // Merged after the user's files, the services keep what the override doesn't say.
        var overrideFile = Path.Combine(_folder, ComposeFastMode.OverrideFileName);
        File.WriteAllText(overrideFile, text);
        var merged = ComposeModel.ReadFiles([file, overrideFile]);
        Assert.Equal(Sample("Compose", "Front", "Dockerfile"), merged.Services[0].Build!.Dockerfile);
        Assert.Equal("base", merged.Services[0].Build!.Target);
        Assert.Equal("http://back:8080", merged.Services[0].Environment["BACK_URL"]);
        Assert.Equal("compose-front:dev", merged.Services[0].Image);
    }

    [Fact]
    public void Paths_are_quoted_whole_and_mounted_as_the_engine_sees_them()
    {
        Assert.Equal("\"C:\\\\src\\\\Web\\\\bin\\\\Debug\"", ComposeFastMode.Quote(@"C:\src\Web\bin\Debug"));
        Assert.Equal(@"C:\src\a ""b""", (string?)ComposeYaml.Parse("p: " + ComposeFastMode.Quote(@"C:\src\a ""b"""))!["p"]);

        var service = new ComposeDotnetService("web", "/src/Web/Web.csproj", "/src/Web/Dockerfile", "base", IsStartup: false);
        var fast = new ComposeFastService(service, @"C:\src\Web\bin\Debug\net10.0", "Web.dll", Debug: false);
        var text = ComposeFastMode.Override("compose.yaml", "src", [fast], p => "/mnt/c/" + p[3..].Replace('\\', '/'));
        Assert.Equal("/mnt/c/src/Web/bin/Debug/net10.0", (string?)ComposeYaml.Parse(text)!["services"]!["web"]!["volumes"]![0]!["source"]);
        Assert.Contains("services: {}", ComposeFastMode.Override("compose.yaml", "src", []), StringComparison.Ordinal);
    }

    [Fact]
    public void A_rerun_restarts_only_the_services_whose_build_changed()
    {
        var front = Path.Combine(_folder, "front");
        var back = Path.Combine(_folder, "back");
        Directory.CreateDirectory(front);
        Directory.CreateDirectory(back);
        File.WriteAllText(Path.Combine(front, "Front.dll"), "1");
        File.WriteAllText(Path.Combine(back, "Back.dll"), "1");
        var before = new Dictionary<string, string> { ["front"] = ComposeFastMode.BuildStamp(front), ["back"] = ComposeFastMode.BuildStamp(back) };

        // The IDE's own static web assets manifest is written each run: no change.
        File.WriteAllText(Path.Combine(back, "Back.staticwebassets.runtime.container.json"), "{}");
        File.SetLastWriteTimeUtc(Path.Combine(front, "Front.dll"), DateTime.UtcNow.AddMinutes(1));
        var after = new Dictionary<string, string> { ["front"] = ComposeFastMode.BuildStamp(front), ["back"] = ComposeFastMode.BuildStamp(back), ["new"] = "1:1" };
        Assert.Equal(["front"], ComposeFastMode.Changed(before, after));
        Assert.Empty(ComposeFastMode.Changed(after, after));
        Assert.Empty(ComposeFastMode.Changed(new Dictionary<string, string>(), after)); // a first run started them all
    }
}
