using System.Text;

namespace Codalus.Workbench.Projects;

/// <summary>An Aspire AppHost and ServiceDefaults project (either may be missing).</summary>
public sealed record AspireProjects(string? AppHost, string? ServiceDefaults)
{
    public static AspireProjects None { get; } = new(null, null);
}

/// <summary>
/// What Enlist in Aspire orchestration did: the AppHost and ServiceDefaults projects (created or the solution's own),
/// which of them it created, and the lines to add by hand when a file didn't have the text the edits look for.
/// </summary>
public sealed record AspireResult(string AppHost, string ServiceDefaults, bool CreatedAppHost, bool CreatedServiceDefaults, string? ManualSteps);

/// <summary>
/// Enlist in Aspire orchestration (ADR 0082), as the classic IDE does it for a new web project: an AppHost and a
/// ServiceDefaults project (the solution's own when it has them, else new ones from the Aspire templates, installed
/// from nuget.org first when they're missing), the web project's <c>Program.cs</c> calling <c>AddServiceDefaults</c>
/// and <c>MapDefaultEndpoints</c>, and the AppHost adding the project. The text edits keep each file's line endings
/// and byte order mark; the project references are the build host's (the shell adds them).
/// </summary>
public static class AspireEnlistment
{
    public const string AppHostTemplate = "aspire-apphost";

    public const string ServiceDefaultsTemplate = "aspire-servicedefaults";

    /// <summary>The package with the Aspire project templates.</summary>
    public const string TemplatesPackage = "Aspire.ProjectTemplates";

    private static readonly string[] s_appHostFiles = ["AppHost.cs", "Program.cs"];

