using System.Collections.Concurrent;
using System.Diagnostics;
using Daedalus.Projects.Protocol;
using Microsoft.Build.Construction;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Evaluation.Context;
using Microsoft.VisualStudio.SolutionPersistence;
using Microsoft.VisualStudio.SolutionPersistence.Model;
using Microsoft.VisualStudio.SolutionPersistence.Serializer;

namespace Daedalus.Projects.Host;

/// <summary>What the session reports back to the shell.</summary>
internal interface ISessionEvents
{
    Task SolutionChangedAsync(SolutionInfo solution);

    Task ProjectEvaluatedAsync(ProjectInfo project);

    Task ProgressAsync(EvaluationProgressParams progress);

    Task RestoreOutputAsync(string line);

    Task RestoreStateAsync(string state, string? message);

    Task LogAsync(string level, string message);
}

/// <summary>
/// One open solution (or a single project opened directly). Reads the solution file, evaluates projects in
/// parallel (only when trusted), re-evaluates on file changes, runs restore, and performs file and solution edits.
/// </summary>
internal sealed class SolutionSession : IAsyncDisposable
{
    private static readonly StringComparer s_pathComparer =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private readonly ISessionEvents _events;
    private readonly string _dotnet;
    private readonly ProjectCollection _collection = new();
    private readonly ProjectEvaluator _evaluator;
    private readonly ProjectFileEditor _editor;
    private readonly ConcurrentDictionary<string, EvaluatedProject> _evaluated = new(s_pathComparer);
    private readonly SemaphoreSlim _solutionLock = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private ProjectWatcher? _watcher;
    private SolutionModel? _model;
    private ISolutionSerializer? _serializer;
    private SolutionInfo _info = null!;
    private CancellationTokenSource _evaluation = new();
    private Task _restore = Task.CompletedTask;

    public SolutionSession(ISessionEvents events, string dotnet)
    {
        _events = events;
        _dotnet = dotnet;
        _evaluator = new ProjectEvaluator(_collection);
        _editor = new ProjectFileEditor(_collection);
    }

    public string Path { get; private set; } = string.Empty;

    public bool Trusted { get; private set; }

    public SolutionConfigurationInfo Active { get; private set; } = new("Debug", "Any CPU");

    private bool IsSingleProject => _model is null;

    private IEnumerable<string> ProjectPaths => _info.Projects.Select(p => p.Path);

    // ------------------------------------------------------------------ open

    public async Task<SolutionInfo> OpenAsync(OpenSolutionParams parameters, CancellationToken cancellationToken)
    {
        Path = System.IO.Path.GetFullPath(parameters.Path);
        Trusted = parameters.Trusted;
        _info = await ReadSolutionAsync(cancellationToken).ConfigureAwait(false);
        if (parameters.Configuration is { } configuration && parameters.Platform is { } platform
            && _info.Configurations.FirstOrDefault(c => c.Configuration == configuration && c.Platform == platform) is { } requested)
        {
            Active = requested;
        }
        else if (_info.Configurations.Length > 0)
        {
            Active = _info.Configurations.FirstOrDefault(c => c.Configuration == "Debug") ?? _info.Configurations[0];
        }

        _info = _info with { Active = Active, Trusted = Trusted };
        if (Trusted)
        {
            StartWatching();
            StartEvaluation(restoreAfterwards: parameters.Restore);
        }

        return _info;
    }

    /// <summary>The user trusted the folder: evaluate (and restore) now.</summary>
    public void Trust(bool restore)
    {
        if (Trusted)
        {
            return;
        }

        Trusted = true;
        _info = _info with { Trusted = true };
        StartWatching();
        StartEvaluation(restore);
    }

    public void SetConfiguration(SetConfigurationParams parameters)
    {
        Active = new SolutionConfigurationInfo(parameters.Configuration, parameters.Platform);
        _info = _info with { Active = Active };
        if (Trusted)
        {
            StartEvaluation(restoreAfterwards: false);
        }
    }

