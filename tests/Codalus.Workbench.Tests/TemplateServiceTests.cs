using Codalus.Workbench.Projects;

namespace Codalus.Workbench.Tests;

/// <summary>
/// The CLI New Project uses (<see cref="ITemplateCli"/>, ADR 0082): <see cref="TemplateService"/> owns the template hive
/// argument and runs every command in its neutral folder. A stand-in <c>dotnet</c> prints its arguments and folder.
/// </summary>
public sealed class TemplateServiceTests : IDisposable
{
    // The real path: on macOS the temporary folder is /var/…, which the stand-in's pwd reports as /private/var/….
    private readonly string _root = Path.Combine(Codalus.Platform.RealPath.Resolve(Path.GetTempPath()), "codalus-template-cli-" + Guid.NewGuid().ToString("N"));

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
            Codalus.Tests.StandInScript.Write(StandIn, "#!/bin/sh\necho \"cwd=$(pwd)\"\nfor a in \"$@\"; do echo \"arg=$a\"; done\n");
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
    public async Task A_command_that_runs_past_its_limit_fails_with_what_it_printed()
    {
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllText(StandIn, "@echo off\r\necho started\r\nping -n 30 127.0.0.1 > nul\r\n");
        }
        else
        {
            File.WriteAllText(StandIn, "#!/bin/sh\necho started\nsleep 30\n");
        }

        var service = new TemplateService(null, Neutral, () => StandIn) { CommandTimeout = TimeSpan.FromSeconds(1) };
        ITemplateCli cli = service;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var result = await cli.RunAsync(["new", "install", "Slow.Templates"], TestContext.Current.CancellationToken);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(25), $"took {watch.Elapsed}");

        // Its own limit isn't a cancel: a failure with the output so far and a line that says why.
        Assert.False(result.Succeeded);
        Assert.Contains("started", result.Output, StringComparison.Ordinal);
        Assert.StartsWith("Stopped dotnet new install Slow.Templates: it didn't finish within ", result.LastMeaningfulLine, StringComparison.Ordinal);
        var (installed, output) = await service.InstallAsync("Slow.Templates", TestContext.Current.CancellationToken);
        Assert.False(installed);
        Assert.Contains("Stopped dotnet new install Slow.Templates --force", output, StringComparison.Ordinal);

        // The caller's cancel still cancels.
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        service.CommandTimeout = TimeSpan.FromMinutes(3);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cli.RunAsync(["new", "list"], cancel.Token));
    }

    [Fact]
    public void The_line_that_says_what_went_wrong()
    {
        // dotnet new 11.0's own words for the failures New Project can meet.
        const string invalidOption = "Error: Invalid option(s):\n--nope\n   '--nope' is not a valid option\nx\n   'x' is not a valid option\n\nFor more information, run:\n   dotnet new console -h\n\nFor details on the exit code, refer to https://aka.ms/templating-exit-codes#127\n";
        Assert.Equal("'--nope' is not a valid option", new CliResult(127, invalidOption).LastMeaningfulLine);
        const string invalidValue = "Error: Invalid option(s):\n--framework net99.0\n   'net99.0' is not a valid value for --framework. The possible values are:\n      net10.0   - Target net10.0\n      net11.0   - Target net11.0\n\nFor more information, run:\n   dotnet new console -h\n\nFor details on the exit code, refer to https://aka.ms/templating-exit-codes#127\n";
        Assert.Equal("'net99.0' is not a valid value for --framework.", new CliResult(127, invalidValue).LastMeaningfulLine);
        const string overwrite = "Creating this template will make changes to existing files:\n  Overwrite   d/Program.cs\n\nTo create the template anyway, run the command with '--force' option:\n   dotnet new console --name D --output d --force\n\n\nFor details on the exit code, refer to https://aka.ms/templating-exit-codes#73\n";
        Assert.Equal("Creating this template will make changes to existing files: Overwrite d/Program.cs", new CliResult(73, overwrite).LastMeaningfulLine);
        const string noTemplate = "No templates or subcommands found matching: 'nosuch'.\n\nTo list installed templates similar to 'nosuch', run:\n   dotnet new list nosuch\nTo search for the templates on NuGet.org, run:\n   dotnet new search nosuch\n\n\nFor details on the exit code, refer to https://aka.ms/templating-exit-codes#103\n";
        Assert.Equal("No templates or subcommands found matching: 'nosuch'.", new CliResult(103, noTemplate).LastMeaningfulLine);
        const string notInstalled = "The following template packages will be installed:\n   Nope.Templates\n\nNope.Templates could not be installed, the package does not exist.\n\nFor details on the exit code, refer to https://aka.ms/templating-exit-codes#103\n";
        Assert.Equal("Nope.Templates could not be installed, the package does not exist.", new CliResult(103, notInstalled).LastMeaningfulLine);
        Assert.Equal("Error: The template couldn't be created.", new CliResult(1, "Error: The template couldn't be created.\nRerun the command with --force.\n").LastMeaningfulLine);
        Assert.Equal("exit code 3", new CliResult(3, "\n\n").LastMeaningfulLine);
    }
}
