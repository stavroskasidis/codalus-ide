using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Codalus.Projects.Host.Packages;
using Codalus.Projects.Host.ProjectManagement;
using Codalus.Projects.Protocol;

namespace Codalus.Projects.Host;

/// <summary>Project items, Show All Files, folder mode and item templates (ADR 0033).</summary>
internal sealed partial class SolutionSession
{
    /// <summary>The environment variable that gives <c>dotnet new</c> a custom settings folder (tests).</summary>
    public const string TemplateHiveVariable = "CODALUS_TEMPLATE_HIVE";

    private static readonly string[] s_projectExtensions = [".csproj", ".fsproj", ".vbproj"];

    /// <summary>Folder mode: a folder opened without a solution file (ADR 0033).</summary>
    public bool IsFolder { get; private set; }

    /// <summary>Whether the shell asked for a restore when the solution opened (projects loaded into a folder later get one too).</summary>
    private bool _restoreOnOpen;

    private string FolderStateFile => System.IO.Path.Combine(Path, ".codalus", "folder.json");

    // ------------------------------------------------------------------ items

    /// <summary>The global properties item edits evaluate a project with: the active configuration, as for properties.</summary>
    private Dictionary<string, string> ItemGlobals(string project)
    {
        var properties = SolutionProperties();
        (properties["Configuration"], properties["Platform"]) = ProjectConfiguration(project);
        return properties;
    }

    public ItemInfoResult GetItem(ItemParams parameters)
    {
        RequireTrusted("Item properties");
        var project = System.IO.Path.GetFullPath(parameters.Project);
        using var items = ProjectItems.Open(project, ItemGlobals(project));
        return items.Describe(parameters.Path);
    }

    public Task<ItemEditResult> SetItemAsync(ItemSetParams parameters) =>
        EditItemsAsync(parameters.Project, items => items.Set(parameters.Path, parameters.ItemType, parameters.Metadata));

    public Task<ItemEditResult> ExcludeAsync(ItemPathsParams parameters) =>
        EditItemsAsync(parameters.Project, items =>
        {
            foreach (var path in parameters.Paths)
            {
                items.Exclude(path);
            }
        });

    public Task<ItemEditResult> IncludeAsync(ItemPathsParams parameters) =>
        EditItemsAsync(parameters.Project, items =>
        {
            foreach (var path in parameters.Paths)
            {
                items.IncludeInProject(path);
            }
        });

    /// <summary>F# compile order (M36): moves a <c>Compile</c> item before or after another in the project file.</summary>
    public Task<ItemEditResult> MoveItemAsync(MoveItemParams parameters) =>
        EditItemsAsync(parameters.Project, items => items.Move(parameters.Path, parameters.Target, parameters.After));

    /// <summary>Add Existing Item: copies the files into the folder (or links them), and makes sure they are items.</summary>
    public async Task<FileOperationResult> AddExistingAsync(AddExistingItemsParams parameters)
    {
        RequireTrusted("Adding items");
        var target = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(parameters.TargetFolder));
        var files = parameters.Files.Select(System.IO.Path.GetFullPath).ToList();
        var created = new List<string>();
        if (!parameters.AsLink)
        {
            foreach (var file in files)
            {
                var destination = System.IO.Path.Combine(target, System.IO.Path.GetFileName(file));
                if (!s_pathComparer.Equals(file, destination))
                {
                    if (File.Exists(destination) || Directory.Exists(destination))
                    {
                        throw new IOException($"'{System.IO.Path.GetFileName(destination)}' already exists in {target}.");
                    }

                    Directory.CreateDirectory(target);
                    File.Copy(file, destination);
                    created.Add(destination);
                }
            }
        }

