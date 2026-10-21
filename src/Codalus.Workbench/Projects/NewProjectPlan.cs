using System.Text.Json;
using Codalus.Platform;
using Codalus.Workbench.Localization;

namespace Codalus.Workbench.Projects;

/// <summary>Where New Project puts the project: in a new solution, or into the open solution or folder.</summary>
public enum SolutionMode
{
    CreateNew,
    AddToSolution,
    AddToFolder,
}

/// <summary>
/// The OS's path rules for New Project's checks: Windows limits a path to 260 characters unless long paths are on
/// (<c>LongPathsEnabled</c>). Tests pass Windows' rules on every OS.
/// </summary>
public sealed record PathRules(bool IsWindows, bool LongPathsEnabled)
{
    /// <summary>Windows' limit without long paths (MAX_PATH, the terminating NUL included).</summary>
    public const int MaxPath = 260;

    /// <summary>This machine's rules (the registry read once).</summary>
    public static PathRules Current { get; } = new(OperatingSystem.IsWindows(), !OperatingSystem.IsWindows() || ReadLongPathsEnabled());

    private static bool ReadLongPathsEnabled()
    {
        if (!OperatingSystem.IsWindows())
        {
            return true;
        }

        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\FileSystem");
            return key?.GetValue("LongPathsEnabled") is int enabled && enabled == 1;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }
}

/// <summary>What the New Project wizard asks for (ADR 0082): the template, its member, names, location and choices.</summary>
public sealed record NewProjectRequest
{
    public required ProjectTemplateGroup Group { get; init; }

    /// <summary>The member the framework picked (<see cref="ProjectTemplateGroup.MemberFor"/>).</summary>
    public ProjectTemplateMember? Member { get; init; }

    public string ProjectName { get; init; } = string.Empty;

    /// <summary>The location as typed (<c>~</c> and variables are expanded).</summary>
    public string Location { get; init; } = string.Empty;

    public SolutionMode SolutionMode { get; init; } = SolutionMode.CreateNew;

    public string SolutionName { get; init; } = string.Empty;

    /// <summary>Place solution and project in the same directory.</summary>
    public bool SameDirectory { get; init; }

    /// <summary>Trust this location (a new solution opens trusted).</summary>
    public bool TrustLocation { get; init; }

    public string? Framework { get; init; }

    /// <summary>The template's values by symbol name, as the template sees them (inverted check boxes already turned back).</summary>
    public IReadOnlyDictionary<string, string?> Values { get; init; } = new Dictionary<string, string?>(StringComparer.Ordinal);

    public HostOptionValues HostOptions { get; init; } = new();
}

/// <summary>
/// A New Project request turned into folders and a <c>dotnet new</c> command, with the classic IDE's checks (pure, so
/// tests check every rule on every OS). A new solution goes in <c>&lt;location&gt;/&lt;solution&gt;</c> with the
/// project in a folder under it, or both in <c>&lt;location&gt;/&lt;project&gt;</c> with Place solution and project in
/// the same directory; a project added to the open solution or folder goes in <c>&lt;location&gt;/&lt;project&gt;</c>;
/// Blank Solution is <c>&lt;location&gt;/&lt;solution&gt;</c> alone.
/// </summary>
public sealed record NewProjectPlan
{
    /// <summary>The longest name New Project accepts.</summary>
    public const int MaxNameLength = 128;

    private static readonly char[] s_invalidNameCharacters = ['/', '?', ':', '&', '\\', '*', '"', '<', '>', '|', '#', '%'];

    private static readonly string[] s_reservedNames = ["CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"];

    public required NewProjectRequest Request { get; init; }

    /// <summary>The location as a full path (null when it isn't one).</summary>
    public string? Location { get; init; }

    /// <summary>The new solution's folder (null when the project goes into the open solution or folder).</summary>
    public string? SolutionDirectory { get; init; }

    /// <summary>The solution's name (the project's, with the same directory).</summary>
    public string? SolutionName { get; init; }

    /// <summary>The project's folder (null for Blank Solution).</summary>
    public string? ProjectDirectory { get; init; }

    /// <summary>The folder "Project will be created in" names, ending with a separator.</summary>
    public string CreatedIn { get; init; } = string.Empty;

    public bool IsBlankSolution => Request.Group.Kind == ProjectTemplateKind.BlankSolution;

    public bool CreatesSolution => IsBlankSolution || Request.SolutionMode == SolutionMode.CreateNew;