    private async Task<SolutionInfo> ReadSolutionAsync(CancellationToken cancellationToken)
    {
        var extension = System.IO.Path.GetExtension(Path);
        if (extension is ".sln" or ".slnx")
        {
            _serializer = SolutionSerializers.GetSerializerByMoniker(Path)
                          ?? throw new InvalidOperationException($"Unsupported solution format: {Path}");
            _model = await _serializer.OpenAsync(Path, cancellationToken).ConfigureAwait(false);
            return Describe(_model);
        }

        // A project opened on its own: an in-memory solution with that project.
        _model = null;
        _serializer = null;
        return new SolutionInfo(
            Path,
            "project",
            [],
            [new SolutionProjectInfo(Path, System.IO.Path.GetFileNameWithoutExtension(Path), null, null)],
            [new("Debug", "Any CPU"), new("Release", "Any CPU")],
            Active,
            Trusted);
    }

    private SolutionInfo Describe(SolutionModel model)
    {
        var directory = System.IO.Path.GetDirectoryName(Path)!;
        var folders = model.SolutionFolders
            .Select(f => new SolutionFolderInfo(f.Path, f.Name, f.Parent?.Path, [.. (f.Files ?? []).Select(file => Full(directory, file))]))
            .ToArray();
        var projects = model.SolutionProjects
            .Select(p => new SolutionProjectInfo(Full(directory, p.FilePath), p.ActualDisplayName, p.Parent?.Path, p.Type))
            .ToArray();
        var buildTypes = model.BuildTypes.Count > 0 ? model.BuildTypes : ["Debug", "Release"];
        var platforms = model.Platforms.Count > 0 ? model.Platforms : ["Any CPU"];
        var configurations = buildTypes.SelectMany(b => platforms.Select(p => new SolutionConfigurationInfo(b, p))).ToArray();
        return new SolutionInfo(Path, System.IO.Path.GetExtension(Path).TrimStart('.'), folders, projects, configurations, Active, Trusted);
    }

    private static string Full(string directory, string path) =>
        System.IO.Path.GetFullPath(System.IO.Path.Combine(directory, path.Replace('\\', System.IO.Path.DirectorySeparatorChar)));

    // ------------------------------------------------------------------ evaluation

