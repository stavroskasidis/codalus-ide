using System.Diagnostics;
using System.Text.Json;
using Codalus.Projects.Protocol;

namespace Codalus.Debugging.Tests;

/// <summary>
/// samples/Debugging's Sessions.slnx (M14c), copied to a temp folder and built once: the Debuggee and Partner programs
/// for compound launches, and the Generator with its GeneratorTarget for Roslyn components.
/// </summary>
public sealed class SessionsFixture : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Codalus.Platform.RealPath.Resolve(Path.GetTempPath()), "codalus-sessions-" + Guid.NewGuid().ToString("N"));

    public string Folder => Path.Combine(_root, "Debugging");

    public string Project(string name) => Path.Combine(Folder, name, name + ".csproj");

    public string Dll(string name, string framework = "net10.0") => Path.Combine(Folder, name, "bin", "Debug", framework, name + ".dll");

    public string Source(string project, string file) => Path.Combine(Folder, project, file);

    public async ValueTask InitializeAsync()
    {
        Copy(Path.Combine(DebuggeeFixture.RepoRoot, "samples", "Debugging"), Folder);
        File.Copy(Path.Combine(DebuggeeFixture.RepoRoot, "samples", "Directory.Build.props"), Path.Combine(_root, "Directory.Build.props"));
        Codalus.Tests.FixtureSdk.Pin(_root);
        File.Copy(Path.Combine(DebuggeeFixture.RepoRoot, "samples", "Directory.Packages.props"), Path.Combine(_root, "Directory.Packages.props"));
        var (code, output) = await RunAsync("dotnet", ["build", Path.Combine(Folder, "Sessions.slnx"), "-nologo", "-v", "quiet"]);
        Assert.True(code == 0, output);
    }

    /// <summary>
    /// What the build host's design-time build gives the shell for <paramref name="project"/> (ADR 0005): the compiler
    /// command line, through MSBuild's <c>-getItem</c>, and the SDK folder.
    /// </summary>
    public async Task<(CompilerInputsInfo Inputs, string SdkPath)> CompilerInputsAsync(string project)
    {
        var path = Project(project);
        var (code, output) = await RunAsync("dotnet",
        [
            "msbuild", path, "-t:Compile", "-nologo",
            "-p:SkipCompilerExecution=true", "-p:ProvideCommandLineArgs=true", "-p:DesignTimeBuild=true",
            "-p:BuildingInsideVisualStudio=true", "-p:BuildProjectReferences=false",
            "-getItem:CscCommandLineArgs", "-getProperty:MSBuildExtensionsPath",
        ]);
        Assert.True(code == 0, output);
        using var json = JsonDocument.Parse(output[output.IndexOf('{', StringComparison.Ordinal)..]);
        var arguments = json.RootElement.GetProperty("Items").GetProperty("CscCommandLineArgs").EnumerateArray().Select(i => i.GetProperty("Identity").GetString()!).ToArray();
        var sdk = json.RootElement.GetProperty("Properties").GetProperty("MSBuildExtensionsPath").GetString()!;
        Assert.NotEmpty(arguments);
        return (new CompilerInputsInfo(path, "net10.0", "C#", CompilerInputStates.Ok, null, arguments, [], null, null), sdk);
    }

    private async Task<(int Code, string Output)> RunAsync(string program, IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo(program, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = _root,
        };
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await output + await error);
    }

    public ValueTask DisposeAsync()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return ValueTask.CompletedTask;
    }

    private static void Copy(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        }

        foreach (var directory in Directory.EnumerateDirectories(source).Where(d => Path.GetFileName(d) is not ("bin" or "obj")))
        {
            Copy(directory, Path.Combine(target, Path.GetFileName(directory)));
        }
    }
}
