using DaedalusStudio.Projects.Protocol;

namespace DaedalusStudio.Workbench.Projects;

/// <summary>
/// Project properties, references, configurations, build dependencies, load/unload, solution filters and SDKs, through
/// the build host (ADR 0013). <see cref="Changed"/> fires after every edit so the views that show them refresh.
/// </summary>
public sealed class ProjectManagementService(SolutionService solutions)
{
    private static ProjectManagementJsonContext Json => ProjectManagementJsonContext.Default;

    /// <summary>Something this service changed (a property, a reference, the solution's configurations…). Any thread.</summary>
    public event EventHandler? Changed;

    public Task<ProjectPropertiesResult> GetPropertiesAsync(string project, IReadOnlyList<string> names, string? configuration, CancellationToken cancellationToken) =>
        solutions.RequestAsync(ProjectManagementMethods.GetProperties, new GetPropertiesParams(project, [.. names], configuration), Json.GetPropertiesParams, Json.ProjectPropertiesResult, cancellationToken);

    /// <summary>Sets a property (null: removes the project file's value in that scope). Returns whether the file changed.</summary>
    public async Task<bool> SetPropertyAsync(string project, string name, string? value, string? configuration, CancellationToken cancellationToken)
    {
        var result = await solutions.RequestAsync(ProjectManagementMethods.SetProperty, new SetPropertyParams(project, name, value, configuration), Json.SetPropertyParams, Json.SetPropertyResult, cancellationToken).ConfigureAwait(false);
        if (result.Changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return result.Changed;
    }

    public Task AddReferenceAsync(string project, string kind, string path, CancellationToken cancellationToken) =>
        EditAsync(ProjectManagementMethods.AddReference, new ReferenceEditParams(project, kind, path), Json.ReferenceEditParams, cancellationToken);

    public Task RemoveReferenceAsync(string project, string kind, string path, CancellationToken cancellationToken) =>
        EditAsync(ProjectManagementMethods.RemoveReference, new ReferenceEditParams(project, kind, path), Json.ReferenceEditParams, cancellationToken);

    public Task<SolutionConfigurationsResult> GetConfigurationsAsync(CancellationToken cancellationToken) =>
        solutions.RequestAsync(ProjectManagementMethods.Configurations, new EmptyParams(), Json.EmptyParams, Json.SolutionConfigurationsResult, cancellationToken);

    public Task SetProjectConfigurationAsync(SetProjectConfigurationParams parameters, CancellationToken cancellationToken) =>
        EditAsync(ProjectManagementMethods.SetProjectConfiguration, parameters, Json.SetProjectConfigurationParams, cancellationToken);

    public Task AddConfigurationAsync(string dimension, string name, string? copyFrom, CancellationToken cancellationToken) =>
        EditAsync(ProjectManagementMethods.AddConfiguration, new SolutionConfigurationEditParams(dimension, name, copyFrom), Json.SolutionConfigurationEditParams, cancellationToken);

    public Task RemoveConfigurationAsync(string dimension, string name, CancellationToken cancellationToken) =>
        EditAsync(ProjectManagementMethods.RemoveConfiguration, new SolutionConfigurationEditParams(dimension, name), Json.SolutionConfigurationEditParams, cancellationToken);

    public async Task<IReadOnlyList<ProjectDependencies>> GetDependenciesAsync(CancellationToken cancellationToken) =>
        (await solutions.RequestAsync(ProjectManagementMethods.Dependencies, new EmptyParams(), Json.EmptyParams, Json.SolutionDependenciesResult, cancellationToken).ConfigureAwait(false)).Projects;

    public Task SetDependenciesAsync(string project, IReadOnlyList<string> dependsOn, CancellationToken cancellationToken) =>
        EditAsync(ProjectManagementMethods.SetDependencies, new SetDependenciesParams(project, [.. dependsOn]), Json.SetDependenciesParams, cancellationToken);

    public Task UnloadAsync(string project, CancellationToken cancellationToken) =>
        EditAsync(ProjectManagementMethods.Unload, new ProjectPathParams(project), Json.ProjectPathParams, cancellationToken);

    public Task ReloadAsync(string project, CancellationToken cancellationToken) =>
        EditAsync(ProjectManagementMethods.Reload, new ProjectPathParams(project), Json.ProjectPathParams, cancellationToken);

    public Task SaveFilterAsync(string path, IReadOnlyList<string>? projects, CancellationToken cancellationToken) =>
        EditAsync(ProjectManagementMethods.SaveFilter, new SaveFilterParams(path, projects is null ? null : [.. projects]), Json.SaveFilterParams, cancellationToken);

    public Task<SdkInfoResult> GetSdkInfoAsync(CancellationToken cancellationToken) =>
        solutions.RequestAsync(ProjectManagementMethods.SdkInfo, new EmptyParams(), Json.EmptyParams, Json.SdkInfoResult, cancellationToken);

    public Task SetGlobalJsonAsync(string? version, string? rollForward, CancellationToken cancellationToken) =>
        EditAsync(ProjectManagementMethods.SetGlobalJson, new SetGlobalJsonParams(version, rollForward), Json.SetGlobalJsonParams, cancellationToken);

    private async Task EditAsync<T>(string method, T parameters, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info, CancellationToken cancellationToken)
    {
        await solutions.RequestAsync(method, parameters, info, Json.EmptyResult, cancellationToken).ConfigureAwait(false);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// The build order: each project after the projects it references and the solution dependencies it declares
    /// (a stable topological order; cycles keep the solution's order).
    /// </summary>
    public static IReadOnlyList<string> BuildOrder(IReadOnlyList<string> projects, IReadOnlyDictionary<string, ProjectInfo> evaluated, IReadOnlyList<ProjectDependencies> dependencies)
    {
        var edges = projects.ToDictionary(p => p, _ => new List<string>(), SolutionService.PathComparer);
        foreach (var project in projects)
        {
            if (evaluated.TryGetValue(project, out var info))
            {
                edges[project].AddRange(info.References.Where(r => r.Kind == ReferenceKinds.Project && r.Path is not null && edges.ContainsKey(r.Path)).Select(r => r.Path!));
            }

            if (dependencies.FirstOrDefault(d => SolutionService.PathComparer.Equals(d.Project, project)) is { } declared)
            {
                edges[project].AddRange(declared.DependsOn.Where(edges.ContainsKey));
            }
        }

        var order = new List<string>();
        var state = new Dictionary<string, int>(SolutionService.PathComparer);
        void Visit(string project)
        {
            if (state.GetValueOrDefault(project) != 0)
            {
                return; // done, or on the stack (a cycle)
            }

            state[project] = 1;
            foreach (var dependency in edges[project].Distinct(SolutionService.PathComparer))
            {
                Visit(dependency);
            }

            state[project] = 2;
            order.Add(project);
        }

        foreach (var project in projects)
        {
            Visit(project);
        }

        return order;
    }
}
