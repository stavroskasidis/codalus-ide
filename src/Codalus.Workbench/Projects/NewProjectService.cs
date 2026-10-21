using System.Text;
using Codalus.Workbench.Localization;

namespace Codalus.Workbench.Projects;

/// <summary>What New Project created: the new solution (if any), the project files, the files to open, and the CLI's log.</summary>
public sealed record NewProjectResult(
    string? SolutionPath,
    string? ProjectPath,
    IReadOnlyList<string> ExtraProjects,
    IReadOnlyList<string> FilesToOpen,
    string Log);

/// <summary>New Project failed: <see cref="Exception.Message"/> is the CLI's last meaningful line, <see cref="Log"/> everything.</summary>
public sealed class NewProjectException(string message, string log) : Exception(message)
{
    public string Log { get; } = log;
}

/// <summary>
/// Creates what a <see cref="NewProjectPlan"/> describes (ADR 0082), off the UI thread and cancellable, through the
/// user's <c>dotnet</c> CLI (<see cref="ITemplateCli"/>: one process per step, in the neutral folder, with
/// <c>--no-restore</c>): the new solution (<c>dotnet new sln</c>, the SDK's default format), then the project
/// (<c>dotnet new &lt;template&gt;</c>, or the .NET Framework files the IDE writes itself). Nothing here opens or edits a
/// solution: the shell adds the project to it through the build host after the wizard closes. A failure or a cancel
/// removes the folders this run created, so the same names can be used again.
/// </summary>
public sealed class NewProjectService(ITemplateCli cli, PathRules? rules = null)
{
    private static readonly string[] s_solutionExtensions = [".slnx", ".sln"];

    private readonly PathRules _rules = rules ?? PathRules.Current;

    public async Task<NewProjectResult> CreateAsync(NewProjectPlan plan, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var log = new StringBuilder();
        var created = new List<string>();
        try
        {
            return await Task.Run(() => RunAsync(plan, progress, log, created, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is NewProjectException or OperationCanceledException or IOException or UnauthorizedAccessException)
        {
            foreach (var folder in created.AsEnumerable().Reverse())
            {
                TryDelete(folder);
            }

            if (ex is IOException or UnauthorizedAccessException)
            {
                throw new NewProjectException(ex.Message, log.ToString());
            }

            throw;
        }
    }

    private async Task<NewProjectResult> RunAsync(NewProjectPlan plan, IProgress<string>? progress, StringBuilder log, List<string> created, CancellationToken cancellationToken)
    {
        var request = plan.Request;
        if (plan.Location is null)
        {
            throw new NewProjectException(plan.ValidateLocation() ?? Localizer.Get("msg.newProject.locationEmpty", "Enter a location."), string.Empty);
        }

        Remember(plan.Location, created);
        Directory.CreateDirectory(plan.Location);
        string? solution = null;
        if (plan.CreatesSolution && plan.SolutionDirectory is { } solutionDirectory && plan.SolutionName is { } solutionName)
        {
            progress?.Report(Localizer.Format("msg.newProject.creating", "Creating {0}…", solutionName));
            Remember(solutionDirectory, created);
            await RunCliAsync(["new", "sln", "--name", solutionName, "--output", solutionDirectory], log, cancellationToken).ConfigureAwait(false);
            solution = s_solutionExtensions.Select(e => Path.Combine(solutionDirectory, solutionName + e)).FirstOrDefault(File.Exists)
                ?? (Directory.Exists(solutionDirectory) ? Directory.EnumerateFiles(solutionDirectory, "*.sln*") : []).FirstOrDefault(f => f.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
                ?? throw new NewProjectException(Localizer.Format("msg.newProject.noSolutionFile", "dotnet new sln created no solution in {0}.", solutionDirectory), log.ToString());
        }

        if (plan.IsBlankSolution || plan.ProjectDirectory is not { } projectDirectory)
        {
            return new NewProjectResult(solution, null, [], [], log.ToString());
        }

        var name = request.ProjectName.Trim();
        progress?.Report(Localizer.Format("msg.newProject.creating", "Creating {0}…", name));
        Remember(projectDirectory, created);
        var member = request.Member ?? request.Group.MemberFor(request.Framework);
        IReadOnlyList<string> filesToOpen = [];
        if (request.Group.Kind == ProjectTemplateKind.NetFramework)
        {
            Directory.CreateDirectory(projectDirectory);
            var framework = request.Framework ?? NetFrameworkTemplates.Frameworks[0];
            var files = NetFrameworkTemplates.Files(request.Group.ShortName, name, framework);
            foreach (var (path, text) in files)
            {
                await File.WriteAllTextAsync(Path.Combine(projectDirectory, path), text, cancellationToken).ConfigureAwait(false);
            }

            log.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"Wrote the .NET Framework project {Path.Combine(projectDirectory, name + ".csproj")} ({framework}).");
            filesToOpen = [Path.Combine(projectDirectory, files.Count > 1 ? files[1].Path : files[0].Path)];
        }
        else
        {
            await RunCliAsync(["new", .. plan.Arguments(_rules)], log, cancellationToken).ConfigureAwait(false);
            if (member?.OpenFileRule is { } rule)
            {
                filesToOpen = rule.Resolve(plan.ConditionValues(), projectDirectory, name);
            }
        }

        var projects = ProjectFiles(projectDirectory);
        var project = projects.FirstOrDefault(p => string.Equals(Path.GetFileNameWithoutExtension(p), name, StringComparison.OrdinalIgnoreCase)) ?? (projects.Count > 0 ? projects[0] : null)
            ?? throw new NewProjectException(Localizer.Format("msg.newProject.noProjectFile", "The template created {0} but no project file.", projectDirectory), log.ToString());
        return new NewProjectResult(solution, project, [.. projects.Where(p => p != project)], filesToOpen, log.ToString());
    }

    /// <summary>The project files a template wrote: in its folder, else one folder down (a Blazor app with a client project).</summary>
    internal static IReadOnlyList<string> ProjectFiles(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return [];
        }

        static bool IsProject(string file) => Path.GetExtension(file) is ".csproj" or ".fsproj" or ".vbproj";
        var top = Directory.EnumerateFiles(directory).Where(IsProject).Order(StringComparer.OrdinalIgnoreCase).ToList();
        return top.Count > 0
            ? top
            : [.. Directory.EnumerateDirectories(directory).SelectMany(d => Directory.EnumerateFiles(d).Where(IsProject)).Order(StringComparer.OrdinalIgnoreCase)];
    }

    private async Task RunCliAsync(IReadOnlyList<string> args, StringBuilder log, CancellationToken cancellationToken)
    {
        log.Append("dotnet ").AppendLine(string.Join(' ', args.Select(a => a.Contains(' ', StringComparison.Ordinal) ? $"\"{a}\"" : a)));
        var result = await cli.RunAsync(args, cancellationToken).ConfigureAwait(false);
        log.AppendLine(result.Output.TrimEnd()).AppendLine();
        if (!result.Succeeded)
        {
            throw new NewProjectException(result.LastMeaningfulLine, log.ToString());
        }
    }

    /// <summary>Notes a folder this run creates (it didn't exist), to remove on failure.</summary>
    private static void Remember(string folder, List<string> created)
    {
        if (!Directory.Exists(folder))
        {
            created.Add(folder);
        }
    }

    private static void TryDelete(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left behind: the next try names another folder or says it exists.
        }
    }
}