    /// <summary>The folders and names of a request (a location that isn't a path leaves them null).</summary>
    public static NewProjectPlan Create(NewProjectRequest request)
    {
        var location = FullLocation(request.Location);
        if (location is null)
        {
            return new NewProjectPlan { Request = request };
        }

        var project = request.ProjectName.Trim();
        string? solutionDirectory = null, projectDirectory = null, solutionName = null;
        if (request.Group.Kind == ProjectTemplateKind.BlankSolution)
        {
            solutionName = request.SolutionName.Trim();
            solutionDirectory = Path.Combine(location, solutionName);
        }
        else if (request.SolutionMode != SolutionMode.CreateNew)
        {
            projectDirectory = Path.Combine(location, project);
        }
        else if (request.SameDirectory)
        {
            solutionName = project;
            projectDirectory = solutionDirectory = Path.Combine(location, project);
        }
        else
        {
            solutionName = request.SolutionName.Trim();
            solutionDirectory = Path.Combine(location, solutionName);
            projectDirectory = Path.Combine(solutionDirectory, project);
        }

        var shown = projectDirectory ?? solutionDirectory ?? location;
        return new NewProjectPlan
        {
            Request = request,
            Location = location,
            SolutionDirectory = solutionDirectory,
            SolutionName = solutionName,
            ProjectDirectory = projectDirectory,
            CreatedIn = Path.TrimEndingDirectorySeparator(shown) + Path.DirectorySeparatorChar,
        };
    }