    private void StartEvaluation(bool restoreAfterwards)
    {
        var previous = Interlocked.Exchange(ref _evaluation, CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token));
        previous.Cancel();
        previous.Dispose();
        var token = _evaluation.Token;
        _ = Task.Run(async () =>
        {
            await EvaluateAsync(ProjectPaths.ToList(), token).ConfigureAwait(false);
            if (restoreAfterwards && !token.IsCancellationRequested)
            {
                StartRestore(null);
            }
        }, token);
    }

    /// <summary>Evaluates <paramref name="projects"/> in parallel, reporting each one as soon as it is done.</summary>
    private async Task EvaluateAsync(IReadOnlyList<string> projects, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var context = EvaluationContext.Create(EvaluationContext.SharingPolicy.Shared);
        var done = 0;
        await _events.ProgressAsync(new EvaluationProgressParams(0, projects.Count, projects.Count == 0)).ConfigureAwait(false);
        await Parallel.ForEachAsync(
            projects,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount / 2), CancellationToken = cancellationToken },
            async (project, _) =>
            {
                var (configuration, platform) = ProjectConfiguration(project);
                var result = _evaluator.Evaluate(project, configuration, platform, context);
                _evaluated[project] = result;
                await _events.ProjectEvaluatedAsync(result.Info).ConfigureAwait(false);
                var count = Interlocked.Increment(ref done);
                await _events.ProgressAsync(new EvaluationProgressParams(count, projects.Count, count == projects.Count)).ConfigureAwait(false);
            }).ConfigureAwait(false);
        _watcher?.SetImports(_evaluated.Values.SelectMany(e => e.Imports));
        await _events.LogAsync("info", $"Evaluated {projects.Count} project(s) in {Stopwatch.GetElapsedTime(started).TotalMilliseconds:0} ms ({Active.Name}).").ConfigureAwait(false);
    }

    /// <summary>The project configuration the active solution configuration maps to (MSBuild spells "Any CPU" "AnyCPU").</summary>
    private (string Configuration, string Platform) ProjectConfiguration(string projectPath)
    {
        var (configuration, platform) = (Active.Configuration, Active.Platform);
        if (_model?.SolutionProjects.FirstOrDefault(p => s_pathComparer.Equals(Full(System.IO.Path.GetDirectoryName(Path)!, p.FilePath), projectPath)) is { } project)
        {
            var mapped = project.GetProjectConfiguration(Active.Configuration, Active.Platform);
            configuration = mapped.Item1 ?? configuration;
            platform = mapped.Item2 ?? platform;
        }

        return (configuration, platform.Replace(" ", string.Empty, StringComparison.Ordinal));
    }

    public Task ReevaluateAsync(IReadOnlyList<string> projects) => Trusted ? EvaluateAsync(projects, _lifetime.Token) : Task.CompletedTask;

    // ------------------------------------------------------------------ watching

    private void StartWatching()
    {
        _watcher ??= new ProjectWatcher(OnFilesChanged);
        _watcher.Watch(Path, ProjectPaths);
    }

    private async Task OnFilesChanged(ProjectWatcher.Changes changes)
    {
        try
        {
            if (changes.SolutionFileChanged && !IsSingleProject)
            {
                await _solutionLock.WaitAsync(_lifetime.Token).ConfigureAwait(false);
                try
                {
                    var old = ProjectPaths.ToHashSet(s_pathComparer);
                    _info = await ReadSolutionAsync(_lifetime.Token).ConfigureAwait(false) with { Active = Active, Trusted = Trusted };
                    await _events.SolutionChangedAsync(_info).ConfigureAwait(false);
                    _watcher?.Watch(Path, ProjectPaths);
                    var added = ProjectPaths.Where(p => !old.Contains(p)).ToList();
                    await EvaluateAsync(added, _lifetime.Token).ConfigureAwait(false);
                }
                finally
                {
                    _solutionLock.Release();
                }
            }

            foreach (var file in changes.MSBuildFiles)
            {
                ProjectRootElement.TryOpen(file, _collection, preserveFormatting: true)?.Reload(throwIfUnsavedChanges: false, preserveFormatting: true);
            }

            // Projects whose own folder changed, plus every project that imports a changed MSBuild file.
            var affected = ProjectPaths
                .Where(p => changes.Directories.Any(d => ProjectFileEditor.IsSameOrUnder(d, System.IO.Path.GetDirectoryName(p)!))
                            || changes.MSBuildFiles.Any(f => s_pathComparer.Equals(f, p)
                                                             || (_evaluated.TryGetValue(p, out var e) && e.Imports.Contains(f, s_pathComparer))))
                .ToList();
            await ReevaluateAsync(affected).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            await _events.LogAsync("warning", $"Could not refresh after file changes: {ex.Message}").ConfigureAwait(false);
        }
    }

    // ------------------------------------------------------------------ restore

    public void StartRestore(string? path)
    {
        if (!Trusted)
        {
            throw new InvalidOperationException("Restore runs only in trusted solutions.");
        }

        _restore = _restore.ContinueWith(_ => RestoreAsync(path ?? Path), TaskScheduler.Default).Unwrap();
    }

    private async Task RestoreAsync(string target)
    {
        await _events.RestoreStateAsync("running", null).ConfigureAwait(false);
        var psi = new ProcessStartInfo(_dotnet)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = System.IO.Path.GetDirectoryName(target)!,
        };
        foreach (var arg in new[] { "restore", target, "--nologo", "--verbosity", "minimal" })
        {
            psi.ArgumentList.Add(arg);
        }

        psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        psi.Environment["DOTNET_NOLOGO"] = "1";
        psi.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";

        // MSBuildLocator pointed this process at the SDK's MSBuild through environment variables; the child
        // dotnet must resolve its own.
        foreach (var variable in new[] { "MSBUILD_EXE_PATH", "MSBuildExtensionsPath", "MSBuildSDKsPath", "MSBuildLoadMicrosoftTargetsReadOnly" })
        {
            psi.Environment.Remove(variable);
        }
        try
        {
            using var process = Process.Start(psi) ?? throw new InvalidOperationException("dotnet restore did not start.");
            var output = PumpAsync(process.StandardOutput);
            var error = PumpAsync(process.StandardError);
            await process.WaitForExitAsync(_lifetime.Token).ConfigureAwait(false);
            await Task.WhenAll(output, error).ConfigureAwait(false);
            var ok = process.ExitCode == 0;
            await _events.RestoreStateAsync(ok ? "succeeded" : "failed", ok ? null : $"dotnet restore exited with code {process.ExitCode}.").ConfigureAwait(false);
            await EvaluateAsync(ProjectPaths.ToList(), _lifetime.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            await _events.RestoreStateAsync("failed", ex.Message).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task PumpAsync(StreamReader reader)
    {
        while (await reader.ReadLineAsync(_lifetime.Token).ConfigureAwait(false) is { } line)
        {
            await _events.RestoreOutputAsync(line).ConfigureAwait(false);
        }
    }

    // ------------------------------------------------------------------ file operations

    public FileOperationResult CreateFile(CreateFileParams parameters)
    {
        var path = System.IO.Path.GetFullPath(parameters.Path);
        if (File.Exists(path) || Directory.Exists(path))
        {
            throw new IOException($"'{System.IO.Path.GetFileName(path)}' already exists.");
        }

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllText(path, parameters.Contents ?? string.Empty);
        return new FileOperationResult([], [path]);
    }

    public FileOperationResult CreateFolder(CreateFolderParams parameters)
    {
        var path = System.IO.Path.GetFullPath(parameters.Path);
        if (File.Exists(path) || Directory.Exists(path))
        {
            throw new IOException($"'{System.IO.Path.GetFileName(path)}' already exists.");
        }

        Directory.CreateDirectory(path);
        return new FileOperationResult([], [path]);
    }

    public async Task<FileOperationResult> RenameAsync(RenameParams parameters, CancellationToken cancellationToken)
    {
        var oldPath = System.IO.Path.GetFullPath(parameters.OldPath).TrimEnd(System.IO.Path.DirectorySeparatorChar);
        var newPath = System.IO.Path.GetFullPath(parameters.NewPath).TrimEnd(System.IO.Path.DirectorySeparatorChar);
        MovePath(oldPath, newPath, copy: false);
        var updated = new List<string>(_editor.Renamed(ProjectPathsAfter(oldPath, newPath), oldPath, newPath));
        updated.AddRange(await UpdateSolutionPathsAsync(oldPath, newPath, cancellationToken).ConfigureAwait(false));
        return new FileOperationResult([.. updated.Distinct(s_pathComparer)], [newPath]);
    }

    public async Task<FileOperationResult> MoveAsync(MoveParams parameters, CancellationToken cancellationToken)
    {
        var target = System.IO.Path.GetFullPath(parameters.TargetFolder);
        var updated = new List<string>();
        var created = new List<string>();
        foreach (var source in parameters.Sources.Select(s => System.IO.Path.GetFullPath(s).TrimEnd(System.IO.Path.DirectorySeparatorChar)))
        {
            var destination = System.IO.Path.Combine(target, System.IO.Path.GetFileName(source));
            if (ProjectFileEditor.IsSameOrUnder(target, source))
            {
                throw new IOException($"Can't move '{System.IO.Path.GetFileName(source)}' into itself.");
            }

            MovePath(source, destination, parameters.Copy);
            created.Add(destination);
            if (!parameters.Copy)
            {
                updated.AddRange(_editor.Renamed(ProjectPathsAfter(source, destination), source, destination));
                updated.AddRange(await UpdateSolutionPathsAsync(source, destination, cancellationToken).ConfigureAwait(false));
            }
        }

        return new FileOperationResult([.. updated.Distinct(s_pathComparer)], [.. created]);
    }

    public async Task<FileOperationResult> DeleteAsync(DeleteParams parameters, CancellationToken cancellationToken)
    {
        var updated = new List<string>();
        foreach (var path in parameters.Paths.Select(p => System.IO.Path.GetFullPath(p).TrimEnd(System.IO.Path.DirectorySeparatorChar)))
        {
            Trash.Move(path);
            updated.AddRange(_editor.Deleted(ProjectPaths.Where(File.Exists), path));
            if (!IsSingleProject && ProjectPaths.Any(p => ProjectFileEditor.IsSameOrUnder(p, path)))
            {
                await EditSolutionAsync(model =>
                {
                    foreach (var project in model.SolutionProjects.Where(p => ProjectFileEditor.IsSameOrUnder(Full(SolutionDirectory, p.FilePath), path)).ToList())
                    {
                        model.RemoveProject(project);
                    }
                }, cancellationToken).ConfigureAwait(false);
                updated.Add(Path);
            }
        }

        return new FileOperationResult([.. updated.Distinct(s_pathComparer)], []);
    }

    public ListDirectoryResult ListDirectory(ListDirectoryParams parameters)
    {
        var directory = new DirectoryInfo(parameters.Path);
        if (!directory.Exists)
        {
            return new ListDirectoryResult([]);
        }

        return new ListDirectoryResult([.. directory.EnumerateFileSystemInfos()
            .Where(e => (e.Attributes & FileAttributes.Hidden) == 0)
            .Select(e => new DirectoryEntry(e.FullName, e is DirectoryInfo))]);
    }

    /// <summary>The projects as they are after a rename (a renamed project file or folder moves projects along).</summary>
    private List<string> ProjectPathsAfter(string oldPath, string newPath) =>
        [.. ProjectPaths.Select(p => ProjectFileEditor.IsSameOrUnder(p, oldPath) ? newPath + p[oldPath.Length..] : p)];

    private static void MovePath(string source, string destination, bool copy)
    {
        if (!File.Exists(source) && !Directory.Exists(source))
        {
            throw new FileNotFoundException($"'{System.IO.Path.GetFileName(source)}' no longer exists.", source);
        }

        var caseOnly = string.Equals(source, destination, StringComparison.OrdinalIgnoreCase) && source != destination;
        if (!caseOnly && (File.Exists(destination) || Directory.Exists(destination)))
        {
            throw new IOException($"'{System.IO.Path.GetFileName(destination)}' already exists.");
        }

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(destination)!);
        if (Directory.Exists(source))
        {
            if (copy)
            {
                CopyDirectory(source, destination);
            }
            else if (caseOnly)
            {
                var temp = destination + ".~" + Guid.NewGuid().ToString("N")[..8];
                Directory.Move(source, temp);
                Directory.Move(temp, destination);
            }
            else
            {
                Directory.Move(source, destination);
            }
        }
        else if (copy)
        {
            File.Copy(source, destination);
        }
        else
        {
            File.Move(source, destination);
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, System.IO.Path.Combine(destination, System.IO.Path.GetFileName(file)));
        }

        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            CopyDirectory(directory, System.IO.Path.Combine(destination, System.IO.Path.GetFileName(directory)));
        }
    }

    // ------------------------------------------------------------------ solution edits

    private string SolutionDirectory => System.IO.Path.GetDirectoryName(Path)!;

    /// <summary>Points solution entries at projects that moved with a file or folder rename.</summary>
    private async Task<IReadOnlyList<string>> UpdateSolutionPathsAsync(string oldPath, string newPath, CancellationToken cancellationToken)
    {
        if (IsSingleProject)
        {
            if (ProjectFileEditor.IsSameOrUnder(Path, oldPath))
            {
                Path = newPath + Path[oldPath.Length..];
            }

            return [];
        }

        var moved = false;
        await EditSolutionAsync(model =>
        {
            foreach (var project in model.SolutionProjects)
            {
                var full = Full(SolutionDirectory, project.FilePath);
                if (ProjectFileEditor.IsSameOrUnder(full, oldPath))
                {
                    project.FilePath = System.IO.Path.GetRelativePath(SolutionDirectory, newPath + full[oldPath.Length..]);
                    moved = true;
                }
            }

            foreach (var folder in model.SolutionFolders)
            {
                foreach (var file in (folder.Files ?? []).ToList())
                {
                    var full = Full(SolutionDirectory, file);
                    if (ProjectFileEditor.IsSameOrUnder(full, oldPath))
                    {
                        folder.RemoveFile(file);
                        folder.AddFile(System.IO.Path.GetRelativePath(SolutionDirectory, newPath + full[oldPath.Length..]));
                        moved = true;
                    }
                }
            }
        }, cancellationToken, saveOnlyIf: () => moved).ConfigureAwait(false);
        return moved ? [Path] : [];
    }

    public Task AddProjectAsync(AddProjectParams parameters, CancellationToken cancellationToken) =>
        EditSolutionAsync(model =>
        {
            var relative = System.IO.Path.GetRelativePath(SolutionDirectory, System.IO.Path.GetFullPath(parameters.ProjectPath));
            if (model.FindProject(relative) is not null)
            {
                return;
            }

            var folder = parameters.SolutionFolder is { } id ? model.FindFolder(id) ?? model.AddFolder(id) : null;
            model.AddProject(relative, null, folder);
        }, cancellationToken);

    public Task RemoveProjectAsync(ProjectPathParams parameters, CancellationToken cancellationToken) =>
        EditSolutionAsync(model =>
        {
            if (FindProject(model, parameters.Path) is { } project)
            {
                model.RemoveProject(project);
            }
        }, cancellationToken);

    public Task AddFolderAsync(SolutionFolderParams parameters, CancellationToken cancellationToken) =>
        EditSolutionAsync(model => model.AddFolder($"{parameters.Parent?.TrimEnd('/') ?? string.Empty}/{parameters.Name}/"), cancellationToken);

    public Task RenameFolderAsync(RenameSolutionFolderParams parameters, CancellationToken cancellationToken) =>
        EditSolutionAsync(model =>
        {
            var folder = model.FindFolder(parameters.Id) ?? throw new InvalidOperationException($"No solution folder '{parameters.Id}'.");
            folder.Name = parameters.NewName;
        }, cancellationToken);

    public Task RemoveFolderAsync(RemoveSolutionFolderParams parameters, CancellationToken cancellationToken) =>
        EditSolutionAsync(model =>
        {
            if (model.FindFolder(parameters.Id) is { } folder)
            {
                model.RemoveFolder(folder);
            }
        }, cancellationToken);

    public Task MoveInSolutionAsync(MoveInSolutionParams parameters, CancellationToken cancellationToken) =>
        EditSolutionAsync(model =>
        {
            SolutionItemModel item = (IsFolderId(parameters.Item) ? model.FindFolder(parameters.Item) : (SolutionItemModel?)FindProject(model, parameters.Item))
                                     ?? throw new InvalidOperationException($"'{parameters.Item}' is not in the solution.");
            var target = parameters.TargetFolder is { } id ? model.FindFolder(id) ?? throw new InvalidOperationException($"No solution folder '{id}'.") : null;
            item.MoveToFolder(target);
        }, cancellationToken);

    public bool AddProjectReference(ProjectReferenceParams parameters) =>
        _editor.AddProjectReference(System.IO.Path.GetFullPath(parameters.ProjectPath), System.IO.Path.GetFullPath(parameters.ReferencePath));

    public bool RemoveProjectReference(ProjectReferenceParams parameters) =>
        _editor.RemoveProjectReference(System.IO.Path.GetFullPath(parameters.ProjectPath), System.IO.Path.GetFullPath(parameters.ReferencePath));

    /// <summary>Solution folder ids start and end with '/' (<c>/src/</c>); absolute Unix paths only start with one.</summary>
    private static bool IsFolderId(string item) => item.Length > 1 && item[0] == '/' && item[^1] == '/';

    private SolutionProjectModel? FindProject(SolutionModel model, string path)
    {
        var full = System.IO.Path.GetFullPath(path);
        return model.SolutionProjects.FirstOrDefault(p => s_pathComparer.Equals(Full(SolutionDirectory, p.FilePath), full));
    }

    /// <summary>Applies <paramref name="edit"/> to the solution file (kept in its format) and reports the new structure.</summary>
    private async Task EditSolutionAsync(Action<SolutionModel> edit, CancellationToken cancellationToken, Func<bool>? saveOnlyIf = null)
    {
        if (_model is null || _serializer is null)
        {
            throw new InvalidOperationException("A project opened on its own has no solution file to edit.");
        }

        await _solutionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Re-read first: the file may have been edited outside Daedalus.
            _model = await _serializer.OpenAsync(Path, cancellationToken).ConfigureAwait(false);
            var old = ProjectPaths.ToHashSet(s_pathComparer);
            edit(_model);
            if (saveOnlyIf is not null && !saveOnlyIf())
            {
                return;
            }

            _watcher?.Suppress(Path);
            await _serializer.SaveAsync(Path, _model, cancellationToken).ConfigureAwait(false);
            _info = Describe(_model);
            await _events.SolutionChangedAsync(_info).ConfigureAwait(false);
            if (Trusted)
            {
                _watcher?.Watch(Path, ProjectPaths);
                _ = EvaluateAsync([.. ProjectPaths.Where(p => !old.Contains(p))], _lifetime.Token);
            }
        }
        finally
        {
            _solutionLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);
        _watcher?.Dispose();
        try
        {
            await _restore.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _evaluation.Dispose();
        _lifetime.Dispose();
        _solutionLock.Dispose();
        _collection.Dispose();
    }
}
