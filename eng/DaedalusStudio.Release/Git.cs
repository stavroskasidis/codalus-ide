using System.Diagnostics;

namespace DaedalusStudio.Release;

/// <summary>Runs git in a repository and returns what it printed (one process per call, like the IDE's GitService).</summary>
internal sealed class Git(string repository)
{
    public string Repository { get; } = repository;

    public string Run(params string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = Repository,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("git did not start.");
        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed ({process.ExitCode}): {error.Result.Trim()}");
        }

        return output;
    }

    public IReadOnlyList<string> Lines(params string[] arguments) =>
        Run(arguments).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public bool TagExists(string tag) => Lines("tag", "--list", tag).Count > 0;

    public bool FileExists(string reference, string path)
    {
        try
        {
            Run("cat-file", "-e", $"{reference}:{path}");
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
