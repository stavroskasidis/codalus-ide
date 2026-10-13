using System.Diagnostics;
using Codalus.Workbench.Debugging.Remote;
using Codalus.Workbench.Testing;
using Environment = Codalus.Workbench.Testing.TestEnvironment;

namespace Codalus.Workbench.Tests;

/// <summary>
/// Remote test environments (M21b, ADR 0055): <c>testenvironments.json</c>, the filter, and a real run of
/// samples/Testing's xUnit v2 project in a stand-in environment: WSL on Windows (<c>CODALUS_TEST_WSL</c>), a fake
/// <c>ssh</c> on Linux and macOS. Never another machine, never a pulled image.
/// </summary>
public sealed class TestEnvironmentTests
{
    [Fact]
    public void The_environments_are_read_with_what_makes_one_unusable()
    {
        var environments = TestEnvironments.Parse("""
            {
              "version": "1",
              // comments are allowed
              "environments": [
                { "name": "Ubuntu", "type": "wsl", "wslDistribution": "Ubuntu-24.04" },
                { "name": "SDK image", "type": "docker", "dockerImage": "mcr.microsoft.com/dotnet/sdk:10.0" },
                { "name": "Built", "type": "docker", "dockerFile": "tests/Dockerfile" },
                { "name": "Pi", "type": "ssh", "remoteUri": "ssh://pi@raspberry:2222" },
                { "name": "No image", "type": "docker" },
                { "name": "Bad uri", "type": "ssh", "remoteUri": "" },
                { "name": "Other", "type": "kubernetes" },
              ]
            }
            """);
        Assert.Equal(["Ubuntu", "SDK image", "Built", "Pi", "No image", "Bad uri", "Other"], environments.Select(e => e.Name));
        Assert.Equal("Ubuntu-24.04", environments[0].WslDistribution);
        Assert.Equal(OperatingSystem.IsWindows(), environments[0].Problem is null);
        Assert.Null(environments[1].Problem);
        Assert.Equal("tests/Dockerfile", environments[2].DockerFile);
        Assert.Null(environments[3].Problem);
        Assert.Contains("dockerImage", environments[4].Problem, StringComparison.Ordinal);
        Assert.Contains("remoteUri", environments[5].Problem, StringComparison.Ordinal);
        Assert.Contains("kubernetes", environments[6].Problem, StringComparison.Ordinal);
        Assert.Empty(TestEnvironments.Parse("{}"));
    }

    [Fact]
    public void The_filter_names_each_test_once_with_reserved_characters_escaped()
    {
        Assert.Null(TestEnvironments.Filter(null));
        Assert.Null(TestEnvironments.Filter([]));
        Assert.Equal("FullyQualifiedName=Ns.A.Adds|FullyQualifiedName=Ns.A.Odd\\(1,2\\)", TestEnvironments.Filter(["Ns.A.Odd(1,2)", "Ns.A.Adds", "Ns.A.Adds"]));
        Assert.Equal("FullyQualifiedName=Ns.A.B\\=C\\|D\\&E\\!F\\~G\\\\H", TestEnvironments.Filter(["Ns.A.B=C|D&E!F~G\\H"]));
    }

    [Fact]
    public void A_docker_environment_sees_the_solution_folder_under_workspaces()
    {
        var solution = Path.Combine(Path.GetTempPath(), "My Solution");
        Assert.Equal("/workspaces/My-Solution", TestEnvironments.ContainerFolder(solution));
        Assert.Equal("/workspaces/My-Solution/tests/T/bin/Debug/net10.0/T.dll", TestEnvironments.ToContainerPath(solution, Path.Combine(solution, "tests", "T", "bin", "Debug", "net10.0", "T.dll")));
        Assert.Null(TestEnvironments.ToContainerPath(solution, Path.Combine(Path.GetTempPath(), "elsewhere", "T.dll")));
        var environment = new Environment("SDK", Environment.Docker) { DockerImage = "sdk" };
        Assert.Equal(TestEnvironments.ContainerName(solution, environment), TestEnvironments.ContainerName(solution + Path.DirectorySeparatorChar, environment));
        Assert.NotEqual(TestEnvironments.ContainerName(solution, environment), TestEnvironments.ContainerName(solution, environment with { Name = "Other" }));
        Assert.StartsWith("codalus-tests-", TestEnvironments.ContainerName(solution, environment), StringComparison.Ordinal);
    }

