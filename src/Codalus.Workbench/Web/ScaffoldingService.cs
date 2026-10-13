using Codalus.Projects.Protocol;
using Codalus.Workbench.Output;
using Codalus.Workbench.Projects;

namespace Codalus.Workbench.Web;

/// <summary>The code generator can't run yet: <see cref="Status"/> says what's missing (the tool, or the design package).</summary>
public sealed class ScaffoldToolMissingException(ScaffoldStatusResult status, string? message = null)
    : Exception(message ?? status.Message ?? "dotnet-aspnet-codegenerator isn't available.")
{
    public ScaffoldStatusResult Status { get; } = status;
}

/// <summary>What a scaffolder made: the files it created and those it changed.</summary>
public sealed record ScaffoldResult(IReadOnlyList<string> Created, IReadOnlyList<string> Modified);

/// <summary>
/// "Add New Scaffolded Item" (ADR 0041): runs a <see cref="Scaffolder"/> in a project. SDK item templates go through
/// <c>templates/instantiate</c>; Entity Framework and Identity scaffolders run the user's
/// <c>dotnet aspnet-codegenerator</c> in the build host (<c>scaffold/run</c>, trusted solutions only); the empty endpoint
/// group is written here. UI-agnostic; events arrive on any thread.
/// </summary>
public sealed class ScaffoldingService
{
    public const string OutputChannelId = "codalus.scaffolding";

    private static readonly TimeSpan s_runTimeout = TimeSpan.FromMinutes(10);
    private static WebToolsJsonContext Json => WebToolsJsonContext.Default;

    /// <summary>Templates whose <c>--namespace</c> option the IDE fills from the project and the folder.</summary>
    private static readonly HashSet<string> s_namespacedTemplates = new(StringComparer.Ordinal) { "apicontroller", "mvccontroller", "page" };

    private readonly SolutionService _solutions;
    private readonly ProjectManagementService _projects;
    private readonly OutputService _output;

    public ScaffoldingService(SolutionService solutions, ProjectManagementService projects, OutputService output)
    {
        _solutions = solutions;
        _projects = projects;
        _output = output;
        solutions.ScaffoldOutput += (_, o) => Channel.AppendLine(o.Level == "error" && !o.Line.StartsWith('>') ? "error: " + o.Line : o.Line);
    }

    public OutputChannel Channel => _output.GetOrCreate(OutputChannelId, "Scaffolding");

    /// <summary>Files were scaffolded (the Solution Explorer and the Endpoints Explorer refresh on their own).</summary>
    public event EventHandler<ScaffoldResult>? Scaffolded;

    public Task<ScaffoldStatusResult> StatusAsync(string project, CancellationToken cancellationToken) =>
        _solutions.RequestAsync(ScaffoldMethods.Status, new ScaffoldStatusParams(project), Json.ScaffoldStatusParams, Json.ScaffoldStatusResult, cancellationToken);

    /// <summary>Installs (or restores) <c>dotnet-aspnet-codegenerator</c> and adds the design package.</summary>
    public Task<ScaffoldStatusResult> InstallAsync(string project, CancellationToken cancellationToken) =>
        _solutions.RequestAsync(ScaffoldMethods.Install, new ScaffoldInstallParams(project), Json.ScaffoldInstallParams, Json.ScaffoldStatusResult, cancellationToken, s_runTimeout);

