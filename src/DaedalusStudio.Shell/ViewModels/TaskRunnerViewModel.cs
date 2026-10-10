using System.Collections.ObjectModel;
using DaedalusStudio.Shell.Infrastructure;
using DaedalusStudio.Workbench.Node;

namespace DaedalusStudio.Shell.ViewModels;

/// <summary>A row of the Task Runner Explorer: a <c>package.json</c> (a header) or one of its scripts.</summary>
public sealed record TaskRunnerRow(NpmPackage Package, NpmScript? Script, string Label, string Detail)
{
    public bool IsHeader => Script is null;

    public string AccessibleName => IsHeader ? $"{Label}, {Detail}" : $"Script {Label}: {Detail}";
}

/// <summary>
/// The Task Runner Explorer (M16d, ADR 0027): the npm scripts of the solution's <c>package.json</c> files. Running a
/// script opens it in a terminal; Restore installs a package's dependencies. Scripts bound to Before Build, After
/// Build, Clean or Project Open (<c>"-vs-binding"</c>) say so.
/// </summary>
public sealed class TaskRunnerViewModel(Func<IReadOnlyList<string>> roots, Func<string?> relativeTo, Func<NpmPackage, NpmScript, Task> run, Func<NpmPackage, Task> restore, Func<string, Task> open) : ObservableObject
{
    public const string ToolWindowId = "web.taskRunner";

    private string? _status = "Open a solution with a package.json.";
    private IReadOnlyList<NpmPackage> _packages = [];
    private int _refreshes;

    public ObservableCollection<TaskRunnerRow> Rows { get; } = [];

    public IReadOnlyList<NpmPackage> Packages => _packages;

    public string? Status
    {
        get => _status;
        private set
        {
            if (SetField(ref _status, value))
            {
                OnPropertyChanged(nameof(HasStatus));
            }
        }
    }

    public bool HasStatus => _status is not null;

    /// <summary>Finds the packages again (off the UI thread) and lists their scripts.</summary>
    public async Task RefreshAsync()
    {
        var refresh = ++_refreshes;
        var folders = roots();
        var packages = await Task.Run(() => Npm.Find(folders).Select(Npm.Read).OfType<NpmPackage>().ToList()).ConfigureAwait(true);
        if (refresh != _refreshes)
        {
            return;
        }

        _packages = packages;
        Rows.Clear();
        var root = relativeTo();
        foreach (var package in packages)
        {
            var label = root is null ? package.Path : Path.GetRelativePath(root, package.Path).Replace('\\', '/');
            var state = package.HasDependencies && !package.IsRestored ? "packages not installed" : null;
            Rows.Add(new TaskRunnerRow(package, null, label, string.Join(" · ", new[] { package.Name, state }.OfType<string>())));
            foreach (var script in package.Scripts)
            {
                var bindings = package.Bindings.Where(b => b.Value.Contains(script.Name, StringComparer.Ordinal)).Select(b => Display(b.Key)).ToList();
                Rows.Add(new TaskRunnerRow(package, script, script.Name, bindings.Count == 0 ? script.Command : $"{script.Command}  [{string.Join(", ", bindings)}]"));
            }
        }

        Status = packages.Count == 0 ? "No package.json in the solution." : null;
    }

    /// <summary>A binding's name as the user reads it: <c>BeforeBuild</c> → Before Build.</summary>
    public static string Display(string binding) => binding switch
    {
        Npm.BeforeBuild => "Before Build",
        Npm.AfterBuild => "After Build",
        Npm.ProjectOpen => "Project Open",
        _ => binding,
    };

    public Task RunAsync(TaskRunnerRow row) => row.Script is { } script ? run(row.Package, script) : open(row.Package.Path);

    public Task RestoreAsync(TaskRunnerRow row) => restore(row.Package);

    public Task OpenAsync(TaskRunnerRow row) => open(row.Package.Path);
}