    /// <summary>A typed location as a full path: trimmed, <c>~</c> and variables expanded (<see cref="UserPaths"/>); null when it isn't a rooted path.</summary>
    public static string? FullLocation(string? location)
    {
        if (string.IsNullOrWhiteSpace(location))
        {
            return null;
        }

        try
        {
            var expanded = UserPaths.Expand(location);
            return Path.IsPathFullyQualified(expanded) ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(expanded)) is { Length: > 0 } full ? full : null : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>
    /// What's wrong with a project or solution name, or null: empty; characters a name can't have; a leading or trailing
    /// dot or space; a device name Windows reserves (<c>CON</c>, <c>com1</c>, <c>LPT9.txt</c>, on every OS, so a project
    /// made anywhere opens on Windows); longer than <see cref="MaxNameLength"/>. Dashes and dots are fine.
    /// </summary>
    public static string? ValidateName(string? name)
    {
        var text = name ?? string.Empty;
        if (text.Trim().Length == 0)
        {
            return Localizer.Get("msg.newProject.nameEmpty", "Enter a name.");
        }

        var invalid = text.Where(c => s_invalidNameCharacters.Contains(c) || char.IsControl(c)).Distinct().ToList();
        if (invalid.Count > 0)
        {
            return Localizer.Format("msg.newProject.nameInvalidCharacters", "A name can't contain {0}.", string.Join(' ', invalid.Select(c => char.IsControl(c) ? $"U+{(int)c:X4}" : c.ToString())));
        }

        if (text[0] is '.' or ' ' || text[^1] is '.' or ' ')
        {
            return Localizer.Get("msg.newProject.nameDotOrSpace", "A name can't start or end with a dot or a space.");
        }

        var stem = text.Split('.')[0].TrimEnd();
        if (s_reservedNames.Contains(stem, StringComparer.OrdinalIgnoreCase))
        {
            return Localizer.Format("msg.newProject.nameReserved", "\"{0}\" is a device name Windows reserves. Choose another name.", stem);
        }

        return text.Length > MaxNameLength ? Localizer.Format("msg.newProject.nameTooLong", "A name can be at most {0} characters long.", MaxNameLength) : null;
    }

    /// <summary>What's wrong with the location, or null.</summary>
    public string? ValidateLocation() =>
        string.IsNullOrWhiteSpace(Request.Location) ? Localizer.Get("msg.newProject.locationEmpty", "Enter a location.")
        : Location is null ? Localizer.Format("msg.newProject.locationInvalid", "\"{0}\" isn't a folder path.", Request.Location.Trim())
        : null;

    /// <summary>
    /// What's wrong with the folders on disk, or null: a folder the plan creates that already exists and isn't empty;
    /// on Windows without long paths, a file of the template whose path would be <see cref="PathRules.MaxPath"/> characters
    /// or more. It reads the disk: call it off the UI thread.
    /// </summary>
    public string? ValidateTarget(PathRules rules)
    {
        foreach (var folder in new[] { SolutionDirectory, ProjectDirectory }.OfType<string>().Distinct(SolutionService.PathComparer))
        {
            if (Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any())
            {
                return Localizer.Format("msg.newProject.folderExists", "The folder \"{0}\" already exists. Choose another name or location.", folder);
            }
        }

        if (LongestPath(rules) is { } length && length >= PathRules.MaxPath)
        {
            return Localizer.Format("msg.newProject.pathTooLong", "The path of a file in this project would be {0} characters long, more than Windows allows ({1}). Choose a shorter location or name, or turn on long paths in Windows.", length, PathRules.MaxPath);
        }

        return null;
    }

    /// <summary>The length of the longest path the template would write (Windows without long paths only; else null).</summary>
    public int? LongestPath(PathRules rules)
    {
        if (!rules.IsWindows || rules.LongPathsEnabled || ProjectDirectory is null || Request.Member?.LongestContentPath is not { } longest)
        {
            return null;
        }

        var name = Request.ProjectName.Trim();
        var relative = Request.Member.SourceName is { } source ? longest.Replace(source, name, StringComparison.Ordinal) : longest;
        return Path.TrimEndingDirectorySeparator(ProjectDirectory).Length + 1 + relative.Length;
    }

    /// <summary>
    /// A warning when the location's <c>global.json</c> pins an SDK older than the framework needs (projects are created
    /// with the default SDK, but build with the pinned one in that folder), or null. It walks up at most 32 folders to
    /// the first <c>global.json</c> and reads <c>sdk.version</c> and <c>sdk.rollForward</c>; no code runs. Off the UI thread.
    /// </summary>
    public static string? GlobalJsonWarning(string? location, string? framework)
    {
        if (FullLocation(location) is not { } folder || framework is null || FrameworkOrder.Key(framework) is not (0, { Major: > 0 } needed))
        {
            return null;
        }

        try
        {
            var current = new DirectoryInfo(folder);
            for (var i = 0; i < 32 && current is not null; i++, current = current.Parent)
            {
                var file = Path.Combine(current.FullName, "global.json");
                if (!File.Exists(file))
                {
                    continue;
                }

                using var document = TemplateJson.Parse(File.ReadAllText(file));
                if (document?.RootElement.Object("sdk") is not { } sdk || sdk.Text("version") is not { Length: > 0 } version)
                {
                    return null;
                }

                var rollForward = sdk.Text("rollForward") ?? string.Empty;
                var major = int.TryParse(version.Split('.')[0], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var m) ? m : 0;
                if (major > 0 && needed.Major > major && !rollForward.Equals("latestMajor", StringComparison.OrdinalIgnoreCase))
                {
                    return Localizer.Format("msg.newProject.globalJson", "This location's global.json selects .NET SDK {0}, which can't build {1} projects.", version, TemplateHelp.Display(framework));
                }

                return null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or System.Security.SecurityException)
        {
            // An unreadable global.json warns about nothing.
        }

        return null;
    }

    /// <summary>The values conditions see: the template's values, the framework and the authentication type.</summary>
    public IReadOnlyDictionary<string, string?> ConditionValues()
    {
        var values = new Dictionary<string, string?>(Request.Values, StringComparer.Ordinal);
        if (Request.Framework is { } framework)
        {
            values["Framework"] = framework;
        }

        if (Request.HostOptions.Auth is { } auth)
        {
            values["auth"] = auth;
        }

        return values;
    }

    /// <summary>
    /// <c>dotnet new</c>'s arguments for the project, from the chosen member only: the short name, name, output folder,
    /// language and framework; then each of the member's parameters whose value differs from the template's default and
    /// whose condition holds (inverted check boxes already turned back, values the member lacks left out); then the host
    /// options; then <c>--no-restore</c> when the template restores (the build host restores once the solution opens).
    /// The template hive is never here: <see cref="ITemplateCli"/> adds it.
    /// </summary>
    public IReadOnlyList<string> Arguments(PathRules rules)
    {
        var request = Request;
        var member = request.Member ?? request.Group.MemberFor(request.Framework);
        var arguments = new List<string> { request.Group.ShortName, "--name", request.ProjectName.Trim(), "--output", ProjectDirectory ?? string.Empty };
        if (request.Group.Language.Length > 0)
        {
            arguments.AddRange(["--language", request.Group.Language]);
        }

        if (member is null)
        {
            return arguments;
        }

        if (request.Framework is { } framework && member.Frameworks.Any(f => string.Equals(f.Moniker, framework, StringComparison.OrdinalIgnoreCase)))
        {
            arguments.AddRange([member.FrameworkOption, framework]);
        }

        var conditions = ConditionValues();
        foreach (var parameter in member.Parameters)
        {
            if (!request.Values.TryGetValue(parameter.Name, out var value) || value is null
                || string.Equals(value, parameter.DefaultValue ?? (parameter.DataType == TemplateDataType.Bool ? "false" : null), StringComparison.OrdinalIgnoreCase)
                || !TemplateCondition.Evaluate(parameter.EnabledCondition, conditions, member.ComputedSymbols))
            {
                continue;
            }

            var cliValue = parameter.InverseCliValue && bool.TryParse(value, out var flag) ? (!flag ? "true" : "false") : value;
            if (parameter.DataType == TemplateDataType.Text && cliValue.Length == 0)
            {
                continue;
            }

            arguments.AddRange([parameter.CliOption, cliValue]);
        }

        arguments.AddRange(TemplateHostOptions.Arguments(member.HostOptions, request.HostOptions, rules));
        if (member.SkipRestoreOption is { } noRestore)
        {
            arguments.Add(noRestore);
        }

        return arguments;
    }
}