    [Fact]
    public void The_file_is_found_next_to_the_solution_or_above_it_up_to_the_repository()
    {
        var root = Path.Combine(Path.GetTempPath(), "codalus-testenv-" + Guid.NewGuid().ToString("N")[..8]);
        var solution = Path.Combine(root, "repo", "src");
        Directory.CreateDirectory(solution);
        try
        {
            File.WriteAllText(Path.Combine(root, TestEnvironments.FileName), "{}"); // outside the repository: not found
            Directory.CreateDirectory(Path.Combine(root, "repo", ".git"));
            Assert.Null(TestEnvironments.Find(solution));
            File.WriteAllText(Path.Combine(root, "repo", TestEnvironments.FileName), "{}");
            Assert.Equal(Path.Combine(root, "repo", TestEnvironments.FileName), TestEnvironments.Find(solution));
            File.WriteAllText(Path.Combine(solution, TestEnvironments.FileName), "{}");
            Assert.Equal(Path.Combine(solution, TestEnvironments.FileName), TestEnvironments.Find(solution));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Tests_built_here_run_in_the_environment_and_their_results_come_back()
    {
        var root = Path.Combine(Platform.RealPath.Resolve(Path.GetTempPath()), "codalus-testenv-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        try
        {
            var target = RemoteServerTests.StandIn(root);
            if (target is null)
            {
                Assert.Skip("No stand-in environment here: WSL with .NET on Windows (CODALUS_TEST_WSL), the fake ssh on Linux and macOS.");
            }

            var solution = CopyTesting(Path.Combine(root, "Testing"));
            var project = Path.Combine(solution, "tests", "XunitV2Tests", "XunitV2Tests.csproj");
            ContainerRuntime.Build(project);
            var targetPath = Path.Combine(solution, "tests", "XunitV2Tests", "bin", "Debug", "net10.0", "XunitV2Tests.dll");
            Assert.True(File.Exists(targetPath));

            var environment = target is WslTarget wsl
                ? new Environment("WSL", Environment.Wsl) { WslDistribution = wsl.Distribution }
                : new Environment("SSH", Environment.Ssh) { RemoteUri = "ssh://stand-in" };
            var log = new List<string>();
            var runner = new EnvironmentTestRunner(environment, solution, line =>
            {
                lock (log)
                {
                    log.Add(line);
                }
            })
            {
                SshProgram = target is SshTarget s ? s.Program : "ssh",
                RemoteFolder = target is SshTarget ? Path.Combine(root, "remote").Replace('\\', '/') : null,
            };

            // Three tests by name (not the one that hangs).
            var names = new[] { "XunitV2Tests.CalculatorTests.Adds", "XunitV2Tests.CalculatorTests.Fails", "XunitV2Tests.CalculatorTests.Skipped" };
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            var documents = await runner.RunAsync([new EnvironmentTestProject(project, targetPath, names)], timeout.Token);
            string Log()
            {
                lock (log)
                {
                    return string.Join('\n', log.TakeLast(60));
                }
            }

            Assert.True(documents.Count == 1, Log());
            var document = documents[0];
            var results = Trx.Import(document, "environment").ToDictionary(r => r.FullyQualifiedName, r => r.Result.Outcome);
            Assert.True(results.Count == 3, $"{results.Count} results:\n{Log()}");
            Assert.Equal(TestOutcome.Passed, results["XunitV2Tests.CalculatorTests.Adds"]);
            Assert.Equal(TestOutcome.Failed, results["XunitV2Tests.CalculatorTests.Fails"]);
            Assert.Equal(TestOutcome.Skipped, results["XunitV2Tests.CalculatorTests.Skipped"]);

            // It ran there: Linux, in the stand-in.
            Assert.Contains(log, l => l.Contains("dotnet test", StringComparison.Ordinal));
            Assert.False(Directory.Exists(Path.Combine(solution, "TestResults", "codalus")) && Directory.EnumerateFiles(Path.Combine(solution, "TestResults", "codalus"), "*.trx").Any(), "the results file is cleaned up");
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>samples/Testing's calculator and xUnit v2 project, standalone, with the SDK pinned above it.</summary>
    private static string CopyTesting(string target)
    {
        static void Copy(string source, string destination)
        {
            Directory.CreateDirectory(destination);
            foreach (var file in Directory.EnumerateFiles(source))
            {
                File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
            }

            foreach (var directory in Directory.EnumerateDirectories(source).Where(d => Path.GetFileName(d) is not ("bin" or "obj")))
            {
                Copy(directory, Path.Combine(destination, Path.GetFileName(directory)));
            }
        }

        var samples = Path.Combine(ContainerToolsTests.RepoRoot, "samples");
        Copy(Path.Combine(samples, "Testing", "src", "Calculator"), Path.Combine(target, "src", "Calculator"));
        Copy(Path.Combine(samples, "Testing", "tests", "XunitV2Tests"), Path.Combine(target, "tests", "XunitV2Tests"));
        File.Copy(Path.Combine(samples, "Testing", "Directory.Build.props"), Path.Combine(target, "Directory.Build.props"));
        File.WriteAllText(Path.Combine(target, "Directory.Packages.props"), "<Project><PropertyGroup><ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally></PropertyGroup></Project>");
        Codalus.Tests.FixtureSdk.Pin(Path.GetDirectoryName(target)!);
        return target;
    }
}