        var result = await EditItemsAsync(parameters.Project, items =>
        {
            if (parameters.AsLink)
            {
                items.AddLinks(files, target);
            }
            else
            {
                foreach (var file in files)
                {
                    items.IncludeInProject(System.IO.Path.Combine(target, System.IO.Path.GetFileName(file)));
                }
            }
        }).ConfigureAwait(false);
        return new FileOperationResult(result.UpdatedFiles, [.. created]);
    }

    private async Task<ItemEditResult> EditItemsAsync(string projectPath, Action<ProjectItems> edit)
    {
        RequireTrusted("Project item edits");
        var project = System.IO.Path.GetFullPath(projectPath);
        bool changed;
        using (var items = ProjectItems.Open(project, ItemGlobals(project)))
        {
            edit(items);
            changed = items.Save();
        }

        if (changed)
        {
            await ReevaluateAsync([project]).ConfigureAwait(false);
        }

        return new ItemEditResult(changed, changed ? [project] : []);
    }

    /// <summary>The loaded project whose folder holds <paramref name="path"/> (the innermost), or null.</summary>
    private string? OwningProject(string path) =>
        ProjectPaths.Where(p => !IsUnloaded(p) && ProjectFileEditor.IsSameOrUnder(path, System.IO.Path.GetDirectoryName(p)!))
            .OrderByDescending(p => System.IO.Path.GetDirectoryName(p)!.Length)
            .FirstOrDefault();

    /// <summary>
    /// After a file moved (or was copied) from one project's folder into another's: takes the source projects' explicit
    /// items for it and applies them in the target project (ADR 0033). Returns the project files that changed.
    /// </summary>
    private List<string> CarryItems(string source, string destination, bool copy)
    {
        var updated = new List<string>();
        var carried = new List<ProjectItems.CarriedItem>();
        var target = OwningProject(destination);
        foreach (var project in ProjectPaths.Where(File.Exists).ToList())
        {
            var directory = System.IO.Path.GetDirectoryName(project)!;
            if (s_pathComparer.Equals(project, target) || !ProjectFileEditor.IsSameOrUnder(source, directory) || ProjectFileEditor.IsSameOrUnder(destination, directory))
            {
                continue; // not its file, or it stays inside: the rename keeps its items
            }

            if (_editor.TakeItems(project, source, copy, carried))
            {
                updated.Add(project);
            }
        }

        if (target is not null && carried.Count > 0 && Trusted)
        {
            using var items = ProjectItems.Open(target, ItemGlobals(target));
            items.Receive(destination, carried);
            if (items.Save())
            {
                updated.Add(target);
            }
        }

        return updated;
    }

    // ------------------------------------------------------------------ Show All Files

    /// <summary>Everything under a folder, without version control, IDE and package folders, up to a limit.</summary>
    public ListTreeResult ListTree(ListTreeParams parameters) => ExplorerFiles.ListTree(parameters);

    // ------------------------------------------------------------------ folder mode

    /// <summary>The projects of a folder workspace, as an in-memory solution.</summary>
    private SolutionInfo FolderInfo(IReadOnlyList<string> projects) => new(
        Path,
        SolutionFormats.Folder,
        [],
        [.. projects.Select(p => new SolutionProjectInfo(p, System.IO.Path.GetFileNameWithoutExtension(p), null, null))],
        [new("Debug", "Any CPU"), new("Release", "Any CPU")],
        Active,
        Trusted);

    /// <summary>The loaded projects of a folder, from <c>.codalus/folder.json</c> (those that still exist).</summary>
    private List<string> LoadFolderProjects()
    {
        try
        {
            if (File.Exists(FolderStateFile) && JsonNode.Parse(File.ReadAllText(FolderStateFile))?["projects"] is JsonArray projects)
            {
                return [.. projects.OfType<JsonValue>()
                    .Select(v => Full(Path, v.GetValue<string>()))
                    .Where(File.Exists)
                    .Distinct(s_pathComparer)];
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or UnauthorizedAccessException)
        {
            _ = _events.LogAsync("warning", $"Could not read {FolderStateFile}: {ex.Message}");
        }

        return [];
    }

    private void SaveFolderProjects(IReadOnlyList<string> projects)
    {
        var state = File.Exists(FolderStateFile) ? JsonNode.Parse(File.ReadAllText(FolderStateFile)) as JsonObject ?? [] : [];
        state["projects"] = new JsonArray([.. projects.Select(p => (JsonNode)System.IO.Path.GetRelativePath(Path, p).Replace('\\', '/'))]);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FolderStateFile)!);
        File.WriteAllText(FolderStateFile, state.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
    }

    /// <summary>The project files under the folder (without bin, obj, version control and packages).</summary>
    public DiscoverProjectsResult DiscoverProjects()
    {
        var root = IsFolder ? Path : SolutionDirectory;
        var found = new List<string>();
        void Walk(string directory)
        {
            try
            {
                found.AddRange(Directory.EnumerateFiles(directory).Where(f => s_projectExtensions.Contains(System.IO.Path.GetExtension(f), StringComparer.OrdinalIgnoreCase)));
                foreach (var sub in Directory.EnumerateDirectories(directory))
                {
                    var name = System.IO.Path.GetFileName(sub);
                    if (!ExplorerFiles.SkippedFolders.Contains(name) && !name.Equals("bin", StringComparison.OrdinalIgnoreCase) && !name.Equals("obj", StringComparison.OrdinalIgnoreCase))
                    {
                        Walk(sub);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        Walk(root);
        return new DiscoverProjectsResult([.. found.Order(StringComparer.OrdinalIgnoreCase)]);
    }

    /// <summary>Folder mode: the projects of the in-memory solution (loaded ones are evaluated, removed ones dropped).</summary>
    public async Task SetFolderProjectsAsync(SetFolderProjectsParams parameters, CancellationToken cancellationToken)
    {
        if (!IsFolder)
        {
            throw new InvalidOperationException("Only a folder opened without a solution has a project list to change.");
        }

        var projects = parameters.Projects.Select(System.IO.Path.GetFullPath).Where(File.Exists).Distinct(s_pathComparer).ToList();
        await ChangeFolderProjectsAsync(projects, cancellationToken).ConfigureAwait(false);
    }

    private async Task ChangeFolderProjectsAsync(IReadOnlyList<string> projects, CancellationToken cancellationToken)
    {
        await _solutionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var old = ProjectPaths.ToHashSet(s_pathComparer);
            SaveFolderProjects(projects);
            _info = FolderInfo(projects);
            foreach (var removed in old.Where(p => !projects.Contains(p, s_pathComparer)))
            {
                _evaluated.TryRemove(removed, out _);
            }

            await _events.SolutionChangedAsync(_info).ConfigureAwait(false);
            WatchNow();
            var added = ProjectPaths.Where(p => !old.Contains(p)).ToList();
            if (Trusted && added.Count > 0)
            {
                _ = Task.Run(async () =>
                {
                    await EvaluateAsync(added, _lifetime.Token).ConfigureAwait(false);
                    if (_restoreOnOpen && !_lifetime.IsCancellationRequested)
                    {
                        StartRestore(null);
                    }
                }, CancellationToken.None);
            }
        }
        finally
        {
            _solutionLock.Release();
        }
    }

    /// <summary>
    /// What a folder workspace builds: its one loaded project, or a generated <c>folder.slnx</c> (in its state folder) of the loaded
    /// projects.
    /// </summary>
    private string FolderBuildEntryPoint()
    {
        var projects = ProjectPaths.Where(p => !IsUnloaded(p) && !CannotBuild(p)).ToList();
        if (projects.Count == 0)
        {
            throw new InvalidOperationException("No project of this folder is loaded. Load a project (or all of them) to build it.");
        }

        if (projects.Count == 1)
        {
            return projects[0];
        }

        return WriteFolderSolution(SolutionStateLayout.GeneratedFile(Path, IsFolder, "folder.slnx"), projects);
    }

    /// <summary>A generated <c>.slnx</c> of <paramref name="projects"/> (a folder workspace's): returns its path.</summary>
    private static string WriteFolderSolution(string file, IReadOnlyList<string> projects)
    {
        var directory = System.IO.Path.GetDirectoryName(file)!;
        var xml = new StringBuilder("<Solution>\n");
        foreach (var project in projects)
        {
            xml.Append("  <Project Path=\"").Append(System.Security.SecurityElement.Escape(System.IO.Path.GetRelativePath(directory, project).Replace('\\', '/'))).Append("\" />\n");
        }

        xml.Append("</Solution>\n");
        Directory.CreateDirectory(directory);
        File.WriteAllText(file, xml.ToString());
        return file;
    }

    // ------------------------------------------------------------------ item templates

    /// <summary>
    /// Runs a <c>dotnet new</c> item template into a folder of a project (ADR 0033). Its constraints evaluate the project,
    /// so this runs only in trusted solutions. The created files are the ones that weren't there before.
    /// </summary>
    public async Task<InstantiateTemplateResult> InstantiateTemplateAsync(InstantiateTemplateParams parameters, CancellationToken cancellationToken)
    {
        RequireTrusted("Item templates");
        if (string.IsNullOrWhiteSpace(parameters.Template) || string.IsNullOrWhiteSpace(parameters.Name))
        {
            throw new ArgumentException("A template and a name are required.");
        }

        var output = System.IO.Path.GetFullPath(parameters.OutputDirectory);
        Directory.CreateDirectory(output);
        var before = Snapshot(output);
        var args = new List<string> { "new", parameters.Template, "--name", parameters.Name, "--output", output };
        if (parameters.Project is { Length: > 0 } project)
        {
            args.AddRange(["--project", System.IO.Path.GetFullPath(project)]);
        }

        if (parameters.Language is { Length: > 0 } language)
        {
            args.AddRange(["--language", language]);
        }

        args.AddRange(parameters.Arguments ?? []);
        if (Environment.GetEnvironmentVariable(TemplateHiveVariable) is { Length: > 0 } hive)
        {
            args.AddRange(["--debug:custom-hive", hive]);
        }

        var (exitCode, text) = await RunCliAsync(output, args, cancellationToken).ConfigureAwait(false);
        if (exitCode != 0 && text.Contains("is not restored", StringComparison.Ordinal) && parameters.Project is { Length: > 0 } unrestored)
        {
            // A project-capability constraint evaluates the project's assets: restore it, then try again.
            await RunCliAsync(SolutionDirectory, ["restore", System.IO.Path.GetFullPath(unrestored), "--nologo"], cancellationToken).ConfigureAwait(false);
            (exitCode, text) = await RunCliAsync(output, args, cancellationToken).ConfigureAwait(false);
        }

        if (exitCode != 0)
        {
            var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .SkipWhile(l => l.StartsWith("Warning", StringComparison.Ordinal) || l.StartsWith('•') || l.StartsWith("The following templates", StringComparison.Ordinal) || l.StartsWith("The template from", StringComparison.Ordinal))
                .Take(2);
            var reason = string.Join(" ", lines) is { Length: > 0 } joined ? joined : $"exit code {exitCode}";
            throw new InvalidOperationException($"dotnet new {parameters.Template} failed: {reason}");
        }

        var created = Snapshot(output).Where(f => !before.Contains(f)).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        return new InstantiateTemplateResult(created, text);
    }

    private async Task<(int ExitCode, string Output)> RunCliAsync(string directory, IEnumerable<string> args, CancellationToken cancellationToken)
    {
        var psi = DotnetProcess.StartInfo(_dotnet, directory, args);
        psi.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en";
        using var process = DotnetProcess.Start(psi) ?? throw new InvalidOperationException("dotnet did not start.");
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        await DotnetProcess.WaitOrKillAsync(process, timeout.Token).ConfigureAwait(false);
        return (process.ExitCode, (await stdout.ConfigureAwait(false)) + (await stderr.ConfigureAwait(false)));
    }

    /// <summary>The files under a folder (without build outputs).</summary>
    private static HashSet<string> Snapshot(string folder)
    {
        var files = new HashSet<string>(s_pathComparer);
        foreach (var entry in Directory.EnumerateFiles(folder, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 }))
        {
            var relative = System.IO.Path.GetRelativePath(folder, entry);
            var first = relative.Split(System.IO.Path.DirectorySeparatorChar)[0];
            if (!first.Equals("bin", StringComparison.OrdinalIgnoreCase) && !first.Equals("obj", StringComparison.OrdinalIgnoreCase))
            {
                files.Add(entry);
            }
        }

        return files;
    }
}