    /// <summary>
    /// The solution's AppHost (a project with the Aspire AppHost SDK, or <c>IsAspireHost</c>) and ServiceDefaults
    /// (<c>IsAspireSharedProject</c>), read from the project files as text (nothing is evaluated). Off the UI thread.
    /// </summary>
    public static AspireProjects Detect(IEnumerable<string> projectFiles)
    {
        string? appHost = null, serviceDefaults = null;
        foreach (var project in projectFiles)
        {
            string text;
            try
            {
                text = File.ReadAllText(project);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            if (appHost is null && (text.Contains("Aspire.AppHost.Sdk", StringComparison.OrdinalIgnoreCase) || text.Contains("<IsAspireHost>true</IsAspireHost>", StringComparison.OrdinalIgnoreCase)))
            {
                appHost = project;
            }
            else if (serviceDefaults is null && text.Contains("<IsAspireSharedProject>true</IsAspireSharedProject>", StringComparison.OrdinalIgnoreCase))
            {
                serviceDefaults = project;
            }
        }

        return new AspireProjects(appHost, serviceDefaults);
    }

    /// <summary>The AppHost's file that builds the distributed application (<c>AppHost.cs</c> in newer templates, <c>Program.cs</c> in older ones).</summary>
    public static string? AppHostFile(string appHostProject)
    {
        var folder = Path.GetDirectoryName(appHostProject)!;
        foreach (var name in s_appHostFiles.Select(n => Path.Combine(folder, n)).Concat(Directory.Exists(folder) ? Directory.EnumerateFiles(folder, "*.cs") : []))
        {
            if (File.Exists(name) && File.ReadAllText(name).Contains("DistributedApplication.CreateBuilder(", StringComparison.Ordinal))
            {
                return name;
            }
        }

        return null;
    }

    /// <summary>The resource name the AppHost gives a project: lowercase, dots as dashes (<c>My.Api</c> → <c>my-api</c>).</summary>
    public static string ResourceName(string projectName) => projectName.ToLowerInvariant().Replace('.', '-').Replace('_', '-');

    /// <summary>The type the AppHost generates for a project reference: <c>Projects.My_Api</c> for <c>My.Api</c>.</summary>
    public static string ProjectsType(string projectName)
    {
        var builder = new StringBuilder("Projects.");
        foreach (var c in projectName)
        {
            builder.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');
        }

        return char.IsDigit(projectName.FirstOrDefault()) ? builder.Insert("Projects.".Length, '_').ToString() : builder.ToString();
    }

    /// <summary>
    /// <c>builder.AddServiceDefaults();</c> after the statement that creates the web application's builder, and
    /// <c>app.MapDefaultEndpoints();</c> after <c>var app = builder.Build();</c>. Null when done; else the lines to add.
    /// </summary>
    public static string? AddServiceDefaults(string programFile)
    {
        var (lines, newline, encoding) = Read(programFile);
        var create = lines.FindIndex(l => l.Contains("WebApplication.CreateBuilder(", StringComparison.Ordinal) || l.Contains("WebApplication.CreateSlimBuilder(", StringComparison.Ordinal));
        var build = lines.FindIndex(l => l.Contains("= builder.Build()", StringComparison.Ordinal));
        if (lines.Any(l => l.Contains("AddServiceDefaults(", StringComparison.Ordinal)))
        {
            return null; // already enlisted
        }

        if (create < 0 || build < 0 || build < create)
        {
            return "builder.AddServiceDefaults();  // after the builder is created\napp.MapDefaultEndpoints();  // after var app = builder.Build();";
        }

        var createEnd = StatementEnd(lines, create);
        var buildEnd = StatementEnd(lines, build);
        var indent = Indent(lines[create]);
        lines.Insert(buildEnd + 1, string.Empty);
        lines.Insert(buildEnd + 2, Indent(lines[build]) + "app.MapDefaultEndpoints();");
        lines.Insert(createEnd + 1, string.Empty);
        lines.Insert(createEnd + 2, indent + "builder.AddServiceDefaults();");
        Write(programFile, lines, newline, encoding);
        return null;
    }

    /// <summary>
    /// <c>builder.AddProject&lt;Projects.X&gt;("x");</c> before <c>builder.Build().Run();</c> in the AppHost file.
    /// Null when done; else the line to add.
    /// </summary>
    public static string? AddToAppHost(string appHostFile, string projectPath)
    {
        var name = Path.GetFileNameWithoutExtension(projectPath);
        var line = $"builder.AddProject<{ProjectsType(name)}>(\"{ResourceName(name)}\");";
        var (lines, newline, encoding) = Read(appHostFile);
        if (lines.Any(l => l.Contains($"<{ProjectsType(name)}>", StringComparison.Ordinal)))
        {
            return null;
        }

        var run = lines.FindIndex(l => l.Contains("builder.Build().Run()", StringComparison.Ordinal) || l.Contains("builder.Build().RunAsync()", StringComparison.Ordinal));
        if (run < 0)
        {
            return line;
        }

        lines.Insert(run, Indent(lines[run]) + line);
        if (run > 0 && lines[run - 1].Trim().Length > 0 && !lines[run - 1].Contains("AddProject<", StringComparison.Ordinal))
        {
            lines.Insert(run, string.Empty);
        }

        Write(appHostFile, lines, newline, encoding);
        return null;
    }

    /// <summary>The line where the statement that starts at <paramref name="start"/> ends (its <c>;</c>).</summary>
    private static int StatementEnd(List<string> lines, int start)
    {
        for (var i = start; i < lines.Count && i < start + 20; i++)
        {
            if (lines[i].TrimEnd().EndsWith(';'))
            {
                return i;
            }
        }

        return start;
    }

    private static string Indent(string line) => line[..(line.Length - line.TrimStart().Length)];

    private static (List<string> Lines, string Newline, Encoding Encoding) Read(string file)
    {
        var bytes = File.ReadAllBytes(file);
        var bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        var text = Encoding.UTF8.GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0));
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        return ([.. text.Split(newline)], newline, new UTF8Encoding(encoderShouldEmitUTF8Identifier: bom));
    }

    private static void Write(string file, List<string> lines, string newline, Encoding encoding) =>
        File.WriteAllText(file, string.Join(newline, lines), encoding);
}
