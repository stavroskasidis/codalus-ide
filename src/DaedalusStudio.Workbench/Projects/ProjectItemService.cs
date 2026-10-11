using DaedalusStudio.Projects.Protocol;

namespace DaedalusStudio.Workbench.Projects;

/// <summary>
/// Project items (the Properties window, Include and Exclude, Add Existing Item and links), Show All Files, folder mode's
/// projects and <c>dotnet new</c> item templates, through the build host (ADR 0033). <see cref="Changed"/> fires after
/// every edit that changed a project file.
/// </summary>
public sealed class ProjectItemService(SolutionService solutions)
{
    private static ItemJsonContext Json => ItemJsonContext.Default;

    /// <summary>A project file changed through this service. Any thread.</summary>
    public event EventHandler? Changed;

    public Task<ItemInfoResult> GetAsync(string project, string path, CancellationToken cancellationToken) =>
        solutions.RequestAsync(ItemMethods.Get, new ItemParams(project, path), Json.ItemParams, Json.ItemInfoResult, cancellationToken);

    /// <summary>Changes the file's Build Action (<paramref name="itemType"/>, null: keep) and metadata (a null value: inherited).</summary>
    public Task<ItemEditResult> SetAsync(string project, string path, string? itemType, IReadOnlyDictionary<string, string?>? metadata, CancellationToken cancellationToken) =>
        EditAsync(ItemMethods.Set, new ItemSetParams(project, path, itemType, metadata is null ? null : new Dictionary<string, string?>(metadata)), Json.ItemSetParams, cancellationToken);

    public Task<ItemEditResult> ExcludeAsync(string project, IReadOnlyList<string> paths, CancellationToken cancellationToken) =>
        EditAsync(ItemMethods.Exclude, new ItemPathsParams(project, [.. paths]), Json.ItemPathsParams, cancellationToken);

    public Task<ItemEditResult> IncludeAsync(string project, IReadOnlyList<string> paths, CancellationToken cancellationToken) =>
        EditAsync(ItemMethods.Include, new ItemPathsParams(project, [.. paths]), Json.ItemPathsParams, cancellationToken);

    /// <summary>Copies files into a folder of the project (or links them with <paramref name="asLink"/>).</summary>
    public async Task<FileOperationResult> AddExistingAsync(string project, string folder, IReadOnlyList<string> files, bool asLink, CancellationToken cancellationToken)
    {
        var result = await solutions.RequestAsync(ItemMethods.AddExisting, new AddExistingItemsParams(project, folder, [.. files], asLink), Json.AddExistingItemsParams, Json.FileOperationResult, cancellationToken).ConfigureAwait(false);
        if (result.UpdatedFiles.Length > 0)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return result;
    }

    /// <summary>Everything under a folder (Show All Files, the folder view).</summary>
    public Task<ListTreeResult> ListTreeAsync(string path, CancellationToken cancellationToken, int? maxEntries = null) =>
        solutions.RequestAsync(ItemMethods.ListTree, new ListTreeParams(path, maxEntries), Json.ListTreeParams, Json.ListTreeResult, cancellationToken);

    /// <summary>Folder mode: the project files under the folder.</summary>
    public async Task<IReadOnlyList<string>> DiscoverProjectsAsync(CancellationToken cancellationToken) =>
        (await solutions.RequestAsync(ItemMethods.Discover, new EmptyParams(), Json.EmptyParams, Json.DiscoverProjectsResult, cancellationToken).ConfigureAwait(false)).Projects;

    /// <summary>Folder mode: the projects of the folder's in-memory solution.</summary>
    public Task SetFolderProjectsAsync(IReadOnlyList<string> projects, CancellationToken cancellationToken) =>
        solutions.RequestAsync(ItemMethods.SetFolderProjects, new SetFolderProjectsParams([.. projects]), Json.SetFolderProjectsParams, Json.EmptyResult, cancellationToken);

    /// <summary>Runs a <c>dotnet new</c> item template in the build host (trusted solutions only).</summary>
    public Task<InstantiateTemplateResult> InstantiateAsync(InstantiateTemplateParams parameters, CancellationToken cancellationToken) =>
        solutions.RequestAsync(ItemMethods.Instantiate, parameters, Json.InstantiateTemplateParams, Json.InstantiateTemplateResult, cancellationToken, TimeSpan.FromMinutes(3));

    private async Task<ItemEditResult> EditAsync<T>(string method, T parameters, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info, CancellationToken cancellationToken)
    {
        var result = await solutions.RequestAsync(method, parameters, info, Json.ItemEditResult, cancellationToken).ConfigureAwait(false);
        if (result.Changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return result;
    }
}