    public async Task<ScaffoldResult> RunAsync(string project, Scaffolder scaffolder, IReadOnlyDictionary<string, string?> values, CancellationToken cancellationToken)
    {
        var plan = Scaffolders.Plan(scaffolder, values);
        var directory = Path.GetDirectoryName(project)!;
        var folder = plan.Folder is { } relative ? Path.GetFullPath(Path.Combine(directory, relative.Replace('/', Path.DirectorySeparatorChar))) : directory;
        ScaffoldResult result;
        switch (scaffolder.Engine)
        {
            case ScaffoldEngine.DotnetNew:
            {
                var arguments = new List<string>(plan.Arguments);
                if (s_namespacedTemplates.Contains(plan.Command))
                {
                    arguments.AddRange(["--namespace", await NamespaceAsync(project, folder, cancellationToken).ConfigureAwait(false)]);
                }

                Channel.AppendLine($"> dotnet new {plan.Command} --name {plan.Name} --output {folder} {string.Join(' ', arguments)}");
                var created = await _solutions.RequestAsync(
                    ItemMethods.Instantiate,
                    new InstantiateTemplateParams(plan.Command, plan.Name!, folder, project, "C#", [.. arguments]),
                    ItemJsonContext.Default.InstantiateTemplateParams,
                    ItemJsonContext.Default.InstantiateTemplateResult,
                    cancellationToken).ConfigureAwait(false);
                Channel.AppendLine(created.Output.Trim());
                result = new ScaffoldResult(created.CreatedPaths, []);
                break;
            }

            case ScaffoldEngine.BuiltIn:
            {
                var path = Path.Combine(folder, plan.Name + ".cs");
                if (File.Exists(path))
                {
                    throw new InvalidOperationException($"{Path.GetFileName(path)} already exists in {folder}.");
                }

                var fileScoped = _solutions.Projects.TryGetValue(project, out var info) && ItemTemplates.SupportsFileScopedNamespaces(info.TargetFrameworks);
                var route = values.TryGetValue("route", out var given) && given is { Length: > 0 } ? given.Trim() : scaffolder.Parameters.FirstOrDefault(p => p.Id == "route")?.Default ?? "/api";
                var text = Scaffolders.EndpointsClass(await NamespaceAsync(project, folder, cancellationToken).ConfigureAwait(false), plan.Name!, route, fileScoped);
                Directory.CreateDirectory(folder);
                await _solutions.CreateFileAsync(path, text.Replace("\n", Environment.NewLine, StringComparison.Ordinal), cancellationToken).ConfigureAwait(false);
                Channel.AppendLine($"Added {path}. Call app.Map{TagOf(plan.Name!)}Endpoints() in Program.cs.");
                result = new ScaffoldResult([path], []);
                break;
            }

            default:
            {
                var status = await StatusAsync(project, cancellationToken).ConfigureAwait(false);
                if (!status.ToolAvailable || status.DesignPackageVersion is null)
                {
                    throw new ScaffoldToolMissingException(status);
                }

                if (scaffolder.UsesEntityFramework && status.EfToolsVersion is null)
                {
                    throw new ScaffoldToolMissingException(status, "The Entity Framework scaffolders need the project to reference Microsoft.EntityFrameworkCore.Tools.");
                }

                var arguments = new List<string>(plan.Arguments);
                if (scaffolder.Parameters.Any(p => p.Kind == ScaffoldParameterKind.DbProvider)
                    && !arguments.Contains("-dbProvider") && Scaffolders.DbProviderFor(status.EfProviders) is { } provider)
                {
                    // The generator assumes SQL Server; the project's own provider is the better default.
                    arguments.AddRange(["-dbProvider", provider]);
                }

                var run = await _solutions.RequestAsync(
                    ScaffoldMethods.Run,
                    new ScaffoldRunParams(project, plan.Command, [.. arguments]),
                    Json.ScaffoldRunParams,
                    Json.ScaffoldRunResult,
                    cancellationToken,
                    s_runTimeout).ConfigureAwait(false);
                result = new ScaffoldResult(run.Created, run.Modified);
                break;
            }
        }

        Scaffolded?.Invoke(this, result);
        return result;
    }

    private static string TagOf(string name) => name.EndsWith("Endpoints", StringComparison.Ordinal) && name.Length > "Endpoints".Length ? name[..^"Endpoints".Length] : name;

    /// <summary>The namespace of a new file in <paramref name="folder"/>: the project's root namespace plus the folders.</summary>
    private async Task<string> NamespaceAsync(string project, string folder, CancellationToken cancellationToken)
    {
        var root = Path.GetFileNameWithoutExtension(project);
        try
        {
            var properties = await _projects.GetPropertiesAsync(project, ["RootNamespace"], null, cancellationToken).ConfigureAwait(false);
            if (properties.Properties.FirstOrDefault(p => p.Name == "RootNamespace")?.Value is { Length: > 0 } configured)
            {
                root = configured;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Rpc.JsonRpcRemoteException)
        {
            // An unloaded project: the file name will do.
        }

        return ItemTemplates.NamespaceFor(root, Path.GetDirectoryName(project)!, folder);
    }
}
