using Codalus.Projects.Protocol;
using Codalus.Workbench.Projects;

namespace Codalus.Workbench.Build;

/// <summary>
/// The projects of a Blazor Web App, from their references: its server references the
/// <c>Microsoft.AspNetCore.Components.WebAssembly.Server</c> package and its WebAssembly client project, which references
/// <c>Microsoft.AspNetCore.Components.WebAssembly</c> (both templates' layout, ADR 0027).
/// </summary>
public static class BlazorProjects
{
    private const string ClientPackage = "Microsoft.AspNetCore.Components.WebAssembly";
    private const string ServerPackage = "Microsoft.AspNetCore.Components.WebAssembly.Server";

    /// <summary>Whether a project is a Blazor WebAssembly app (its components run in the browser).</summary>
    public static bool IsWebAssemblyClient(ProjectInfo project) => HasPackage(project, ClientPackage);

    /// <summary>The WebAssembly client projects a Blazor Web App's server references; empty for other projects.</summary>
    public static IReadOnlyList<string> ClientsOf(string server, IReadOnlyDictionary<string, ProjectInfo> projects)
    {
        if (!projects.TryGetValue(server, out var info) || !HasPackage(info, ServerPackage))
        {
            return [];
        }

        return [.. info.References
            .Where(r => r.Kind == ReferenceKinds.Project && r.Path is not null)
            .Select(r => r.Path!)
            .Where(path => projects.TryGetValue(path, out var client) && IsWebAssemblyClient(client))
            .Distinct(SolutionService.PathComparer)];
    }

    /// <summary>
    /// The projects whose code runs in the browser for a Blazor Web App's server (ADR 0090): its WebAssembly clients and
    /// the projects they reference, directly or not (a library the server shares runs in both).
    /// </summary>
    public static IReadOnlyList<string> BrowserProjectsOf(string server, IReadOnlyDictionary<string, ProjectInfo> projects)
    {
        var found = new HashSet<string>(SolutionService.PathComparer);
        var pending = new Stack<string>(ClientsOf(server, projects));
        while (pending.TryPop(out var project))
        {
            if (!found.Add(project) || !projects.TryGetValue(project, out var info))
            {
                continue;
            }

            foreach (var reference in info.References.Where(r => r.Kind == ReferenceKinds.Project && r.Path is not null))
            {
                pending.Push(reference.Path!);
            }
        }

        return [.. found];
    }

    /// <summary>Whether a project is a Blazor Web App's server: it hosts a WebAssembly client project of the solution.</summary>
    public static bool IsWebAppServer(string project, IReadOnlyDictionary<string, ProjectInfo> projects) => ClientsOf(project, projects).Count > 0;

    /// <summary>The Blazor Web App server that hosts <paramref name="client"/>, or null (a standalone WebAssembly app, another project).</summary>
    public static string? WebAppServerOf(string client, IReadOnlyDictionary<string, ProjectInfo> projects)
    {
        if (!projects.TryGetValue(client, out var info) || !IsWebAssemblyClient(info))
        {
            return null;
        }

        return projects.Keys.FirstOrDefault(server => ClientsOf(server, projects).Contains(client, SolutionService.PathComparer));
    }

    private static bool HasPackage(ProjectInfo project, string id) =>
        project.References.Any(r => r.Kind == ReferenceKinds.Package && string.Equals(r.Name, id, StringComparison.OrdinalIgnoreCase));
}
