using Codalus.Workbench.Projects;

namespace Codalus.Workbench.Tests;

/// <summary>
/// The CLI New Project uses (<see cref="ITemplateCli"/>, ADR 0082): <see cref="TemplateService"/> owns the template hive
/// argument and runs every command in its neutral folder. A stand-in <c>dotnet</c> prints its arguments and folder.
/// </summary>
public sealed class TemplateServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "codalus-template-cli-" + Guid.NewGuid().ToString("N"));

    public TemplateServiceTests()
    {
        Directory.CreateDirectory(_root);
        Neutral = Path.Combine(_root, "state");
        Directory.CreateDirectory(Neutral);
        if (OperatingSystem.IsWindows())
        {
            StandIn = Path.Combine(_root, "dotnet.cmd");
            File.WriteAllText(StandIn, "@echo off\r\necho cwd=%CD%\r\n:loop\r\nif \"%~1\"==\"\" goto end\r\necho arg=%~1\r\nshift\r\ngoto loop\r\n:end\r\n");
        }
        else
        {
            StandIn = Path.Combine(_root, "dotnet");
            File.WriteAllText(StandIn, "#!/bin/sh\necho \"cwd=$(pwd)\"\nfor a in \"$@\"; do echo \"arg=$a\"; done\n");
            File.SetUnixFileMode(StandIn, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private string StandIn { get; }

    private string Neutral { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static List<string> Arguments(CliResult result) =>
        [.. result.Output.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Where(l => l.StartsWith("arg=", StringComparison.Ordinal)).Select(l => l[4..].Trim())];

    private static string Folder(CliResult result) =>
        result.Output.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Single(l => l.StartsWith("cwd=", StringComparison.Ordinal))[4..].Trim();

    [Fact]
    public async Task The_hive_goes_on_dotnet_new_once_and_never_otherwise()
    {
        var hive = Path.Combine(_root, "hive");
        ITemplateCli cli = new TemplateService(hive, Neutral, () => StandIn);
        Assert.Equal(hive, cli.Hive);
        var result = await cli.RunAsync(["new", "console", "--name", "App"], TestContext.Current.CancellationToken);
        Assert.True(result.Succeeded, result.Output);
        Assert.Equal(["new", "console", "--name", "App", "--debug:custom-hive", hive], Arguments(result));
        Assert.Single(Arguments(result), a => a == "--debug:custom-hive");

        Assert.Equal(["--version"], Arguments(await cli.RunAsync(["--version"], TestContext.Current.CancellationToken)));

        ITemplateCli userHive = new TemplateService(null, Neutral, () => StandIn);
        Assert.DoesNotContain("--debug:custom-hive", Arguments(await userHive.RunAsync(["new", "list"], TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task Every_command_runs_in_the_neutral_folder()
    {
        ITemplateCli cli = new TemplateService(null, Neutral, () => StandIn);
        var folder = Folder(await cli.RunAsync(["new", "list"], TestContext.Current.CancellationToken));
        Assert.Equal(Path.GetFullPath(Neutral).TrimEnd(Path.DirectorySeparatorChar), Codalus.Platform.RealPath.Resolve(folder).TrimEnd(Path.DirectorySeparatorChar), ignoreCase: OperatingSystem.IsWindows());

        // A missing dotnet is an exit code, not an exception.
        ITemplateCli missing = new TemplateService(null, Neutral, () => Path.Combine(_root, "no-such-dotnet"));
        var failed = await missing.RunAsync(["--version"], TestContext.Current.CancellationToken);
        Assert.False(failed.Succeeded);
    }

    [Fact]
    public void The_last_meaningful_line_of_a_failure()
    {
        var output = "Error: Invalid option(s):\n--nope\n   '--nope' is not a valid option\n\nFor more information, run:\n   dotnet new console -h\n\nFor details on the exit code, refer to https://aka.ms/templating-exit-codes#127\n";
        Assert.Equal("For more information, run:", new CliResult(127, output).LastMeaningfulLine);
        Assert.Equal("exit code 3", new CliResult(3, "\n\n").LastMeaningfulLine);
    }
}
