using System.Diagnostics;
using System.Globalization;
using Codalus.Platform;
using Codalus.Rpc;
using Codalus.Tests;
using Codalus.Workbench;
using Codalus.Workbench.Plugins;

namespace Codalus.Plugins.Tests;

/// <summary>
/// <c>dotnet new codalus-plugin</c> (M22b): the template instantiates, builds against the API packed from this
/// repository, its own tests pass, and the plugin it makes loads and runs in the real plugin host.
/// </summary>
public sealed class PluginTemplateTests : IDisposable
{
    private static readonly string s_dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host && File.Exists(host) ? host : "dotnet";

    // A version of our own, so the NuGet cache never answers with an older pack of the same version.
    private readonly string _version = "1.0.0-template." + DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + "." + Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
    private readonly string _root = Directory.CreateTempSubdirectory("codalus-template-").FullName;

    private static string Repo => PluginHostIntegrationTests.RepoRoot;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_plugin_template_builds_passes_its_tests_and_runs_in_the_plugin_host()
    {
        FixtureSdk.Pin(_root);
        var feed = Path.Combine(_root, "feed");
        foreach (var project in new[] { "Codalus.Plugins.Abstractions", "Codalus.Plugins.Testing" })
        {
            await Run(Repo, "pack", Path.Combine(Repo, "src", project, project + ".csproj"), "--no-build", "-c", PluginHostIntegrationTests.Configuration,
                "-p:PackageVersion=" + _version, "-o", feed);
        }

        var hive = Path.Combine(_root, "hive");
        await Run(_root, "new", "install", Path.Combine(Repo, "src", "Codalus.Templates", "content", "CodalusPlugin"), "--debug:custom-hive", hive);
        var work = Path.Combine(_root, "Contoso.Greeter");
        await Run(_root, "new", "codalus-plugin", "-n", "Contoso.Greeter", "-o", work, "--publisher", "contoso", "--codalusVersion", _version, "--debug:custom-hive", hive);
        File.WriteAllText(Path.Combine(work, "nuget.config"), $"""
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <clear />
                <add key="local" value="{feed}" />
                <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
              </packageSources>
            </configuration>
            """);

        Assert.True(File.Exists(Path.Combine(work, "Contoso.Greeter.slnx")));
        var manifest = File.ReadAllText(Path.Combine(work, "src", "Contoso.Greeter", "codalus-plugin.json"));
        Assert.Contains("\"id\": \"contoso.contoso.greeter\"", manifest, StringComparison.Ordinal);
        Assert.Contains("\"contoso.greeter.greet\"", manifest, StringComparison.Ordinal);

        await Run(work, "build", "Contoso.Greeter.slnx", "-c", "Debug");
        await Run(work, Path.Combine(work, "tests", "Contoso.Greeter.Tests", "bin", "Debug", "net10.0", "Contoso.Greeter.Tests.dll"));

        // The plugin as the IDE loads it: from its build output, in a plugin host of this repository.
        var output = Path.Combine(work, "src", "Contoso.Greeter", "bin", "Debug", "net10.0");
        Assert.False(File.Exists(Path.Combine(output, "Codalus.Plugins.Abstractions.dll"))); // the host provides it
        var userData = Path.Combine(_root, "user-data");
        var paths = AppPaths.Resolve(PlatformInfo.Current, _ => null, userData);
        paths.EnsureCreated();
        using var workbench = new WorkbenchServices(paths, PlatformInfo.Current, watchFiles: false);
        await using var manager = new PluginManager(workbench, _ => ProcessLaunchInfo.FromPath(PluginHostIntegrationTests.HostAssembly), "test");
        manager.AddPlugins(PluginDiscovery.Scan([(output, false)], PlatformInfo.Current));
        var plugin = Assert.Single(manager.Plugins, p => p.Id == "contoso.contoso.greeter");
        Assert.True(plugin.State == PluginState.Ready, plugin.Error);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        // The command waits for its notification: dismiss it, as the user would.
        var shown = new List<string>();
        workbench.Notifications.Added += (_, n) =>
        {
            lock (shown)
            {
                shown.Add(n.Message);
            }

            workbench.Notifications.Dismiss(n);
        };
        var result = await workbench.Commands.ExecuteAsync("contoso.greeter.greet", [], timeout.Token);
        Assert.Equal(1, result!.Value.GetInt32());
        lock (shown)
        {
            Assert.Contains("Hello from Contoso.Greeter!", shown);
        }
    }

    private static async Task Run(string folder, params string[] args)
    {
        var start = new ProcessStartInfo(s_dotnet) { WorkingDirectory = folder, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        // The repository's own build settings stay out of the generated projects.
        start.Environment.Remove("MSBuildExtensionsPath");
        start.Environment.Remove("MSBUILD_EXE_PATH");
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        start.Environment["DOTNET_NOLOGO"] = "1";
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(Ct);
        var stderr = process.StandardError.ReadToEndAsync(Ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        Assert.True(process.ExitCode == 0, $"dotnet {string.Join(' ', args)} failed ({process.ExitCode}):\n{await stdout}\n{await stderr}");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        // The packs went into the user's NuGet cache under our own version: take them out again.
        var cache = Environment.GetEnvironmentVariable("NUGET_PACKAGES") is { Length: > 0 } packages
            ? packages
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
        foreach (var id in new[] { "codalus.plugins.abstractions", "codalus.plugins.testing" })
        {
            try
            {
                var folder = Path.Combine(cache, id, _version);
                if (Directory.Exists(folder))
                {
                    Directory.Delete(folder, recursive: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
