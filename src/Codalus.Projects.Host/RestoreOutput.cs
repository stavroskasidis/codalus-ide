namespace Codalus.Projects.Host;

/// <summary>What <c>dotnet restore</c>'s output says.</summary>
internal static class RestoreOutput
{
    /// <summary>
    /// The project an MSBuild error line names, if any: the one it was building (<c>…targets(38,5): error NETSDK1147: …
    /// [C:\src\App\App.csproj]</c>), or, without one, the project file the error is in (<c>App.csproj(3,5): error : …</c>).
    /// </summary>
    public static string? FailingProject(string line)
    {
        var at = line.IndexOf(": error ", StringComparison.Ordinal);
        if (at < 0)
        {
            return null;
        }

        if (line.EndsWith(']') && line.LastIndexOf(" [", StringComparison.Ordinal) is var open and > 0)
        {
            return Project(line[(open + 2)..^1]);
        }

        // The origin: "path(line,column)" or "path".
        var origin = line[..at].Trim();
        if (origin.EndsWith(')') && origin.LastIndexOf('(') is var position and > 0)
        {
            origin = origin[..position];
        }

        return Project(origin);
    }

    private static string? Project(string path) =>
        Path.IsPathFullyQualified(path) && Path.GetExtension(path).EndsWith("proj", StringComparison.OrdinalIgnoreCase) ? Path.GetFullPath(path) : null;
}
