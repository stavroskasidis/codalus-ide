using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Codalus.Projects.Protocol;
using Codalus.Workbench.Build;

namespace Codalus.Workbench.Containers;

/// <summary>
/// How a project runs in a container (M17a, ADR 0042), from its "Container (Dockerfile)" launch profile.
/// <list type="bullet">
/// <item><b>Fast mode</b> (Debug builds, as Visual Studio's): the Dockerfile's <c>base</c> stage is built, and a
/// container of it idles (<c>tail -f /dev/null</c>) with the build output mounted at <c>/app</c>; the app starts in
/// it through <c>exec</c>, or under netcoredbg copied there for debugging. A rebuild needs no image build.</item>
/// <item><b>Regular mode</b> (Release, or <c>containers.fastMode</c> off): the whole Dockerfile is built and its
/// container runs its own entry point; debugging attaches to the app in it.</item>
/// </list>
/// </summary>
public sealed record ContainerLaunch(
    string Project,
    string Name,
    string Dockerfile,
    string Context,
    string Image,
    string? Target,
    ContainerRunSpec Run,
    string Program,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string> Environment,
    string? BrowserUrl,
    bool FastMode)
{
    public const string FastModeSetting = "containers.fastMode";

    /// <summary>Where the build output is mounted in a fast-mode container.</summary>
    public const string AppFolder = "/app";

    /// <summary>
    /// Plans a project's container run. <paramref name="info"/> is the built project (its <c>TargetPath</c> is mounted
    /// in fast mode); <paramref name="environment"/> is the run's environment (the profile's, with the IDE's
    /// overrides); <paramref name="hostPath"/> turns a local path into one the engine can mount (WSL engines).
    /// </summary>
    public static ContainerLaunch Plan(
        RunInfo info,
        LaunchProfile profile,
        string dockerfile,
        string? solutionFolder,
        bool fastMode,
        IReadOnlyDictionary<string, string> environment,
        Func<string, string>? hostPath = null,
        Func<int>? freePort = null)
    {
        hostPath ??= p => p;
        freePort ??= FreePort;
        var dockerfileInfo = DockerfileInfo.Parse(File.ReadAllText(dockerfile));
        var name = DockerSupport.ContainerName(info.Path);
        var context = DockerSupport.BuildContext(info.Path, solutionFolder);
        var useFast = fastMode && dockerfileInfo.BaseStage is not null && info.TargetPath is { Length: > 0 };

        // Ports: HTTP (and HTTPS) from the app's settings or the image's EXPOSE, all exposed ones with publishAllPorts.
        var httpPort = PortFrom(environment, "ASPNETCORE_HTTP_PORTS") ?? (dockerfileInfo.ExposedPorts.Contains(8080) ? 8080 : dockerfileInfo.ExposedPorts.Contains(80) ? 80 : null);
        var httpsPort = PortFrom(environment, "ASPNETCORE_HTTPS_PORTS");
        var exposed = new List<int>();
        if (httpPort is { } http)
        {
            exposed.Add(http);
        }

        if (httpsPort is { } https)
        {
            exposed.Add(https);
        }

        if (profile.PublishAllPorts)
        {
            exposed.AddRange(dockerfileInfo.ExposedPorts.Where(p => !exposed.Contains(p) && !(httpsPort is null && p is 8081 or 443)));
        }

        var ports = new List<string>();
        int? httpHostPort = null;
        foreach (var port in exposed)
        {
            var host = port == httpPort ? profile.HttpPort ?? freePort() : port == httpsPort ? profile.SslPort ?? freePort() : freePort();
            if (port == httpPort)
            {
                httpHostPort = host;
            }

            ports.Add(string.Create(CultureInfo.InvariantCulture, $"127.0.0.1:{host}:{port}"));
        }

        var variables = new Dictionary<string, string>(environment, StringComparer.Ordinal);
        variables.Remove("DOTNET_LAUNCH_PROFILE");
        variables.Remove("ASPNETCORE_URLS"); // the profile's applicationUrl is this machine's, not the container's
        variables.TryAdd("ASPNETCORE_ENVIRONMENT", "Development");
        if (httpPort is { } containerHttp)
        {
            variables.TryAdd("ASPNETCORE_HTTP_PORTS", containerHttp.ToString(CultureInfo.InvariantCulture));
        }

        string? browser = null;
        if (profile.LaunchBrowser && httpHostPort is { } browserPort)
        {
            browser = BrowserUrlFor(profile.LaunchUrl, browserPort);
        }

        var labels = new[] { new KeyValuePair<string, string>(ContainerEngine.ProjectLabel, info.Path) };
        var extra = Debugging.CommandLine.Split(profile.ContainerRunArguments ?? string.Empty);
        var arguments = Debugging.CommandLine.Split(profile.CommandLineArgs ?? string.Empty);
        if (useFast)
        {
            var output = Path.GetDirectoryName(info.TargetPath!)!;
            var program = Path.GetFileName(info.TargetPath!);
            if (!program.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                program = Path.ChangeExtension(program, ".dll");
            }

            var image = name + ":dev";
            var spec = new ContainerRunSpec(name, image)
            {
                Labels = labels,
                Ports = ports,
                Mounts = [hostPath(output) + ":" + AppFolder],
                Environment = [.. variables],
                WorkingDirectory = AppFolder,
                Entrypoint = "tail",
                Command = ["-f", "/dev/null"],
                ExtraArguments = extra,
            };
            return new ContainerLaunch(info.Path, name, dockerfile, context, image, dockerfileInfo.BaseStage!.Name, spec, "dotnet", [AppFolder + "/" + program, .. arguments], AppFolder, variables, browser, FastMode: true);
        }

        var full = Path.GetFileNameWithoutExtension(info.Path).ToLowerInvariant() + ":latest";
        var regular = new ContainerRunSpec(name, full)
        {
            Labels = labels,
            Ports = ports,
            Environment = [.. variables],
            Command = arguments,
            ExtraArguments = extra,
        };
        return new ContainerLaunch(info.Path, name, dockerfile, context, full, null, regular, string.Empty, arguments, string.Empty, variables, browser, FastMode: false);
    }

    /// <summary>The launch URL with the template's placeholders: <c>{Scheme}://{ServiceHost}:{ServicePort}</c>, or a relative path.</summary>
    public static string BrowserUrlFor(string? launchUrl, int port)
    {
        var root = string.Create(CultureInfo.InvariantCulture, $"http://localhost:{port}");
        if (string.IsNullOrWhiteSpace(launchUrl))
        {
            return root;
        }

        var url = launchUrl
            .Replace("{Scheme}", "http", StringComparison.OrdinalIgnoreCase)
            .Replace("{ServiceHost}", "localhost", StringComparison.OrdinalIgnoreCase)
            .Replace("{ServicePort}", port.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);
        return Uri.TryCreate(url, UriKind.Absolute, out _) ? url : root + "/" + url.TrimStart('/');
    }

    private static int? PortFrom(IReadOnlyDictionary<string, string> environment, string name) =>
        environment.TryGetValue(name, out var value)
        && int.TryParse(value.Split(';', ',')[0].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is > 0 and < 65536
            ? port
            : null;

    /// <summary>A free TCP port on the loopback interface.</summary>
    public static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>The <c>exec</c> arguments that start the app in a fast-mode container (output on its stdout and stderr).</summary>
    public IReadOnlyList<string> ExecArguments()
    {
        List<string> arguments = ["exec", "-i", "-w", WorkingDirectory, Name, Program, .. Arguments];
        return arguments;
    }
}
